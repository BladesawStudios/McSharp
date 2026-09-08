using System;
using System.Runtime.InteropServices;

namespace McSharp.Internal;

internal sealed unsafe class VertexDecompContext : IDisposable
{
    public VertexDecodeGroup* Groups;
    public uint NumGroups;

    public uint Stage;
    public uint GroupMask;
    public VertexDecodingStreamSizes BlockSizes;
    public AllocationSet Allocations;
    public BitStreamReader BitStream;

    private readonly VertexDecodingStreamSet* _decodeStreams;
    private readonly VertexDecodeGroup* _currentGroup;

    public VertexDecompContext()
    {
        _decodeStreams = (VertexDecodingStreamSet*)NativeMemory.AllocZeroed((nuint)sizeof(VertexDecodingStreamSet));
        _currentGroup = (VertexDecodeGroup*)NativeMemory.AllocZeroed((nuint)sizeof(VertexDecodeGroup));
    }

    public VertexDecodingStreamSet* DecodeStreams => _decodeStreams;

    public void Dispose()
    {
        NativeMemory.Free(_decodeStreams);
        NativeMemory.Free(_currentGroup);
    }

    public void ReadVertexBlockGroup(DecompContext ctx, VertexDecompressor decompressor, int attrCount)
    {
        GroupMask = (uint)ctx.BitStream0.Read((uint)attrCount);

        if (GroupMask == 0)
        {
            Stage = 1;
            return;
        }

        Stage = (uint)ctx.BitStream0.Read(1);
        if (Stage == 1)
            return;

        uint count = Bits.Popcount(GroupMask);
        BlockSizes.VertexCountStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
        BlockSizes.BitStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
        BlockSizes.BackrefCountStreamSize = BlockSizes.VertexCountStreamSize - (int)count;
        BlockSizes.BackrefOffsetStreamSize = BlockSizes.VertexCountStreamSize - (int)ctx.BitStream0.Read(0x20 - Bits.Clz(count));

        VertexDecodingStreamSet* s = _decodeStreams;

        if (BlockSizes.VertexCountStreamSize < 1)
            s->VertexCountStream = (byte*)&s->VertexCountStream;
        else
            Allocations.VertexCountStream = decompressor.ProcessBlock(out s->VertexCountStream, ElementType.U8, 1, BlockSizes.VertexCountStreamSize, 0, ctx);

        if (BlockSizes.BackrefCountStreamSize < 1)
            s->BackrefCountStream = (byte*)&s->BackrefCountStream;
        else
            Allocations.BackrefCountStream = decompressor.ProcessBlock(out s->BackrefCountStream, ElementType.U8, 1, BlockSizes.BackrefCountStreamSize, 0, ctx);

        if (BlockSizes.BackrefOffsetStreamSize < 1)
            s->BackrefOffsetStream = (byte*)&s->BackrefOffsetStream;
        else
            Allocations.BackrefOffsetStream = decompressor.ProcessBlock(out s->BackrefOffsetStream, ElementType.U16, 1, BlockSizes.BackrefOffsetStreamSize, 0, ctx);

        if (BlockSizes.BitStreamSize < 1)
            s->BitStream = (byte*)&s->BitStream;
        else
            Allocations.BitStream = decompressor.ProcessBlock(out s->BitStream, ElementType.U8, 1, BlockSizes.BitStreamSize, 0xf, ctx);

        BitStream.BitOffset = 0;
        BitStream.SetStream((ulong*)s->BitStream);
        BitStream.Remainder = 0;
        BitStream.SetDirection(BitStreamDirection.Forwards);
    }

