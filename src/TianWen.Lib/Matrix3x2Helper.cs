using System;
using System.Numerics;

namespace TianWen.Lib;

public static class Matrix3x2Helper
{
    /// <summary>
    /// See <seealso href="https://stackoverflow.com/a/51921217/281306"/>
    /// </summary>
    /// <returns></returns>
    public static (Vector2 Scale, Vector2 Skew, Vector2 Translation, float Rotation) Decompose(this Matrix3x2 matrix)
    {
        var a = matrix.M11;
        var b = matrix.M12;
        var c = matrix.M21;
        var d = matrix.M22;
        var delta = a * d - b * c;

        var translation = new Vector2(matrix.M31, matrix.M32);
        // Apply the QR-like decomposition.
        if (a != 0 || b != 0)
        {
            var r = MathF.Sqrt(a * a + b * b);
            var rotation = b > 0 ? MathF.Acos(a / r) : -MathF.Acos(a / r);
            var scale = new Vector2(r, delta / r);
            var skew = new Vector2(MathF.Atan((a * c + b * d) / (r * r)), 0);

            return (scale, skew, translation, rotation);
        }
        else if (c != 0 || d != 0)
        {
            var s = MathF.Sqrt(c * c + d * d);
            var rotation =
              MathF.PI / 2 - (d > 0 ? MathF.Acos(-c / s) : -MathF.Acos(c / s));
            var scale = new Vector2(delta / s, s);
            var skew = new Vector2(0, MathF.Atan((a * c + b * d) / (s * s)));

            return (scale, skew, translation, rotation);
        }
        else
        {
            // a = b = c = d = 0
            return (new Vector2(), new Vector2(), new Vector2(), 0);
        }
    }
}