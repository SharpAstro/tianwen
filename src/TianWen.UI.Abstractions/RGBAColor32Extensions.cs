using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

public static class RGBAColor32Extensions
{
    extension(RGBAColor32)
    {
        /// <summary>
        /// Creates an <see cref="RGBAColor32"/> from float RGBA components (0..1 each).
        /// </summary>
        public static RGBAColor32 FromFloat(float r, float g, float b, float a) => new(
            (byte)MathF.Min(MathF.FusedMultiplyAdd(r, 255f, 0.5f), 255f),
            (byte)MathF.Min(MathF.FusedMultiplyAdd(g, 255f, 0.5f), 255f),
            (byte)MathF.Min(MathF.FusedMultiplyAdd(b, 255f, 0.5f), 255f),
            (byte)MathF.Min(MathF.FusedMultiplyAdd(a, 255f, 0.5f), 255f));
    }
}