    public uint ProcessVertexBlockGroup(out VertexDecodeGroup* outGroups, out uint groupCount, DecompContext ctx,
                                        VertexDecompressor decompressor, VertexStreamContext streamCtx,
                                        int vertexCount, StackAllocator allocator)
    {
        uint oldMask = GroupMask;
        GroupMask >>= 1;
        if ((oldMask & 1) == 0)
        {
            _currentGroup->VertexCount = (uint)vertexCount;
            _currentGroup->BackRefOffset = 0;
            outGroups = _currentGroup;
            groupCount = 1;
            return (uint)vertexCount;
        }

        uint attrFlags = streamCtx.AttrFlags[streamCtx.AttrIndex];
        uint unk = (attrFlags >> 3) & 3;
        uint sizeBytes = ((((attrFlags >> 8) & 0xff) + 7) >> 3) * (attrFlags & 7);
        uint format = sizeBytes > 5 ? 1u : (sizeBytes < 3 ? 3u : 2u);

        VertexDecodingStreamSet* s = _decodeStreams;

        if (Stage == 0)
        {
            if (oldMask < 2)
            {
                NumGroups = (uint)BlockSizes.VertexCountStreamSize;
            }
            else
            {
                NumGroups = VByte.Decode(ref ctx.CurrentPos);
                BlockSizes.VertexCountStreamSize -= (int)NumGroups;
            }

            Groups = NumGroups == 0
                ? null
                : (VertexDecodeGroup*)allocator.Alloc(NumGroups * (nuint)sizeof(VertexDecodeGroup), 8);

            uint vertCount = Parse(Groups, (int)NumGroups, s, ref BitStream, attrFlags >> 0x18, unk, format, (uint)vertexCount);

            outGroups = Groups;
            groupCount = NumGroups;
            return vertCount;
        }

        if (Stage != 1)
        {
            outGroups = null;
            groupCount = 0;
            return 0;
        }

        BlockSizes.VertexCountStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
        NumGroups = (uint)BlockSizes.VertexCountStreamSize;
        BlockSizes.BitStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
        BlockSizes.BackrefCountStreamSize = BlockSizes.VertexCountStreamSize - 1;
        BlockSizes.BackrefOffsetStreamSize = BlockSizes.VertexCountStreamSize - (int)ctx.BitStream0.Read(1);

        if (BlockSizes.VertexCountStreamSize < 1)
        {
            s->VertexCountStream = (byte*)&s->VertexCountStream;
            Allocations.VertexCountStream = null;
        }
        else
        {
            Groups = (VertexDecodeGroup*)allocator.Alloc(NumGroups * (nuint)sizeof(VertexDecodeGroup), 8);
            Allocations.VertexCountStream = decompressor.ProcessBlock(out s->VertexCountStream, ElementType.U8, 1, BlockSizes.VertexCountStreamSize, 0, ctx);
        }

        if (BlockSizes.BackrefCountStreamSize < 1)
        {
            s->BackrefCountStream = (byte*)&s->BackrefCountStream;
            Allocations.BackrefCountStream = null;
        }
        else
        {
            Allocations.BackrefCountStream = decompressor.ProcessBlock(out s->BackrefCountStream, ElementType.U8, 1, BlockSizes.BackrefCountStreamSize, 0, ctx);
        }

        if (BlockSizes.BackrefOffsetStreamSize < 1)
        {
            s->BackrefOffsetStream = (byte*)&s->BackrefOffsetStream;
            Allocations.BackrefOffsetStream = null;
        }
        else
        {
            Allocations.BackrefOffsetStream = decompressor.ProcessBlock(out s->BackrefOffsetStream, ElementType.U16, 1, BlockSizes.BackrefOffsetStreamSize, 0, ctx);
        }

        if (BlockSizes.BitStreamSize < 1)
        {
            s->BitStream = (byte*)&s->BitStream;
            Allocations.BitStream = null;
        }
        else
        {
            Allocations.BitStream = decompressor.ProcessBlock(out s->BitStream, ElementType.U8, 1, BlockSizes.BitStreamSize, 0xf, ctx);
        }

        BitStream.SetStream((ulong*)s->BitStream);
        BitStream.Remainder = 0;
        BitStream.BitOffset = 0;
        BitStream.SetDirection(BitStreamDirection.Forwards);

        uint parsed = Parse(Groups, (int)NumGroups, s, ref BitStream, attrFlags >> 0x18, unk, format, (uint)vertexCount);

        allocator.Free(Allocations.BitStream);
        allocator.Free(Allocations.BackrefOffsetStream);
        allocator.Free(Allocations.BackrefCountStream);
        allocator.Free(Allocations.VertexCountStream);

        outGroups = Groups;
        groupCount = NumGroups;
        return parsed;
    }

