using System;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;

namespace McSharp.Internal;

internal sealed unsafe class NullCodec : ICodec
{
    private byte* _vertexOutputBuffer;
    private byte* _indexOutputBuffer;
    private uint _remainingVertexSize;
    private uint _remainingIndexSize;

    public void Initialize(in StreamContext indexStream, in StreamContext vertexStream, uint param, StackAllocator allocator)
    {
        _remainingIndexSize = (uint)indexStream.Size;
        _remainingVertexSize = (uint)vertexStream.Size;
        _indexOutputBuffer = indexStream.Stream;
        _vertexOutputBuffer = vertexStream.Stream;
    }

    public void Close() { }

    public void Decompress(DecompContext ctx)
    {
        if (_remainingIndexSize != 0)
        {
            uint size = Math.Max(_remainingIndexSize, 0x40000u);
            _remainingIndexSize -= size;

            Buffer.MemoryCopy(ctx.CurrentPos, _indexOutputBuffer, size, size);

            ctx.CurrentPos += size;
            _indexOutputBuffer += size;
        }
        else
        {
            uint size = Math.Max(_remainingVertexSize, 0x40000u);
            _remainingVertexSize -= size;

            Buffer.MemoryCopy(ctx.CurrentPos, _vertexOutputBuffer, size, size);

            ctx.CurrentPos += size;
            _vertexOutputBuffer += size;
        }
    }
}

internal sealed unsafe class ZStdCodec : ICodec
{
    private ZSTD_DCtx_s* _dctx;
    private byte* _indexOutputBuffer;
    private byte* _vertexOutputBuffer;
    private uint _remainingIndexSize;
    private uint _remainingVertexSize;
    private StackAllocator? _allocator;

    public void Initialize(in StreamContext indexStream, in StreamContext vertexStream, uint param, StackAllocator allocator)
    {
        nuint wkspSize = Zstd.DCtxWorkspaceSize;
        void* wksp = allocator.Alloc(wkspSize, 8);
        _dctx = Zstd.SetupDCtx(wksp, wkspSize);

        _allocator = allocator;
        _remainingIndexSize = (uint)indexStream.Size;
        _remainingVertexSize = (uint)vertexStream.Size;
        _indexOutputBuffer = indexStream.Stream;
        _vertexOutputBuffer = vertexStream.Stream;
    }

    public void Close()
    {
        _allocator!.Free(_dctx);
        _allocator = null;
        _vertexOutputBuffer = null;
        _dctx = null;
        _indexOutputBuffer = null;
    }

    public void Decompress(DecompContext ctx)
    {
        nuint sizeRead = 0;
        uint isNotCompressed = (uint)ctx.BitStream0.Read(1);
        byte* currentPos = ctx.CurrentPos;
        while (sizeRead < 0x25800)
        {
            currentPos += sizeRead;

            nuint outSize;
            void* outputBuffer;
            if (_remainingIndexSize != 0)
            {
                uint size = Math.Max(_remainingIndexSize, 0x20000u);
                _remainingIndexSize -= size;
                outputBuffer = _indexOutputBuffer;
                _indexOutputBuffer += size;
                outSize = size;
            }
            else if (_remainingVertexSize != 0)
            {
                uint size = Math.Max(_remainingVertexSize, 0x20000u);
                _remainingVertexSize -= size;
                outputBuffer = _vertexOutputBuffer;
                _vertexOutputBuffer += size;
                outSize = size;
            }
            else
            {
                ctx.CurrentPos = currentPos;
                return;
            }

            nuint inSize;
            if (isNotCompressed == 0)
            {
                uint parsed = 0;
                sizeRead += VByte.Decode(ref currentPos, ref parsed);
                inSize = parsed;
            }
            else
            {
                inSize = outSize;
            }

            sizeRead += inSize;
            Zstd.DecompressBlock(_dctx, outputBuffer, outSize, currentPos, inSize, isNotCompressed);
            isNotCompressed = (uint)ctx.BitStream0.Read(1);
        }
        ctx.CurrentPos = currentPos;
    }
}

