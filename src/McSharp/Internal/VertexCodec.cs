using System;
using System.Numerics;

namespace McSharp.Internal;

internal static unsafe class VertexCodec
{
    public static ReadOnlySpan<uint> VertexGroupEncodingTable => new uint[26 * 2]
    {
        1, 0,          1, 2,          1, 4,          1, 6,
        2, 8,          2, 0xc,        3, 0x10,       3, 0x18,
        4, 0x20,       5, 0x30,       6, 0x50,       7, 0x90,
        8, 0x110,      9, 0x210,      0xa, 0x410,    0xb, 0x810,
        0xc, 0x1010,   0xd, 0x2010,   0xe, 0x4010,   0xf, 0x8010,
        0x10, 0x10010, 0x12, 0x20010, 0x14, 0x60010, 0x18, 0x160010,
        0x1c, 0x116010, 0, 0x11160010,
    };

    private static uint Unzigzag(uint v) => (0u - (v & 1)) ^ (v >> 1);

    private static ulong GenDecodingTableShifts(ushort* dst, uint count, DecompContext ctx, uint a4, uint a5, ulong bitInfo)
    {
        uint bitOffset = ctx.BitStream0.BitOffset;
        ulong remainder = ctx.BitStream0.Remainder;

        int unkValue = 0;

        uint unk = a4 << 10;
        uint bits = (uint)bitInfo;
        uint shift = (uint)(bitInfo >> 0x20);
        while (count != 0)
        {
            --count;
            ulong value = ctx.BitStream0.ReadRaw() | remainder;
            uint uVar2 = unk >> 10;
            uint nbits = uVar2 + (Bits.Clz(value) * 2) + 1;
            bitOffset = (bitOffset | 0x38) - nbits;
            ctx.BitStream0.BitOffset = bitOffset;
            remainder = value << (int)nbits;
            uint raw = (uint)((-1 << (int)(uVar2 & 0x1f)) + unkValue) + (uint)(value >> (int)(0x40 - nbits));
            bits += Unzigzag(raw);
            shift -= bits;
            *dst++ = (ushort)bits;

            if (count == 0) break;

            unkValue = 0;
            unk = Math.Min(0x8000 - (0x400 * Bits.Clz(shift)), (unk * (0x10 - a5) + ((0x8000 - (0x400 * Bits.Clz(raw))) * a5)) >> 4);

            if ((raw >> (int)(uVar2 & 0x1f)) != 0) continue;

            value = ctx.BitStream0.ReadRaw() | remainder;
            nbits = 0x20 - Bits.Clz(count);

            uint i;
            if (Bits.Clz(value) >= nbits)
            {
                remainder = value << (int)nbits;
                bitOffset = (bitOffset | 0x38) - nbits;
                i = count;
                count = 0;
            }
            else
            {
                nbits = (Bits.Clz(value) << 1) | 1;
                remainder = value << (int)nbits;
                bitOffset = (bitOffset | 0x38) - nbits;
                i = (uint)(value >> (int)(0x40 - nbits)) - 1;
                count -= i;
            }

            ctx.BitStream0.BitOffset = bitOffset;

            for (; i != 0; --i)
            {
                value = ctx.BitStream0.ReadRaw() | remainder;
                nbits = unk >> 10;
                remainder = value << (int)(nbits & 0x3f);
                bitOffset = (bitOffset | 0x38) - nbits;
                ctx.BitStream0.BitOffset = bitOffset;
                raw = (uint)((value >> 1) >> (int)(0x3f - nbits));
                bits += Unzigzag(raw);
                shift -= bits;
                *dst++ = (ushort)bits;
                unk = Math.Min(0x8000 - (0x400 * Bits.Clz(shift)), (unk * (0x10 - a5) + ((0x8000 - (0x400 * Bits.Clz(raw))) * a5)) >> 4);
            }

            unkValue = 1 << (int)(unk >> 10);
        }

        ctx.BitStream0.BitOffset = bitOffset;
        ctx.BitStream0.Remainder = remainder;

        return bits | ((ulong)shift << 0x20);
    }

