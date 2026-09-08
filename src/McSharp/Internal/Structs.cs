using System.Runtime.InteropServices;

namespace McSharp.Internal;

internal enum IndexFormat : uint
{
    U32 = 0,
    U16 = 1,
    Invalid = 3,
}

internal enum EncodingType : uint
{
    Type00 = 0,
    Type01 = 1,
    Type02 = 2,
    Type03 = 3,
    Invalid = 4,
}

internal enum ElementType : uint
{
    U8 = 0,
    U16 = 1,
}

internal enum ByteStreamEncoding : uint
{
    Table = 0,
    SingleStream = 1,
    Repeat = 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 0x70)]
internal unsafe struct Encoding1Struct
{
    public fixed ulong IndexMasks[4];
    public uint Field20;
}

[StructLayout(LayoutKind.Explicit, Size = 0x3080)]
internal unsafe struct DecodingContext
{
    [FieldOffset(0x00)] public ByteStreamEncoding Encoding;
    [FieldOffset(0x04)] public uint MaxIndexBitSize;
    [FieldOffset(0x08)] public uint RepeatEncodingValue;
    [FieldOffset(0x10)] public Encoding1Struct Enc1;
    [FieldOffset(0x80)] public fixed ushort DecodingTable[0x1800];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BufferView
{
    public byte* Ptr;
    public uint Field08;
    public uint Offset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DecodeInfo
{
    public int TotalSize;
    public int Count;
    public int ElementSize;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct VertexDecodeGroup
{
    public uint VertexCount;
    public uint BackRefOffset;

    public readonly ushort RawCount => (ushort)(VertexCount & 0xffff);

    public readonly ushort CopyCount => (ushort)(VertexCount >> 0x10);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VertexInfoTableInfo
{
    public uint Field00;
    public uint Field04;
    public uint Field08;
    public uint Field0C;
    public uint* Table;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AllocationSet
{
    public void* VertexCountStream;
    public void* BackrefCountStream;
    public void* BackrefOffsetStream;
    public void* BitStream;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VertexDecodingStreamSet
{
    public byte* VertexCountStream;

    public byte* BackrefCountStream;
    public byte* BackrefOffsetStream;
    public byte* BitStream;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VertexDecodingStreamSizes
{
    public int VertexCountStreamSize;
    public int BackrefCountStreamSize;
    public int BackrefOffsetStreamSize;
    public int BitStreamSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AttrStreamInfo
{
    public ElementType ElementType;
    public int TableCount;
    public int ElementCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WorkBuffer
{
    public byte* Addr;
    public uint Offset;
    public uint Capacity;
    public uint Size;
}

internal sealed unsafe class VertexStreamContext
{
    public uint AttrIndex;

    public ulong* IndexBufferTable;

    public uint* VertexBufferTable;
    public byte* OutputBuffer;
    public DecompContext? DecompContext;

    public readonly uint[] AttrFlags = new uint[15];
    public readonly uint[] AttrOffsets = new uint[15];
    public uint BaseVertexIndex;
    public uint VertexAlign;
    public uint AttrCount;
    public uint TotalVertexOutputSize;
    public uint VertexOutputSize;
    public uint MaxAttrBitSize;
    public readonly uint[] VertexBufferFlags = new uint[15];
    public readonly byte[] LocalAttrOffsets = new byte[15];
}