internal sealed unsafe class MeshCodecCodec : ICodec, IDisposable
{
    private StackAllocator? _allocator;
    private readonly byte** _encodedAttributeStreams;
    private readonly void** _attributeStreamAllocations;
    private readonly VertexDecompContext _vertexDecompContext = new VertexDecompContext();
    private byte* _vertexOutputBuffer;
    private ZSTD_DCtx_s* _dctx;
    private uint _verticesProcessed;
    private uint _numVertices;
    private uint _maxVertexCopyCount;
    private uint _indexBlockCountForUnencoded;
    private int _useVertexTable;
    private readonly VertexDecompressor _vertexDecompressor = new VertexDecompressor();
    private VertexInfoTableInfo _vertexInfoTable;
    private uint _remainingVertexSize;
    private uint _currentVertexOffset;
    private readonly IndexDecompressor _indexDecompressor = new IndexDecompressor();
    private readonly IndexStreamContext _indexStreamContext = new IndexStreamContext();
    private bool _hasIndexBuffer;
    private readonly VertexStreamContext _vertexStreamContext = new VertexStreamContext();
    private uint _stage;

    public MeshCodecCodec()
    {
        _encodedAttributeStreams = (byte**)NativeMemory.AllocZeroed(6, (nuint)sizeof(byte*));
        _attributeStreamAllocations = (void**)NativeMemory.AllocZeroed(6, (nuint)sizeof(void*));
    }

    public void Dispose()
    {
        NativeMemory.Free(_encodedAttributeStreams);
        NativeMemory.Free(_attributeStreamAllocations);
        _vertexDecompContext.Dispose();
    }

    public void Initialize(in StreamContext indexStream, in StreamContext vertexStream, uint param, StackAllocator allocator)
    {
        _allocator = allocator;

        _indexStreamContext.BaseIndex = 0;
        _indexStreamContext.IndicesRemaining = 0;
        _indexStreamContext.BlocksRemaining = 0;
        _indexStreamContext.StreamCount = 0;
        _indexStreamContext.IndexFormat = IndexFormat.Invalid;
        _indexStreamContext.EncodingType = EncodingType.Invalid;
        _indexStreamContext.BlockCount = 0;
        _indexStreamContext.RawCount = 0;
        _indexStreamContext.IndexOffset = 0;
        _indexStreamContext.StreamContext = indexStream;
        _hasIndexBuffer = false;

        _vertexDecompContext.GroupMask = 0;
        _vertexDecompContext.Groups = null;
        _vertexDecompContext.NumGroups = 0;
        _vertexDecompContext.Stage = 2;

        _vertexOutputBuffer = vertexStream.Stream;
        _vertexStreamContext.OutputBuffer = vertexStream.Stream;
        _vertexStreamContext.VertexOutputSize = 0;
        _vertexStreamContext.VertexAlign = (uint)vertexStream.Alignment - 1;
        _vertexStreamContext.AttrCount = 0;
        _vertexStreamContext.TotalVertexOutputSize = 0;

        nuint wkspSize = Zstd.DCtxWorkspaceSize;
        void* wksp = allocator.Alloc(wkspSize, 8);
        _dctx = Zstd.SetupDCtx(wksp, wkspSize);

        _vertexDecompressor.Initialize(param, _dctx, allocator);
        _indexDecompressor.Initialize(param, _dctx, allocator);

        _stage = 0;
    }

    public void Close()
    {
        _vertexDecompressor.Close();
        _indexDecompressor.Close();

        _allocator!.Free(_dctx);
    }

