using System;

namespace TianWen.Lib.Imaging;

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
    extension(BitDepth bitDepth)
    {
        public bool IsFloatingPoint => Enum.IsDefined(bitDepth) && bitDepth < 0;

        public bool IsIntegral => Enum.IsDefined(bitDepth) && bitDepth > 0;

        public int? MaxIntValue => bitDepth switch
        {
            BitDepth.Int8 => sbyte.MaxValue,
            BitDepth.Int16 => short.MaxValue,
            BitDepth.Int32 => int.MaxValue,
            BitDepth.Int64 => throw new ArgumentException($"{bitDepth} is not supported or too large"),
            _ => null // unknown
        };

        public int BitSize => Math.Abs((int)bitDepth);
    }

    extension(BitDepth)
    {
        public static BitDepth? FromValue(int value) => Enum.IsDefined(typeof(BitDepth), value) ? (BitDepth)value : null;
    }
}