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
    /// Returns <see langword="null"/> if the input is not a package McSharp can decode; use the
    /// <see cref="McStatus"/> overload to find out why.
    /// </summary>
    public static byte[]? DecompressMc(ReadOnlySpan<byte> src) => DecompressMc(src, out _);

    /// <inheritdoc cref="DecompressMc(ReadOnlySpan{byte})"/>
    public static byte[]? DecompressMc(ReadOnlySpan<byte> src, out McStatus status)
    {
        if (!TryReadPackageHeader(src, out ResMeshCodecPackageHeader header))
        {
            status = McStatus.NotAPackage;
            return null;
        }

        uint decompressedSize = header.GetDecompressedSize();
        uint workSize = GetRequiredWorkBufferSize(src);

        if (decompressedSize > MaxDecompressedSize || workSize > DefaultWorkBufferSize)
        {
            status = McStatus.SizeLimitExceeded;
            return null;
        }

        byte[] dst = new byte[decompressedSize];
        byte[] work = new byte[workSize];
        return DecompressMc(dst, src, work, out status) ? dst : null;
    }

    /// <summary>
    /// Decodes a package into <paramref name="dst"/>. Returns <see langword="false"/> rather than
    /// throwing for malformed or truncated input.
    /// </summary>
    public static bool DecompressMc(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
        => DecompressMc(dst, src, workBuffer, out _);

    /// <inheritdoc cref="DecompressMc(Span{byte}, ReadOnlySpan{byte}, Span{byte})"/>
    public static bool DecompressMc(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer, out McStatus status)
    {
        if (src.Length < 0xc)
        {
            status = McStatus.NotAPackage;
            return false;
        }

        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                status = DecompressMcCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
        }
        catch (MeshCodecException)
        {
            status = McStatus.WorkBufferTooSmall;
        }

        return status == McStatus.Ok;
    }

    private static McStatus DecompressMcCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        if (srcSize < 0xc || src == null)
            return McStatus.NotAPackage;

        ResMeshCodecPackageHeader* header = (ResMeshCodecPackageHeader*)src;

        if (header->MagicValue != ResMeshCodecPackageHeader.Magic)
            return McStatus.NotAPackage;

        if (header->VersionMajor != 0 || header->VersionMinor > 1)
            return McStatus.UnsupportedVersion;

        nuint decompressedSize = header->GetDecompressedSize();

        if (dstSize < decompressedSize)
            return McStatus.DestinationTooSmall;

        ZSTD_DCtx_s* dctx = ZSTD_createDCtx();

        if (dctx == null)
            return McStatus.CorruptStream;

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
                    return McStatus.TruncatedStream;

                nuint result = ZSTD_decompressContinue(dctx, output, remainingOutput, ptr, size);
                if (ZSTD_isError(result))
                    return McStatus.CorruptStream;
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
            return McStatus.Ok;

        uint fileSize = *(uint*)(dst + 0x1c);

        if (fileSize > decompressedSize)
            return McStatus.InvalidMeshSection;

        byte* fmshStart = Align(ptr, 4);
        nuint fmshOffset = (nuint)(fmshStart - src);

        if (fmshOffset > srcSize || (nuint)FmshHeaderSize > srcSize - fmshOffset)
            return McStatus.TruncatedStream;

        ResMeshCodecHeader* fmshHeader = (ResMeshCodecHeader*)fmshStart;

        if (fmshHeader->MagicValue != ResMeshCodecHeader.Magic)
            return McStatus.InvalidMeshSection;

        uint align = Math.Max(fmshHeader->VertexAlign, fmshHeader->IndexAlign);

        if (!IsUsableAlignment(align))
            return McStatus.InvalidMeshSection;

        ulong outputOffset = AlignUp(AlignUp(fileSize, 8) + 0x120, align);

        if (outputOffset > decompressedSize)
            return McStatus.InvalidMeshSection;

        if (workBufferSize < fmshHeader->WorkMemSize)
            return McStatus.WorkBufferTooSmall;

        NativeMemory.Clear(dst + fileSize, dstSize - fileSize);

        uint* sizeHeader = (uint*)(dst + AlignUp(fileSize, 8));
        sizeHeader[0] = (uint)outputOffset;
        sizeHeader[1] = (uint)decompressedSize;

        DecompressFmshCore(dst + outputOffset, (nuint)(decompressedSize - outputOffset), fmshStart,
                           srcSize - fmshOffset, workBuffer, workBufferSize, out McStatus status);

        return status;
    }

    /// <summary>
    /// Decodes a bare FMSH mesh section into <paramref name="dst"/>. Returns 0 on success and a
    /// non-zero status for malformed input; it does not throw.
    /// </summary>
    public static uint DecompressFmsh(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
        => DecompressFmsh(dst, src, workBuffer, out _);

    /// <inheritdoc cref="DecompressFmsh(Span{byte}, ReadOnlySpan{byte}, Span{byte})"/>
    public static uint DecompressFmsh(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer, out McStatus status)
    {
        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                return DecompressFmshCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork,
                                          (nuint)workBuffer.Length, out status);
        }
        catch (MeshCodecException)
        {
            status = McStatus.WorkBufferTooSmall;
            return 0x1c;
        }
    }

    private static uint DecompressFmshCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer,
                                           nuint workBufferSize, out McStatus status)
    {
        if (srcSize < (nuint)FmshHeaderSize)
        {
            status = McStatus.TruncatedStream;
            return 0x1c;
        }

        ResMeshCodecHeader* header = (ResMeshCodecHeader*)src;

        if (header->MagicValue != ResMeshCodecHeader.Magic)
        {
            status = McStatus.InvalidMeshSection;
            return 0x1c;
        }

        if (!IsUsableAlignment(header->IndexAlign) || !IsUsableAlignment(header->VertexAlign))
        {
            status = McStatus.InvalidMeshSection;
            return 0x1c;
        }

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
        {
            status = McStatus.InvalidMeshSection;
            return 0x1c;
        }

        if (workBufferSize < header->WorkMemSize)
        {
            status = McStatus.WorkBufferTooSmall;
            return 0x1c;
        }

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
                    {
                        if (offset != srcSize)
                        {
                            status = McStatus.CorruptStream;
                            return 0x1c;
                        }

                        status = McStatus.Ok;
                        return 0;
                    }

                    // Each frame declares its own length, which has to stay inside src.
                    if ((nuint)blockSize > srcSize - offset)
                    {
                        status = McStatus.TruncatedStream;
                        return 0x1c;
                    }

                    offset += (uint)blockSize;
                    result = allocator!.DecompressFrame(pos, (nuint)blockSize);
                    pos += blockSize;
                    blockSize = result;
                }
            }

            uint converted = StackAllocator.ConvertResult((ulong)(long)result);
            status = converted == 0 ? McStatus.Ok : McStatus.CorruptStream;
            return converted;
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    /// <summary>
    /// Decodes a terrain chunk, allocating the output and scratch buffers from sizes declared in the
    /// file. Returns <see langword="null"/> on failure; use the <see cref="McStatus"/> overload to
    /// find out why.
    /// </summary>
    public static byte[]? DecompressChunk(ReadOnlySpan<byte> src) => DecompressChunk(src, out _);

    /// <inheritdoc cref="DecompressChunk(ReadOnlySpan{byte})"/>
    public static byte[]? DecompressChunk(ReadOnlySpan<byte> src, out McStatus status)
    {
        if (!TryReadChunkHeader(src, out ResChunkHeader header))
        {
            status = McStatus.NotAPackage;
            return null;
        }

        if (header.DecompressedSize > MaxDecompressedSize || header.WorkMemSize > DefaultWorkBufferSize)
        {
            status = McStatus.SizeLimitExceeded;
            return null;
        }

        byte[] dst = new byte[header.DecompressedSize];
        byte[] work = new byte[header.WorkMemSize];
        return DecompressChunk(dst, src, work, out status) ? dst : null;
    }

    /// <summary>
    /// Decodes a terrain chunk into <paramref name="dst"/>. Returns <see langword="false"/> rather
    /// than throwing for malformed or truncated input.
    /// </summary>
    public static bool DecompressChunk(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
        => DecompressChunk(dst, src, workBuffer, out _);

    /// <inheritdoc cref="DecompressChunk(Span{byte}, ReadOnlySpan{byte}, Span{byte})"/>
    public static bool DecompressChunk(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer, out McStatus status)
    {
        if (src.Length < 0x1c)
        {
            status = McStatus.NotAPackage;
            return false;
        }

        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            fixed (byte* pWork = workBuffer)
                status = DecompressChunkCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
        }
        catch (MeshCodecException)
        {
            status = McStatus.WorkBufferTooSmall;
        }

        return status == McStatus.Ok;
    }

    private static McStatus DecompressChunkCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        if (srcSize < 0x1c)
            return McStatus.NotAPackage;

        ResChunkHeader* header = (ResChunkHeader*)src;

        if (dstSize < header->DecompressedSize)
            return McStatus.DestinationTooSmall;

        if (workBufferSize < header->WorkMemSize)
            return McStatus.WorkBufferTooSmall;

        if (!IsConsistentChunkHeader(*header))
            return McStatus.NotAPackage;

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
                        return offset == srcSize ? McStatus.Ok : McStatus.CorruptStream;

                    if ((nuint)blockSize > srcSize - offset)
                        return McStatus.TruncatedStream;

                    offset += (uint)blockSize;
                    result = allocator!.DecompressFrame(pos, (nuint)blockSize);
                    pos += blockSize;
                    blockSize = result;
                }
            }

            return StackAllocator.ConvertResult((ulong)(long)result) == 0 ? McStatus.Ok : McStatus.CorruptStream;
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    public static bool DecompressQuad(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
        => DecompressQuad(dst, src, workBuffer, out _);

    /// <inheritdoc cref="DecompressQuad(Span{byte}, ReadOnlySpan{byte}, Span{byte})"/>
    public static bool DecompressQuad(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer, out McStatus status)
    {
        if (src.Length < 4)
        {
            status = McStatus.NotAPackage;
            return false;
        }

        fixed (byte* pDst = dst)
        fixed (byte* pSrc = src)
        fixed (byte* pWork = workBuffer)
        {
            byte* frameHeader = pSrc + 4;
            nuint frameSize = (nuint)src.Length - 4;

            if ((nuint)workBuffer.Length < ZSTD_estimateDCtxSize())
            {
                status = McStatus.WorkBufferTooSmall;
                return false;
            }

            ulong decompressedSize = ZSTD_getFrameContentSize(frameHeader, frameSize);
            if ((ulong)dst.Length < decompressedSize)
            {
                status = McStatus.DestinationTooSmall;
                return false;
            }

            ZSTD_DCtx_s* dctx = ZSTD_initStaticDCtx(pWork, (nuint)workBuffer.Length);
            if (dctx == null)
            {
                status = McStatus.WorkBufferTooSmall;
                return false;
            }

            nuint result = ZSTD_decompressDCtx(dctx, pDst, (nuint)dst.Length, frameHeader, frameSize);

            status = ZSTD_isError(result) ? McStatus.CorruptStream : McStatus.Ok;
            return status == McStatus.Ok;
        }
    }

    public static byte[]? DecompressQuad(ReadOnlySpan<byte> src) => DecompressQuad(src, out _);

    /// <inheritdoc cref="DecompressQuad(ReadOnlySpan{byte})"/>
    public static byte[]? DecompressQuad(ReadOnlySpan<byte> src, out McStatus status)
    {
        if (src.Length < 4)
        {
            status = McStatus.NotAPackage;
            return null;
        }

        fixed (byte* pSrc = src)
        {
            ulong size = ZSTD_getFrameContentSize(pSrc + 4, (nuint)src.Length - 4);

            if (size == ulong.MaxValue || size == ulong.MaxValue - 1)
            {
                status = McStatus.NotAPackage;
                return null;
            }

            if (size > MaxDecompressedSize)
            {
                status = McStatus.SizeLimitExceeded;
                return null;
            }

            byte[] dst = new byte[size];
            byte[] work = new byte[ZSTD_estimateDCtxSize() + 0x1000];
            return DecompressQuad(dst, src, work, out status) ? dst : null;
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