    public static void ReadVertexInfoTableBlock(ref VertexDecodingStreamSet streams, ref VertexDecodingStreamSizes sizes,
                                                ref AllocationSet allocations, uint a4, DecompContext ctx,
                                                VertexDecompressor decompressor)
    {
        int count = (int)VByte.Decode(ref ctx.CurrentPos);
        if (count < 1)
        {
            sizes.VertexCountStreamSize = 0;
            sizes.BackrefCountStreamSize = count;
            sizes.BackrefOffsetStreamSize = count;
            sizes.BitStreamSize = 0;
            allocations.VertexCountStream = null;
            streams.VertexCountStream = null;
        }
        else
        {
            sizes.VertexCountStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
            sizes.BackrefCountStreamSize = count;
            sizes.BackrefOffsetStreamSize = count;
            sizes.BitStreamSize = (int)VByte.Decode(ref ctx.CurrentPos);
        }

        if (sizes.VertexCountStreamSize < 1)
        {
            streams.VertexCountStream = null;
            allocations.VertexCountStream = null;
        }
        else
        {
            allocations.VertexCountStream = decompressor.ProcessBlock(out streams.VertexCountStream, ElementType.U8, 1, sizes.VertexCountStreamSize, 0, ctx);
        }

        if (sizes.BackrefCountStreamSize < 1)
        {
            streams.BackrefCountStream = null;
            allocations.BackrefCountStream = null;
        }
        else
        {
            allocations.BackrefCountStream = decompressor.ProcessBlock(out streams.BackrefCountStream, ElementType.U8, 1, sizes.BackrefCountStreamSize, 0, ctx);
        }

        if (sizes.BackrefOffsetStreamSize < 1)
        {
            streams.BackrefOffsetStream = null;
            allocations.BackrefOffsetStream = null;
        }
        else
        {
            allocations.BackrefOffsetStream = decompressor.ProcessBlock(out streams.BackrefOffsetStream, ElementType.U8, 1, sizes.BackrefOffsetStreamSize, 0, ctx);
        }

        if (sizes.BitStreamSize < 1)
        {
            streams.BitStream = null;
            allocations.BitStream = null;
        }
        else
        {
            allocations.BitStream = decompressor.ProcessBlock(out streams.BitStream, ElementType.U8, 1, sizes.BitStreamSize, 0xf, ctx);
        }
    }

