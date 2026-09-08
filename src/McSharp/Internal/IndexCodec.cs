using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace McSharp.Internal;

internal static unsafe class IndexCodec
{
    private const uint FecMax = 0xf;

    private static ReadOnlySpan<byte> CodeAuxEncodingTable => new byte[16]
    {
        0x00, 0x76, 0x87, 0x56, 0x67, 0x78, 0xa9, 0x86, 0x65, 0x89, 0x68, 0x98, 0x01, 0x69,
        0, 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushVertexFifo(uint* fifo, uint v, ref uint offset, int cond = 1)
    {
        fifo[(offset * 4) + 3] = v;
        offset = (offset + (uint)cond) & 0xf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushVertexFifo64(uint* fifo, uint v, ref uint offset)
    {
        fifo[offset] = v;
        offset = (offset + 1) & 0x3f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushTriangleFifo(uint* fifo, uint a, uint b, uint c, ref uint offset)
    {
        fifo[(offset * 4) + 0] = a;
        fifo[(offset * 4) + 1] = b;
        fifo[(offset * 4) + 2] = c;
        offset = (offset + 1) & 0xf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTriangle<T>(T* dst, uint a, uint b, uint c) where T : unmanaged, IBinaryInteger<T>
    {
        dst[0] = T.CreateTruncating(a);
        dst[1] = T.CreateTruncating(b);
        dst[2] = T.CreateTruncating(c);
    }

    private static uint DecodeVByteFixed(ref byte* data)
    {
        uint raw = *(uint*)data;
        uint result = raw & 0x7f;

        ++data;
        if (((raw >> 7) & 1) != 0)
        {
            ++data;
            result |= (raw >> 1) & 0x3f80;
            if (((raw >> 0xf) & 1) != 0)
            {
                ++data;
                result |= (raw >> 2) & 0x3fc000;
            }
        }

        return result;
    }

    private static uint DecodeIndexFixed(ref byte* data, uint last)
    {
        uint v = DecodeVByteFixed(ref data);
        uint d = (v >> 1) ^ (uint)(-(int)(v & 1));

        return last + d;
    }

    private static uint DecodeIndex(ref byte* data, uint last)
    {
        uint v = VByte.Decode(ref data);
        uint d = (v >> 1) ^ (uint)(-(int)(v & 1));

        return last + d;
    }

    public static uint DecodeIndexBuffer0WithTable(void* dst, int indexCount, uint baseIndex, ulong* tbl,
                                                   uint copied, uint remaining, ref byte* src0, ref byte* src1,
                                                   IndexFormat format)
    {
        if (format == IndexFormat.U16)
            return DecodeIndexBuffer0WithTableT<ushort>(dst, indexCount, baseIndex, tbl, copied, remaining, ref src0, ref src1);
        return DecodeIndexBuffer0WithTableT<uint>(dst, indexCount, baseIndex, tbl, copied, remaining, ref src0, ref src1);
    }

    private static uint DecodeIndexBuffer0WithTableT<T>(void* dst, int indexCount, uint baseIndex, ulong* tbl,
                                                        uint copied, uint remaining, ref byte* src0, ref byte* src1)
        where T : unmanaged, IBinaryInteger<T>
    {
        uint* trigfifo = stackalloc uint[16 * 4];
        uint trigfifooffset = 0;
        uint vertexfifooffset = 0;

        uint next = baseIndex;
        uint last = baseIndex;

        T* outBuf = (T*)dst;

        if (indexCount > 2)
        {
            for (uint n = (uint)(indexCount / 3); n != 0; --n)
            {
                byte codetri = *src0++;

                if (codetri < 0xf0)
                {
                    int fe = codetri >> 4;
                    int fec = codetri & 0xf;

                    uint slot = (trigfifooffset - 1 - (uint)fe) & 0xf;
                    uint a = trigfifo[(slot * 4) + 0];
                    uint b = trigfifo[(slot * 4) + 1];
                    uint unk = trigfifo[(slot * 4) + 2];

                    uint c;
                    if (fec == FecMax)
                    {
                        c = DecodeIndexFixed(ref src1, last);
                        last = c;

                        WriteTriangle(outBuf, a, b, c);

                        PushVertexFifo(trigfifo, c, ref vertexfifooffset);
                    }
                    else
                    {
                        uint cf = trigfifo[((((vertexfifooffset - 1 - (uint)fec) & 0xf) * 4) + 3)];
                        c = (fec == 0) ? next : cf;
                        int fec0 = fec == 0 ? 1 : 0;
                        next += (uint)fec0;

                        WriteTriangle(outBuf, a, b, c);

                        PushVertexFifo(trigfifo, c, ref vertexfifooffset, fec0);
                    }

                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);

                    uint maxAB = Math.Max(a, b);
                    uint max = Math.Max(maxAB, c);
                    if (max >= copied && (tbl[max] & 0x3fffff) == 0)
                    {
                        if (maxAB > c)
                        {
                            tbl[max] = ((ulong)(max - (max != a ? a : c)) << 0x2b) | ((ulong)(max - (max != a ? c : b)) << 0x16);
                        }
                        else
                        {
                            tbl[max] = (ulong)(max >= unk ? max - unk : 0) | ((ulong)(max - a) << 0x16) | ((ulong)(max - b) << 0x2b);
                        }
                    }
                }
                else if (codetri < 0xfe)
                {
                    byte codeaux = CodeAuxEncodingTable[codetri & 0xf];

                    uint a = next++;

                    uint feb = (uint)(codeaux >> 4);
                    int fec = codeaux & 0xf;

                    uint feb0 = (0xd001u >> (codetri & 0xf)) & 1;
                    uint bf = trigfifo[((((vertexfifooffset - feb) & 0xf) * 4) + 3)];
                    uint b = (feb0 != 0) ? next : bf;
                    next += feb0;

                    uint cf = trigfifo[((((vertexfifooffset - (uint)fec) & 0xf) * 4) + 3)];
                    uint c = (fec == 0) ? next : cf;

                    int fec0 = fec == 0 ? 1 : 0;
                    next += (uint)fec0;

                    WriteTriangle(outBuf, a, b, c);

                    PushVertexFifo(trigfifo, a, ref vertexfifooffset);
                    PushVertexFifo(trigfifo, b, ref vertexfifooffset, (int)feb0);
                    PushVertexFifo(trigfifo, c, ref vertexfifooffset, fec0);

                    PushTriangleFifo(trigfifo, b, a, c, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);

                    uint maxBC = Math.Max(b, c);
                    uint max = Math.Max(maxBC, a);
                    if (max >= copied && (tbl[max] & 0x3fffff) == 0)
                    {
                        if (maxBC >= a)
                        {
                            tbl[max] = ((ulong)(max - (c >= b ? b : a)) << 0x2b) | ((ulong)(max - (c >= b ? a : c)) << 0x16);
                        }
                        else
                        {
                            tbl[max] = ((ulong)(max - c) << 0x2b) | ((ulong)(max - b) << 0x16);
                        }
                    }
                }
                else
                {
                    byte codeaux = *src1++;

                    if (codeaux == 0)
                    {
                        next = 0;
                        last = 0;
                    }

                    int fea = codetri == 0xfe ? 1 : 0;
                    int feb = codeaux >> 4;
                    int fec = codeaux & 0xf;

                    uint a = codeaux != 0 ? next : 0;
                    next += (uint)fea;

                    uint b = (feb == 0) ? next++ : trigfifo[((((vertexfifooffset - (uint)feb) & 0xf) * 4) + 3)];
                    uint c = (fec == 0) ? next++ : trigfifo[((((vertexfifooffset - (uint)fec) & 0xf) * 4) + 3)];

                    if (fea == 0)
                        last = a = DecodeIndexFixed(ref src1, last);

                    if (feb == 0xf)
                        last = b = DecodeIndexFixed(ref src1, last);

                    if (fec == 0xf)
                        last = c = DecodeIndexFixed(ref src1, last);

                    WriteTriangle(outBuf, a, b, c);

                    PushVertexFifo(trigfifo, a, ref vertexfifooffset);
                    PushVertexFifo(trigfifo, b, ref vertexfifooffset, (feb == 0 || feb == 0xf) ? 1 : 0);
                    PushVertexFifo(trigfifo, c, ref vertexfifooffset, (fec == 0 || fec == 0xf) ? 1 : 0);

                    PushTriangleFifo(trigfifo, b, a, c, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);

                    uint maxBC = Math.Max(b, c);
                    uint max = Math.Max(maxBC, a);
                    if (max >= copied && (tbl[max] & 0x3fffff) == 0)
                    {
                        if (maxBC >= a)
                        {
                            tbl[max] = ((ulong)(max - (c >= b ? b : a)) << 0x2b) | ((ulong)(max - (c >= b ? a : c)) << 0x16);
                        }
                        else
                        {
                            tbl[max] = ((ulong)(max - c) << 0x2b) | ((ulong)(max - b) << 0x16);
                        }
                    }
                }

                outBuf += 3;
            }
        }

        return next;
    }

    public static uint DecodeIndexBuffer0WithoutTable(void* dst, int indexCount, uint baseIndex,
                                                      ref byte* src0, ref byte* src1, IndexFormat format)
    {
        if (format == IndexFormat.U16)
            return DecodeIndexBuffer0WithoutTableT<ushort>(dst, indexCount, baseIndex, ref src0, ref src1);
        return DecodeIndexBuffer0WithoutTableT<uint>(dst, indexCount, baseIndex, ref src0, ref src1);
    }

    private static uint DecodeIndexBuffer0WithoutTableT<T>(void* dst, int indexCount, uint baseIndex,
                                                           ref byte* src0, ref byte* src1)
        where T : unmanaged, IBinaryInteger<T>
    {
        uint* trigfifo = stackalloc uint[16 * 4];
        uint trigfifooffset = 0;
        uint vertexfifooffset = 0;

        uint next = baseIndex;
        uint last = baseIndex;

        T* outBuf = (T*)dst;

        if (indexCount > 2)
        {
            for (uint n = (uint)(indexCount / 3); n != 0; --n)
            {
                byte codetri = *src0++;

                if (codetri < 0xf0)
                {
                    int fe = codetri >> 4;
                    int fec = codetri & 0xf;

                    uint slot = (trigfifooffset - 1 - (uint)fe) & 0xf;
                    uint a = trigfifo[(slot * 4) + 0];
                    uint b = trigfifo[(slot * 4) + 1];

                    uint c;
                    if (fec == FecMax)
                    {
                        c = DecodeIndexFixed(ref src1, last);
                        last = c;

                        WriteTriangle(outBuf, a, b, c);

                        PushVertexFifo(trigfifo, c, ref vertexfifooffset);
                    }
                    else
                    {
                        uint cf = trigfifo[((((vertexfifooffset - 1 - (uint)fec) & 0xf) * 4) + 3)];
                        c = (fec == 0) ? next : cf;
                        int fec0 = fec == 0 ? 1 : 0;
                        next += (uint)fec0;

                        WriteTriangle(outBuf, a, b, c);

                        PushVertexFifo(trigfifo, c, ref vertexfifooffset, fec0);
                    }

                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);
                }
                else if (codetri < 0xfe)
                {
                    byte codeaux = CodeAuxEncodingTable[codetri & 0xf];

                    uint a = next++;

                    uint feb = (uint)(codeaux >> 4);
                    int fec = codeaux & 0xf;

                    uint feb0 = (0xd001u >> (codetri & 0xf)) & 1;
                    uint bf = trigfifo[((((vertexfifooffset - feb) & 0xf) * 4) + 3)];
                    uint b = (feb0 != 0) ? next : bf;
                    next += feb0;

                    uint cf = trigfifo[((((vertexfifooffset - (uint)fec) & 0xf) * 4) + 3)];
                    uint c = (fec == 0) ? next : cf;

                    int fec0 = fec == 0 ? 1 : 0;
                    next += (uint)fec0;

                    WriteTriangle(outBuf, a, b, c);

                    PushVertexFifo(trigfifo, a, ref vertexfifooffset);
                    PushVertexFifo(trigfifo, b, ref vertexfifooffset, (int)feb0);
                    PushVertexFifo(trigfifo, c, ref vertexfifooffset, fec0);

                    PushTriangleFifo(trigfifo, b, a, c, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);
                }
                else
                {
                    byte codeaux = *src1++;

                    if (codeaux == 0)
                    {
                        next = 0;
                        last = 0;
                    }

                    int fea = codetri == 0xfe ? 1 : 0;
                    int feb = codeaux >> 4;
                    int fec = codeaux & 0xf;

                    uint a = codeaux != 0 ? next : 0;
                    next += (uint)fea;

                    uint b = (feb == 0) ? next++ : trigfifo[((((vertexfifooffset - (uint)feb) & 0xf) * 4) + 3)];
                    uint c = (fec == 0) ? next++ : trigfifo[((((vertexfifooffset - (uint)fec) & 0xf) * 4) + 3)];

                    if (fea == 0)
                        last = a = DecodeIndexFixed(ref src1, last);

                    if (feb == 0xf)
                        last = b = DecodeIndexFixed(ref src1, last);

                    if (fec == 0xf)
                        last = c = DecodeIndexFixed(ref src1, last);

                    WriteTriangle(outBuf, a, b, c);

                    PushVertexFifo(trigfifo, a, ref vertexfifooffset);
                    PushVertexFifo(trigfifo, b, ref vertexfifooffset, (feb == 0 || feb == 0xf) ? 1 : 0);
                    PushVertexFifo(trigfifo, c, ref vertexfifooffset, (fec == 0 || fec == 0xf) ? 1 : 0);

                    PushTriangleFifo(trigfifo, b, a, c, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, c, b, a, ref trigfifooffset);
                    PushTriangleFifo(trigfifo, a, c, b, ref trigfifooffset);
                }

                outBuf += 3;
            }
        }

        return next;
    }

    public static uint DecodeIndexBuffer2(void* dst, IndexFormat indexFormat, int indexCount, uint baseIndex,
                                          ulong* tbl, uint a6, uint numCopied, uint start,
                                          ref byte* src0, ref byte* src1)
    {
        if (indexFormat == IndexFormat.U16)
            return DecodeIndexBuffer2T<ushort>(dst, indexCount, baseIndex, tbl, a6, start, ref src0, ref src1);
        return DecodeIndexBuffer2T<uint>(dst, indexCount, baseIndex, tbl, a6, start, ref src0, ref src1);
    }

    private static uint DecodeIndexBuffer2T<T>(void* dst, int indexCount, uint baseIndex, ulong* tbl, uint a6,
                                               uint start, ref byte* src0, ref byte* src1)
        where T : unmanaged, IBinaryInteger<T>
    {
        uint* vertexfifo = stackalloc uint[64];
        uint vertexfifooffset = 0;
        uint current = start - baseIndex;

        T* outBuf = (T*)dst;

        byte codetria = *src0++;
        uint a;
        if (codetria == 0)
        {
            a = current++;
            PushVertexFifo64(vertexfifo, a, ref vertexfifooffset);
        }
        else if (codetria > 0x40)
        {
            a = DecodeIndex(ref src1, current);
            PushVertexFifo64(vertexfifo, a, ref vertexfifooffset);
        }
        else
        {
            a = vertexfifo[(0u - codetria) & 0x3f];
        }

        byte codetrib = *src0++;
        uint b;
        if (codetrib == 0)
        {
            b = current++;
            PushVertexFifo64(vertexfifo, b, ref vertexfifooffset);
        }
        else if (codetrib > 0x40)
        {
            b = DecodeIndex(ref src1, current);
            PushVertexFifo64(vertexfifo, b, ref vertexfifooffset);
        }
        else
        {
            b = vertexfifo[(0u - codetrib) & 0x3f];
        }

        byte codetric = *src0++;
        uint c;
        if (codetric == 0)
        {
            c = current++;
            PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
        }
        else if (codetric > 0x40)
        {
            c = DecodeIndex(ref src1, current);
            PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
        }
        else
        {
            c = vertexfifo[(0u - codetric) & 0x3f];
        }

        outBuf[0] = T.CreateTruncating(a);
        outBuf[1] = T.CreateTruncating(b);
        outBuf[2] = T.CreateTruncating(c);

        current = (a != 0 || b != 1 || c != 2) ? current : 3;

        if (tbl != null)
        {
            uint copied = a6 - baseIndex;
            uint tblBase = baseIndex - a6;

            ulong unk = (ulong)(long)((1 << (int)((a & 0xf) << 2)) + (1 << (int)((b & 0xf) << 2)) + (1 << (int)((c & 0xf) << 2)));
            if ((unk & 0x6666666666666666) == 0)
            {
                uint maxAC = Math.Max(a, c);
                uint max = Math.Max(maxAC, b);
                uint val0 = (maxAC >= b) ? ((c >= a) ? c : b) : c;
                uint val1 = (maxAC >= b) ? ((c >= a) ? a : b) : a;
                uint unkValue = Math.Min(val1, val0);

                if (unkValue >= copied)
                {
                    tbl[tblBase + max] = ((ulong)(max - val1) << 0x2b) | ((ulong)(max - val0) << 0x16);
                }
            }

            if (indexCount < 4)
                return baseIndex + current;

            uint flip = 0;
            for (int i = 3; i < indexCount; ++i)
            {
                byte codetri = *src0++;
                uint value;
                if (codetri == 0)
                {
                    value = current++;
                    PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
                }
                else if (codetri > 0x40)
                {
                    value = DecodeIndex(ref src1, current);
                    PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
                }
                else
                {
                    value = vertexfifo[(0u - codetri) & 0x3f];
                }
                outBuf[i] = T.CreateTruncating(value);

                unk += (ulong)(long)(1 << (int)((value & 0xf) << 2));
                uint v0 = uint.CreateTruncating(outBuf[i - 1 - (int)flip]);
                uint v1 = uint.CreateTruncating(outBuf[i - 1 - (int)(flip ^ 1)]);
                uint v2 = uint.CreateTruncating(outBuf[i - 3]);

                bool unkCond = (unk & 0xeeeeeeeeeeeeeeee) != 0;
                unk += (ulong)(long)(-1 << (int)((v2 & 0xf) << 2));

                if ((unkCond || v0 >= value || v1 > value)
                    || (!unkCond && value >= v0 && (unkCond || value != v0) && value == v1)
                    || v2 > value
                    || ((((!unkCond && value >= v0 && (unkCond || value != v0)) && value >= v1)
                         && (unkCond || !(value >= v0) || value == v0 || value != v1)) && value == v2))
                {
                    if (!unkCond)
                    {
                        uint maxAB = Math.Max(value, v0);
                        uint max = Math.Max(maxAB, v1);
                        uint val0 = (maxAB >= v1) ? ((value >= v0) ? v1 : value) : v0;
                        uint val1 = (maxAB >= v1) ? ((value >= v0) ? v0 : v1) : value;
                        uint unkValue = Math.Min(val0, val1);

                        if (unkValue >= copied && (tbl[tblBase + max] & 0x3fffff) == 0)
                        {
                            tbl[tblBase + max] = ((ulong)(max - val0) << 0x2b) | ((ulong)(max - val1) << 0x16);
                        }
                    }
                }
                else
                {
                    uint uVar2 = Math.Min(Math.Min(v0, v1), v2);
                    if (copied > uVar2)
                    {
                        if (!unkCond)
                        {
                            uint maxAB = Math.Max(value, v0);
                            uint max = Math.Max(maxAB, v1);
                            uint val0 = (maxAB >= v1) ? ((value >= v0) ? v1 : value) : v0;
                            uint val1 = (maxAB >= v1) ? ((value >= v0) ? v0 : v1) : value;
                            uint unkValue = Math.Min(val0, val1);

                            if (unkValue >= copied && (tbl[tblBase + max] & 0x3fffff) == 0)
                            {
                                tbl[tblBase + max] = ((ulong)(max - val0) << 0x2b) | ((ulong)(max - val1) << 0x16);
                            }
                        }
                    }
                    else
                    {
                        tbl[tblBase + value] = (ulong)(value - v2) | ((ulong)(value - v0) << 0x16) | ((ulong)(value - v1) << 0x2b);
                    }
                }

                flip ^= 1;
            }
        }
        else
        {
            if (indexCount < 4)
                return baseIndex + current;

            for (int i = 3; i < indexCount; ++i)
            {
                byte codetri = *src0++;
                uint value;
                if (codetri == 0)
                {
                    value = current++;
                    PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
                }
                else if (codetri > 0x40)
                {
                    value = DecodeIndex(ref src1, current);
                    PushVertexFifo64(vertexfifo, c, ref vertexfifooffset);
                }
                else
                {
                    value = vertexfifo[(0u - codetri) & 0x3f];
                }
                outBuf[i] = T.CreateTruncating(value);
            }
        }

        return baseIndex + current;
    }

    public static uint DecodeIndexBuffer3(void* dst, IndexFormat indexFormat, int indexCount, uint baseIndex,
                                          uint a5, ulong* decodeBuf, uint numCopied, uint start,
                                          ref byte* src0, ref byte* src1)
    {
        if (indexFormat == IndexFormat.U16)
            return DecodeIndexBuffer3T<ushort>(dst, indexCount, baseIndex, start, ref src0, ref src1);
        return DecodeIndexBuffer3T<uint>(dst, indexCount, baseIndex, start, ref src0, ref src1);
    }

    private static uint DecodeIndexBuffer3T<T>(void* dst, int indexCount, uint baseIndex, uint start,
                                               ref byte* src0, ref byte* src1)
        where T : unmanaged, IBinaryInteger<T>
    {
        uint* vertexfifo = stackalloc uint[64];
        uint vertexfifooffset = 0;
        uint current = start - baseIndex;

        T* outBuf = (T*)dst;

        for (int i = 0; i < indexCount; ++i)
        {
            byte codetri = *src0++;

            uint value;
            if (codetri == 0)
            {
                PushVertexFifo64(vertexfifo, current, ref vertexfifooffset);
                value = current++;
            }
            else if (codetri < 0x41)
            {
                value = vertexfifo[(vertexfifooffset - codetri) & 0x3f];
            }
            else
            {
                value = DecodeIndex(ref src1, current);
                PushVertexFifo64(vertexfifo, value, ref vertexfifooffset);
            }

            outBuf[i] = T.CreateTruncating(value);
        }

        return baseIndex + current;
    }
}
