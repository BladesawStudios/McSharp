using System;
using System.Runtime.InteropServices;
using McSharp.Internal;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp;

public static unsafe class MeshCodec
{
    public const int DefaultWorkBufferSize = 0x10000000;

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
        return true;
    }

    public static uint GetRequiredWorkBufferSize(ReadOnlySpan<byte> package)
    {
        int size = Marshal.SizeOf<ResMeshCodecHeader>();
        int limit = package.Length - size;

        for (int i = 0xc; i <= limit; ++i)
        {
            if (MemoryMarshal.Read<uint>(package.Slice(i, 4)) == ResMeshCodecHeader.Magic)
                return MemoryMarshal.Read<ResMeshCodecHeader>(package.Slice(i, size)).WorkMemSize;
        }

        return 0;
    }

    public static byte[]? DecompressMc(ReadOnlySpan<byte> src)
    {
        if (!TryReadPackageHeader(src, out ResMeshCodecPackageHeader header))
            return null;

        byte[] dst = new byte[header.GetDecompressedSize()];
        byte[] work = new byte[GetRequiredWorkBufferSize(src)];
        return DecompressMc(dst, src, work) ? dst : null;
    }

    public static bool DecompressMc(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        if (src.Length < 0xc)
            return false;

        fixed (byte* pDst = dst)
        fixed (byte* pSrc = src)
        fixed (byte* pWork = workBuffer)
            return DecompressMcCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
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
        nuint remaining;
        byte* ptr;
        try
        {
            ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_experimentalParam1, 1);
            ZSTD_decompressBegin(dctx);
            nuint size = 1;
            remaining = srcSize - 0xc;
            nuint remainingOutput = decompressedSize;
            ptr = src + 0xc;
            byte* output = dst;
            do
            {
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

        if (((dst[0xee] >> 3) & 1) == 0)
            return true;

        uint fileSize = *(uint*)(dst + 0x1c);

        ResMeshCodecHeader* fmshHeader = (ResMeshCodecHeader*)Align(ptr, 4);

        if (fmshHeader->MagicValue != ResMeshCodecHeader.Magic)
            return false;

        NativeMemory.Clear(dst + fileSize, dstSize - fileSize);

        uint align = Math.Max(fmshHeader->VertexAlign, fmshHeader->IndexAlign);
        byte* outputBuf = Align(Align(dst + fileSize, 8) + 0x120, align);
        uint* sizeHeader = (uint*)Align(dst + fileSize, 8);
        sizeHeader[0] = (uint)(outputBuf - dst);
        sizeHeader[1] = (uint)decompressedSize;

        if (workBufferSize < fmshHeader->WorkMemSize)
            return false;

        nuint compressedSize = remaining - (nuint)((byte*)fmshHeader - ptr);

        return DecompressFmshCore(outputBuf, fmshHeader->VertexOutputSize + fmshHeader->IndexOutputSize,
                                  (byte*)fmshHeader, compressedSize, workBuffer, workBufferSize) == 0;
    }

    public static uint DecompressFmsh(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        fixed (byte* pDst = dst)
        fixed (byte* pSrc = src)
        fixed (byte* pWork = workBuffer)
            return DecompressFmshCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
    }

    private static uint DecompressFmshCore(byte* dst, nuint dstSize, byte* src, nuint srcSize, byte* workBuffer, nuint workBufferSize)
    {
        ResMeshCodecHeader* header = (ResMeshCodecHeader*)src;

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

    public static byte[]? DecompressChunk(ReadOnlySpan<byte> src)
    {
        if (!TryReadChunkHeader(src, out ResChunkHeader header))
            return null;

        byte[] dst = new byte[header.DecompressedSize];
        byte[] work = new byte[header.WorkMemSize];
        return DecompressChunk(dst, src, work) ? dst : null;
    }

    public static bool DecompressChunk(Span<byte> dst, ReadOnlySpan<byte> src, Span<byte> workBuffer)
    {
        if (src.Length < 0x1c)
            return false;

        fixed (byte* pDst = dst)
        fixed (byte* pSrc = src)
        fixed (byte* pWork = workBuffer)
            return DecompressChunkCore(pDst, (nuint)dst.Length, pSrc, (nuint)src.Length, pWork, (nuint)workBuffer.Length);
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
            if (size == ulong.MaxValue || size == ulong.MaxValue - 1 || size > int.MaxValue)
                return null;

            byte[] dst = new byte[size];
            byte[] work = new byte[ZSTD_estimateDCtxSize() + 0x1000];
            return DecompressQuad(dst, src, work) ? dst : null;
        }
    }

    private static byte* Align(byte* ptr, uint align) => (byte*)(((nuint)ptr + align - 1) & (nuint)(-(long)align));
}
