using ZstdSharp.Unsafe;

namespace McSharp.Internal;

internal sealed unsafe class VertexDecompressor
{
    private ZSTD_DCtx_s* _dctx;
    private byte* _buffer;
    private uint _offset;
    private uint _bufferSize;
    private DecodingContext* _decodingContext;
    private StackAllocator? _allocator;

    public void Initialize(uint _, ZSTD_DCtx_s* dctx, StackAllocator allocator)
    {
        _dctx = dctx;
        _buffer = (byte*)allocator.Alloc(0x80000, 8);
        _offset = 0;
        _bufferSize = 0x80000;
        _decodingContext = (DecodingContext*)allocator.Alloc((nuint)sizeof(DecodingContext), 0x40);
        _allocator = allocator;
        _decodingContext->Enc1.Field20 = 0;
    }

    public void Close()
    {
        _allocator!.Free(_decodingContext);
        _allocator!.Free(_buffer);
    }

    public void* ProcessBlock(out byte* dst, ElementType elementType, int tableCount, int elementCount,
                              uint baseOutSize, DecompContext ctx)
    {
        int streamSize = elementCount * tableCount;
        int outputSize = streamSize << (int)((uint)elementType & 0x1f);

        byte* output;
        void* allocation;
        switch (ctx.BitStream0.Read(2))
        {
            case 0:
            {
                output = (byte*)_allocator!.Alloc((nuint)((baseOutSize + (uint)outputSize + 0xf) & 0xfffffff0), 0x10);
                allocation = output;

                uint elementSize = (uint)((streamSize > -1 ? streamSize : streamSize + 0xf) >> 4);
                if (elementSize < 2)
                    elementSize = 1;

                int temp = (int)(0x20 - Bits.Clz(elementSize - 1));
                elementSize = (uint)(temp >= 5 ? temp : 4);
                elementSize = streamSize < 0x4000 ? elementSize : 10;

                if (elementType == ElementType.U8)
                    VertexCodec.DecodeByteStream(output, outputSize, tableCount, elementSize, _decodingContext, ctx);
                else
                    VertexCodec.DecodeByteStream((ushort*)output, outputSize, tableCount, elementSize, _decodingContext, ctx);
                break;
            }
            case 1:
            {
                output = (byte*)_allocator!.Alloc((nuint)((baseOutSize + (uint)outputSize + 0xf) & 0xfffffff0), 0x10);
                allocation = output;

                BufferView view;
                view.Ptr = ctx.CurrentPos;
                view.Field08 = 0;
                view.Offset = 0;
                if (elementType == ElementType.U8)
                {
                    byte* outPtr = output;
                    if (streamSize > 0x7ffff)
                    {
                        for (uint i = (uint)(streamSize - 0x40000) >> 0x12; i != 0; --i)
                        {
                            VertexCodec.GenDecodingTable(_decodingContext, ctx);
                            VertexCodec.DecodeTable(outPtr, 1, 0x40000, _decodingContext, ref view, ctx);
                            outPtr += 0x40000;
                            streamSize -= 0x40000;
                        }
                    }
                    VertexCodec.GenDecodingTable(_decodingContext, ctx);
                    VertexCodec.DecodeTable(outPtr, 1, streamSize, _decodingContext, ref view, ctx);
                }
                else
                {
                    ushort* outPtr = (ushort*)output;
                    if (streamSize > 0x7ffff)
                    {
                        for (uint i = (uint)(streamSize - 0x40000) >> 0x12; i != 0; --i)
                        {
                            VertexCodec.GenDecodingTable(_decodingContext, ctx);
                            VertexCodec.DecodeTable(outPtr, 1, 0x40000, _decodingContext, ref view, ctx);
                            outPtr += 0x40000;
                            streamSize -= 0x40000;
                        }
                    }
                    VertexCodec.GenDecodingTable(_decodingContext, ctx);
                    VertexCodec.DecodeTable(outPtr, 1, streamSize, _decodingContext, ref view, ctx);
                }
                ctx.CurrentPos = view.Ptr + view.Offset;
                break;
            }
            case 2:
            {
                allocation = null;

                uint offset = _offset;
                Zstd.InsertBlocks(_dctx, _buffer + offset, (int)(_bufferSize - offset), _buffer, (int)offset);
                if (offset + outputSize > 0x80000)
                {
                    _bufferSize = offset;
                    offset = 0;
                }
                Zstd.DecompressVertexStream(_dctx, _buffer + offset, outputSize, ctx, 0x20000);
                _offset = offset + (uint)outputSize;
                output = _buffer + offset;
                break;
            }
            default:
            {
                allocation = null;

                output = ctx.CurrentPos;
                ctx.CurrentPos += outputSize;
                break;
            }
        }

        dst = output;

        return allocation;
    }
}
