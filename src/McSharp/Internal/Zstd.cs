using System;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp.Internal;

internal static unsafe class Zstd
{
    private static ReadOnlySpan<uint> RepStartValue => new uint[] { 1, 4, 8 };

    public static nuint DCtxWorkspaceSize => ZSTD_estimateDCtxSize() + 0x4d8;

    public static ZSTD_DCtx_s* SetupDCtx(void* wksp, nuint wkspSize)
    {
        ZSTD_DCtx_s* dctx = ZSTD_initStaticDCtx(wksp, wkspSize);
        if (dctx == null)
            throw new MeshCodecException("Failed to initialise a static zstd decompression context.");
        ZSTD_decompressBegin(dctx);
        return dctx;
    }

    private static void ResetRep(ZSTD_DCtx_s* dctx)
    {
        uint* rep = dctx->entropy.rep;
        ReadOnlySpan<uint> start = RepStartValue;
        rep[0] = start[0];
        rep[1] = start[1];
        rep[2] = start[2];
    }

    public static void DecompressBlock(ZSTD_DCtx_s* dctx, void* dst, nuint dstSize, void* src, nuint srcSize, uint notCompressed)
    {
        if (notCompressed != 0)
        {
            Buffer.MemoryCopy(src, dst, srcSize & 0xffffffff, srcSize & 0xffffffff);
            ZSTD_insertBlock(dctx, dst, srcSize & 0xffffffff);
        }
        else
        {
            ZSTD_decompressBlock(dctx, dst, dstSize & 0xffffffff, src, srcSize & 0xffffffff);
        }
    }

    public static void InsertUncompressedBlock(ZSTD_DCtx_s* dctx, void* dst, int size)
    {
        if (size != 0)
            ZSTD_insertBlock(dctx, dst, (nuint)size);

        ResetRep(dctx);
    }

    public static void InsertBlocks(ZSTD_DCtx_s* dctx, void* buf1, int bufSize1, void* buf2, int bufSize2)
    {
        if (bufSize1 != 0)
            ZSTD_insertBlock(dctx, buf1, (nuint)bufSize1);

        if (bufSize2 != 0)
            ZSTD_insertBlock(dctx, buf2, (nuint)bufSize2);

        ResetRep(dctx);
    }

    public static uint ProcessBlock(ZSTD_DCtx_s* dctx, void* dst, nuint dstSize, void* src, nuint srcSize, uint notCompressed)
    {
        if (notCompressed != 0)
        {
            Buffer.MemoryCopy(src, dst, srcSize & 0xffffffff, srcSize & 0xffffffff);
            return (uint)ZSTD_insertBlock(dctx, dst, srcSize & 0xffffffff);
        }

        return (uint)ZSTD_decompressBlock(dctx, dst, dstSize & 0xffffffff, src, srcSize & 0xffffffff);
    }

    public static uint DecompressIndexStream(ZSTD_DCtx_s* dctx, DecompContext ctx, ref byte* outBuf, ref WorkBuffer buffer, int numBlocks)
    {
        uint offset;
        uint size;
        byte* baseAddr;
        if (buffer.Offset + numBlocks * 0x20000 > buffer.Capacity)
        {
            buffer.Size = buffer.Offset;
            offset = 0;
            size = buffer.Offset;
            baseAddr = buffer.Addr;
        }
        else
        {
            size = buffer.Size;
            offset = buffer.Offset;
            baseAddr = outBuf;
        }

        InsertBlocks(dctx, buffer.Addr + offset, (int)(size - offset), buffer.Addr, (int)offset);

        for (int i = 0; i < numBlocks; ++i)
        {
            uint inSize = VByte.Decode(ref ctx.CurrentPos);
            offset += ProcessBlock(dctx, buffer.Addr + offset, 0x20000, ctx.CurrentPos, inSize, (uint)ctx.BitStream0.Read(1));
            ctx.CurrentPos += inSize;
        }

        outBuf = baseAddr;
        buffer.Offset = offset;
        return (uint)(offset + (nuint)(buffer.Addr - baseAddr));
    }

    public static void DecompressVertexStream(ZSTD_DCtx_s* dctx, void* dst, int dstSize, DecompContext ctx, int maxBlockSize)
    {
        uint isNotCompressed = (uint)ctx.BitStream0.Read(1);
        int dstCapacity = dstSize;

        if (dstSize > maxBlockSize)
        {
            while (dstCapacity > maxBlockSize)
            {
                if (isNotCompressed != 0)
                {
                    Buffer.MemoryCopy(ctx.CurrentPos, dst, maxBlockSize, maxBlockSize);
                    ZSTD_insertBlock(dctx, dst, (nuint)maxBlockSize);
                    ctx.CurrentPos += maxBlockSize;
                }
                else
                {
                    uint blockSize = VByte.Decode(ref ctx.CurrentPos);
                    ZSTD_decompressBlock(dctx, dst, (nuint)maxBlockSize, ctx.CurrentPos, blockSize);
                    ctx.CurrentPos += blockSize;
                }
                dstCapacity -= maxBlockSize;
                dst = (byte*)dst + maxBlockSize;
                isNotCompressed = (uint)ctx.BitStream0.Read(1);
            }
        }

        if (isNotCompressed != 0)
        {
            Buffer.MemoryCopy(ctx.CurrentPos, dst, dstCapacity, dstCapacity);
            ZSTD_insertBlock(dctx, dst, (nuint)dstCapacity);
            ctx.CurrentPos += dstCapacity;
        }
        else
        {
            uint blockSize = VByte.Decode(ref ctx.CurrentPos);
            ZSTD_decompressBlock(dctx, dst, (nuint)dstCapacity, ctx.CurrentPos, blockSize);
            ctx.CurrentPos += blockSize;
        }
    }
}
