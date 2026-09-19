using System;

namespace McSharp.Internal;

internal static unsafe class VByte
{
    public static uint Decode(ref byte* data)
    {
        byte lead = *data++;

        if (lead < 0x80)
            return lead;

        uint result = (uint)(lead & 0x7f);

        for (; ; )
        {
            byte group = *data++;
            result = (uint)(group & 0x7f) | (result << 7);

            if (group < 0x80)
                break;
        }

        return result;
    }

    /// <summary>
    /// Decodes a varint and reports how many bytes it occupied, writing the value to
    /// <paramref name="output"/>.
    /// </summary>
    public static uint Decode(ref byte* data, ref uint output)
    {
        byte lead = *data++;

        if (lead < 0x80)
        {
            // A single byte is one byte long and holds the whole value; returning the value here
            // instead reported a zero length to every caller of a short varint.
            output = lead;
            return 1;
        }

        uint result = (uint)(lead & 0x7f);
        uint size = 1;

        for (; ; )
        {
            byte group = *data++;
            result = (uint)(group & 0x7f) | (result << 7);
            ++size;

            if (group < 0x80)
                break;
        }

        output = result;

        return size;
    }

    /// <summary>
    /// Number of bytes <see cref="EncodeReversed"/> will emit for <paramref name="value"/>.
    /// </summary>
    public static int ReversedLength(uint value)
    {
        int n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            ++n;
        }
        return n;
    }

    /// <summary>
    /// Inverse of <see cref="DecodeReversed"/>: seven bits per byte, least significant group first,
    /// continuation bit set on every byte but the last.
    /// </summary>
    public static int EncodeReversed(uint value, Span<byte> dst)
    {
        int n = 0;
        while (value >= 0x80)
        {
            dst[n++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dst[n++] = (byte)value;
        return n;
    }

    /// <summary>Number of bytes <see cref="EncodeForward"/> will emit for <paramref name="value"/>.</summary>
    public static int ForwardLength(uint value)
    {
        int n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            ++n;
        }
        return n;
    }

    /// <summary>
    /// Inverse of <see cref="Decode(ref byte*)"/>: seven bits per byte, most significant group
    /// first, continuation bit set on every byte but the last.
    /// </summary>
    public static int EncodeForward(uint value, Span<byte> dst)
    {
        int n = ForwardLength(value);

        for (int i = 0; i < n; ++i)
        {
            byte group = (byte)((value >> (7 * (n - 1 - i))) & 0x7f);
            dst[i] = i + 1 < n ? (byte)(group | 0x80) : group;
        }

        return n;
    }

    public static uint DecodeReversed(ref byte* data)
    {
        byte lead = *data++;

        if (lead < 0x80)
            return lead;

        uint result = (uint)(lead & 0x7f);
        int shift = 7;

        for (; ; )
        {
            byte group = *data++;
            result += (uint)(group & 0x7f) << shift;
            shift += 7;

            if (group < 0x80)
                break;
        }

        return result;
    }
}