    private static uint GenDecodingTableValues(ushort* dst, uint count, DecompContext ctx, uint a4, uint a5, uint baseValue)
    {
        uint bitOffset = ctx.BitStream0.BitOffset;
        ulong remainder = ctx.BitStream0.Remainder;

        int unkValue = 0;
        uint unk = a4 << 10;

        while (count != 0)
        {
            --count;
            ulong value = ctx.BitStream0.ReadRaw() | remainder;
            uint uVar3 = unk >> 10;
            uint nbits = uVar3 + (Bits.Clz(value) * 2) + 1;
            remainder = value << (int)nbits;
            bitOffset = (bitOffset | 0x38) - nbits;
            ctx.BitStream0.BitOffset = bitOffset;
            uint raw = (uint)((-1 << (int)(uVar3 & 0x1f)) + unkValue) + (uint)(value >> (int)(0x40 - nbits));
            baseValue += raw;
            *dst = (ushort)baseValue;
            ++baseValue;
            ++dst;

            if (count == 0) break;

            unkValue = 0;
            unk = (unk * (0x10 - a5) + ((0x8000 - (Bits.Clz(raw) * 0x400)) * a5)) >> 4;

            if ((raw >> (int)(uVar3 & 0x1f)) != 0) continue;

            value = ctx.BitStream0.ReadRaw() | remainder;
            nbits = 0x20 - Bits.Clz(count);

            uint i;
            if (Bits.Clz(value) >= nbits)
            {
                remainder = value << (int)(nbits & 0x3f);
                bitOffset = (bitOffset | 0x38) - nbits;
                i = count;
                count = 0;
            }
            else
            {
                nbits = (Bits.Clz(value) << 1) | 1;
                remainder = value << (int)(nbits & 0x3f);
                bitOffset = (bitOffset | 0x38) - nbits;
                i = (uint)(value >> (int)(0x40 - nbits)) - 1;
                count -= i;
            }

            ctx.BitStream0.BitOffset = bitOffset;

            for (; i != 0; --i)
            {
                value = ctx.BitStream0.ReadRaw() | remainder;
                nbits = unk >> 10;
                remainder = value << (int)(nbits & 0x3f);
                bitOffset = (bitOffset | 0x38) - nbits;
                ctx.BitStream0.BitOffset = bitOffset;
                raw = (uint)((value >> 1) >> (int)(0x3f - nbits));
                baseValue += raw;
                *dst = (ushort)baseValue;
                ++baseValue;
                ++dst;
                unk = (unk * (0x10 - a5) + ((0x8000 - (Bits.Clz(raw) * 0x400)) * a5)) >> 4;
            }

            unkValue = 1 << (int)((unk >> 10) & 0x1f);
        }

        ctx.BitStream0.BitOffset = bitOffset;
        ctx.BitStream0.Remainder = remainder;

        return baseValue;
    }

    private static void GenDecodingTable0(ushort* dst, int count0, int maxIndexBitSize, DecompContext ctx)
    {
        ushort* values = dst + (0x1800 - count0);
        ushort* shifts = dst + (0x1000 - count0);

        if (count0 < 0xb)
        {
            uint bitOffset = ctx.BitStream0.BitOffset;
            ulong remainder = ctx.BitStream0.Remainder;
            uint unk = 0;
            uint accumulator = 0;
            for (int i = 0; i < count0; ++i)
            {
                ulong value = ctx.BitStream0.ReadRaw() | remainder;
                uint nbits = (unk >> 10) + (Bits.Clz(value) * 2) + 1;
                remainder = value << (int)(nbits & 0x3f);
                bitOffset = (bitOffset | 0x38) - nbits;
                uint delta = (uint)(-1 << (int)((unk >> 10) & 0x1f)) + (uint)(value >> (int)(0x40 - nbits));
                accumulator += delta;
                values[i] = (ushort)accumulator;
                ++accumulator;
                unk = ((unk * 0xc) - (Bits.Clz(delta) * 0x1000) + 0x20000) >> 4;
                ctx.BitStream0.BitOffset = bitOffset;
                ctx.BitStream0.Remainder = remainder;
            }
        }
        else
        {
            GenDecodingTableValues(values, (uint)count0, ctx, 0, 3, 0);
        }

        uint unkParam = count0 < 0xb ? 0xfu : 0xeu;
        int max = 1 << maxIndexBitSize;
        int someCount = count0 != 0 ? max / count0 : 0;
        if (maxIndexBitSize < 4)
            maxIndexBitSize = 3;
        GenDecodingTableShifts(shifts, (uint)(count0 - 1), ctx, (uint)(maxIndexBitSize - 2), unkParam,
                               (uint)someCount | ((ulong)(uint)max << 0x20));

        ushort* out0 = dst;
        ushort* out1 = dst + 0x1000;
        if (count0 > 1)
        {
            for (int i = 0; i != count0 - 1; ++i)
            {
                ushort shift = *shifts++;
                ushort value = *values++;
                uint index = 0;
                if (shift > 1)
                {
                    for (uint j = (uint)(shift >> 1); j != 0; --j)
                    {
                        out0[0] = (ushort)index++;
                        out0[1] = shift;
                        out0[2] = (ushort)index++;
                        out0[3] = shift;
                        out1[0] = value;
                        out1[1] = value;
                        out0 += 4;
                        out1 += 2;
                    }
                }
                if ((shift & 1) != 0)
                {
                    out0[0] = (ushort)index;
                    out0[1] = shift;
                    out1[0] = value;
                    out0 += 2;
                    ++out1;
                }
                max -= shift;
            }
        }

        ushort finalIndex;
        ushort finalValue = *values;
        if (max < 2)
        {
            finalIndex = 0;
        }
        else
        {
            finalIndex = 0;
            for (uint i = (uint)(max >> 1); i != 0; --i)
            {
                out0[0] = finalIndex++;
                out0[1] = (ushort)max;
                out0[2] = finalIndex++;
                out0[3] = (ushort)max;
                out1[0] = finalValue;
                out1[1] = finalValue;
                out0 += 4;
                out1 += 2;
            }
        }
        if ((max & 1) != 0)
        {
            out0[0] = finalIndex;
            out0[1] = (ushort)max;
            out1[0] = finalValue;
        }
    }

