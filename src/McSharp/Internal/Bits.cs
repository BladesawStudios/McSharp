using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace McSharp.Internal;

internal static class Bits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Swap(ulong value) => BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Swap(uint value) => BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Clz(ulong value)
    {
        if (value == 0) return 0x20;
        return (uint)BitOperations.LeadingZeroCount(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Clz(uint value)
    {
        if (value == 0) return 0x20;
        return (uint)BitOperations.LeadingZeroCount(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Popcount(uint value) => (uint)BitOperations.PopCount(value);
}
