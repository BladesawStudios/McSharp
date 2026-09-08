using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace McSharp.Internal;

internal static unsafe class FloatMath
{
    public const float Epsilon = 8f * 1.19209290e-07f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HalfToFloat(ushort raw)
    {
        if (FlushDenormalHalves && (raw & 0x7c00) == 0)
            raw &= 0x8000;
        return (float)BitConverter.UInt16BitsToHalf(raw);
    }

    internal static bool FlushDenormalHalves;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort FloatToHalf(float value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        if (FlushDenormalHalves && (bits & 0x7c00) == 0)
            bits &= 0x8000;
        return bits;
    }
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal unsafe struct Vec4h
{
    public fixed ushort Raw[4];
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct Vec4f
{
    [FieldOffset(0)] public fixed float F[4];
    [FieldOffset(0)] public fixed uint Raw[4];
}

[StructLayout(LayoutKind.Sequential, Size = 12)]
internal struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public readonly Vec3 Cross(in Vec3 other) => new Vec3(
        (Y * other.Z) - (Z * other.Y),
        (Z * other.X) - (X * other.Z),
        (X * other.Y) - (Y * other.X));

    public readonly float Dot(in Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public readonly float SquaredLength() => (X * X) + (Y * Y) + (Z * Z);

    public readonly float Length()
    {
        float squareDist = SquaredLength();
        return squareDist > FloatMath.Epsilon * FloatMath.Epsilon ? MathF.Sqrt(squareDist) : 0f;
    }

    public static Vec3 operator +(in Vec3 a, in Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(in Vec3 a, in Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(in Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

    public void Normalize()
    {
        float len = Length();
        if (len > 0f)
        {
            X /= len;
            Y /= len;
            Z /= len;
        }
    }
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct Vec2
{
    public float X;
    public float Y;
}