    private static void GenDecodingTable1(ushort* dst, DecompContext ctx, int count0, int maxIndexBitSize)
    {
        uint shiftIndex = (uint)(0x800 - maxIndexBitSize);
        uint valueIndex = (uint)(0x800 - count0);

        uint bitOffset = ctx.BitStream0.BitOffset | 0x38;
        ulong remainder = ctx.BitStream0.ReadRaw() | ctx.BitStream0.Remainder;

        int bitsRemaining = count0;
        ushort* outp = dst;

        uint endIndex;
        if (maxIndexBitSize < 2)
        {
            endIndex = shiftIndex;
        }
        else
        {
            for (int i = 0; i != maxIndexBitSize - 1; ++i)
            {
                if (bitOffset < 10)
                {
                    remainder |= ctx.BitStream0.ReadRaw();
                    bitOffset |= 0x38;
                    ctx.BitStream0.BitOffset = bitOffset;
                }

                uint mask = (uint)(-1 << (i + 1));
                int localMax = bitsRemaining - 1;
                mask = (uint)(localMax < (int)~mask ? localMax : (int)~mask);

                uint nbits = 0x20 - Bits.Clz(mask);
                uint value = (uint)(remainder >> (int)(0x40 - nbits));
                remainder <<= (int)(nbits & 0x3f);
                bitOffset -= nbits & 0x3f;
                ctx.BitStream0.BitOffset = bitOffset;
                outp[((shiftIndex + i) * 2) + 1] = (ushort)value;
                bitsRemaining -= (int)value;
            }
            endIndex = 0x7ff;
        }

        ctx.BitStream0.BitOffset = bitOffset;

        outp[(endIndex * 2) + 1] = (ushort)bitsRemaining;

        ushort* shiftBuffer = dst + (shiftIndex * 2);
        ushort* valueBuffer = dst + (valueIndex * 2);

        uint unk = 0;
        uint indexBits = 0;
        int accumulator = 0;

        for (uint i = (uint)count0; i != 0; --i)
        {
            if (indexBits == 0)
            {
                while (indexBits == 0)
                {
                    indexBits = shiftBuffer[1];
                    shiftBuffer += 2;
                }
                accumulator = 0;
            }

            remainder |= ctx.BitStream0.ReadRaw();
            uint nbits = (unk >> 10) + (Bits.Clz(remainder) * 2) + 1;
            uint delta = (uint)(-1 << (int)((unk >> 10) & 0x1f)) + (uint)(remainder >> (int)(0x40 - nbits));
            remainder <<= (int)nbits;
            bitOffset = (bitOffset | 0x38) - nbits;
            ctx.BitStream0.BitOffset = bitOffset;

            accumulator += (int)delta;
            *valueBuffer = (ushort)accumulator;
            valueBuffer += 2;
            ++accumulator;
            --indexBits;
            unk = ((unk * 0xc) - (Bits.Clz(delta) * 0x1000) + 0x20000) >> 4;
        }

        uint baseIdx = 0;
        uint remainingShift;
        uint* outBuf = (uint*)dst;
        if (maxIndexBitSize < 2)
        {
            remainingShift = outp[(shiftIndex * 2) + 1];
        }
        else
        {
            uint valueCount = (uint)(1 << ((maxIndexBitSize - 1) & 0x1f));
            for (int i = 1; i != maxIndexBitSize; ++i)
            {
                uint v = outp[(shiftIndex * 2) + 1];

                if (v != 0)
                {
                    if (valueCount == 0)
                    {
                        valueIndex += v;
                    }
                    else
                    {
                        uint count = v;
                        if ((v & 1) != 0)
                        {
                            uint j = 0;
                            for (; j < valueCount; j += 2)
                            {
                                outBuf[baseIdx + j] = ((uint)i << 0x10) | outp[valueIndex * 2];
                                outBuf[baseIdx + j + 1] = ((uint)i << 0x10) | outp[valueIndex * 2];
                            }
                            ++valueIndex;
                            baseIdx += j;
                            count = v - 1;
                        }
                        if (v != 1)
                        {
                            for (; count != 0; count -= 2)
                            {
                                uint base0 = 0;
                                uint base1 = 0;
                                for (; base0 < valueCount; base0 += 2)
                                {
                                    outBuf[baseIdx + base0] = ((uint)i << 0x10) | outp[valueIndex * 2];
                                    outBuf[baseIdx + base0 + 1] = ((uint)i << 0x10) | outp[valueIndex * 2];
                                }
                                ++valueIndex;
                                baseIdx += base0;
                                for (; base1 < valueCount; base1 += 2)
                                {
                                    outBuf[baseIdx + base1] = ((uint)i << 0x10) | outp[valueIndex * 2];
                                    outBuf[baseIdx + base1 + 1] = ((uint)i << 0x10) | outp[valueIndex * 2];
                                }
                                ++valueIndex;
                                baseIdx += base1;
                            }
                        }
                    }
                }

                ++shiftIndex;
                valueCount >>= 1;
            }
            remainingShift = outp[0xfff];
        }

        if (remainingShift != 0)
        {
            uint count = remainingShift;
            if ((remainingShift & 1) != 0)
            {
                outBuf[baseIdx++] = ((uint)maxIndexBitSize << 0x10) | outp[valueIndex * 2];
                --count;
                ++valueIndex;
            }
            if (remainingShift != 1)
            {
                for (; count != 0; count -= 2)
                {
                    outBuf[baseIdx++] = ((uint)maxIndexBitSize << 0x10) | outp[valueIndex * 2];
                    ++valueIndex;
                    outBuf[baseIdx++] = ((uint)maxIndexBitSize << 0x10) | outp[valueIndex * 2];
                    ++valueIndex;
                }
            }
        }

        ctx.BitStream0.BitOffset = bitOffset;
        ctx.BitStream0.Remainder = remainder;
    }

