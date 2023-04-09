using System;

namespace Astap.Lib.Devices;

public enum BitDepth : sbyte
{
    Int8 = 8,
    Int16 = 16,
    Int32 = 32,
    Int64 = 64,
    Float32 = -32,
    Float64 = -64
}

public static class BitDepthEx
{
    public static bool IsFloatingPoint(this BitDepth @this) => Enum.IsDefined(@this) && @this < 0;

    public static bool IsIntegral(this BitDepth @this) => Enum.IsDefined(@this) && @this > 0;

    public static int MaxIntValue(this BitDepth @this) => @this switch
    {
        BitDepth.Int8 => sbyte.MaxValue,
        BitDepth.Int16 => short.MaxValue,
        BitDepth.Int32 => int.MaxValue,
        BitDepth.Int64 => throw new ArgumentException($"{@this} is not supported or too large"),
        _ => int.MinValue // unknown
    };

    public static BitDepth? FromValue(int value) => Enum.IsDefined(typeof(BitDepth), value) ? (BitDepth)value : null;
}