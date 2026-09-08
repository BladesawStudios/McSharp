using System;

namespace McSharp.Internal;

internal static unsafe class AttributeStreamInfo
{
    private static int AdjustToByte(int size) => ((size + 7 > -1) ? (size + 7) : (size + 0xe)) >> 3;

    private static ElementType ToElementType(int size) => (ElementType)(AdjustToByte(size) - 1);

    private static int PerByte(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        info[0].ElementType = ElementType.U8;
        info[0].TableCount = AdjustToByte(componentBitSize) * componentCount;
        info[0].ElementCount = vertexCount;
        return 1;
    }

    private static int PerComponent(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        info[0].ElementType = ToElementType(componentBitSize);
        info[0].TableCount = componentCount;
        info[0].ElementCount = vertexCount;
        return 1;
    }

    private static int PerByteSplit(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);
        int tableCount = AdjustToByte(componentBitSize) * componentCount;

        info[0].ElementType = ElementType.U8;
        info[0].TableCount = tableCount;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = ElementType.U8;
        info[1].TableCount = tableCount;
        info[1].ElementCount = vertexCount - (int)elementCount;

        return 2;
    }

    private static int FirstThenRest(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 1;
        info[0].ElementCount = vertexCount;
        info[1].ElementType = elementType;
        info[1].TableCount = componentCount - 1;
        info[1].ElementCount = vertexCount;

        return 2;
    }

    private static int ThreePlusByte(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        info[0].ElementType = ToElementType(componentBitSize);
        info[0].TableCount = 3;
        info[0].ElementCount = vertexCount;
        info[1].ElementType = ElementType.U8;
        info[1].TableCount = 1;
        info[1].ElementCount = vertexCount;

        return 2;
    }

    private static int PerComponentSplit(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = componentCount;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = elementType;
        info[1].TableCount = componentCount;
        info[1].ElementCount = vertexCount - (int)elementCount;

        return 2;
    }

    private static int ThreeSplit(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 3;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = elementType;
        info[1].TableCount = 3;
        info[1].ElementCount = vertexCount - (int)elementCount;

        return 2;
    }

    private static int TwoU16SplitPlusByte(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);

        info[0].ElementType = ElementType.U16;
        info[0].TableCount = 2;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = ElementType.U16;
        info[1].TableCount = 2;
        info[1].ElementCount = vertexCount - (int)elementCount;
        info[2].ElementType = ElementType.U8;
        info[2].TableCount = 1;
        info[2].ElementCount = vertexCount - (int)elementCount;

        return 3;
    }

    private static int ThreeSplitThreeWay(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount0 = VByte.Decode(ref pos);
        uint elementCount1 = VByte.Decode(ref pos);
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 3;
        info[0].ElementCount = (int)elementCount0;
        info[1].ElementType = elementType;
        info[1].TableCount = 3;
        info[1].ElementCount = (int)elementCount1;
        info[2].ElementType = elementType;
        info[2].TableCount = 3;
        info[2].ElementCount = vertexCount - (int)elementCount0 - (int)elementCount1;

        return 3;
    }

    private static int TwoOneByte(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 2;
        info[0].ElementCount = vertexCount;
        info[1].ElementType = elementType;
        info[1].TableCount = 1;
        info[1].ElementCount = vertexCount;
        info[2].ElementType = ElementType.U8;
        info[2].TableCount = 1;
        info[2].ElementCount = vertexCount;

        return 3;
    }

    private static int FirstThenRestSplit(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 1;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = elementType;
        info[1].TableCount = componentCount - 1;
        info[1].ElementCount = (int)elementCount;
        info[2].ElementType = elementType;
        info[2].TableCount = componentCount;
        info[2].ElementCount = vertexCount - (int)elementCount;

        return 3;
    }

    private static int TwoOneByteSplit(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount = VByte.Decode(ref pos);
        ElementType elementType = ToElementType(componentBitSize);

        info[0].ElementType = elementType;
        info[0].TableCount = 2;
        info[0].ElementCount = (int)elementCount;
        info[1].ElementType = elementType;
        info[1].TableCount = 1;
        info[1].ElementCount = (int)elementCount;
        info[2].ElementType = ElementType.U8;
        info[2].TableCount = 1;
        info[2].ElementCount = (int)elementCount;
        info[3].ElementType = elementType;
        info[3].TableCount = 3;
        info[3].ElementCount = vertexCount - (int)elementCount;

        return 4;
    }

    private static int HalfFloatStreams(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount0 = VByte.Decode(ref pos);
        uint elementCount1 = VByte.Decode(ref pos);
        int totalComponentCount = componentCount * vertexCount;

        info[0].ElementType = ElementType.U8;
        info[0].TableCount = 1;
        info[0].ElementCount = totalComponentCount;
        info[1].ElementType = ElementType.U8;
        info[1].TableCount = 1;
        info[1].ElementCount = (int)elementCount0;
        info[2].ElementType = ElementType.U8;
        info[2].TableCount = 1;
        info[2].ElementCount = totalComponentCount - (int)elementCount0;
        info[3].ElementType = ElementType.U16;
        info[3].TableCount = 1;
        info[3].ElementCount = (int)elementCount1;
        info[4].ElementType = ElementType.U16;
        info[4].TableCount = 1;
        info[4].ElementCount = totalComponentCount - (int)elementCount1;

        return 5;
    }

    private static int SingleFloatStreams(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        uint elementCount0 = VByte.Decode(ref pos);
        uint elementCount1 = VByte.Decode(ref pos);
        int totalComponentCount = componentCount * vertexCount;

        info[0].ElementType = ElementType.U8;
        info[0].TableCount = 1;
        info[0].ElementCount = totalComponentCount;
        info[1].ElementType = ElementType.U8;
        info[1].TableCount = 1;
        info[1].ElementCount = (int)elementCount0;
        info[2].ElementType = ElementType.U8;
        info[2].TableCount = 1;
        info[2].ElementCount = totalComponentCount - (int)elementCount0;
        info[3].ElementType = ElementType.U8;
        info[3].TableCount = 3;
        info[3].ElementCount = (int)elementCount1;
        info[4].ElementType = ElementType.U8;
        info[4].TableCount = 3;
        info[4].ElementCount = totalComponentCount - (int)elementCount1;

        return 5;
    }

    private static int FlipPlusHalfFloat(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        info[0].ElementType = ElementType.U8;
        info[0].TableCount = 1;
        info[0].ElementCount = (int)VByte.Decode(ref pos);

        return HalfFloatStreams(info + 1, maxStreams - 1, ref pos, componentCount, componentBitSize, vertexCount) + 1;
    }

    private static int FlipPlusSingleFloat(AttrStreamInfo* info, uint maxStreams, ref byte* pos, int componentCount, int componentBitSize, int vertexCount)
    {
        info[0].ElementType = ElementType.U8;
        info[0].TableCount = 1;
        info[0].ElementCount = (int)VByte.Decode(ref pos);

        return SingleFloatStreams(info + 1, maxStreams - 1, ref pos, componentCount, componentBitSize, vertexCount) + 1;
    }

    public static readonly delegate*<AttrStreamInfo*, uint, ref byte*, int, int, int, int>[] Functions = BuildTable();

    private static delegate*<AttrStreamInfo*, uint, ref byte*, int, int, int, int>[] BuildTable()
    {
        var t = new delegate*<AttrStreamInfo*, uint, ref byte*, int, int, int, int>[0x71];
        int i = 0;

        for (int n = 0; n < 7; ++n) t[i++] = &FirstThenRest;
        for (int n = 0; n < 7; ++n) t[i++] = &FirstThenRestSplit;
        for (int n = 0; n < 14; ++n) t[i++] = &PerByte;
        for (int n = 0; n < 32; ++n) t[i++] = &PerByteSplit;
        for (int n = 0; n < 14; ++n) t[i++] = &PerComponent;
        for (int n = 0; n < 14; ++n) t[i++] = &PerComponentSplit;
        for (int n = 0; n < 4; ++n) t[i++] = &ThreeSplit;
        t[i++] = &HalfFloatStreams;
        t[i++] = &SingleFloatStreams;
        for (int n = 0; n < 4; ++n) t[i++] = &ThreeSplitThreeWay;
        t[i++] = &HalfFloatStreams;
        t[i++] = &SingleFloatStreams;
        for (int n = 0; n < 3; ++n) t[i++] = &ThreePlusByte;
        for (int n = 0; n < 2; ++n) t[i++] = &TwoU16SplitPlusByte;
        t[i++] = &FlipPlusHalfFloat;
        t[i++] = &FlipPlusSingleFloat;
        for (int n = 0; n < 3; ++n) t[i++] = &TwoOneByte;
        for (int n = 0; n < 3; ++n) t[i++] = &TwoOneByteSplit;

        if (i != 0x71)
            throw new InvalidOperationException($"attribute stream info table has {i} entries, expected 0x71");

        return t;
    }
}