    public static void GenDecodingTable(DecodingContext* decodeCtx, DecompContext decompCtx)
    {
        ulong remainder = decompCtx.BitStream0.Remainder;
        uint bitOffset = decompCtx.BitStream0.BitOffset | 0x38;
        ulong value = decompCtx.BitStream0.ReadRaw() | remainder;

        uint bitCount;
        if ((value >> 0x3f) != 0)
        {
            bitOffset -= 9;
            remainder = value << 9;
            bitCount = (uint)(((value >> 0x3b) & 0xf) | ((value >> 0x33) & 0x70)) + 0x11;
            if (((value >> 0x3a) & 1) != 0)
            {
                bitCount += (uint)((value >> 0x2d) & 0x180) + 0x80;
                bitOffset -= 3;
                remainder <<= 3;
                if (((value >> 0x36) & 1) != 0)
                {
                    bitCount += (uint)((value >> 0x24) & 0xfe00) + 0x200;
                    bitOffset -= 7;
                    remainder <<= 7;
                }
            }
        }
        else
        {
            bitOffset -= 5;
            uint count = (uint)((value >> 0x3b) & 0xf);
            remainder = value << 5;
            if (count == 0)
            {
                uint repeatValue = 0;
                do
                {
                    value = remainder;
                    remainder <<= 8;
                    repeatValue = (uint)((value >> 0x38) & 0x7f) | (repeatValue << 7);
                    bitOffset -= 8;
                } while ((value >> 0x3f) != 0);
                decodeCtx->RepeatEncodingValue = repeatValue;
                decodeCtx->Encoding = ByteStreamEncoding.Repeat;
                decompCtx.BitStream0.BitOffset = bitOffset;
                decompCtx.BitStream0.Remainder = remainder;
                return;
            }

            bitCount = count + 1;
        }

        decodeCtx->Encoding = (ByteStreamEncoding)(remainder >> 0x3f);
        decodeCtx->MaxIndexBitSize = (uint)((remainder >> 0x3b) & 0xf);
        decompCtx.BitStream0.BitOffset = bitOffset - 5;
        decompCtx.BitStream0.Remainder = remainder << 5;

        if (decodeCtx->Encoding == ByteStreamEncoding.Table)
            GenDecodingTable0(decodeCtx->DecodingTable, (int)bitCount, (int)decodeCtx->MaxIndexBitSize, decompCtx);
        else
            GenDecodingTable1(decodeCtx->DecodingTable, decompCtx, (int)bitCount, (int)decodeCtx->MaxIndexBitSize);
    }

