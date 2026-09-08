using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace McSharp.Internal;

internal static unsafe class AttributeCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy(byte* dst, byte* src, int n) => Unsafe.CopyBlockUnaligned(dst, src, (uint)n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort* U16(byte* p) => (ushort*)p;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint* U32(byte* p) => (uint*)p;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong* U64(byte* p) => (ulong*)p;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* OutputStart(VertexStreamContext ctx, uint stride)
        => ctx.OutputBuffer + (ctx.AttrOffsets[ctx.AttrIndex] + (ctx.BaseVertexIndex * stride));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T MaskOnly<T>(T value, int shift, T mask) where T : unmanaged, IBinaryInteger<T>
        => ((value >>> shift) & mask) << shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T MaskAdd<T>(T value, int shift, T mask, T add) where T : unmanaged, IBinaryInteger<T>
    {
        uint t = uint.CreateTruncating(((value >>> shift) & mask) + add);
        uint m = uint.CreateTruncating(mask);
        return T.CreateTruncating(t & m) << shift;
    }

    public static void DecodeInternalDeltas<TB, TC, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                        uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TC : INum where TT : IFlag
    {
        int bits = TB.N;
        int cc = TC.N;

        byte* baseValueStream = inputStreams[0];
        byte* valueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);
        byte* refBaseValueStream = TT.V ? inputStreams[2] : null;
        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex++] : 0;
                if (TT.V && offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (bits == 8)
                    {
                        for (int j = 0; j < cc; ++j)
                            output[j] = (byte)(*refBaseValueStream++ + refp[j]);
                    }
                    else if (bits == 10)
                    {
                        ushort base0 = U16(refBaseValueStream)[0];
                        ushort base1 = U16(refBaseValueStream)[1];
                        ushort base2 = U16(refBaseValueStream)[2];
                        uint value = U32(refp)[0];
                        *U32(output) = ((value + base0) & 0x3ff)
                                     | ((value + (uint)(base1 * 0x400)) & 0xffc00)
                                     | ((value + (uint)(base2 * 0x100000)) & 0x3ff00000);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        for (int j = 0; j < cc; ++j)
                        {
                            U16(output)[j] = (ushort)(*U16(refBaseValueStream) + U16(refp)[j]);
                            refBaseValueStream += sizeof(ushort);
                        }
                    }
                }
                else
                {
                    if (bits == 8)
                    {
                        byte sum = 0;
                        for (int j = 1; j < cc; ++j)
                        {
                            byte value = *valueStream++;
                            output[j] = value;
                            sum += value;
                        }
                        output[0] = (byte)(*baseValueStream++ + ~sum);
                    }
                    else if (bits == 10)
                    {
                        ushort value0 = U16(valueStream)[0];
                        ushort value1 = U16(valueStream)[1];
                        ushort baseV = U16(baseValueStream)[0];
                        *U32(output) = (uint)((baseV + ~(value0 + value1)) & 0x3ff)
                                     | ((uint)value0 << 10)
                                     | ((uint)value1 << 20);
                        valueStream += 2 * sizeof(ushort);
                        baseValueStream += sizeof(ushort);
                    }
                    else
                    {
                        ushort sum = 0;
                        for (int j = 1; j < cc; ++j)
                        {
                            ushort value = *U16(valueStream);
                            U16(output)[j] = value;
                            sum += value;
                            valueStream += sizeof(ushort);
                        }

                        output[0] = (byte)(*U16(baseValueStream) + ~sum);
                        baseValueStream += sizeof(ushort);
                    }
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits * cc == 0x1e)
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * cc);
                    output += stride;
                }
                if (TT.V)
                    tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeRaw<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                     uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
    {
        int bits = TB.N;

        byte* valueStream = inputStreams[0];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                if (bits == 2)
                {
                    *U32(output) = (*U32(output) & 0x3fffffff) | ((uint)*valueStream++ << 0x1e);
                }
                else if (bits == 0x1e)
                {
                    *U32(output) = U16(valueStream)[0] | ((uint)U16(valueStream)[1] << 10) | ((uint)U16(valueStream)[2] << 20);
                    valueStream += 3 * sizeof(ushort);
                }
                else
                {
                    Copy(output, valueStream, bits >> 3);
                    valueStream += bits >> 3;
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 2)
                        *U32(output) = (*U32(output - groups->BackRefOffset) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                    else if (bits == 0x1e)
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                    else
                        Copy(output, output - groups->BackRefOffset, bits >> 3);
                    output += stride;
                }
            }
            ++groups;
        }
    }

    public static void DecodeRawCustomSize<TIn, TOut, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                          uint numGroups, byte** inputStreams, int streamsRemaining)
        where TIn : unmanaged, IBinaryInteger<TIn>
        where TOut : unmanaged, IBinaryInteger<TOut>
        where TT : IFlag
    {
        uint flags = ctx.AttrFlags[ctx.AttrIndex];
        uint stride = flags >> 0x18;
        int attrShift = (int)((flags >> 0x10) & 0xff);
        int compShift = (int)((flags >> 8) & 0xff);
        uint compCount = flags & 7;
        TOut copyMask = TOut.CreateTruncating((2UL << (int)((compShift * compCount) - 1)) - 1);
        TOut keepMask = ~(copyMask << attrShift);
        TIn* input = (TIn*)inputStreams[0];
        TOut* output = (TOut*)OutputStart(ctx, stride);
        TIn* refBaseValueStream = TT.V ? (TIn*)inputStreams[1] : null;
        TOut mask = TOut.CreateTruncating(~(-1L << compShift));
        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex++] : 0;
                if (TT.V && offset != 0)
                {
                    TOut value = *(TOut*)((byte*)output - ((offset >> 3) * stride)) >>> attrShift;
                    if (compCount == 1)
                    {
                        *output = (((value & mask) + TOut.CreateTruncating(*refBaseValueStream++)) << attrShift) | (*output & keepMask);
                    }
                    else
                    {
                        TOut acc = TOut.Zero;
                        for (int c = 0; c < (int)compCount; ++c)
                        {
                            TOut comp = (((value >>> (compShift * c)) & mask) + TOut.CreateTruncating(*refBaseValueStream++)) & mask;
                            acc |= comp << (compShift * c);
                        }
                        *output = (acc << attrShift) | (*output & keepMask);
                    }
                }
                else
                {
                    TOut acc = TOut.Zero;
                    for (int c = 0; c < (int)compCount; ++c)
                        acc |= TOut.CreateTruncating(*input++) << (compShift * c);
                    *output = (acc << attrShift) | (*output & keepMask);
                }
                output = (TOut*)((byte*)output + stride);
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    *output = MaskOnly(*(TOut*)((byte*)output - groups->BackRefOffset), attrShift, copyMask) | (*output & keepMask);
                    output = (TOut*)((byte*)output + stride);
                }
                if (TT.V)
                    tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeRawWithTable<TB, TC>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                  uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TC : INum
    {
        int bits = TB.N;
        int cc = TC.N;

        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (bits == 2)
                    {
                        *U32(output) = (*U32(output) & 0x3fffffff) | ((*U32(refp) + (uint)(*refBaseValueStream++ * 0x40000000)) & 0xc0000000);
                    }
                    else if (bits == 8)
                    {
                        for (int j = 0; j < cc; ++j)
                            output[j] = (byte)(*refBaseValueStream++ + refp[j]);
                    }
                    else if (bits == 10)
                    {
                        ushort base0 = U16(refBaseValueStream)[0];
                        ushort base1 = U16(refBaseValueStream)[1];
                        ushort base2 = U16(refBaseValueStream)[2];
                        uint value = U32(refp)[0];
                        *U32(output) = ((value + base0) & 0x3ff)
                                     | ((value + (uint)(base1 * 0x400)) & 0xffc00)
                                     | ((value + (uint)(base2 * 0x100000)) & 0x3ff00000);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else if (bits == 16)
                    {
                        for (int j = 0; j < cc; ++j)
                        {
                            U16(output)[j] = (ushort)(*U16(refBaseValueStream) + U16(refp)[j]);
                            refBaseValueStream += sizeof(ushort);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < cc; ++j)
                        {
                            U32(output)[j] = *U32(refBaseValueStream) + U32(refp)[j];
                            refBaseValueStream += sizeof(uint);
                        }
                    }
                }
                else
                {
                    if (bits == 2)
                    {
                        *U32(output) = (*U32(output) & 0x3fffffff) | ((uint)*valueStream++ << 0x1e);
                    }
                    else if (bits == 10)
                    {
                        *U32(output) = U16(valueStream)[0] | ((uint)U16(valueStream)[1] << 10) | ((uint)U16(valueStream)[2] << 20);
                        valueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        Copy(output, valueStream, (bits >> 3) * cc);
                        valueStream += (bits >> 3) * cc;
                    }
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 2)
                        *U32(output) = (*U32(output - groups->BackRefOffset) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                    else if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * cc);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeXor1<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                      uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
        => DecodeXorNegate<TB>(ctx, vertexCount, groups, numGroups, inputStreams, streamsRemaining, addFlip: true);

    public static void DecodeXor2<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                      uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
        => DecodeXorNegate<TB>(ctx, vertexCount, groups, numGroups, inputStreams, streamsRemaining, addFlip: false);

    private static void DecodeXorNegate<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                            uint numGroups, byte** inputStreams, int streamsRemaining, bool addFlip)
        where TB : INum
    {
        int bits = TB.N;

        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    int f0 = (int)(offset & 1);
                    int f1 = (int)((offset >> 1) & 1);
                    int f2 = (int)((offset >> 2) & 1);
                    int a0 = addFlip ? f0 : 0;
                    int a1 = addFlip ? f1 : 0;
                    int a2 = addFlip ? f2 : 0;

                    if (bits == 8)
                    {
                        output[0] = (byte)(*refBaseValueStream++ + (refp[0] ^ -f0) + a0);
                        output[1] = (byte)(*refBaseValueStream++ + (refp[1] ^ -f1) + a1);
                        output[2] = (byte)(*refBaseValueStream++ + (refp[2] ^ -f2) + a2);
                    }
                    else if (bits == 10)
                    {
                        uint value = *U32(refp);
                        *U32(output) = ((uint)(U16(refBaseValueStream)[0] + (value ^ (uint)-f0) + (uint)a0) & 0x3ff)
                                     | (((uint)(U16(refBaseValueStream)[1] + ((value >> 10) ^ (uint)-f1) + (uint)a1) & 0x3ff) << 10)
                                     | (((uint)(U16(refBaseValueStream)[2] + ((value >> 20) ^ (uint)-f2) + (uint)a2) & 0x3ff) << 20);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + (U16(refp)[0] ^ (ushort)-f0) + a0);
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + (U16(refp)[1] ^ (ushort)-f1) + a1);
                        U16(output)[2] = (ushort)(U16(refBaseValueStream)[2] + (U16(refp)[2] ^ (ushort)-f2) + a2);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                }
                else
                {
                    if (bits == 10)
                    {
                        *U32(output) = U16(valueStream)[0] | ((uint)U16(valueStream)[1] << 10) | ((uint)U16(valueStream)[2] << 20);
                        valueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        Copy(output, valueStream, (bits >> 3) * 3);
                        valueStream += (bits >> 3) * 3;
                    }
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeXor3<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                      uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
    {
        int bits = TB.N;
        int limit = bits == 8 ? 0x7f : 0x7fff;

        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    int value0, value1;
                    if (bits == 8)
                    {
                        value0 = (refp[0] ^ (int)(offset & 1)) + (int)(offset & 1);
                        value1 = (refp[1] ^ (int)((offset >> 1) & 1)) + (int)((offset >> 1) & 1);
                    }
                    else
                    {
                        value0 = (U16(refp)[0] ^ (int)(offset & 1)) + (int)(offset & 1);
                        value1 = (U16(refp)[1] ^ (int)((offset >> 1) & 1)) + (int)((offset >> 1) & 1);
                    }
                    if (((offset >> 2) & 1) != 0)
                    {
                        int value = ((value0 >> 0x1f) + (value1 >> 0x1f)) - (((value0 >> 0x1f) ^ value0) + ((value1 >> 0x1f) ^ value1));
                        value0 += (value0 < 0) ? (-limit - value) : (limit + value);
                        value1 += (value1 < 0) ? (-limit - value) : (limit + value);
                    }
                    if (bits == 8)
                    {
                        output[0] = (byte)(*refBaseValueStream++ + value0);
                        output[1] = (byte)(*refBaseValueStream++ + value1);
                    }
                    else
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + value0);
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + value1);
                        refBaseValueStream += 2 * sizeof(ushort);
                    }
                }
                else
                {
                    Copy(output, valueStream, (bits >> 3) * 2);
                    valueStream += (bits >> 3) * 2;
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, (bits >> 3) * 2);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeXor4<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                      uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
    {
        int bits = TB.N;
        int span = bits == 8 ? 0x100 : 0x10000;
        int maxVal = bits == 8 ? 0xff : 0xffff;

        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    int value0, value1;
                    if (bits == 8)
                    {
                        value0 = (((refp[0] * 2) - maxVal) ^ (int)(offset & 1)) + (int)(offset & 1);
                        value1 = (((refp[1] * 2) - maxVal) ^ (int)((offset >> 1) & 1)) + (int)((offset >> 1) & 1);
                    }
                    else
                    {
                        value0 = (((U16(refp)[0] * 2) - maxVal) ^ (int)(offset & 1)) + (int)(offset & 1);
                        value1 = (((U16(refp)[1] * 2) - maxVal) ^ (int)((offset >> 1) & 1)) + (int)((offset >> 1) & 1);
                    }
                    if (((offset >> 2) & 1) != 0)
                    {
                        int value = ((value0 >> 0x1f) + (value1 >> 0x1f)) - (((value0 >> 0x1f) ^ value0) + ((value1 >> 0x1f) ^ value1));
                        value0 += (value0 < 0) ? (-span - value) : (span + value);
                        value1 += (value1 < 0) ? (-span - value) : (span + value);
                    }
                    if (bits == 8)
                    {
                        output[0] = (byte)(*refBaseValueStream++ + (((byte)value0 >> 1) ^ 0x80));
                        output[1] = (byte)(*refBaseValueStream++ + (((byte)value1 >> 1) ^ 0x80));
                    }
                    else
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + (((ushort)value0 >> 1) ^ 0x8000));
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + (((ushort)value1 >> 1) ^ 0x8000));
                        refBaseValueStream += 2 * sizeof(ushort);
                    }
                }
                else
                {
                    Copy(output, valueStream, (bits >> 3) * 2);
                    valueStream += (bits >> 3) * 2;
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, (bits >> 3) * 2);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeXor5<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                      uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
    {
        int bits = TB.N;

        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (bits == 16)
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + (U16(refp)[0] ^ (offset << 0xf)));
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + (U16(refp)[1] ^ ((offset & 2) << 0xe)));
                        U16(output)[2] = (ushort)(U16(refBaseValueStream)[2] + (U16(refp)[2] ^ ((offset & 4) << 0xd)));
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        U32(output)[0] = U32(refBaseValueStream)[0] + (U32(refp)[0] ^ (offset << 0x1f));
                        U32(output)[1] = U32(refBaseValueStream)[1] + (U32(refp)[1] ^ ((offset & 2) << 0x1e));
                        U32(output)[2] = U32(refBaseValueStream)[2] + (U32(refp)[2] ^ ((offset & 4) << 0x1d));
                        refBaseValueStream += 3 * sizeof(uint);
                    }
                }
                else
                {
                    Copy(output, valueStream, (bits >> 3) * 3);
                    valueStream += (bits >> 3) * 3;
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeXorCustomSize<TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                               uint numGroups, byte** inputStreams, int streamsRemaining)
        where TT : IFlag
    {
        bool isType2 = TT.V;

        uint flags = ctx.AttrFlags[ctx.AttrIndex];
        uint stride = flags >> 0x18;
        int attrShift = (int)((flags >> 0x10) & 0xff);
        int compShift = (int)((flags >> 8) & 0xff);
        ulong compMask = (ulong)~(-1L << compShift);
        ulong copyMask = isType2
            ? (2UL << ((compShift * 3) - 1)) - 1
            : (2UL << ((compShift * 2) - 1)) - 1;
        ulong keepMask = ~(copyMask << attrShift);
        byte* valueStream = inputStreams[0];
        byte* refBaseValueStream = inputStreams[1];
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = ctx.VertexBufferTable[tableIndex++];
                if (offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (isType2)
                    {
                        ulong value = *U64(refp) >> attrShift;
                        int v0 = (int)(offset & 1);
                        int v1 = (int)((offset >> 1) & 1);
                        int v2 = (int)((offset >> 2) & 1);
                        ulong value0 = ((((value & compMask) ^ (ulong)(long)-v0) & compMask) + U16(refBaseValueStream)[0]) & compMask;
                        ulong value1 = (((((value >> compShift) & compMask) ^ (ulong)(long)-v1) & compMask) + U16(refBaseValueStream)[1]) & compMask;
                        ulong value2 = (((((value >> (compShift * 2)) & compMask) ^ (ulong)(long)-v2) & compMask) + U16(refBaseValueStream)[2]) & compMask;
                        *U64(output) = ((value0 | (value1 << compShift) | (value2 << (compShift * 2))) << attrShift) | (*U64(output) & keepMask);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        int add0 = -1 << (compShift - 1);
                        int add1 = 1 << (compShift - 1);
                        uint value = *U32(refp) >> attrShift;
                        int value0 = ((int)((((value & (uint)compMask) + (uint)add0) * 2) | 1) ^ -(int)(offset & 1)) + (int)(offset & 1);
                        int value1 = ((int)(((((value >> compShift) & (uint)compMask) + (uint)add0) * 2) | 1) ^ -(int)((offset >> 1) & 1)) + (int)((offset >> 1) & 1);
                        if (((offset >> 2) & 1) != 0)
                        {
                            int v = (value0 >> 0x1f) + (value1 >> 0x1f) + (1 << compShift) - (((value0 >> 0x1f) ^ value0) + ((value1 >> 0x1f) ^ value1));
                            value0 += (value0 < 0) ? -v : v;
                            value1 += (value1 < 0) ? -v : v;
                        }
                        uint b0 = *refBaseValueStream++;
                        uint b1 = *refBaseValueStream++;
                        *U32(output) = ((((uint)(add1 + (value0 >> 1)) + b0) & (uint)compMask)
                                        | ((((uint)(add1 + (value1 >> 1)) + b1) & (uint)compMask) << compShift)) << attrShift
                                     | (*U32(output) & (uint)keepMask);
                    }
                }
                else
                {
                    if (isType2)
                    {
                        ulong value0 = U16(valueStream)[0];
                        ulong value1 = U16(valueStream)[1];
                        ulong value2 = U16(valueStream)[2];
                        *U64(output) = ((value0 | (value1 << compShift) | (value2 << (compShift * 2))) << attrShift) | (*U64(output) & keepMask);
                        valueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        uint value0 = *valueStream++;
                        uint value1 = *valueStream++;
                        *U32(output) = ((value0 | (value1 << compShift)) << attrShift) | (*U32(output) & (uint)keepMask);
                    }
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (isType2)
                        *U64(output) = (((*U64(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift) | (*U64(output) & keepMask);
                    else
                        *U32(output) = (((*U32(output - groups->BackRefOffset) >> attrShift) & (uint)copyMask) << attrShift) | (*U32(output) & (uint)keepMask);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeDeltas<TB, TC, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TC : INum where TT : IFlag
    {
        int bits = TB.N;
        int cc = TC.N;

        byte* valueStream = inputStreams[0];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);
        byte* refBaseValueStream = TT.V ? inputStreams[1] : null;
        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex] : 0;
                if (TT.V && offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (bits == 2)
                    {
                        *U32(output) = (((uint)(*refBaseValueStream++ * 0x40000000) + *U32(refp)) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                    }
                    else if (bits == 8)
                    {
                        for (int j = 0; j < cc; ++j)
                            output[j] = (byte)(refp[j] + *refBaseValueStream++);
                    }
                    else if (bits == 10)
                    {
                        uint value = *U32(refp);
                        *U32(output) = ((U16(refBaseValueStream)[0] + value) & 0x3ff)
                                     | (((uint)(U16(refBaseValueStream)[1] * 0x400) + value) & 0xffc00)
                                     | (((uint)U16(refBaseValueStream)[2] * 0x100000 + value) & 0x3ff00000);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        for (int j = 0; j < cc; ++j)
                        {
                            U16(output)[j] = (ushort)(U16(refp)[j] + *U16(refBaseValueStream));
                            refBaseValueStream += sizeof(ushort);
                        }
                    }
                }
                else if (tableIndex == 0)
                {
                    if (bits == 2)
                    {
                        *U32(output) = (*U32(output) & 0x3fffffff) | ((uint)*valueStream++ << 0x1e);
                    }
                    else if (bits == 10)
                    {
                        *U32(output) = U16(valueStream)[0] | ((uint)U16(valueStream)[1] << 10) | ((uint)U16(valueStream)[2] << 20);
                        valueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        Copy(output, valueStream, (bits >> 3) * cc);
                        valueStream += (bits >> 3) * cc;
                    }
                }
                else
                {
                    byte* prev = output - stride;
                    if (bits == 2)
                    {
                        *U32(output) = (((uint)(*valueStream++ * 0x40000000) + *U32(prev)) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                    }
                    else if (bits == 8)
                    {
                        for (int j = 0; j < cc; ++j)
                            output[j] = (byte)(prev[j] + *valueStream++);
                    }
                    else if (bits == 10)
                    {
                        uint value = *U32(prev);
                        *U32(output) = ((U16(valueStream)[0] + value) & 0x3ff)
                                     | (((uint)(U16(valueStream)[1] * 0x400) + value) & 0xffc00)
                                     | (((uint)U16(valueStream)[2] * 0x100000 + value) & 0x3ff00000);
                        valueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        for (int j = 0; j < cc; ++j)
                        {
                            U16(output)[j] = (ushort)(U16(prev)[j] + *U16(valueStream));
                            valueStream += sizeof(ushort);
                        }
                    }
                }
                output += stride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 2)
                        *U32(output) = (*U32(output - groups->BackRefOffset) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                    else if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * cc);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeDeltasCustomSize<TIn, TOut, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                             uint numGroups, byte** inputStreams, int streamsRemaining)
        where TIn : unmanaged, IBinaryInteger<TIn>
        where TOut : unmanaged, IBinaryInteger<TOut>
        where TT : IFlag
    {
        uint flags = ctx.AttrFlags[ctx.AttrIndex];
        uint stride = flags >> 0x18;
        int attrShift = (int)((flags >> 0x10) & 0xff);
        int compShift = (int)((flags >> 8) & 0xff);
        uint compCount = flags & 7;
        TOut copyMask = TOut.CreateTruncating((2UL << (int)((compShift * compCount) - 1)) - 1);
        TOut keepMask = ~(copyMask << attrShift);
        TIn* input = (TIn*)inputStreams[0];
        TOut* output = (TOut*)OutputStart(ctx, stride);
        TIn* refBaseValueStream = TT.V ? (TIn*)inputStreams[1] : null;
        TOut mask = TOut.CreateTruncating(~(-1L << compShift));
        uint tableIndex = 0;

        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex] : 0;
                if (TT.V && offset != 0)
                {
                    TOut value = *(TOut*)((byte*)output - ((offset >> 3) * stride));
                    TOut acc = TOut.Zero;
                    for (int c = 0; c < (int)compCount; ++c)
                        acc |= MaskAdd(value >>> (compShift * c), attrShift, mask, TOut.CreateTruncating(*refBaseValueStream++)) << (compShift * c);
                    *output = acc | (*output & keepMask);
                }
                else if (tableIndex == 0)
                {
                    TOut acc = TOut.Zero;
                    for (int c = 0; c < (int)compCount; ++c)
                        acc |= TOut.CreateTruncating(*input++) << (compShift * c);
                    *output = (acc << attrShift) | (*output & keepMask);
                }
                else
                {
                    TOut value = *(TOut*)((byte*)output - stride);
                    TOut acc = TOut.Zero;
                    for (int c = 0; c < (int)compCount; ++c)
                        acc |= MaskAdd(value >>> (compShift * c), attrShift, mask, TOut.CreateTruncating(*input++)) << (compShift * c);
                    *output = acc | (*output & keepMask);
                }
                output = (TOut*)((byte*)output + stride);
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    *output = MaskOnly(*(TOut*)((byte*)output - groups->BackRefOffset), attrShift, copyMask) | (*output & keepMask);
                    output = (TOut*)((byte*)output + stride);
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeTriangleDeltas<TB, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                    uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TT : IFlag
    {
        int bits = TB.N;

        byte* deltaValues = inputStreams[0];
        byte* trigDeltas = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);
        byte* refBaseValueStream = TT.V ? inputStreams[2] : null;

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex] : 0;
                if (TT.V && offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    int f0 = (int)(offset & 1);
                    int f1 = (int)((offset >> 1) & 1);
                    int f2 = (int)((offset >> 2) & 1);
                    if (bits == 8)
                    {
                        output[0] = (byte)(*refBaseValueStream++ + (refp[0] ^ -f0));
                        output[1] = (byte)(*refBaseValueStream++ + (refp[1] ^ -f1));
                        output[2] = (byte)(*refBaseValueStream++ + (refp[2] ^ -f2));
                    }
                    else if (bits == 10)
                    {
                        uint value = *U32(refp);
                        *U32(output) = ((uint)(U16(refBaseValueStream)[0] + (value ^ (uint)-f0)) & 0x3ff)
                                     | (((uint)(U16(refBaseValueStream)[1] + ((value >> 10) ^ (uint)-f1)) & 0x3ff) << 10)
                                     | (((uint)(U16(refBaseValueStream)[2] + ((value >> 20) ^ (uint)-f2)) & 0x3ff) << 20);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + (U16(refp)[0] ^ (ushort)-f0));
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + (U16(refp)[1] ^ (ushort)-f1));
                        U16(output)[2] = (ushort)(U16(refBaseValueStream)[2] + (U16(refp)[2] ^ (ushort)-f2));
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                }
                else
                {
                    ulong value = ctx.IndexBufferTable[tableIndex];
                    if ((value & 0x3fffff) != 0)
                    {
                        uint index0 = (uint)(value & 0x3fffff);
                        uint index1 = (uint)((value >> 0x16) & 0x1fffff);
                        uint index2 = (uint)(value >> 0x2b);
                        byte* p0 = output - (index0 * stride);
                        byte* p1 = output - (index1 * stride);
                        byte* p2 = output - (index2 * stride);
                        if (bits == 8)
                        {
                            for (int j = 0; j < 3; ++j)
                                output[j] = (byte)(*trigDeltas++ + p1[j] + p2[j] - p0[j]);
                        }
                        else if (bits == 10)
                        {
                            uint v0 = *U32(p0);
                            uint v1 = *U32(p1);
                            uint v2 = *U32(p2);
                            ushort value0 = (ushort)((U16(trigDeltas)[0] + (v1 & 0x3ff) + (v2 & 0x3ff) - (v0 & 0x3ff)) & 0x3ff);
                            ushort value1 = (ushort)((U16(trigDeltas)[1] + ((v1 >> 10) & 0x3ff) + ((v2 >> 10) & 0x3ff) - ((v0 >> 10) & 0x3ff)) & 0x3ff);
                            ushort value2 = (ushort)((U16(trigDeltas)[2] + ((v1 >> 20) & 0x3ff) + ((v2 >> 20) & 0x3ff) - ((v0 >> 20) & 0x3ff)) & 0x3ff);
                            *U32(output) = value0 | ((uint)value1 << 10) | ((uint)value2 << 20);
                            trigDeltas += 3 * sizeof(ushort);
                        }
                        else
                        {
                            for (int j = 0; j < 3; ++j)
                            {
                                U16(output)[j] = (ushort)(*U16(trigDeltas) + U16(p1)[j] + U16(p2)[j] - U16(p0)[j]);
                                trigDeltas += sizeof(ushort);
                            }
                        }
                    }
                    else if (tableIndex == 0)
                    {
                        if (bits == 8)
                        {
                            for (int j = 0; j < 3; ++j)
                                output[j] = *deltaValues++;
                        }
                        else if (bits == 10)
                        {
                            *U32(output) = U16(deltaValues)[0] | ((uint)U16(deltaValues)[1] << 10) | ((uint)U16(deltaValues)[2] << 20);
                            deltaValues += 3 * sizeof(ushort);
                        }
                        else
                        {
                            for (int j = 0; j < 3; ++j)
                            {
                                U16(output)[j] = *U16(deltaValues);
                                deltaValues += sizeof(ushort);
                            }
                        }
                    }
                    else
                    {
                        byte* prev = output - stride;
                        if (bits == 8)
                        {
                            for (int j = 0; j < 3; ++j)
                                output[j] = (byte)(*deltaValues++ + prev[j]);
                        }
                        else if (bits == 10)
                        {
                            ushort value0 = U16(deltaValues)[0];
                            ushort value1 = U16(deltaValues)[1];
                            ushort value2 = U16(deltaValues)[2];
                            uint baseV = *U32(prev);
                            *U32(output) = (uint)(value0 + (baseV & 0x3ff))
                                         | ((uint)(value1 + ((baseV >> 10) & 0x3ff)) << 10)
                                         | ((uint)(value2 + ((baseV >> 20) & 0x3ff)) << 20);
                            deltaValues += 3 * sizeof(ushort);
                        }
                        else
                        {
                            for (int j = 0; j < 3; ++j)
                            {
                                U16(output)[j] = (ushort)(*U16(deltaValues) + U16(prev)[j]);
                                deltaValues += sizeof(ushort);
                            }
                        }
                    }
                }
                output += stride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset);
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeTriangleDeltasCustomSize<TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                          uint numGroups, byte** inputStreams, int streamsRemaining)
        where TT : IFlag
    {
        ushort* deltaValues = (ushort*)inputStreams[0];
        ushort* trigDeltas = (ushort*)inputStreams[1];
        uint flags = ctx.AttrFlags[ctx.AttrIndex];
        uint stride = flags >> 0x18;
        int attrShift = (int)((flags >> 0x10) & 0xff);
        int compSize = (int)((flags >> 8) & 0xff);
        uint compMask = (uint)~(-1 << compSize);
        ulong copyMask = (ulong)~(-1L << (compSize * 3));
        ulong keepMask = ~(copyMask << attrShift);
        byte* output = OutputStart(ctx, stride);
        ushort* refBaseValueStream = TT.V ? (ushort*)inputStreams[2] : null;

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex] : 0;
                if (TT.V && offset != 0)
                {
                    ulong value = *U64(output - ((offset >> 3) * stride)) >> attrShift;
                    int v0 = (int)(offset & 1);
                    int v1 = (int)((offset >> 1) & 1);
                    int v2 = (int)((offset >> 2) & 1);
                    ulong value0 = ((((value & compMask) ^ (ulong)(long)-v0) & compMask) + refBaseValueStream[0]) & compMask;
                    ulong value1 = (((((value >> compSize) & compMask) ^ (ulong)(long)-v1) & compMask) + refBaseValueStream[1]) & compMask;
                    ulong value2 = (((((value >> (compSize * 2)) & compMask) ^ (ulong)(long)-v2) & compMask) + refBaseValueStream[2]) & compMask;
                    *U64(output) = ((value0 | (value1 << compSize) | (value2 << (compSize * 2))) << attrShift) | (*U64(output) & keepMask);

                    refBaseValueStream += 3;
                }
                else
                {
                    ulong value = ctx.IndexBufferTable[tableIndex];
                    if ((value & 0x3fffff) != 0)
                    {
                        uint index0 = (uint)(value & 0x3fffff);
                        uint index1 = (uint)((value >> 0x16) & 0x1fffff);
                        uint index2 = (uint)(value >> 0x2b);
                        ulong v0 = *U64(output - (index0 * stride)) >> attrShift;
                        ulong v1 = *U64(output - (index1 * stride)) >> attrShift;
                        ulong v2 = *U64(output - (index2 * stride)) >> attrShift;
                        ulong value0 = (*trigDeltas++ + ((v1 & compMask) + (v2 & compMask) - (v0 & compMask))) & compMask;
                        ulong value1 = (*trigDeltas++ + (((v1 >> compSize) & compMask) + ((v2 >> compSize) & compMask) - ((v0 >> compSize) & compMask))) & compMask;
                        ulong value2 = (*trigDeltas++ + (((v1 >> (compSize * 2)) & compMask) + ((v2 >> (compSize * 2)) & compMask) - ((v0 >> (compSize * 2)) & compMask))) & compMask;
                        *U64(output) = ((value0 | (value1 << compSize) | (value2 << (compSize * 2))) << attrShift) | (*U64(output) & keepMask);
                    }
                    else if (tableIndex == 0)
                    {
                        ulong value0 = *deltaValues++ & compMask;
                        ulong value1 = *deltaValues++ & compMask;
                        ulong value2 = *deltaValues++ & compMask;
                        *U64(output) = ((value0 | (value1 << compSize) | (value2 << (compSize * 2))) << attrShift) | (*U64(output) & keepMask);
                    }
                    else
                    {
                        ulong prev = *U64(output - stride) >> attrShift;
                        ulong value0 = (*deltaValues++ + (prev & compMask)) & compMask;
                        ulong value1 = (*deltaValues++ + ((prev >> compSize) & compMask)) & compMask;
                        ulong value2 = (*deltaValues++ + ((prev >> (compSize * 2)) & compMask)) & compMask;
                        *U64(output) = ((value0 | (value1 << compSize) | (value2 << (compSize * 2))) << attrShift) | (*U64(output) & keepMask);
                    }
                }
                output += stride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    *U64(output) = (((*U64(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift) | (*U64(output) & keepMask);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    private static void WriteHalfFloatDeltas(ushort* output, byte** inputStreams, int componentCount, Vec4h* value)
    {
        byte* signBits = inputStreams[0];
        byte* exponentBitsPos = inputStreams[1];
        byte* exponentBitsNeg = inputStreams[2];
        ushort* mantissaBits0 = (ushort*)inputStreams[3];
        ushort* mantissaBits1 = (ushort*)inputStreams[4];

        for (int i = 0; i < componentCount; ++i)
        {
            byte sign = *signBits++;
            byte exponent = sign != 0 ? *exponentBitsNeg++ : *exponentBitsPos++;
            bool cond = exponent + sign == 0;
            ushort v = cond ? *mantissaBits0++ : *mantissaBits1++;
            ushort mantissa = cond ? (ushort)(((-(v & 1) ^ (v >> 1)) + value->Raw[i]) & 0x3ff) : v;

            output[i] = (ushort)(((value->Raw[i] + (exponent * 0x400)) & 0x7c00)
                               | ((value->Raw[i] & 0x8000) ^ (sign << 0xf))
                               | mantissa);
        }

        inputStreams[0] = signBits;
        inputStreams[1] = exponentBitsPos;
        inputStreams[2] = exponentBitsNeg;
        inputStreams[3] = (byte*)mantissaBits0;
        inputStreams[4] = (byte*)mantissaBits1;
    }

    private static void WriteFloatDeltas(uint* output, byte** inputStreams, int componentCount, Vec4f* value)
    {
        byte* signBits = inputStreams[0];
        byte* exponentBitsPos = inputStreams[1];
        byte* exponentBitsNeg = inputStreams[2];
        byte* mantissaBits0 = inputStreams[3];
        byte* mantissaBits1 = inputStreams[4];

        for (int i = 0; i < componentCount; ++i)
        {
            byte sign = *signBits++;
            byte exponent = sign != 0 ? *exponentBitsNeg++ : *exponentBitsPos++;
            bool cond = exponent + sign == 0;
            uint v;
            if (cond)
            {
                v = Bits.Swap(*(uint*)mantissaBits0);
                mantissaBits0 += 3;
            }
            else
            {
                v = Bits.Swap(*(uint*)mantissaBits1);
                mantissaBits1 += 3;
            }
            uint mantissa = cond ? (((0u - ((v >> 8) & 1)) ^ (v >> 9)) + value->Raw[i]) & 0x7fffff : v >> 8;

            output[i] = ((value->Raw[i] + (exponent * 0x800000u)) & 0x7f800000)
                      | ((value->Raw[i] & 0x80000000) ^ ((uint)sign << 0x1f))
                      | mantissa;
        }

        inputStreams[0] = signBits;
        inputStreams[1] = exponentBitsPos;
        inputStreams[2] = exponentBitsNeg;
        inputStreams[3] = mantissaBits0;
        inputStreams[4] = mantissaBits1;
    }

    public static void DecodeTriangleFloatDeltas<TB, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                         uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TT : IFlag
    {
        int bits = TB.N;

        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex] : 0;
                if (TT.V && offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    if (bits == 16)
                    {
                        Vec4h values = default;
                        Copy((byte*)&values, refp, 3 * sizeof(ushort));
                        if ((offset & 7) != 0)
                        {
                            values.Raw[0] ^= (ushort)((offset & 1) << 0xf);
                            values.Raw[1] ^= (ushort)((offset & 2) << 0xe);
                            values.Raw[2] ^= (ushort)((offset & 4) << 0xd);
                        }
                        WriteHalfFloatDeltas(U16(output), inputStreams, 3, &values);
                    }
                    else
                    {
                        Vec4f values = default;
                        Copy((byte*)&values, refp, 3 * sizeof(uint));
                        if ((offset & 7) != 0)
                        {
                            values.Raw[0] ^= (offset & 1) << 0x1f;
                            values.Raw[1] ^= (offset & 2) << 0x1e;
                            values.Raw[2] ^= (offset & 4) << 0x1d;
                        }
                        WriteFloatDeltas(U32(output), inputStreams, 3, &values);
                    }
                }
                else
                {
                    ulong value = ctx.IndexBufferTable[tableIndex];
                    if ((value & 0x3fffff) != 0)
                    {
                        uint index0 = (uint)(value & 0x3fffff);
                        uint index1 = (uint)((value >> 0x16) & 0x1fffff);
                        uint index2 = (uint)(value >> 0x2b);
                        byte* p0 = output - (index0 * stride);
                        byte* p1 = output - (index1 * stride);
                        byte* p2 = output - (index2 * stride);
                        if (bits == 16)
                        {
                            Vec4h value0 = default, value1 = default, value2 = default;
                            Copy((byte*)&value0, p0, 3 * sizeof(ushort));
                            Copy((byte*)&value1, p1, 3 * sizeof(ushort));
                            Copy((byte*)&value2, p2, 3 * sizeof(ushort));

                            Vec4h values = default;
                            for (int j = 0; j < 3; ++j)
                            {
                                float r = FloatMath.HalfToFloat(value1.Raw[j]) + FloatMath.HalfToFloat(value2.Raw[j]) - FloatMath.HalfToFloat(value0.Raw[j]);
                                values.Raw[j] = FloatMath.FloatToHalf(r);
                            }
                            WriteHalfFloatDeltas(U16(output), inputStreams, 3, &values);
                        }
                        else
                        {
                            Vec4f values = default;
                            for (int j = 0; j < 3; ++j)
                                values.F[j] = ((float*)p1)[j] + ((float*)p2)[j] - ((float*)p0)[j];
                            WriteFloatDeltas(U32(output), inputStreams, 3, &values);
                        }
                    }
                    else if (tableIndex == 0)
                    {
                        if (bits == 16)
                        {
                            Vec4h values = default;
                            WriteHalfFloatDeltas(U16(output), inputStreams, 3, &values);
                        }
                        else
                        {
                            Vec4f values = default;
                            WriteFloatDeltas(U32(output), inputStreams, 3, &values);
                        }
                    }
                    else
                    {
                        if (bits == 16)
                        {
                            Vec4h values = default;
                            Copy((byte*)&values, output - stride, sizeof(ushort) * 3);
                            WriteHalfFloatDeltas(U16(output), inputStreams, 3, &values);
                        }
                        else
                        {
                            Vec4f values = default;
                            Copy((byte*)&values, output - stride, sizeof(uint) * 3);
                            WriteFloatDeltas(U32(output), inputStreams, 3, &values);
                        }
                    }
                }
                output += stride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    private static Vec3 ConvertSigned(byte* ptr, uint bitSize)
    {
        switch (bitSize)
        {
            case 8:
                return new Vec3(((sbyte*)ptr)[0] / 127f, ((sbyte*)ptr)[1] / 127f, ((sbyte*)ptr)[2] / 127f);
            case 10:
            {
                int value = *(int*)ptr;
                return new Vec3(((value << 0x16) >> 0x16) / 511f, ((value << 0xc) >> 0x16) / 511f, ((value << 2) >> 0x16) / 511f);
            }
            default:
                return new Vec3(((short*)ptr)[0] / 32767f, ((short*)ptr)[1] / 32767f, ((short*)ptr)[2] / 32767f);
        }
    }

    public static void DecodeCrossProduct<TB>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                              uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
    {
        int bits = TB.N;

        DecompContext dctx = ctx.DecompContext!;
        byte index0 = *dctx.CurrentPos++;
        byte index1 = *dctx.CurrentPos++;
        uint flags0 = ctx.AttrFlags[index0];
        uint stride0 = flags0 >> 0x18;
        uint bitSize0 = (flags0 >> 8) & 0xff;
        uint offset0 = ctx.AttrOffsets[index0] + (ctx.BaseVertexIndex * stride0);
        uint flags1 = ctx.AttrFlags[index1];
        uint stride1 = flags1 >> 0x18;
        uint bitSize1 = (flags1 >> 8) & 0xff;
        uint offset1 = ctx.AttrOffsets[index1] + (ctx.BaseVertexIndex * stride1);

        byte* baseValues = inputStreams[0];
        byte* flipValues = inputStreams[1];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);

        uint index = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                Vec3 pos0 = ConvertSigned(ctx.OutputBuffer + (index * stride0) + offset0, bitSize0);
                Vec3 pos1 = ConvertSigned(ctx.OutputBuffer + (index * stride1) + offset1, bitSize1);
                Vec3 cross = pos0.Cross(pos1);
                cross.Normalize();

                int flip = *flipValues++;
                if (bits == 8)
                {
                    output[0] = (byte)(*baseValues++ + ((int)(cross.X * 127f) ^ -flip) + flip);
                    output[1] = (byte)(*baseValues++ + ((int)(cross.Y * 127f) ^ -flip) + flip);
                    output[2] = (byte)(*baseValues++ + ((int)(cross.Z * 127f) ^ -flip) + flip);
                }
                else if (bits == 10)
                {
                    ushort value0 = (ushort)((U16(baseValues)[0] + ((int)(cross.X * 511f) ^ -flip) + flip) & 0x3ff);
                    ushort value1 = (ushort)((U16(baseValues)[1] + ((int)(cross.Y * 511f) ^ -flip) + flip) & 0x3ff);
                    ushort value2 = (ushort)((U16(baseValues)[2] + ((int)(cross.Z * 511f) ^ -flip) + flip) & 0x3ff);
                    *U32(output) = value0 | ((uint)value1 << 10) | ((uint)value2 << 20);
                    baseValues += 3 * sizeof(ushort);
                }
                else
                {
                    U16(output)[0] = (ushort)(U16(baseValues)[0] + ((int)(cross.X * 32767f) ^ -flip) + flip);
                    U16(output)[1] = (ushort)(U16(baseValues)[1] + ((int)(cross.Y * 32767f) ^ -flip) + flip);
                    U16(output)[2] = (ushort)(U16(baseValues)[2] + ((int)(cross.Z * 32767f) ^ -flip) + flip);
                    baseValues += 3 * sizeof(ushort);
                }
                output += stride;
                ++index;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset);
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                index += groups->CopyCount;
            }
            ++groups;
        }
    }

    private static Vec3 ReadVec(int kind, byte* input)
    {
        switch (kind)
        {
            case 0:
                return new Vec3(input[0], input[1], input[2]);
            case 1:
            {
                uint value = *(uint*)input;
                return new Vec3(value & 0x3ff, (value >> 10) & 0x3ff, (value >> 20) & 0x3ff);
            }
            case 2:
                return new Vec3(((ushort*)input)[0], ((ushort*)input)[1], ((ushort*)input)[2]);
            case 3:
                return new Vec3(FloatMath.HalfToFloat(((ushort*)input)[0]),
                                FloatMath.HalfToFloat(((ushort*)input)[1]),
                                FloatMath.HalfToFloat(((ushort*)input)[2]));
            default:
                return new Vec3(((float*)input)[0], ((float*)input)[1], ((float*)input)[2]);
        }
    }

    internal interface ITexElem
    {
        static abstract float Read(byte* p, int i);
    }

    internal readonly struct TexS16 : ITexElem { public static float Read(byte* p, int i) => ((short*)p)[i]; }
    internal readonly struct TexU16 : ITexElem { public static float Read(byte* p, int i) => ((ushort*)p)[i]; }
    internal readonly struct TexF16 : ITexElem { public static float Read(byte* p, int i) => FloatMath.HalfToFloat(((ushort*)p)[i]); }
    internal readonly struct TexF32 : ITexElem { public static float Read(byte* p, int i) => ((float*)p)[i]; }

    private static Vec2 DecodeTexCoords<TElem>(int kind, byte* input, byte* output, uint stride,
                                               byte* refStream, uint refStreamStride,
                                               uint indexA, uint indexB, uint index)
        where TElem : ITexElem
    {
        Vec3 a = ReadVec(kind, refStream + (index * refStreamStride) - (indexA * refStreamStride));
        Vec3 b = ReadVec(kind, refStream + (index * refStreamStride) - (indexB * refStreamStride));
        Vec3 c = ReadVec(kind, refStream + (index * refStreamStride));
        Vec3 diffBA = b - a;
        Vec3 diffCA = c - a;

        float squaredLen = MathF.Max(diffBA.SquaredLength(), FloatMath.Epsilon);
        float ratio = diffBA.Dot(diffCA) / squaredLen;

        Vec3 normalized = (a + (diffBA * ratio)) - a;

        byte* stream0 = output - (indexA * stride);
        byte* stream1 = output - (indexB * stride);

        float dist = MathF.Max((diffCA.SquaredLength() - normalized.SquaredLength()) / squaredLen, 0f);

        float range0 = TElem.Read(stream1, 0) - TElem.Read(stream0, 0);
        float range1 = TElem.Read(stream1, 1) - TElem.Read(stream0, 1);

        float value0 = (*input != 0) ? -(MathF.Sqrt(dist) * -range1) : (MathF.Sqrt(dist) * -range1);
        float value1 = (*input != 0) ? -(MathF.Sqrt(dist) * range0) : (MathF.Sqrt(dist) * range0);

        Vec2 result;
        result.X = (range0 * ratio) + TElem.Read(stream0, 0) + value0;
        result.Y = (range1 * ratio) + TElem.Read(stream0, 1) + value1;
        return result;
    }

    public static void DecodeTriangleTexCoords<T, TElem>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                         uint numGroups, byte** inputStreams, int streamsRemaining)
        where T : unmanaged, IBinaryInteger<T>
        where TElem : ITexElem
    {
        uint v = (uint)ctx.DecompContext!.BitStream0.Read(5);

        T* deltaValues = (T*)inputStreams[0];
        T* baseValues = (T*)inputStreams[1];
        byte* flipValues = inputStreams[2];

        uint flags = ctx.AttrFlags[(v >> 1) & 0xf];
        uint stride = flags >> 0x18;
        uint offset = ctx.AttrOffsets[(v >> 1) & 0xf];

        uint attrFlags = ctx.AttrFlags[ctx.AttrIndex];
        uint vertStride = attrFlags >> 0x18;
        byte* output = ctx.OutputBuffer + (ctx.AttrOffsets[ctx.AttrIndex] + (ctx.BaseVertexIndex * vertStride));

        int kind = (int)(((flags >> 3) & 3) + ((v & 1) << 1));

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                ulong value = ctx.IndexBufferTable[tableIndex];
                if (value != 0)
                {
                    Vec2 vec = DecodeTexCoords<TElem>(kind, flipValues, output, vertStride,
                                                      ctx.OutputBuffer + (offset + (ctx.BaseVertexIndex * stride)), stride,
                                                      (uint)((value >> 0x16) & 0x1fffff), (uint)(value >> 0x2b), tableIndex);

                    ((T*)output)[0] = T.CreateTruncating((int)MathF.Round(vec.X)) + *baseValues++;
                    ((T*)output)[1] = T.CreateTruncating((int)MathF.Round(vec.Y)) + *baseValues++;
                    ++flipValues;
                }
                else if (tableIndex == 0)
                {
                    Copy(output, (byte*)deltaValues, sizeof(T) * 2);
                    deltaValues += 2;
                }
                else
                {
                    for (int j = 0; j < 2; ++j)
                        ((T*)output)[j] = ((T*)(output - vertStride))[j] + *deltaValues++;
                }
                output += vertStride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, sizeof(T) * 2);
                    output += vertStride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeTriangleTexCoordsFloat<TB, TElem>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                               uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum
        where TElem : ITexElem
    {
        bool isHalf = TB.N == 16;
        int elemSize = TB.N >> 3;

        uint v = (uint)ctx.DecompContext!.BitStream0.Read(5);

        byte* flipValues = inputStreams[0];

        uint flags = ctx.AttrFlags[(v >> 1) & 0xf];
        uint stride = flags >> 0x18;
        uint offset = ctx.AttrOffsets[(v >> 1) & 0xf];

        uint attrFlags = ctx.AttrFlags[ctx.AttrIndex];
        uint vertStride = attrFlags >> 0x18;
        byte* output = ctx.OutputBuffer + (ctx.AttrOffsets[ctx.AttrIndex] + (ctx.BaseVertexIndex * vertStride));

        int kind = (int)(((flags >> 3) & 3) + ((v & 1) << 1));

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                ulong value = ctx.IndexBufferTable[tableIndex];
                if (value != 0)
                {
                    Vec2 vec = DecodeTexCoords<TElem>(kind, flipValues, output, vertStride,
                                                      ctx.OutputBuffer + (offset + (ctx.BaseVertexIndex * stride)), stride,
                                                      (uint)((value >> 0x16) & 0x1fffff), (uint)(value >> 0x2b), tableIndex);
                    if (isHalf)
                    {
                        Vec4h computed = default;
                        computed.Raw[0] = FloatMath.FloatToHalf(vec.X);
                        computed.Raw[1] = FloatMath.FloatToHalf(vec.Y);
                        WriteHalfFloatDeltas(U16(output), &inputStreams[1], 2, &computed);
                    }
                    else
                    {
                        Vec4f computed = default;
                        computed.F[0] = vec.X;
                        computed.F[1] = vec.Y;
                        WriteFloatDeltas(U32(output), &inputStreams[1], 2, &computed);
                    }
                    ++flipValues;
                }
                else if (tableIndex == 0)
                {
                    if (isHalf)
                    {
                        Vec4h computed = default;
                        WriteHalfFloatDeltas(U16(output), &inputStreams[1], 2, &computed);
                    }
                    else
                    {
                        Vec4f computed = default;
                        WriteFloatDeltas(U32(output), &inputStreams[1], 2, &computed);
                    }
                }
                else
                {
                    if (isHalf)
                    {
                        Vec4h computed = default;
                        Copy((byte*)&computed, output - vertStride, elemSize * 2);
                        WriteHalfFloatDeltas(U16(output), &inputStreams[1], 2, &computed);
                    }
                    else
                    {
                        Vec4f computed = default;
                        Copy((byte*)&computed, output - vertStride, elemSize * 2);
                        WriteFloatDeltas(U32(output), &inputStreams[1], 2, &computed);
                    }
                }
                output += vertStride;
                ++tableIndex;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, elemSize * 2);
                    output += vertStride;
                }
                tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeFixedDistance<TB, TT>(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups,
                                                   uint numGroups, byte** inputStreams, int streamsRemaining)
        where TB : INum where TT : IFlag
    {
        int bits = TB.N;

        byte* valueStream = inputStreams[0];
        byte* addValues = inputStreams[1];
        byte* flipValues = inputStreams[2];
        uint stride = ctx.AttrFlags[ctx.AttrIndex] >> 0x18;
        byte* output = OutputStart(ctx, stride);
        byte* refBaseValueStream = TT.V ? inputStreams[3] : null;

        uint tableIndex = 0;
        for (; numGroups != 0; --numGroups)
        {
            for (uint i = groups->RawCount; i != 0; --i)
            {
                uint offset = TT.V ? ctx.VertexBufferTable[tableIndex++] : 0;
                if (TT.V && offset != 0)
                {
                    byte* refp = output - ((offset >> 3) * stride);
                    int f0 = (int)(offset & 1);
                    int f1 = (int)((offset >> 1) & 1);
                    int f2 = (int)((offset >> 2) & 1);
                    if (bits == 8)
                    {
                        output[0] = (byte)(*refBaseValueStream++ + (refp[0] ^ -f0) + f0);
                        output[1] = (byte)(*refBaseValueStream++ + (refp[1] ^ -f1) + f1);
                        output[2] = (byte)(*refBaseValueStream++ + (refp[2] ^ -f2) + f2);
                    }
                    else if (bits == 10)
                    {
                        uint value = *U32(refp);
                        *U32(output) = ((uint)(U16(refBaseValueStream)[0] + (value ^ (uint)-f0) + (uint)f0) & 0x3ff)
                                     | (((uint)(U16(refBaseValueStream)[1] + ((value >> 10) ^ (uint)-f1) + (uint)f1) & 0x3ff) << 10)
                                     | (((uint)(U16(refBaseValueStream)[2] + ((value >> 20) ^ (uint)-f2) + (uint)f2) & 0x3ff) << 20);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                    else
                    {
                        U16(output)[0] = (ushort)(U16(refBaseValueStream)[0] + (U16(refp)[0] ^ (ushort)-f0) + f0);
                        U16(output)[1] = (ushort)(U16(refBaseValueStream)[1] + (U16(refp)[1] ^ (ushort)-f1) + f1);
                        U16(output)[2] = (ushort)(U16(refBaseValueStream)[2] + (U16(refp)[2] ^ (ushort)-f2) + f2);
                        refBaseValueStream += 3 * sizeof(ushort);
                    }
                }
                else
                {
                    if (bits == 8)
                    {
                        sbyte value0 = (sbyte)*valueStream++;
                        sbyte value1 = (sbyte)*valueStream++;
                        long sqrDist = (0x7f * 0x7f) - ((value0 * value0) + (value1 * value1));
                        byte dist = (byte)((byte)(int)MathF.Round(MathF.Sqrt(sqrDist < 0 ? 0f : sqrDist)) + *addValues++);
                        output[0] = (byte)value0;
                        output[1] = (byte)value1;
                        output[2] = (byte)(*flipValues++ == 1 ? -dist : dist);
                    }
                    else if (bits == 10)
                    {
                        int value0 = (int)(U16(valueStream)[0] << 0x16) >> 0x16;
                        int value1 = (int)(U16(valueStream)[1] << 0x16) >> 0x16;
                        long sqrDist = (0x1ff * 0x1ff) - ((value0 * value0) + (value1 * value1));
                        uint dist = (uint)(int)MathF.Round(MathF.Sqrt(sqrDist < 0 ? 0f : sqrDist)) + *U16(addValues);
                        *U32(output) = U16(valueStream)[0]
                                     | ((uint)U16(valueStream)[1] << 10)
                                     | (((*flipValues++ == 1 ? (0u - dist) : dist) & 0x3ff) << 20);
                        valueStream += 2 * sizeof(ushort);
                        addValues += sizeof(ushort);
                    }
                    else
                    {
                        short value0 = ((short*)valueStream)[0];
                        short value1 = ((short*)valueStream)[1];
                        long sqrDist = (0x1ff * 0x1ff) - ((value0 * value0) + (value1 * value1));
                        ushort dist = (ushort)((ushort)(int)MathF.Round(MathF.Sqrt(sqrDist < 0 ? 0f : sqrDist)) + *U16(addValues));
                        U16(output)[0] = (ushort)value0;
                        U16(output)[1] = (ushort)value1;
                        U16(output)[2] = (ushort)(*flipValues++ == 1 ? -dist : dist);
                        valueStream += 2 * sizeof(ushort);
                        addValues += sizeof(ushort);
                    }
                }
                output += stride;
            }

            if (groups->VertexCount > 0xffff)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    if (bits == 10)
                        *U32(output) = *U32(output - groups->BackRefOffset);
                    else
                        Copy(output, output - groups->BackRefOffset, (bits >> 3) * 3);
                    output += stride;
                }
                if (TT.V)
                    tableIndex += groups->CopyCount;
            }
            ++groups;
        }
    }

    public static void DecodeBackrefs(VertexStreamContext ctx, int vertexCount, VertexDecodeGroup* groups, uint numGroups)
    {
        uint flags = ctx.AttrFlags[ctx.AttrIndex];
        uint stride = flags >> 0x18;
        uint compSize = (flags >> 8) & 0xff;
        int attrShift = (int)((flags >> 0x10) & 0xff);
        uint compCount = flags & 7;
        byte* output = OutputStart(ctx, stride);

        if ((compSize & 7) != 0 || attrShift != 0)
        {
            bool is10Bit = attrShift == 0 && compSize == 10;
            if (is10Bit)
            {
                for (; numGroups != 0; --numGroups)
                {
                    for (uint i = groups->CopyCount; i != 0; --i)
                    {
                        *U32(output) = *U32(output - groups->BackRefOffset) & 0x3fffffff;
                        output += stride;
                    }
                    ++groups;
                }
            }
            else if (attrShift == 0x1e && compSize == 2)
            {
                for (; numGroups != 0; --numGroups)
                {
                    for (uint i = groups->CopyCount; i != 0; --i)
                    {
                        *U32(output) = (*U32(output - groups->BackRefOffset) & 0xc0000000) | (*U32(output) & 0x3fffffff);
                        output += stride;
                    }
                    ++groups;
                }
            }
            else
            {
                uint bitSize = compSize * compCount;
                uint endOffset = bitSize + (uint)attrShift;
                if (endOffset <= 8)
                {
                    byte copyMask = (byte)((2UL << (int)(bitSize - 1)) - 1);
                    byte keepMask = (byte)~(copyMask << attrShift);
                    for (; numGroups != 0; --numGroups)
                    {
                        for (uint i = groups->CopyCount; i != 0; --i)
                        {
                            *output = (byte)(((((*(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift)) | (*output & keepMask));
                            output += stride;
                        }
                        ++groups;
                    }
                }
                else if (endOffset <= 0x10)
                {
                    ushort copyMask = (ushort)((2UL << (int)(bitSize - 1)) - 1);
                    ushort keepMask = (ushort)~(copyMask << attrShift);
                    for (; numGroups != 0; --numGroups)
                    {
                        for (uint i = groups->CopyCount; i != 0; --i)
                        {
                            *U16(output) = (ushort)(((((*U16(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift)) | (*U16(output) & keepMask));
                            output += stride;
                        }
                        ++groups;
                    }
                }
                else if (endOffset <= 0x20)
                {
                    uint copyMask = (uint)((2UL << (int)(bitSize - 1)) - 1);
                    uint keepMask = ~(copyMask << attrShift);
                    for (; numGroups != 0; --numGroups)
                    {
                        for (uint i = groups->CopyCount; i != 0; --i)
                        {
                            *U32(output) = (((*U32(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift) | (*U32(output) & keepMask);
                            output += stride;
                        }
                        ++groups;
                    }
                }
                else if (endOffset <= 0x40)
                {
                    ulong copyMask = (2UL << (int)(bitSize - 1)) - 1;
                    ulong keepMask = ~(copyMask << attrShift);
                    for (; numGroups != 0; --numGroups)
                    {
                        for (uint i = groups->CopyCount; i != 0; --i)
                        {
                            *U64(output) = (((*U64(output - groups->BackRefOffset) >> attrShift) & copyMask) << attrShift) | (*U64(output) & keepMask);
                            output += stride;
                        }
                        ++groups;
                    }
                }
            }
        }
        else
        {
            int size = (int)((compSize >> 3) * compCount);
            for (; numGroups != 0; --numGroups)
            {
                for (uint i = groups->CopyCount; i != 0; --i)
                {
                    Copy(output, output - groups->BackRefOffset, size);
                    output += stride;
                }
                ++groups;
            }
        }
    }
}
