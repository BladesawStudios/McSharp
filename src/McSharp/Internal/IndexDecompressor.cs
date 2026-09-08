using System;
using ZstdSharp.Unsafe;

namespace McSharp.Internal;

internal sealed unsafe class IndexDecompressor
{
    private ZSTD_DCtx_s* _dctx;
    private DecompContext? _decompContext;
    private WorkBuffer _workBuffer0;
    private WorkBuffer _workBuffer1;
    private byte* _inputStream0;
    private byte* _inputStream1;
    private int _trianglesRemaining;
    private uint _numVertices;
    private uint _baseIndex;
    private StackAllocator? _allocator;

    public void Initialize(uint _, ZSTD_DCtx_s* dctx, StackAllocator allocator)
    {
        _allocator = allocator;
        _dctx = dctx;
        _decompContext = null;
        _workBuffer0.Addr = (byte*)allocator.Alloc(0x60010, 8);
        _workBuffer0.Offset = 0;
        _workBuffer0.Capacity = 0x60000;
        _workBuffer0.Size = 0x60000;
        _trianglesRemaining = 0;
        _workBuffer1.Addr = (byte*)allocator.Alloc(0x20010, 8);
        _workBuffer1.Offset = 0;
        _workBuffer1.Capacity = 0x20000;
        _workBuffer1.Size = 0x20000;
        _numVertices = 0;
        _baseIndex = 0;
        _inputStream0 = _workBuffer0.Addr;
        _inputStream1 = _workBuffer1.Addr;
    }

    public void Close()
    {
        _dctx = null;
        _allocator!.Free(_workBuffer1.Addr);
        _allocator!.Free(_workBuffer0.Addr);
    }

    public void SetContext(DecompContext ctx) => _decompContext = ctx;

    public void SetVertexCount(uint count) => _numVertices = count;

    public void SetBaseIndex(uint idx) => _baseIndex = idx;

    public uint Decompress1(void* dst, IndexFormat indexFormat, uint count, uint baseIndex, ulong* tblBuf, uint numCopied, uint remaining)
    {
        DecompContext ctx = _decompContext!;

        uint indexCount = (_numVertices > numCopied + remaining) ? VByte.Decode(ref ctx.CurrentPos) * 3 : count;
        uint baseValue = Math.Max(baseIndex, _baseIndex);
        if (indexCount == 0)
        {
            _baseIndex = baseValue;
            return count - indexCount;
        }

        int totalTrigs = (int)(indexCount / 3);
        int trigs = totalTrigs - _trianglesRemaining;
        if (trigs != 0 && totalTrigs >= _trianglesRemaining)
        {
            int blockCount = ((trigs + 0x1ffff > -1) ? trigs + 0x1ffff : trigs + 0x3fffe) >> 0x11;
            _trianglesRemaining = (int)Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream0, ref _workBuffer0, blockCount) - totalTrigs;
        }
        else
        {
            _trianglesRemaining -= totalTrigs;
        }

        uint compressedBlocks = (uint)ctx.BitStream0.ReadZeroes();
        if (compressedBlocks != 0)
            Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream1, ref _workBuffer1, (int)compressedBlocks);

        uint indicesProcessed;
        if (tblBuf != null)
        {
            uint idxCount = baseIndex >= numCopied ? baseIndex - numCopied : 0;
            uint copied = numCopied >= baseIndex ? numCopied - baseIndex : 0;

            indicesProcessed = IndexCodec.DecodeIndexBuffer0WithTable(
                dst, (int)indexCount, baseValue - baseIndex,
                tblBuf + ((long)baseIndex - (long)numCopied), copied, remaining - idxCount,
                ref _inputStream0, ref _inputStream1, indexFormat);
        }
        else
        {
            indicesProcessed = IndexCodec.DecodeIndexBuffer0WithoutTable(
                dst, (int)indexCount, baseValue - baseIndex, ref _inputStream0, ref _inputStream1, indexFormat);
        }

        _baseIndex = baseIndex + indicesProcessed;
        return count - indexCount;
    }

    public uint Decompress2(void* dst, IndexFormat indexFormat, uint count, uint baseIndex, ulong* decodeBuf, uint numCopied, uint remaining)
    {
        DecompContext ctx = _decompContext!;

        int indexCount = (int)((_numVertices > numCopied + remaining) ? VByte.Decode(ref ctx.CurrentPos) : count);
        uint indicesProcessed = Math.Max(baseIndex, _baseIndex);

        if (indexCount != 0)
        {
            int triangles = _trianglesRemaining;
            if (indexCount > triangles)
            {
                int size = indexCount - triangles + 0x1ffff;
                size = ((size > -1) ? size : indexCount - triangles + 0x3fffe) >> 0x11;

                triangles = (int)Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream0, ref _workBuffer0, size);
            }

            _trianglesRemaining = triangles - indexCount;

            uint compressedBlocks = (uint)ctx.BitStream0.ReadZeroes();
            if (compressedBlocks != 0)
                Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream1, ref _workBuffer1, (int)compressedBlocks);

            indicesProcessed = IndexCodec.DecodeIndexBuffer2(dst, indexFormat, indexCount, baseIndex, decodeBuf,
                                                             numCopied, numCopied, indicesProcessed,
                                                             ref _inputStream0, ref _inputStream1);
        }

        _baseIndex = indicesProcessed;
        return count - (uint)indexCount;
    }

    public uint Decompress3(void* dst, IndexFormat indexFormat, uint count, uint baseIndex, ulong* decodeBuf, uint numCopied, uint remaining)
    {
        DecompContext ctx = _decompContext!;

        int indexCount = (int)((_numVertices > numCopied + remaining) ? VByte.Decode(ref ctx.CurrentPos) : count);
        uint indicesProcessed = Math.Max(baseIndex, _baseIndex);

        if (indexCount != 0)
        {
            int triangles = _trianglesRemaining;
            if (indexCount > triangles)
            {
                int size = indexCount - triangles + 0x1ffff;
                size = ((size > -1) ? size : indexCount - triangles + 0x3fffe) >> 0x11;

                triangles = (int)Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream0, ref _workBuffer0, size);
            }

            _trianglesRemaining = triangles - indexCount;

            uint compressedBlocks = (uint)ctx.BitStream0.ReadZeroes();
            if (compressedBlocks != 0)
                Zstd.DecompressIndexStream(_dctx, ctx, ref _inputStream1, ref _workBuffer1, (int)compressedBlocks);

            indicesProcessed = IndexCodec.DecodeIndexBuffer3(dst, indexFormat, indexCount, baseIndex, compressedBlocks,
                                                             decodeBuf, numCopied, indicesProcessed,
                                                             ref _inputStream0, ref _inputStream1);
        }

        _baseIndex = indicesProcessed;
        return count - (uint)indexCount;
    }
}
