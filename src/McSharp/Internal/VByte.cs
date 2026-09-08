
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
            return lead;

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
