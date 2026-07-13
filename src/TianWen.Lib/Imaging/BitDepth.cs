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

        /// <summary>Maximum UNSIGNED value representable at this depth: 2^BitSize - 1 (255 for Int8,
        /// 65535 for Int16, 4294967295 for Int32). This is the container full-scale a frame stored at
        /// this depth occupies -- e.g. N.I.N.A. left-aligns a camera's native ADC output into a
        /// 16-bit container, so its lights and darks span [0, 65535]. It is therefore the divisor a
        /// tool (Astro Pixel Processor) uses to normalise such a frame to [0,1], and thus the factor
        /// to rescale a normalised master BACK into that frame's ADU domain for subtraction. Null for
        /// floating-point depths (no fixed integer container). Distinct from
        /// <see cref="MaxIntValue"/>, which is the SIGNED max (32767 for Int16) -- wrong here, since
        /// 16-bit astro frames are unsigned (FITS BZERO=32768).</summary>
        public long? UnsignedFullScale => bitDepth switch
        {
            BitDepth.Int8 => byte.MaxValue,
            BitDepth.Int16 => ushort.MaxValue,
            BitDepth.Int32 => uint.MaxValue,
            _ => null
        };

        public int BitSize => Math.Abs((int)bitDepth);
    }

    extension(BitDepth)
    {
        public static BitDepth? FromValue(int value) => Enum.IsDefined(typeof(BitDepth), value) ? (BitDepth)value : null;
    }
}