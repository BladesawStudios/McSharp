using System.Runtime.InteropServices;

namespace McSharp;

public enum CodecType : uint
{
    Null = 0,
    ZStandard = 1,
    MeshCodec = 2,
    Invalid = 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 3)]
public struct U24
{
    private ushort _low;
    private byte _high;

    public readonly uint Value => (uint)(_low | (_high << 0x10));

    public void Set(uint value)
    {
        _low = (ushort)value;
        _high = (byte)(value >> 0x10);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 6)]
public struct ResFrameSize
{
    public U24 StreamOffset;
    public U24 EndOffset;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
public struct ResCompressionHeader
{
    public ushort Flags;
    public ResFrameSize SizeInfo;

    public readonly CodecType GetCodecType() => (CodecType)(Flags & 3);
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x22)]
public struct ResMeshCodecHeader
{
    public const uint Magic = 0x48534d46;

    public uint MagicValue;
    public uint Version;
    public uint WorkMemSize;
    public uint Reserved0C;
    public uint IndexOutputSize;
    public uint VertexOutputSize;
    public byte IndexAlign;
    public byte VertexAlign;
    public ResCompressionHeader CompHeader;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0xc)]
public struct ResMeshCodecPackageHeader
{
    public const uint Magic = 0x4b50434d;

    public uint MagicValue;
    public byte VersionMicro;
    public byte VersionMinor;
    public ushort VersionMajor;
    public uint Flags;

    public readonly uint GetDecompressedSize() => (Flags >> 5) << (int)(Flags & 0xf);
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x1c)]
public struct ResChunkHeader
{
    public uint ChunkHash;
    public uint VertexOutputSize;
    public uint IndexOutputSize;
    public uint DecompressedSize;
    public uint WorkMemSize;
    public ResCompressionHeader CompHeader;
}