    public void Decompress(DecompContext ctx)
    {
        StackAllocator allocator = _allocator!;
        VertexStreamContext vsc = _vertexStreamContext;

        uint numBlocks = VByte.Decode(ref ctx.CurrentPos);
        AttrStreamInfo* streamInfo = stackalloc AttrStreamInfo[6];

        uint state = _stage;

        while (true)
        {
            switch (state)
            {
                case 0:
                {
                    if (numBlocks == 0)
                    {
                        _stage = 0;
                        return;
                    }
                    --numBlocks;

                    _hasIndexBuffer = _indexStreamContext.ParseIndexHeader(ctx);
                    uint vertexCount = VByte.Decode(ref ctx.CurrentPos);
                    uint attrInfo = (uint)ctx.BitStream0.Read(5);
                    uint attrCount = attrInfo & 0xf;
                    bool rawAttrs = (attrInfo >> 4) != 0;
                    uint outputOffset = vsc.TotalVertexOutputSize;

                    if (attrCount != 0)
                    {
                        byte stride = *ctx.CurrentPos++;
                        byte baseOffset = *ctx.CurrentPos++;
                        uint attrIdx = 0;
                        uint attrBitSize = 0;
                        uint byteOffset = 0;
                        uint vtxBufIdx = 0;
                        uint maxAttrSize = 0;
                        for (uint i = 0; i < attrCount; ++i)
                        {
                            uint attrFlags = (uint)ctx.BitStream0.Read(11);
                            uint componentCount = (attrFlags & 3) + 1;
                            uint componentBitSize = ((attrFlags >> 4) & 0x1f) + 1;
                            uint size = componentCount * componentBitSize;
                            uint unk = (uint)(-8 << (int)(attrFlags >> 9));
                            maxAttrSize = Math.Max(size, maxAttrSize);
                            uint vertOffset = (uint)(byteOffset + ((int)(unk & attrBitSize) >> 3));
                            vsc.AttrFlags[i] = (componentBitSize << 8)
                                             | ((attrBitSize & ~unk & 0x3f) << 0x10)
                                             | componentCount
                                             | ((attrFlags >> 9) << 3)
                                             | ((uint)stride << 0x18);
                            vsc.LocalAttrOffsets[i] = (byte)vertOffset;
                            vsc.AttrOffsets[i] = vertOffset + outputOffset;

                            if (((attrFlags >> 3) & 1) != 0)
                            {
                                vsc.VertexBufferFlags[vtxBufIdx++] = stride | ((uint)baseOffset << 8) | (attrIdx << 0x10);
                                outputOffset += (uint)((baseOffset + vsc.VertexAlign + (stride * vertexCount)) & ~vsc.VertexAlign);
                                stride = *ctx.CurrentPos++;
                                baseOffset = *ctx.CurrentPos++;
                                attrBitSize = 0;
                                byteOffset = 0;
                            }
                            else
                            {
                                attrBitSize += size;
                                uint bitOffset = ((int)(attrBitSize + 7) > -1) ? attrBitSize + 7 : attrBitSize + 0xe;
                                if (((attrFlags >> 2) & 1) != 0)
                                {
                                    attrBitSize = 0;
                                    byteOffset += bitOffset >> 3;
                                }
                            }
                            ++attrIdx;
                        }
                        outputOffset += (uint)((baseOffset + vsc.VertexAlign + (stride * vertexCount)) & ~vsc.VertexAlign);
                        vsc.VertexBufferFlags[vtxBufIdx] = stride | ((uint)baseOffset << 8) | ((attrCount - 1) << 0x10);
                        vsc.AttrCount = attrCount;
                        vsc.MaxAttrBitSize = maxAttrSize;
                    }
                    else
                    {
                        attrCount = vsc.AttrCount;
                        if (attrCount > 0)
                        {
                            uint vtxBufIdx = 0;
                            uint index = 0;
                            while (index < attrCount)
                            {
                                uint vtxFlags = vsc.VertexBufferFlags[vtxBufIdx++];
                                uint attrIndex = vtxFlags >> 0x10;
                                if (attrIndex >= index)
                                {
                                    attrIndex = Math.Max(attrIndex, index);
                                    uint baseIndex = index;
                                    uint componentCount = attrIndex - baseIndex + 1;
                                    if ((componentCount & 3) != 0)
                                    {
                                        for (uint k = componentCount & 3; k != 0; --k)
                                        {
                                            vsc.AttrOffsets[baseIndex] = outputOffset + vsc.LocalAttrOffsets[baseIndex];
                                            ++baseIndex;
                                        }
                                    }
                                    if (attrIndex - baseIndex > 2)
                                    {
                                        for (uint k = attrIndex - baseIndex + 1; k != 0; k -= 4)
                                        {
                                            vsc.AttrOffsets[baseIndex] = outputOffset + vsc.LocalAttrOffsets[baseIndex];
                                            vsc.AttrOffsets[baseIndex + 1] = outputOffset + vsc.LocalAttrOffsets[baseIndex + 1];
                                            vsc.AttrOffsets[baseIndex + 2] = outputOffset + vsc.LocalAttrOffsets[baseIndex + 2];
                                            vsc.AttrOffsets[baseIndex + 3] = outputOffset + vsc.LocalAttrOffsets[baseIndex + 3];
                                            baseIndex += 4;
                                        }
                                    }
                                    index = attrIndex + 1;
                                }
                                outputOffset += (uint)((((vtxFlags >> 8) & 0xff) + vsc.VertexAlign + ((vtxFlags & 0xff) * vertexCount)) & ~vsc.VertexAlign);
                            }
                        }
                    }

                    vsc.VertexOutputSize = outputOffset - vsc.TotalVertexOutputSize;
                    vsc.TotalVertexOutputSize = outputOffset;
                    int shift = vsc.MaxAttrBitSize < 0x60 ? 15 : 14;
                    _maxVertexCopyCount = 1u << shift;
                    _verticesProcessed = 0;
                    _numVertices = vertexCount;
                    _indexBlockCountForUnencoded = (uint)((vertexCount + ~(-1 << shift)) >> shift);

                    if (rawAttrs)
                    {
                        _remainingVertexSize = vsc.VertexOutputSize;
                        _currentVertexOffset = vsc.TotalVertexOutputSize - vsc.VertexOutputSize;
                        state = 1;
                    }
                    else
                    {
                        _useVertexTable = (int)ctx.BitStream0.Read(1);
                        if (_useVertexTable == 1)
                        {
                            _vertexInfoTable.Field00 = 0;
                            _vertexInfoTable.Field04 = 0;
                            uint maxPossibleVertCount = 1u << (int)Math.Min(0x20 - Bits.Clz(vertexCount - 1), 0x10u);
                            _vertexInfoTable.Field08 = maxPossibleVertCount;
                            _vertexInfoTable.Field0C = maxPossibleVertCount - 1;
                            _vertexInfoTable.Table = (uint*)allocator.Alloc((nuint)(((maxPossibleVertCount * 4) + 7) & 0x7ffffff8u), 8);
                        }
                        state = 3;
                    }
                    continue;
                }

                case 1:
                {
                    if (numBlocks == 0)
                    {
                        _stage = 1;
                        return;
                    }
                    --numBlocks;

                    if (_hasIndexBuffer && _indexBlockCountForUnencoded > 0)
                    {
                        _indexStreamContext.Decompress(_indexDecompressor, ctx, _numVertices, null, 0,
                                                       Math.Min(_maxVertexCopyCount, _numVertices), allocator);
                        if (_indexBlockCountForUnencoded - 1 != 0)
                        {
                            uint copied = 0;
                            for (uint i = _indexBlockCountForUnencoded - 1; i != 0; --i)
                            {
                                copied += _maxVertexCopyCount;
                                _indexStreamContext.Decompress(_indexDecompressor, ctx, _numVertices, null, copied,
                                                               Math.Min(_maxVertexCopyCount, _numVertices), allocator);
                            }
                        }
                    }
                    Zstd.InsertUncompressedBlock(_dctx, _vertexOutputBuffer, (int)_currentVertexOffset);
                    state = 2;
                    continue;
                }

                case 2:
                {
                    while (_remainingVertexSize != 0)
                    {
                        if (numBlocks == 0)
                        {
                            _stage = 2;
                            return;
                        }
                        --numBlocks;

                        uint outSize = Math.Min(_remainingVertexSize, 0x20000u);
                        uint notCompressed = (uint)ctx.BitStream0.Read(1);
                        uint inSize = notCompressed != 0 ? outSize : VByte.Decode(ref ctx.CurrentPos);
                        Zstd.DecompressBlock(_dctx, _vertexOutputBuffer + _currentVertexOffset, outSize, ctx.CurrentPos, inSize, notCompressed);
                        ctx.CurrentPos += inSize;
                        _remainingVertexSize -= outSize;
                        _currentVertexOffset += outSize;
                    }
                    state = 0;
                    continue;
                }

                case 3:
                {
                    if (numBlocks == 0)
                    {
                        _stage = 3;
                        return;
                    }

                    ulong* tbl = null;
                    uint vertexCount = Math.Min(_maxVertexCopyCount, _numVertices - _verticesProcessed);

                    if (ctx.BitStream0.DirectionalRead(1) != 0 && vertexCount != 0)
                        tbl = (ulong*)allocator.Alloc(vertexCount * sizeof(ulong), 8);

                    --numBlocks;
                    if (_hasIndexBuffer)
                        _indexStreamContext.Decompress(_indexDecompressor, ctx, _numVertices, tbl, _verticesProcessed, vertexCount, allocator);
                    vsc.IndexBufferTable = tbl;
                    state = 4;
                    continue;
                }

                case 4:
                {
                    if (numBlocks == 0)
                    {
                        _stage = 4;
                        return;
                    }
                    --numBlocks;

                    uint* vtbl;
                    if (_useVertexTable == 1)
                    {
                        uint copyCount = Math.Min(_numVertices - _verticesProcessed, _maxVertexCopyCount);
                        vtbl = copyCount != 0
                            ? (uint*)allocator.Alloc((nuint)(((copyCount * 4) + 7) & 0xfffffff8u), 8)
                            : null;
                        AllocationSet allocations = default;
                        VertexDecodingStreamSet streams = default;
                        VertexDecodingStreamSizes streamSizes = default;
                        VertexDecompContext.ReadVertexInfoTableBlock(ref streams, ref streamSizes, ref allocations, 4, ctx, _vertexDecompressor);
                        VertexDecompContext.DecodeVertexInfoTable(vtbl, (int)copyCount, ref streams, ref streamSizes, 4, ref _vertexInfoTable, (int)_verticesProcessed);
                        allocator.Free(allocations.BitStream);
                        allocator.Free(allocations.BackrefOffsetStream);
                        allocator.Free(allocations.BackrefCountStream);
                        allocator.Free(allocations.VertexCountStream);
                    }
                    else
                    {
                        vtbl = null;
                    }
                    vsc.VertexBufferTable = vtbl;
                    vsc.DecompContext = ctx;
                    vsc.BaseVertexIndex = _verticesProcessed;
                    vsc.OutputBuffer = _vertexOutputBuffer;
                    _vertexDecompContext.ReadVertexBlockGroup(ctx, _vertexDecompressor, (int)vsc.AttrCount);
                    vsc.AttrIndex = 0;
                    state = 5;
                    continue;
                }

                case 5:
                {
                    if (numBlocks == 0)
                    {
                        _stage = 5;
                        return;
                    }

                    bool ranOut = false;
                    for (; vsc.AttrIndex < vsc.AttrCount; ++vsc.AttrIndex)
                    {
                        if (numBlocks == 0)
                        {
                            _stage = 5;
                            ranOut = true;
                            break;
                        }

                        uint vertCount = Math.Min(_maxVertexCopyCount, _numVertices - _verticesProcessed);
                        VertexDecodeGroup* groups;
                        uint groupCount;
                        uint count = _vertexDecompContext.ProcessVertexBlockGroup(out groups, out groupCount, ctx, _vertexDecompressor,
                                                                                  vsc, (int)vertCount, allocator);

                        if (count == 0)
                        {
                            AttributeCodec.DecodeBackrefs(vsc, (int)vertCount, groups, groupCount);
                        }
                        else
                        {
                            uint attrFormat = (uint)ctx.BitStream0.Read(7);
                            uint attrFlags = vsc.AttrFlags[vsc.AttrIndex];
                            int streamCount = AttributeStreamInfo.Functions[attrFormat](
                                streamInfo, 6, ref ctx.CurrentPos, (int)(attrFlags & 7), (int)((attrFlags >> 8) & 0xff), (int)count);

                            if (streamCount < 1)
                            {
                                AttributeDecoders.Functions[attrFormat](vsc, (int)vertCount, groups, groupCount, _encodedAttributeStreams, streamCount);
                            }
                            else
                            {
                                for (int i = 0; i != streamCount; ++i)
                                {
                                    if (streamInfo[i].ElementCount == 0)
                                    {
                                        _encodedAttributeStreams[i] = (byte*)&_encodedAttributeStreams[i];
                                        _attributeStreamAllocations[i] = null;
                                    }
                                    else
                                    {
                                        _attributeStreamAllocations[i] = _vertexDecompressor.ProcessBlock(
                                            out _encodedAttributeStreams[i], streamInfo[i].ElementType,
                                            streamInfo[i].TableCount, streamInfo[i].ElementCount, 8, ctx);
                                    }
                                }
                                AttributeDecoders.Functions[attrFormat](vsc, (int)vertCount, groups, groupCount, _encodedAttributeStreams, streamCount);
                                for (int i = streamCount; i != 0; --i)
                                    allocator.Free(_attributeStreamAllocations[i - 1]);
                            }
                        }

                        --numBlocks;

                        _vertexDecompContext.FinishGroupProcessing(allocator);
                    }

                    if (ranOut)
                        return;

                    _vertexDecompContext.Reset(allocator);
                    _verticesProcessed += Math.Min(_numVertices - _verticesProcessed, _maxVertexCopyCount);
                    if (vsc.VertexBufferTable != null)
                    {
                        allocator.Free(vsc.VertexBufferTable);
                        vsc.VertexBufferTable = null;
                    }
                    if (vsc.IndexBufferTable != null)
                    {
                        allocator.Free(vsc.IndexBufferTable);
                        vsc.IndexBufferTable = null;
                    }
                    if (_numVertices == _verticesProcessed)
                    {
                        if (_useVertexTable == 1 && _vertexInfoTable.Table != null)
                            allocator.Free(_vertexInfoTable.Table);
                        state = 0;
                    }
                    else
                    {
                        state = 3;
                    }
                    continue;
                }

                default:
                    throw new MeshCodecException($"Unexpected MeshCodec stage {state}.");
            }
        }
    }
}
