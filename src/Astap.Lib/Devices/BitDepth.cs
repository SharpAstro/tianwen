using System;

namespace Astap.Lib.Devices;

public enum BitDepth
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

    public static BitDepth? FromValue(int value) => Enum.IsDefined(typeof(BitDepth), value) ? (BitDepth)value : null;
}