
namespace McSharp.Internal;

internal interface INum
{
    static abstract int N { get; }
}

internal readonly struct N1 : INum { public static int N => 1; }
internal readonly struct N2 : INum { public static int N => 2; }
internal readonly struct N3 : INum { public static int N => 3; }
internal readonly struct N4 : INum { public static int N => 4; }
internal readonly struct N8 : INum { public static int N => 8; }
internal readonly struct N10 : INum { public static int N => 10; }
internal readonly struct N16 : INum { public static int N => 16; }
internal readonly struct N24 : INum { public static int N => 24; }
internal readonly struct N30 : INum { public static int N => 30; }
internal readonly struct N32 : INum { public static int N => 32; }
internal readonly struct N48 : INum { public static int N => 48; }
internal readonly struct N64 : INum { public static int N => 64; }
internal readonly struct N96 : INum { public static int N => 96; }
internal readonly struct N128 : INum { public static int N => 128; }

internal interface IFlag
{
    static abstract bool V { get; }
}

internal readonly struct FTrue : IFlag { public static bool V => true; }
internal readonly struct FFalse : IFlag { public static bool V => false; }