    public static void DecodeVertexInfoTable(uint* tbl, int numVertices, ref VertexDecodingStreamSet inputStreams,
                                             ref VertexDecodingStreamSizes inputStreamSizes, uint a5,
                                             ref VertexInfoTableInfo info, int baseVertex)
    {
        uint backRefOffsetCount = (uint)inputStreamSizes.BackrefOffsetStreamSize;
        if (backRefOffsetCount == 0)
        {
            if (numVertices != 0)
                NativeMemory.Clear(tbl, (nuint)numVertices * sizeof(uint));
            return;
        }

        uint indexAccumulator = info.Field00;
        uint unkCounter = info.Field04;
        int copied = 0;

        byte* vertexCountStream = inputStreams.VertexCountStream;
        byte* indexStream = inputStreams.BackrefCountStream;
        byte* fifoIndexStream = inputStreams.BackrefOffsetStream;
        BitStreamReader reader = new BitStreamReader((ulong*)inputStreams.BitStream, BitStreamDirection.Forwards);
        int lastIndex = -1;
        uint mask = info.Field0C;

        ReadOnlySpan<uint> groupTable = VertexCodec.VertexGroupEncodingTable;

        for (uint n = backRefOffsetCount; n != 0; --n)
        {
            byte codepoint = *indexStream++;
            int index = codepoint;
            if (codepoint > 0xf)
                index = (int)(groupTable[((codepoint - 0x10) * 2) + 1] + reader.ReadForwards(groupTable[(codepoint - 0x10) * 2]) + 0x10);

            codepoint = *fifoIndexStream++;
            int packedValue = codepoint;
            if (codepoint > 0xf)
                packedValue = (int)(groupTable[((codepoint - 0x10) * 2) + 1] + reader.ReadForwards(groupTable[(codepoint - 0x10) * 2]) + 0x10);

            index += lastIndex;
            lastIndex = index;
            int unkIndexValue = (int)((uint)(-(packedValue & 1) ^ (packedValue >> 1)) + indexAccumulator);
            unkIndexValue += (int)((unkCounter + 1) & (uint)(unkIndexValue >> 0x1f));
            indexAccumulator = (uint)(unkIndexValue - ((int)unkCounter >= unkIndexValue ? 0 : (int)(unkCounter + 1)));
            uint fifoIndex = indexAccumulator & mask;

            if (indexAccumulator == unkCounter)
            {
                info.Table[fifoIndex] = (uint)((index + baseVertex) * -8) | *vertexCountStream++;
                ++unkCounter;
            }
            else
            {
                uint count = (uint)(index - copied);
                if (count != 0)
                {
                    uint extra = count & 7;
                    if (extra != 0)
                    {
                        for (; extra != 0; --extra)
                            tbl[copied++] = 0;
                        count = (uint)(index - copied);
                    }
                    if (count > 7)
                    {
                        for (uint j = 0; j != count; j += 8)
                        {
                            tbl[copied + j + 0] = 0;
                            tbl[copied + j + 1] = 0;
                            tbl[copied + j + 2] = 0;
                            tbl[copied + j + 3] = 0;
                            tbl[copied + j + 4] = 0;
                            tbl[copied + j + 5] = 0;
                            tbl[copied + j + 6] = 0;
                            tbl[copied + j + 7] = 0;
                        }
                    }
                }
                copied = index + 1;
                tbl[lastIndex] = (uint)(info.Table[fifoIndex] + ((index + baseVertex) * 8));
                info.Table[fifoIndex] = (uint)((index + baseVertex) * -8);
            }
        }

        if (copied < numVertices)
            NativeMemory.Clear(tbl + copied, (nuint)(numVertices - copied) * sizeof(uint));

        info.Field00 = indexAccumulator;
        info.Field04 = unkCounter;
    }