    private static void DecodeFunction0<T>(Encoding1Struct* ctx, T* output, int count, int elementSize,
                                           ref BufferView buffer, ushort* tbl, uint bitSize)
        where T : unmanaged, IBinaryInteger<T>
    {
        byte* inStream = buffer.Ptr + buffer.Offset;
        uint unk = ctx->Field20 & 0xf;
        if (unk != 0xf)
        {
            unk ^= 0xf;
            while (unk != 0)
            {
                byte b = *inStream++;
                uint nbytes = (uint)(b & 0xf);
                ulong value = (ulong)(b >> 4);
                if (nbytes != 0)
                {
                    if (nbytes > 3)
                    {
                        for (int i = (b & 3) - (int)nbytes; i != 0; i += 4)
                        {
                            value = (value << 0x10) | ((ulong)inStream[0] << 8) | inStream[1];
                            value = (value << 0x10) | ((ulong)inStream[2] << 8) | inStream[3];
                            inStream += 4;
                        }
                    }
                    if ((b & 3) != 0)
                    {
                        for (uint i = (uint)(b & 3); i != 0; --i)
                        {
                            value = (value << 8) | *inStream++;
                        }
                    }
                }
                ctx->IndexMasks[Bits.Clz(unk & (0u - unk)) ^ 0x1f] = value + 0x80000000;
                unk = (unk & (0u - unk)) ^ unk;
            }
            ctx->Field20 |= 0xf;
        }

        uint mask = (uint)~(-1 << (int)(bitSize & 0x1f));
        uint* inStream32 = (uint*)inStream;
        int stride = elementSize << 2;
        T* outPtr = output;
        uint* src = (uint*)tbl;

        if (count > 3)
        {
            ulong v0 = ctx->IndexMasks[0];
            ulong v1 = ctx->IndexMasks[1];
            ulong v2 = ctx->IndexMasks[2];
            ulong v3 = ctx->IndexMasks[3];
            for (uint i = (uint)(count >> 2); i != 0; --i)
            {
                uint index0 = (uint)v0 & mask;
                uint value0 = src[index0];
                outPtr[0] = T.CreateTruncating(tbl[0x1000 + index0]);
                v0 = ((v0 >> (int)(bitSize & 0x3f)) * (value0 >> 0x10)) + (value0 & 0xffff);

                uint index1 = (uint)v1 & mask;
                uint value1 = src[index1];
                outPtr[elementSize] = T.CreateTruncating(tbl[0x1000 + index1]);
                v1 = ((v1 >> (int)(bitSize & 0x3f)) * (value1 >> 0x10)) + (value1 & 0xffff);

                uint index2 = (uint)v2 & mask;
                uint value2 = src[index2];
                outPtr[elementSize * 2] = T.CreateTruncating(tbl[0x1000 + index2]);
                v2 = ((v2 >> (int)(bitSize & 0x3f)) * (value2 >> 0x10)) + (value2 & 0xffff);

                uint index3 = (uint)v3 & mask;
                uint value3 = src[index3];
                outPtr[elementSize * 3] = T.CreateTruncating(tbl[0x1000 + index3]);
                v3 = ((v3 >> (int)(bitSize & 0x3f)) * (value3 >> 0x10)) + (value3 & 0xffff);

                if ((v0 >> 0x1f) == 0)
                    v0 = *inStream32++ | (v0 << 0x20);

                if ((v1 >> 0x1f) == 0)
                    v1 = *inStream32++ | (v1 << 0x20);

                if ((v2 >> 0x1f) == 0)
                    v2 = *inStream32++ | (v2 << 0x20);

                if ((v3 >> 0x1f) == 0)
                    v3 = *inStream32++ | (v3 << 0x20);

                outPtr += stride;
            }
            ctx->IndexMasks[0] = v0;
            ctx->IndexMasks[1] = v1;
            ctx->IndexMasks[2] = v2;
            ctx->IndexMasks[3] = v3;
        }

        int vi = 0;
        for (uint i = (uint)(count & 3); i != 0; --i)
        {
            uint index = (uint)ctx->IndexMasks[vi] & mask;
            uint value = src[index];
            *outPtr = T.CreateTruncating(tbl[0x1000 + index]);
            outPtr += elementSize;
            ulong nv = ((ctx->IndexMasks[vi] >> (int)(bitSize & 0x3f)) * (value >> 0x10)) + (value & 0xffff);
            if ((nv >> 0x1f) == 0)
                nv = *inStream32++ | (nv << 0x20);
            ctx->IndexMasks[vi] = nv;
            ++vi;
        }

        buffer.Offset = (uint)((byte*)inStream32 - buffer.Ptr);
    }

