using System;
using System.Numerics;

namespace TianWen.Lib;

public static class Matrix3x2Helper
{
    extension(Matrix3x2 matrix)
    {
        /// <summary>
        /// Fits a <see cref="Matrix3x2"/> affine transform that maps <paramref name="source"/> points
        /// to <paramref name="dest"/> points using least squares (normal equations solved via Cramer's rule).
        /// <para>
        /// Solves two independent 3-variable systems:
        /// <c>dest.X = m11·src.X + m21·src.Y + m31</c> and
        /// <c>dest.Y = m12·src.X + m22·src.Y + m32</c>,
        /// where the design matrix columns are [X, Y, 1].
        /// </para>
        /// </summary>
        /// <param name="source">Source (input) 2D positions.</param>
        /// <param name="dest">Destination (output) 2D positions; must have the same count as <paramref name="source"/>.</param>
        /// <returns>The best-fit affine transform, or <c>null</c> if fewer than 3 points or the system is singular.</returns>
        public static Matrix3x2? FitAffineTransform(ReadOnlySpan<Vector2> source, ReadOnlySpan<Vector2> dest)
        {
            int n = source.Length;
            if (n < 3)
            {
                return null;
            }

            // Build A^T A (3x3) and A^T b (3x1) for each component
            double sXX = 0, sYY = 0, sXY = 0, sX = 0, sY = 0;
            double sDxX = 0, sDxY = 0, sDx = 0;
            double sDyX = 0, sDyY = 0, sDy = 0;

            for (int i = 0; i < n; i++)
            {
                double sx = source[i].X, sy = source[i].Y;
                double dx = dest[i].X, dy = dest[i].Y;

                sXX += sx * sx;
                sYY += sy * sy;
                sXY += sx * sy;
                sX += sx;
                sY += sy;

                sDxX += dx * sx;
                sDxY += dx * sy;
                sDx += dx;

                sDyX += dy * sx;
                sDyY += dy * sy;
                sDy += dy;
            }

            // A^T A = [[sXX, sXY, sX], [sXY, sYY, sY], [sX, sY, n]]
            // Solve via Cramer's rule
            double det = sXX * (sYY * n - sY * sY)
                       - sXY * (sXY * n - sY * sX)
                       + sX * (sXY * sY - sYY * sX);

            if (Math.Abs(det) < 1e-12)
            {
                return null;
            }

            double invDet = 1.0 / det;

            // Cofactor matrix for inversion
            double c00 = sYY * n - sY * sY;
            double c01 = -(sXY * n - sY * sX);
            double c02 = sXY * sY - sYY * sX;
            double c10 = -(sXY * n - sX * sY);
            double c11 = sXX * n - sX * sX;
            double c12 = -(sXX * sY - sXY * sX);
            double c20 = sXY * sY - sYY * sX;
            double c21 = -(sXX * sY - sX * sXY);
            double c22 = sXX * sYY - sXY * sXY;

            // Solve for X component: [m11, m21, m31]
            double m11 = (c00 * sDxX + c01 * sDxY + c02 * sDx) * invDet;
            double m21 = (c10 * sDxX + c11 * sDxY + c12 * sDx) * invDet;
            double m31 = (c20 * sDxX + c21 * sDxY + c22 * sDx) * invDet;

            // Solve for Y component: [m12, m22, m32]
            double m12 = (c00 * sDyX + c01 * sDyY + c02 * sDy) * invDet;
            double m22 = (c10 * sDyX + c11 * sDyY + c12 * sDy) * invDet;
            double m32 = (c20 * sDyX + c21 * sDyY + c22 * sDy) * invDet;

            return new Matrix3x2(
                (float)m11, (float)m12,
                (float)m21, (float)m22,
                (float)m31, (float)m32
            );
        }

        /// <summary>
        /// See <seealso href="https://stackoverflow.com/a/51921217/281306"/>
        /// </summary>
        /// <returns></returns>
        public (Vector2 Scale, Vector2 Skew, Vector2 Translation, float Rotation) Decompose()
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
}