    public static uint Parse(VertexDecodeGroup* groups, int count, VertexDecodingStreamSet* inputStreams,
                             ref BitStreamReader bitStream, uint stride, uint a6, uint format, uint totalVertexCount)
    {
        byte* vertexCountStream = inputStreams->VertexCountStream;
        byte* backrefCountStream = inputStreams->BackrefCountStream;
        ushort* backrefOffsetStream = (ushort*)inputStreams->BackrefOffsetStream;

        VertexDecodeGroup* baseGroup = groups - 1;

        int totalRemaining = 0;
        int vertexCount = 0;

        ReadOnlySpan<uint> groupTable = VertexCodec.VertexGroupEncodingTable;

        if (count > 1)
        {
            uint advanceIndex = 0;
            for (int i = count; i > 1; --i)
            {
                byte codepoint = *vertexCountStream++;
                uint vertCount = codepoint;
                if (codepoint > 0xf)
                    vertCount = groupTable[((codepoint - 0x10) * 2) + 1] + (uint)bitStream.ReadForwards(groupTable[(codepoint - 0x10) * 2]) + 0x10;

                codepoint = *backrefCountStream++;
                uint backrefs = codepoint;
                if (codepoint > 0xf)
                    backrefs = groupTable[((codepoint - 0x10) * 2) + 1] + (uint)bitStream.ReadForwards(groupTable[(codepoint - 0x10) * 2]) + 0x10;

                ushort codepointo = *backrefOffsetStream++;
                uint backrefIndex = codepointo;
                int backrefOffset;
                if (codepointo > 2)
                {
                    uint nbits = (uint)(codepointo - 3) & 0x1f;
                    ulong value = bitStream.ReadForwards(nbits);
                    backrefOffset = (int)((uint)((codepointo - 3) >> 5) << (int)a6)
                                  + (int)(((nbits == 0 ? 0 : (uint)value) + (uint)~(-1 << (int)nbits)) * stride);
                    baseGroup += advanceIndex + 1;
                    advanceIndex = 0;
                }
                else
                {
                    if (vertCount == 0)
                        ++backrefIndex;

                    backrefOffset = (int)(baseGroup - backrefIndex)->BackRefOffset;
                    baseGroup += (advanceIndex + 1) & (uint)(-(backrefIndex != 0 ? 1 : 0));
                    advanceIndex = (advanceIndex + 1) & (uint)(-(backrefIndex == 0 ? 1 : 0));
                }

                groups->VertexCount = vertCount | ((backrefs + format) << 0x10);
                groups->BackRefOffset = (uint)backrefOffset;
                ++groups;

                totalRemaining += (int)(vertCount + backrefs + format);
                vertexCount += (int)vertCount;
            }
        }

        {
            byte codepoint = *vertexCountStream++;
            uint vertCount = codepoint;
            if (codepoint > 0xf)
                vertCount = groupTable[((codepoint - 0x10) * 2) + 1] + (uint)bitStream.ReadForwards(groupTable[(codepoint - 0x10) * 2]) + 0x10;

            uint backrefCount = 0;
            uint backrefOffset = 0;
            if (vertCount + totalRemaining < totalVertexCount)
            {
                backrefCount = totalVertexCount - (vertCount + (uint)totalRemaining);
                ushort codepointo = *backrefOffsetStream++;
                uint backrefIndex = codepointo;
                if (codepointo < 3)
                {
                    if (vertCount == 0)
                        ++backrefIndex;

                    backrefOffset = (baseGroup - backrefIndex)->BackRefOffset;
                }
                else
                {
                    uint nbits = (uint)(codepointo - 3) & 0x1f;
                    ulong value = bitStream.ReadForwards(nbits);
                    backrefOffset = ((uint)((codepointo - 3) >> 5) << (int)a6)
                                  + (((nbits == 0 ? 0 : (uint)value) + (uint)~(-1 << (int)nbits)) * stride);
                }
            }

            groups->VertexCount = vertCount | (backrefCount << 0x10);
            groups->BackRefOffset = backrefOffset;
            inputStreams->VertexCountStream = vertexCountStream;
            inputStreams->BackrefCountStream = backrefCountStream;
            inputStreams->BackrefOffsetStream = (byte*)backrefOffsetStream;

            return vertCount + (uint)vertexCount;
        }
    }

    public void FinishGroupProcessing(StackAllocator allocator)
    {
        if ((Stage == 0 || Stage == 1) && Groups != null)
        {
            allocator.Free(Groups);
            Groups = null;
            NumGroups = 0;
        }
    }

    public void Reset(StackAllocator allocator)
    {
        if (Stage == 0)
        {
            allocator.Free(Allocations.BitStream);
            allocator.Free(Allocations.BackrefOffsetStream);
            allocator.Free(Allocations.BackrefCountStream);
            allocator.Free(Allocations.VertexCountStream);
        }

        Stage = 2;
        GroupMask = 0;
    }
}
