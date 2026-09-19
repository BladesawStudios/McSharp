using System;
using System.Runtime.InteropServices;
using McSharp.Internal;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp;

public static unsafe class MeshCodec
{
    public const int DefaultWorkBufferSize = 0x10000000;

    /// <summary>
    /// Ceiling the <c>byte[]</c>-returning convenience overloads place on a size read out of a file
    /// header before allocating for it. The <see cref="Span{T}"/> overloads do not allocate and are
    /// not subject to it.
    /// </summary>
    public const uint MaxDecompressedSize = 0x40000000;

    private static readonly int FmshHeaderSize = Marshal.SizeOf<ResMeshCodecHeader>();

    /// <summary>
    /// Whether denormal half floats are flushed to zero while decoding. This is per-thread: set it
    /// on the thread that will do the decoding.
    /// </summary>
    public static bool FlushDenormalHalves
    {
        get => FloatMath.FlushDenormalHalves;
        set => FloatMath.FlushDenormalHalves = value;
    }

    public static nuint GetFrameSize(in ResCompressionHeader header)
        => header.SizeInfo.StreamOffset.Value + header.SizeInfo.EndOffset.Value;

    public static bool HasFmshSection(ReadOnlySpan<byte> decompressedBfres)
    {
        if (decompressedBfres.Length <= 0xee)
            return false;
        return ((decompressedBfres[0xee] >> 3) & 1) != 0;
    }

    public static bool TryReadPackageHeader(ReadOnlySpan<byte> src, out ResMeshCodecPackageHeader header)
    {
        if (src.Length < Marshal.SizeOf<ResMeshCodecPackageHeader>())
        {
            header = default;
            return false;
        }
        header = MemoryMarshal.Read<ResMeshCodecPackageHeader>(src);
        return header.MagicValue == ResMeshCodecPackageHeader.Magic;
    }

    public static bool TryReadChunkHeader(ReadOnlySpan<byte> src, out ResChunkHeader header)
    {
        if (src.Length < Marshal.SizeOf<ResChunkHeader>())
        {
            header = default;
            return false;
        }

        header = MemoryMarshal.Read<ResChunkHeader>(src);

        if (!IsConsistentChunkHeader(header))
        {
            header = default;
            return false;
        }

        return true;
    }

    private static bool IsConsistentChunkHeader(in ResChunkHeader header)
    {
        if (header.CompHeader.GetCodecType() == CodecType.Invalid)
            return false;

        // The two streams are laid out back to back inside the decompressed buffer.
        if (header.VertexOutputSize > header.DecompressedSize)
            return false;

        return header.IndexOutputSize <= header.DecompressedSize - header.VertexOutputSize;
    }

    public static uint GetRequiredWorkBufferSize(ReadOnlySpan<byte> package)
    {
        int offset = McEncoder.FindFmshOffset(package);
        if (offset < 0)
            return 0;

        return MemoryMarshal.Read<ResMeshCodecHeader>(package.Slice(offset, FmshHeaderSize)).WorkMemSize;
    }

    /// <summary>
    /// Decodes a package, allocating the output and scratch buffers from sizes declared in the file.
    /// Returns <see langword="null"/> if the input is not a package McSharp can decode, including
    /// when its declared sizes are inconsistent or exceed <see cref="MaxDecompressedSize"/>.
    /// </summary>
    public static byte[]? DecompressMc(ReadOnlySpan<byte> src)
    {
        if (!TryReadPackageHeader(src, out ResMeshCodecPackageHeader header))
            return null;

        uint decompressedSize = header.GetDecompressedSize();
        if (decompressedSize > MaxDecompressedSize)
            return null;

        uint workSize = GetRequiredWorkBufferSize(src);
        if (workSize > DefaultWorkBufferSize)
            return null;

        byte[] dst = new byte[decompressedSize];
        byte[] work = new byte[workSize];
        return DecompressMc(dst, src, work) ? dst : null;
    }

