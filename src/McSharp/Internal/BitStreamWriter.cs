using System;

namespace McSharp.Internal;

/// <summary>
/// Inverse of <see cref="BitStreamReader"/> reading backwards.
/// </summary>
/// <remarks>
/// The backward reader starts eight bytes below the end of its region and walks down. Probing it
/// bit by bit shows the layout is plain most-significant-bit-first through descending bytes: read
/// <c>k</c> is bit <c>7 - (k % 8)</c> of byte <c>end - 1 - (k / 8)</c>, with no overlap between
/// reads. Writing one is therefore just setting those bits.
/// </remarks>
internal struct BitStreamWriter
{
    private int _count;

    /// <summary>Bits written so far.</summary>
    public readonly int Count => _count;

    /// <summary>Bytes a stream of <paramref name="bits"/> bits occupies at the end of its region.</summary>
    public static int ByteLength(int bits) => (bits + 7) / 8;

    /// <summary>
    /// Appends one bit. <paramref name="region"/> is the whole region the reader was handed; bits
    /// fill it from the last byte downwards.
    /// </summary>
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
