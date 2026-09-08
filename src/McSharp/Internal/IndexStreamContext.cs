using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace McSharp.Internal;

internal sealed unsafe class IndexStreamContext
{
    public IndexFormat IndexFormat;
    public EncodingType EncodingType;
    public uint BlockCount;
    public uint RawCount;
    public uint BaseIndex;
    public uint IndicesRemaining;
    public uint BlocksRemaining;
    public uint StreamCount;
    public StreamContext StreamContext;
    public uint IndexOffset;

    public bool ParseIndexHeader(DecompContext ctx)
    {
        uint count = VByte.Decode(ref ctx.CurrentPos);

        if (count != 0)
        {
            byte meshoptIdxHeader = *ctx.CurrentPos++;
            IndexFormat = (IndexFormat)(meshoptIdxHeader & 0xf);
            EncodingType = (EncodingType)(meshoptIdxHeader >> 4);
            BlockCount = VByte.Decode(ref ctx.CurrentPos);
            RawCount = VByte.Decode(ref ctx.CurrentPos);
            BaseIndex = VByte.Decode(ref ctx.CurrentPos);
            BlocksRemaining = BlockCount;
            StreamCount = count;
            IndicesRemaining = RawCount >> (EncodingType == EncodingType.Type01 ? 1 : 0);
        }

        return count != 0;
    }

    private enum DecompFunc { Stream1, Stream2, Stream3 }

    private static DecompFunc GetDecompFunc(EncodingType t) => t switch
    {
        EncodingType.Type00 or EncodingType.Type01 => DecompFunc.Stream1,
        EncodingType.Type03 => DecompFunc.Stream3,
        _ => DecompFunc.Stream2,
    };

    private static uint Invoke(DecompFunc f, IndexDecompressor d, void* dst, IndexFormat format, uint count,
                               uint baseIndex, ulong* decodeBuf, uint numCopied, uint remaining) => f switch
                               {
                                   DecompFunc.Stream1 => d.Decompress1(dst, format, count, baseIndex, decodeBuf, numCopied, remaining),
                                   DecompFunc.Stream3 => d.Decompress3(dst, format, count, baseIndex, decodeBuf, numCopied, remaining),
                                   _ => d.Decompress2(dst, format, count, baseIndex, decodeBuf, numCopied, remaining),
                               };

    public void Decompress(IndexDecompressor decompressor, DecompContext ctx, uint numVertices, ulong* decodeBuf,
                           uint verticesCopied, uint copyCount, StackAllocator allocator)
    {
        if (decodeBuf != null)
            NativeMemory.Clear(decodeBuf, copyCount * 8);

        if (StreamCount == 0)
            return;

        decompressor.SetContext(ctx);
        decompressor.SetVertexCount(numVertices);
        if (verticesCopied == 0)
            decompressor.SetBaseIndex(0);

        uint offset = IndexOffset;
        uint numBlocks = BlocksRemaining;
        uint streams = StreamCount;

        DecompFunc decompFunc = GetDecompFunc(EncodingType);

        uint remaining = Invoke(decompFunc, decompressor, StreamContext.Stream + offset, IndexFormat,
                                IndicesRemaining, BaseIndex, decodeBuf, verticesCopied, copyCount);
        offset += (IndicesRemaining - remaining) << (int)IndexFormat;

        while (remaining == 0)
        {
            if (EncodingType == EncodingType.Type01)
            {
                uint size = (RawCount >> 1) << (int)IndexFormat;
                byte* ptr = StreamContext.Stream + offset - size;
                offset += size;
                PostProcessIndexBuffer(ptr, ptr, (int)RawCount, IndexFormat, allocator);
            }

            if (numBlocks == 1)
            {
                --streams;
                offset = (uint)((StreamContext.Alignment + offset - 1) & (ulong)(-(long)StreamContext.Alignment));
                if (streams == 0)
                {
                    remaining = 0;
                    numBlocks = 0;
                    break;
                }

                byte meshoptIdxHeader = *ctx.CurrentPos++;
                IndexFormat = (IndexFormat)(meshoptIdxHeader & 0xf);
                EncodingType = (EncodingType)(meshoptIdxHeader >> 4);
                BlockCount = VByte.Decode(ref ctx.CurrentPos);
                numBlocks = BlockCount;

                decompFunc = GetDecompFunc(EncodingType);
            }
            else
            {
                --numBlocks;
            }

            RawCount = VByte.Decode(ref ctx.CurrentPos);
            BaseIndex = VByte.Decode(ref ctx.CurrentPos);

            remaining = Invoke(decompFunc, decompressor, StreamContext.Stream + offset, IndexFormat,
                               RawCount, BaseIndex, decodeBuf, verticesCopied, copyCount);
            offset += (RawCount - remaining) << (int)IndexFormat;
        }

        IndicesRemaining = remaining;
        BlocksRemaining = numBlocks;
        StreamCount = streams;
        IndexOffset = offset;
    }