    /// <summary>
    /// Decodes a package into <paramref name="dst"/>. Returns <see langword="false"/> rather than
    /// throwing for malformed or truncated input.
    /// </summary>
    public static bool DecompressMc(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        if (src.Length < 0xc)
            return false;

        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                return DecompressMcCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
        }
        catch (MeshCodecException)
        {
            return false;
        }
    }

    private static bool DecompressMcCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        if (srcSize < 0xc || src == null)
            return false;

        ResMeshCodecPackageHeader* header = (ResMeshCodecPackageHeader*)src;

        if (header->MagicValue != ResMeshCodecPackageHeader.Magic)
            return false;

        if (header->VersionMajor != 0 || header->VersionMinor > 1)
            return false;

        nuint decompressedSize = header->GetDecompressedSize();

        if (dstSize < decompressedSize)
            return false;

        ZSTD_DCtx_s* dctx = ZSTD_createDCtx();

        if (dctx == null)
            return false;

        byte* ptr;
        try
        {
            ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_experimentalParam1, 1);
            ZSTD_decompressBegin(dctx);
            nuint size = 1;
            nuint remaining = srcSize - 0xc;
            nuint remainingOutput = decompressedSize;
            ptr = src + 0xc;
            byte* output = dst;
            do
            {
                // zstd asks for a fixed number of bytes at a time; on a truncated file that would
                // walk off the end of src.
                if (size > remaining)
                    return false;

                nuint result = ZSTD_decompressContinue(dctx, output, remainingOutput, ptr, size);
                if (ZSTD_isError(result))
                    return false;
                ptr += size;
                remaining -= size;
                size = ZSTD_nextSrcSizeToDecompress(dctx);
                output += result;
                remainingOutput -= result;
            } while (size != 0);
        }
        finally
        {
            ZSTD_freeDCtx(dctx);
        }

        // Everything below is driven by fields inside the payload we just decompressed and by the
        // trailing FMSH header. None of it is trusted: every offset is checked against dst and src.
        if (decompressedSize <= 0xee || ((dst[0xee] >> 3) & 1) == 0)
            return true;

        uint fileSize = *(uint*)(dst + 0x1c);

        if (fileSize > decompressedSize)
            return false;

        byte* fmshStart = Align(ptr, 4);
        nuint fmshOffset = (nuint)(fmshStart - src);

        if (fmshOffset > srcSize || (nuint)FmshHeaderSize > srcSize - fmshOffset)
            return false;

        ResMeshCodecHeader* fmshHeader = (ResMeshCodecHeader*)fmshStart;

        if (fmshHeader->MagicValue != ResMeshCodecHeader.Magic)
            return false;

        uint align = Math.Max(fmshHeader->VertexAlign, fmshHeader->IndexAlign);

        if (!IsUsableAlignment(align))
            return false;

        ulong outputOffset = AlignUp(AlignUp(fileSize, 8) + 0x120, align);

        if (outputOffset > decompressedSize)
            return false;

        if (workBufferSize < fmshHeader->WorkMemSize)
            return false;

        NativeMemory.Clear(dst + fileSize, dstSize - fileSize);

        uint* sizeHeader = (uint*)(dst + AlignUp(fileSize, 8));
        sizeHeader[0] = (uint)outputOffset;
        sizeHeader[1] = (uint)decompressedSize;

        return DecompressFmshCore(dst + outputOffset, (nuint)(decompressedSize - outputOffset), fmshStart,
                                  srcSize - fmshOffset, workBuffer, workBufferSize) == 0;
    }

    /// <summary>
    /// Decodes a bare FMSH mesh section into <paramref name="dst"/>. Returns 0 on success and a
    /// non-zero status for malformed input; it does not throw.
    /// </summary>
    public static uint DecompressFmsh(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                return DecompressFmshCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
        }
        catch (MeshCodecException)
        {
            return 0x1c;
        }
    }

    private static uint DecompressFmshCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        if (srcSize < (nuint)FmshHeaderSize)
            return 0x1c;

        ResMeshCodecHeader* header = (ResMeshCodecHeader*)src;

        if (header->MagicValue != ResMeshCodecHeader.Magic)
            return 0x1c;

        if (!IsUsableAlignment(header->IndexAlign) || !IsUsableAlignment(header->VertexAlign))
            return 0x1c;

        StreamContext indexContext = new StreamContext
        {
            Stream = dst,
            Size = header->IndexOutputSize,
            Alignment = header->IndexAlign,
        };
        StreamContext vertexContext = new StreamContext
        {
            Stream = (byte*)(((nuint)dst + header->VertexAlign + indexContext.Size - 1) & (nuint)(-(long)header->VertexAlign)),
            Size = header->VertexOutputSize,
            Alignment = header->VertexAlign,
        };

        // The stream placement above comes straight from the file; both have to land inside dst.
        if (!Fits(dst, dstSize, indexContext.Stream, indexContext.Size) ||
            !Fits(dst, dstSize, vertexContext.Stream, vertexContext.Size))
            return 0x1c;

        if (workBufferSize < header->WorkMemSize)
            return 0x1c;

        int result = StackAllocator.Create(out StackAllocator? allocator, indexContext, vertexContext,
                                           workBuffer, header->WorkMemSize, header->CompHeader, 8);
        int blockSize = result;

        MeshCodecCodec? disposable = allocator?.Codec as MeshCodecCodec;
        try
        {
            if (result > -1)
            {
                uint offset = 0x22;
                byte* pos = src + 0x22;

                while (result > -1)
                {
                    if (blockSize == 0)
                        return offset != srcSize ? 0x1cu : 0u;

                    // Each frame declares its own length, which has to stay inside src.
                    if ((nuint)blockSize > srcSize - offset)
                        return 0x1c;

                    offset += (uint)blockSize;
                    result = allocator!.DecompressFrame(pos, (nuint)blockSize);
                    pos += blockSize;
                    blockSize = result;
                }
            }

            return StackAllocator.ConvertResult((ulong)(long)result);
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    /// <summary>
    /// Decodes a terrain chunk, allocating the output and scratch buffers from sizes declared in the
    /// file. Returns <see langword="null"/> for a header whose sizes are inconsistent or exceed
    /// <see cref="MaxDecompressedSize"/>.
    /// </summary>
    public static byte[]? DecompressChunk(ReadOnlySpan<byte> src)
    {
        if (!TryReadChunkHeader(src, out ResChunkHeader header))
            return null;

        if (header.DecompressedSize > MaxDecompressedSize || header.WorkMemSize > DefaultWorkBufferSize)
            return null;

        byte[] dst = new byte[header.DecompressedSize];
        byte[] work = new byte[header.WorkMemSize];
        return DecompressChunk(dst, src, work) ? dst : null;
    }

    /// <summary>
    /// Decodes a terrain chunk into <paramref name="dst"/>. Returns <see langword="false"/> rather
    /// than throwing for malformed or truncated input.
    /// </summary>
    public static bool DecompressChunk(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        if (src.Length < 0x1c)
            return false;

        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                return DecompressChunkCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
        }
        catch (MeshCodecException)
        {
            return false;
        }
    }

    private static bool DecompressChunkCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        if (srcSize < 0x1c)
            return false;

        ResChunkHeader* header = (ResChunkHeader*)src;

        if (dstSize < header->DecompressedSize)
            return false;

        if (workBufferSize < header->WorkMemSize)
            return false;

        if (!IsConsistentChunkHeader(*header))
            return false;

        StreamContext indexContext = new StreamContext
        {
            Stream = dst + header->VertexOutputSize,
            Size = header->IndexOutputSize,
            Alignment = 2,
        };
        StreamContext vertexContext = new StreamContext
        {
            Stream = dst,
            Size = header->VertexOutputSize,
            Alignment = 4,
        };

        int result = StackAllocator.Create(out StackAllocator? allocator, indexContext, vertexContext,
                                           workBuffer, header->WorkMemSize, header->CompHeader, 8);
        int blockSize = result;

        MeshCodecCodec? disposable = allocator?.Codec as MeshCodecCodec;
        try
        {
            if (result > -1)
            {
                uint offset = 0x1c;
                byte* pos = src + 0x1c;

                while (result > -1)
                {
                    if (blockSize == 0)
                        return offset == srcSize;

                    if ((nuint)blockSize > srcSize - offset)
                        return false;

                    offset += (uint)blockSize;
                    result = allocator!.DecompressFrame(pos, (nuint)blockSize);
                    pos += blockSize;
                    blockSize = result;
                }
            }

            return StackAllocator.ConvertResult((ulong)(long)result) == 0;
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    public static bool DecompressQuad(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        if (src.Length < 4)
            return false;

        fixed (byte* pDst = dst)
        fixed (byte* pSrc = src)
        fixed (byte* pWork = workBuffer)
        {
            byte* frameHeader = pSrc + 4;
            nuint frameSize = (nuint)src.Length - 4;

            if ((nuint)workBuffer.Length < ZSTD_estimateDCtxSize())
                return false;

            ulong decompressedSize = ZSTD_getFrameContentSize(frameHeader, frameSize);
            if ((ulong)dst.Length < decompressedSize)
                return false;

            ZSTD_DCtx_s* dctx = ZSTD_initStaticDCtx(pWork, (nuint)workBuffer.Length);
            if (dctx == null)
                return false;

            nuint result = ZSTD_decompressDCtx(dctx, pDst, (nuint)dst.Length, frameHeader, frameSize);

            return !ZSTD_isError(result);
        }
    }

    public static byte[]? DecompressQuad(ReadOnlySpan<byte> src)
    {
        if (src.Length < 4)
            return null;

        fixed (byte* pSrc = src)
        {
            ulong size = ZSTD_getFrameContentSize(pSrc + 4, (nuint)src.Length - 4);
            if (size == ulong.MaxValue || size == ulong.MaxValue - 1 || size > MaxDecompressedSize)
                return null;

            byte[] dst = new byte[size];
            byte[] work = new byte[ZSTD_estimateDCtxSize() + 0x1000];
            return DecompressQuad(dst, src, work) ? dst : null;
        }
    }

    /// <summary>
    /// A stream alignment read out of a file header is a single byte and is used as a mask: zero
    /// would produce a null base pointer and a non-power-of-two a bogus one.
    /// </summary>
    internal static bool IsUsableAlignment(uint align)
        => align != 0 && align <= 0x1000 && (align & (align - 1)) == 0;

    private static bool Fits(byte* bufferStart, nuint bufferSize, byte* start, nuint size)
    {
        if (start < bufferStart)
            return false;

        nuint offset = (nuint)(start - bufferStart);
        return offset <= bufferSize && size <= bufferSize - offset;
    }

    private static ulong AlignUp(ulong value, ulong align) => (value + align - 1) & ~(align - 1);

    private static byte* Align(byte* ptr, uint align) => (byte*)(((nuint)ptr + align - 1) & (nuint)(-(long)align));
}