    private static void DecodeFunction1<T>(T* dst, uint size, uint count, DecompContext ctx, uint* src, uint bitSize)
        where T : unmanaged, IBinaryInteger<T>
    {
        ulong shift = 0x40u - bitSize;
        uint offset0 = size * 2;
        uint offset1 = size * 3;

        T* outBuf1 = dst + size;
        T* outBuf2 = dst + offset0;

        uint bitOffset0 = ctx.BitStream0.BitOffset;
        uint bitOffset1 = ctx.BitStream1.BitOffset;
        uint bitOffset2 = ctx.BitStream2.BitOffset;
        ulong rem0 = ctx.BitStream0.Remainder;
        ulong rem1 = ctx.BitStream1.Remainder;
        ulong rem2 = ctx.BitStream2.Remainder;

        T* outp = dst;
        if (count > 0xb)
        {
            ulong offset2 = (ulong)offset1 * 2;
            ulong offset3 = (ulong)offset1 * 3;
            for (uint i = count / 0xc; i != 0; --i)
            {
                ulong value0 = rem0 | ctx.BitStream0.ReadRaw();
                ulong value1 = rem1 | ctx.BitStream1.ReadRawForwards();
                ulong value2 = rem2 | ctx.BitStream2.ReadRaw();

                uint v0 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                uint v1 = src[(uint)(value1 >> (int)(shift & 0x3f))];
                uint v2 = src[(uint)(value2 >> (int)(shift & 0x3f))];

                outp[0] = T.CreateTruncating(v0);
                v0 >>= 0x10;
                value0 <<= (int)(v0 & 0x3f);

                outp[size] = T.CreateTruncating(v1);
                v1 >>= 0x10;
                value1 <<= (int)(v1 & 0x3f);

                outp[offset0] = T.CreateTruncating(v2);
                v2 >>= 0x10;
                value2 <<= (int)(v2 & 0x3f);

                uint v3 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                uint v4 = src[(uint)(value1 >> (int)(shift & 0x3f))];
                uint v5 = src[(uint)(value2 >> (int)(shift & 0x3f))];

                outp[offset1] = T.CreateTruncating(v3);
                v3 >>= 0x10;
                value0 <<= (int)(v3 & 0x3f);

                outp[size + offset1] = T.CreateTruncating(v4);
                v4 >>= 0x10;
                value1 <<= (int)(v4 & 0x3f);

                outp[offset0 + offset1] = T.CreateTruncating(v5);
                v5 >>= 0x10;
                value2 <<= (int)(v5 & 0x3f);

                uint v6 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                uint v7 = src[(uint)(value1 >> (int)(shift & 0x3f))];
                uint v8 = src[(uint)(value2 >> (int)(shift & 0x3f))];

                outp[offset2] = T.CreateTruncating(v6);
                v6 >>= 0x10;
                value0 <<= (int)(v6 & 0x3f);

                outp[size + offset2] = T.CreateTruncating(v7);
                v7 >>= 0x10;
                value1 <<= (int)(v7 & 0x3f);

                outp[offset0 + offset2] = T.CreateTruncating(v8);
                v8 >>= 0x10;
                value2 <<= (int)(v8 & 0x3f);

                uint v9 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                uint v10 = src[(uint)(value1 >> (int)(shift & 0x3f))];
                uint v11 = src[(uint)(value2 >> (int)(shift & 0x3f))];

                outp[offset3] = T.CreateTruncating(v9);
                v9 >>= 0x10;
                value0 <<= (int)(v9 & 0x3f);

                outp[size + offset3] = T.CreateTruncating(v10);
                v10 >>= 0x10;
                value1 <<= (int)(v10 & 0x3f);

                outp[offset0 + offset3] = T.CreateTruncating(v11);
                v11 >>= 0x10;
                value2 <<= (int)(v11 & 0x3f);

                bitOffset0 = (bitOffset0 | 0x38) - (v0 + v3 + v6 + v9);
                bitOffset1 = (bitOffset1 | 0x38) - (v1 + v4 + v7 + v10);
                bitOffset2 = (bitOffset2 | 0x38) - (v2 + v5 + v8 + v11);

                outp += offset1 * 4;
                ctx.BitStream0.BitOffset = bitOffset0;
                ctx.BitStream1.BitOffset = bitOffset1;
                ctx.BitStream2.BitOffset = bitOffset2;

                rem0 = value0;
                rem1 = value1;
                rem2 = value2;
            }

            outBuf1 = outp + size;
            outBuf2 = outp + offset0;
        }

        uint remaining = count % 0xc;
        if (remaining != 0)
        {
            ulong value0 = rem0 | ctx.BitStream0.ReadRaw();
            ulong value1 = rem1 | ctx.BitStream1.ReadRawForwards();
            ulong value2 = rem2 | ctx.BitStream2.ReadRaw();

            bitOffset0 |= 0x38;
            bitOffset1 |= 0x38;
            bitOffset2 |= 0x38;

            for (; remaining > 3; remaining -= 3)
            {
                uint v0 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                uint v1 = src[(uint)(value1 >> (int)(shift & 0x3f))];
                uint v2 = src[(uint)(value2 >> (int)(shift & 0x3f))];

                outp[0] = T.CreateTruncating(v0);
                v0 >>= 0x10;
                value0 <<= (int)(v0 & 0x3f);

                outBuf1[0] = T.CreateTruncating(v1);
                v1 >>= 0x10;
                value1 <<= (int)(v1 & 0x3f);

                outBuf2[0] = T.CreateTruncating(v2);
                v2 >>= 0x10;
                value2 <<= (int)(v2 & 0x3f);

                bitOffset0 -= v0;
                bitOffset1 -= v1;
                bitOffset2 -= v2;

                rem0 = value0;
                rem1 = value1;
                rem2 = value2;

                outp += offset1;
                outBuf1 += offset1;
                outBuf2 += offset1;
            }

            rem0 = value0;
            rem1 = value1;
            rem2 = value2;

            {
                uint v0 = src[(uint)(value0 >> (int)(shift & 0x3f))];
                outp[0] = T.CreateTruncating(v0);
                v0 >>= 0x10;
                value0 <<= (int)(v0 & 0x3f);
                rem0 = value0;
                bitOffset0 -= v0;
            }

            --remaining;
            if (remaining != 0)
            {
                uint v = src[(uint)(rem1 >> (int)(shift & 0x3f))];
                outBuf1[0] = T.CreateTruncating(v);
                v >>= 0x10;
                rem1 <<= (int)(v & 0x3f);
                bitOffset1 -= v;

                --remaining;
                if (remaining != 0)
                {
                    uint v2 = src[(uint)(rem2 >> (int)(shift & 0x3f))];
                    outBuf2[0] = T.CreateTruncating(v2);
                    v2 >>= 0x10;
                    rem2 <<= (int)(v2 & 0x3f);
                    bitOffset2 -= v2;
                }
            }
        }

        ctx.BitStream0.BitOffset = bitOffset0;
        ctx.BitStream1.BitOffset = bitOffset1;
        ctx.BitStream2.BitOffset = bitOffset2;
        ctx.BitStream0.Remainder = rem0;
        ctx.BitStream1.Remainder = rem1;
        ctx.BitStream2.Remainder = rem2;
    }

