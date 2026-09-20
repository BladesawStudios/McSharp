using System;

namespace McSharp.Internal;

internal struct BitStreamWriter
{
    private int _count;

    public readonly int Count => _count;

    public static int ByteLength(int bits) => (bits + 7) / 8;
    public void Write(Span<byte> region, bool value)
    {
        int index = region.Length - 1 - (_count >> 3);

        if (index < 0)
            throw new MeshCodecException("Bit stream ran past the start of its region.");

        if (value)
            region[index] |= (byte)(0x80 >> (_count & 7));

        ++_count;
    }
}
