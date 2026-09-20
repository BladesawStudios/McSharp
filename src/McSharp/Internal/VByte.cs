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

    public static uint Decode(ref byte* data, ref uint output)
    {
        byte lead = *data++;

        if (lead < 0x80)
        {
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