internal static unsafe class AttributeDecoders
{
    public static readonly delegate*<VertexStreamContext, int, VertexDecodeGroup*, uint, byte**, int, void>[] Functions = BuildTable();

    private static delegate*<VertexStreamContext, int, VertexDecodeGroup*, uint, byte**, int, void>[] BuildTable()
    {
        var t = new delegate*<VertexStreamContext, int, VertexDecodeGroup*, uint, byte**, int, void>[0x71];
        int i = 0;

        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N2, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N4, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N10, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N2, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N4, FFalse>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N2, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N8, N4, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N10, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N2, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeInternalDeltas<N16, N4, FTrue>;

        t[i++] = &AttributeCodec.DecodeRaw<N2>;
        t[i++] = &AttributeCodec.DecodeRaw<N8>;
        t[i++] = &AttributeCodec.DecodeRaw<N16>;
        t[i++] = &AttributeCodec.DecodeRaw<N24>;
        t[i++] = &AttributeCodec.DecodeRaw<N32>;
        t[i++] = &AttributeCodec.DecodeRaw<N48>;
        t[i++] = &AttributeCodec.DecodeRaw<N64>;
        t[i++] = &AttributeCodec.DecodeRaw<N96>;
        t[i++] = &AttributeCodec.DecodeRaw<N128>;
        t[i++] = &AttributeCodec.DecodeRaw<N30>;

        t[i++] = &AttributeCodec.DecodeRawCustomSize<byte, uint, FFalse>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<ushort, uint, FFalse>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<byte, ulong, FFalse>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<ushort, ulong, FFalse>;

        t[i++] = &AttributeCodec.DecodeRawWithTable<N2, N1>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N8, N1>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N8, N2>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N8, N3>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N8, N4>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N10, N3>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N16, N1>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N16, N2>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N16, N3>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N16, N4>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N32, N1>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N32, N2>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N32, N3>;
        t[i++] = &AttributeCodec.DecodeRawWithTable<N32, N4>;

        t[i++] = &AttributeCodec.DecodeRawCustomSize<byte, uint, FTrue>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<ushort, uint, FTrue>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<byte, ulong, FTrue>;
        t[i++] = &AttributeCodec.DecodeRawCustomSize<ushort, ulong, FTrue>;

        t[i++] = &AttributeCodec.DecodeXor1<N8>;
        t[i++] = &AttributeCodec.DecodeXor1<N10>;
        t[i++] = &AttributeCodec.DecodeXor1<N16>;
        t[i++] = &AttributeCodec.DecodeXor2<N8>;
        t[i++] = &AttributeCodec.DecodeXor2<N10>;
        t[i++] = &AttributeCodec.DecodeXor2<N16>;
        t[i++] = &AttributeCodec.DecodeXorCustomSize<FTrue>;
        t[i++] = &AttributeCodec.DecodeXor3<N8>;
        t[i++] = &AttributeCodec.DecodeXor3<N16>;
        t[i++] = &AttributeCodec.DecodeXor4<N8>;
        t[i++] = &AttributeCodec.DecodeXor4<N16>;
        t[i++] = &AttributeCodec.DecodeXorCustomSize<FFalse>;
        t[i++] = &AttributeCodec.DecodeXor5<N16>;
        t[i++] = &AttributeCodec.DecodeXor5<N32>;

        t[i++] = &AttributeCodec.DecodeDeltas<N2, N1, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N1, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N2, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N4, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N10, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N1, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N2, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N3, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N4, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<byte, uint, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<ushort, uint, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<byte, ulong, FFalse>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<ushort, ulong, FFalse>;

        t[i++] = &AttributeCodec.DecodeDeltas<N2, N1, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N1, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N2, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N8, N4, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N10, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N1, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N2, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N3, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltas<N16, N4, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<byte, uint, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<ushort, uint, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<byte, ulong, FTrue>;
        t[i++] = &AttributeCodec.DecodeDeltasCustomSize<ushort, ulong, FTrue>;

        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N8, FFalse>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N10, FFalse>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N16, FFalse>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltasCustomSize<FFalse>;
        t[i++] = &AttributeCodec.DecodeTriangleFloatDeltas<N16, FFalse>;
        t[i++] = &AttributeCodec.DecodeTriangleFloatDeltas<N32, FFalse>;

        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N8, FTrue>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N10, FTrue>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltas<N16, FTrue>;
        t[i++] = &AttributeCodec.DecodeTriangleDeltasCustomSize<FTrue>;
        t[i++] = &AttributeCodec.DecodeTriangleFloatDeltas<N16, FTrue>;
        t[i++] = &AttributeCodec.DecodeTriangleFloatDeltas<N32, FTrue>;

        t[i++] = &AttributeCodec.DecodeCrossProduct<N8>;
        t[i++] = &AttributeCodec.DecodeCrossProduct<N10>;
        t[i++] = &AttributeCodec.DecodeCrossProduct<N16>;

        t[i++] = &AttributeCodec.DecodeTriangleTexCoords<short, AttributeCodec.TexS16>;
        t[i++] = &AttributeCodec.DecodeTriangleTexCoords<ushort, AttributeCodec.TexU16>;
        t[i++] = &AttributeCodec.DecodeTriangleTexCoordsFloat<N16, AttributeCodec.TexF16>;
        t[i++] = &AttributeCodec.DecodeTriangleTexCoordsFloat<N32, AttributeCodec.TexF32>;

        t[i++] = &AttributeCodec.DecodeFixedDistance<N8, FFalse>;
        t[i++] = &AttributeCodec.DecodeFixedDistance<N10, FFalse>;
        t[i++] = &AttributeCodec.DecodeFixedDistance<N16, FFalse>;
        t[i++] = &AttributeCodec.DecodeFixedDistance<N8, FTrue>;
        t[i++] = &AttributeCodec.DecodeFixedDistance<N10, FTrue>;
        t[i++] = &AttributeCodec.DecodeFixedDistance<N16, FTrue>;

        if (i != 0x71)
            throw new InvalidOperationException($"attribute decoder table has {i} entries, expected 0x71");

        return t;
    }
}