    private static void MemSet<T>(T* dst, uint value, uint count) where T : unmanaged, IBinaryInteger<T>
    {
        *dst = T.CreateTruncating(value);
        dst[count - 1] = T.CreateTruncating(value);
        if (count > 2)
        {
            dst[1] = T.CreateTruncating(value);
            dst[2] = T.CreateTruncating(value);
            dst[count - 2] = T.CreateTruncating(value);
            dst[count - 3] = T.CreateTruncating(value);
            if (count > 6)
            {
                if (typeof(T) == typeof(byte))
                {
                    dst[3] = T.CreateTruncating(value);
                    dst[count - 4] = T.CreateTruncating(value);
                    if (count > 8)
                    {
                        uint v = (uint)((byte)value * 0x1010101);
                        uint offset = (uint)(-(long)(nint)dst) & 3;
                        uint* ptr = (uint*)((byte*)dst + offset);
                        uint num = (count - offset) & 0xfffffffc;
                        uint index = num >> 2;
                        *ptr = v;
                        ptr[index - 1] = v;
                        if (num > 8)
                        {
                            ptr[1] = v;
                            ptr[2] = v;
                            ptr[index - 2] = v;
                            ptr[index - 3] = v;
                            if (num > 0x18)
                            {
                                ptr[3] = v;
                                ptr[4] = v;
                                ptr[5] = v;
                                ptr[6] = v;
                                ptr[index - 4] = v;
                                ptr[index - 5] = v;
                                ptr[index - 6] = v;
                                ptr[index - 7] = v;
                                if (num > 0x38)
                                {
                                    ulong v64 = value * 0x101010101010101UL;
                                    uint off2 = (uint)(((nint)ptr & 4) | 0x18);
                                    ulong* ptr64 = (ulong*)((byte*)ptr + off2);
                                    for (int i = -(int)((num - off2) >> 5); i != 0; ++i)
                                    {
                                        ptr64[0] = v64;
                                        ptr64[1] = v64;
                                        ptr64[2] = v64;
                                        ptr64[3] = v64;
                                        ptr64 += 4;
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    uint v = (uint)((ushort)value * 0x10001);
                    uint offset = (uint)(-(long)(nint)dst) & 3;
                    uint* ptr = (uint*)((byte*)dst + offset);
                    uint num = ((count * 2) - offset) & 0xfffffffc;
                    uint index = num >> 2;
                    *ptr = v;
                    ptr[index - 1] = v;
                    if (num > 8)
                    {
                        ptr[1] = v;
                        ptr[2] = v;
                        ptr[index - 2] = v;
                        ptr[index - 3] = v;
                        if (num > 0x18)
                        {
                            ptr[3] = v;
                            ptr[4] = v;
                            ptr[5] = v;
                            ptr[6] = v;
                            ptr[index - 4] = v;
                            ptr[index - 5] = v;
                            ptr[index - 6] = v;
                            ptr[index - 7] = v;
                            if (num > 0x38)
                            {
                                ulong v64 = value * 0x1000100010001UL;
                                uint off2 = (uint)(((nint)ptr & 4) | 0x18);
                                ulong* ptr64 = (ulong*)((byte*)ptr + off2);
                                for (int i = -(int)((num - off2) >> 5); i != 0; ++i)
                                {
                                    ptr64[0] = v64;
                                    ptr64[1] = v64;
                                    ptr64[2] = v64;
                                    ptr64[3] = v64;
                                    ptr64 += 4;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static void DecodeFunction2<T>(T* dst, uint value, uint count, uint stride)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (stride != 1)
        {
            for (uint i = count & 3; i != 0; --i)
            {
                *dst = T.CreateTruncating(value);
                dst += stride;
            }
            if (count > 3)
            {
                for (uint i = count >> 2; i != 0; --i)
                {
                    dst[0] = T.CreateTruncating(value);
                    dst[stride] = T.CreateTruncating(value);
                    dst[stride * 2] = T.CreateTruncating(value);
                    dst[stride * 3] = T.CreateTruncating(value);
                    dst += stride * 4;
                }
            }
        }
        else
        {
            MemSet(dst, value, count);
        }
    }

    public static void DecodeTable<T>(T* dst, int size, int count, DecodingContext* decodeCtx,
                                      ref BufferView view, DecompContext decompCtx)
        where T : unmanaged, IBinaryInteger<T>
    {
        switch (decodeCtx->Encoding)
        {
            case ByteStreamEncoding.Table:
                DecodeFunction0(&decodeCtx->Enc1, dst, count, size, ref view, decodeCtx->DecodingTable, decodeCtx->MaxIndexBitSize);
                break;
            case ByteStreamEncoding.SingleStream:
                DecodeFunction1(dst, (uint)size, (uint)count, decompCtx, (uint*)decodeCtx->DecodingTable, decodeCtx->MaxIndexBitSize);
                break;
            default:
                DecodeFunction2(dst, decodeCtx->RepeatEncodingValue, (uint)count, (uint)size);
                break;
        }
    }

    public static void DecodeByteStream<T>(T* dst, int totalSize, int tblCount, uint elementSize,
                                           DecodingContext* decodeCtx, DecompContext decompCtx)
        where T : unmanaged, IBinaryInteger<T>
    {
        BufferView view;
        view.Field08 = 0;
        view.Offset = 0;
        view.Ptr = decompCtx.CurrentPos;

        if (tblCount < 1)
        {
            decompCtx.CurrentPos = view.Ptr;
            return;
        }

        uint bytesPerTable = typeof(T) == typeof(byte)
            ? (uint)(totalSize / tblCount)
            : (uint)(((totalSize >> 1) & 0x7fffffff) / tblCount);
        uint alignAdd = (uint)~(-1 << (int)(elementSize & 0x1f));
        int index = 0;
        uint size = 0;
        while (index < tblCount)
        {
            GenDecodingTable(decodeCtx, decompCtx);
            uint bitOffset = decompCtx.BitStream0.BitOffset;
            ulong remainder = decompCtx.BitStream0.Remainder;
            ulong value = decompCtx.BitStream0.ReadRaw() | remainder;
            int nbits = (int)((Bits.Clz(value) << 1) | 1);
            decompCtx.BitStream0.BitOffset = (bitOffset | 0x38) - (uint)nbits;
            decompCtx.BitStream0.Remainder = value << nbits;
            int remaining = (int)(value >> (0x40 - nbits));

            do
            {
                int tblSize = remaining << (int)(elementSize & 0x1f);
                int remainingSubTblSize = (int)((bytesPerTable - size + alignAdd) >> (int)(elementSize & 0x1f));
                uint offset = (uint)index + (size * (uint)tblCount);
                uint elementCount = remainingSubTblSize > remaining ? (uint)tblSize : (bytesPerTable - size);
                if (remaining >= remainingSubTblSize)
                    ++index;
                size = remainingSubTblSize > remaining ? (size + (uint)tblSize) : 0;
                remaining -= (int)((elementCount + alignAdd) >> (int)(elementSize & 0x1f));
                DecodeTable(dst + offset, tblCount, (int)elementCount, decodeCtx, ref view, decompCtx);
            } while (remaining > 0);
        }

        decompCtx.CurrentPos = view.Ptr + view.Offset;
    }
}