    private static void PostProcessIndexBuffer(void* outBuf, void* inBuf, int count, IndexFormat format, StackAllocator allocator)
    {
        if (format == IndexFormat.U16)
            PostProcessIndexBuffer<ushort>((ushort*)outBuf, (ushort*)inBuf, count, allocator);
        else
            PostProcessIndexBuffer<uint>((uint*)outBuf, (uint*)inBuf, count, allocator);
    }

    [StructLayout(LayoutKind.Sequential, Size = 8)]
    private struct IndexInfo
    {
        public uint BufferPos;
        public uint IndexValue;
    }

    private static void PostProcessIndexBuffer<T>(T* outBuf, T* inBuf, int indexCount, StackAllocator allocator)
        where T : unmanaged, IBinaryInteger<T>
    {
        uint indices = (uint)(indexCount < 0 ? indexCount + 1 : indexCount) >> 1;
        ulong indices64 = indices;

        uint min;
        uint max;
        if (indexCount + 1 < 3)
        {
            min = 0;
            max = 0;
        }
        else
        {
            min = *(uint*)inBuf;
            max = min;
            if (indexCount > 3)
            {
                uint index;
                if ((indexCount & 0xfffffffe) == 4)
                {
                    index = 1;
                }
                else
                {
                    for (index = 0; index + 2 != ((indices64 - 1) & 0xfffffffffffffffe); index += 2)
                    {
                        min = Math.Min(uint.CreateTruncating(inBuf[index + 1]), min);
                        max = Math.Max(uint.CreateTruncating(inBuf[index + 1]), max);
                        min = Math.Min(uint.CreateTruncating(inBuf[index + 2]), min);
                        max = Math.Max(uint.CreateTruncating(inBuf[index + 2]), max);
                    }
                    ++index;
                }
                if (((indices64 - 1) & 1) != 0)
                {
                    min = Math.Min(uint.CreateTruncating(inBuf[index]), min);
                    max = Math.Max(uint.CreateTruncating(inBuf[index]), max);
                }
            }
        }

        int range = (int)(max - min);
        int uniqueCount = range + 1;

        int* occurrences;
        uint* occurrenceBases;
        IndexInfo* swapTargets;
        if (range == -1)
        {
            occurrences = null;
            occurrenceBases = null;
        }
        else
        {
            occurrences = (int*)allocator.Alloc((nuint)((uniqueCount * sizeof(int) + 7) & ~7), 8);
            if ((uint)range < 0x7fffffff)
                NativeMemory.Clear(occurrences, (nuint)(uniqueCount * sizeof(int)));
            occurrenceBases = (uint*)allocator.Alloc((nuint)((uniqueCount * sizeof(uint) + 7) & ~7), 8);
        }

        if (indexCount + 1 > 2)
        {
            swapTargets = (IndexInfo*)allocator.Alloc((nuint)indices * (nuint)sizeof(IndexInfo), 8);
            if (indexCount > 1)
            {
                uint index;
                if (((uint)indexCount & 0xfffffffe) == 2)
                {
                    index = 0;
                }
                else
                {
                    for (index = 0; index != (indices & 0xfffffffe); index += 2)
                    {
                        occurrences[uint.CreateTruncating(inBuf[index]) - min]++;
                        occurrences[uint.CreateTruncating(inBuf[index + 1]) - min]++;
                    }
                }
                if ((indices & 1) != 0)
                    occurrences[uint.CreateTruncating(inBuf[index]) - min]++;
            }
        }
        else
        {
            swapTargets = null;
        }

        if (uniqueCount != 0)
        {
            uint index = 0;
            uint sum = 0;
            if (range != 0)
            {
                for (; index != (uniqueCount & 0xfffffffe); index += 2)
                {
                    occurrenceBases[index] = sum;
                    occurrenceBases[index + 1] = sum + (uint)occurrences[index];
                    sum += (uint)occurrences[index + 1] + (uint)occurrences[index];
                    occurrences[index] = 0;
                    occurrences[index + 1] = 0;
                }
            }
            if ((uniqueCount & 1) != 0)
            {
                occurrenceBases[index] = sum;
                occurrences[index] = 0;
            }
        }

        if (indexCount >= 6)
        {
            uint inIndex = indices;
            uint outIndex = (uint)indexCount;
            for (uint iter = (uint)(indexCount / 6); iter != 0; --iter)
            {
                T val0 = inBuf[inIndex - 3];
                T val1 = inBuf[inIndex - 2];
                T val2 = inBuf[inIndex - 1];

                outBuf[outIndex - 6] = val0;
                outBuf[outIndex - 5] = val2;
                outBuf[outIndex - 4] = val1;
                outBuf[outIndex - 3] = val0;
                outBuf[outIndex - 2] = val2;
                outBuf[outIndex - 1] = val1;

                uint index0 = uint.CreateTruncating(val0) - min;
                uint index1 = uint.CreateTruncating(val1) - min;
                uint index2 = uint.CreateTruncating(val2) - min;

                int cnt = occurrences[index2];
                bool matched = false;
                if (cnt > 0)
                {
                    uint slot = occurrenceBases[index2];
                    ulong unkValue = (ulong)(cnt - 1) * 8;
                    for (int k = cnt; k != 0; --k)
                    {
                        if (swapTargets[slot].IndexValue == index1)
                        {
                            outBuf[outIndex - 3] = outBuf[swapTargets[slot].BufferPos];
                            outBuf[swapTargets[slot].BufferPos] = T.CreateTruncating(index0);
                            if (unkValue != 0)
                                swapTargets[slot].BufferPos = swapTargets[slot + cnt - 1].BufferPos;
                            occurrences[index2]--;
                            matched = true;
                            break;
                        }
                        unkValue -= 8;
                    }
                }
                if (!matched)
                {
                    swapTargets[occurrences[index1] + occurrenceBases[index1]].BufferPos = outIndex - 3;
                    swapTargets[occurrences[index1] + occurrenceBases[index1]].IndexValue = index2;
                    occurrences[index1]++;
                }

                cnt = occurrences[index0];
                matched = false;
                if (cnt > 0)
                {
                    uint slot = occurrenceBases[index0];
                    ulong unkValue = (ulong)(cnt - 1) * 8;
                    for (int k = cnt; k != 0; --k)
                    {
                        if (swapTargets[slot].IndexValue == index2)
                        {
                            outBuf[outIndex - 3] = outBuf[swapTargets[slot].BufferPos];
                            outBuf[swapTargets[slot].BufferPos] = T.CreateTruncating(index1);
                            if (unkValue != 0)
                                swapTargets[slot].BufferPos = swapTargets[slot + cnt - 1].BufferPos;
                            occurrences[index0]--;
                            matched = true;
                            break;
                        }
                        unkValue -= 8;
                    }
                }
                if (!matched)
                {
                    swapTargets[occurrences[index2] + occurrenceBases[index2]].BufferPos = outIndex - 3;
                    swapTargets[occurrences[index2] + occurrenceBases[index2]].IndexValue = index0;
                    occurrences[index2]++;
                }

                cnt = occurrences[index1];
                matched = false;
                if (cnt > 0)
                {
                    uint slot = occurrenceBases[index1];
                    ulong unkValue = (ulong)(cnt - 1) * 8;
                    for (int k = cnt; k != 0; --k)
                    {
                        if (swapTargets[slot].IndexValue == index0)
                        {
                            outBuf[outIndex - 3] = outBuf[swapTargets[slot].BufferPos];
                            outBuf[swapTargets[slot].BufferPos] = T.CreateTruncating(index2);
                            if (unkValue != 0)
                                swapTargets[slot].BufferPos = swapTargets[slot + cnt - 1].BufferPos;
                            occurrences[index1]--;
                            matched = true;
                            break;
                        }
                        unkValue -= 8;
                    }
                }
                if (!matched)
                {
                    swapTargets[occurrences[index0] + occurrenceBases[index0]].BufferPos = outIndex - 3;
                    swapTargets[occurrences[index0] + occurrenceBases[index0]].IndexValue = index1;
                    occurrences[index0]++;
                }
            }
        }

        if (occurrences != null)
            allocator.Free(occurrences);

        if (occurrenceBases != null)
            allocator.Free(occurrenceBases);

        if (swapTargets != null)
            allocator.Free(swapTargets);
    }
}
