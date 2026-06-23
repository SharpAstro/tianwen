using System;
using System.Numerics;

namespace TianWen.Lib.Stat;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey complex FFT (power-of-two length). The existing
/// <see cref="FFT"/> class is a real-input, half-spectrum analyser with a built-in normalisation scale;
/// phase-correlation alignment needs the <i>full</i> complex spectrum with phases preserved exactly, plus
/// an inverse transform, so this is a separate minimal primitive rather than a reuse of that class.
/// <para>
/// <see cref="Forward"/> is unnormalised; <see cref="Inverse"/> divides by N, so
/// <c>Inverse(Forward(x)) == x</c>.
/// </para>
/// </summary>
public static class ComplexFft
{
    /// <summary>True when <paramref name="n"/> is a positive power of two.</summary>
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Unnormalised forward FFT, in place.</summary>
    public static void Forward(Span<Complex> data) => Transform(data, inverse: false);

    /// <summary>Inverse FFT (normalised by 1/N), in place.</summary>
    public static void Inverse(Span<Complex> data) => Transform(data, inverse: true);

    private static void Transform(Span<Complex> a, bool inverse)
    {
        var n = a.Length;
        if (n <= 1)
        {
            return;
        }

        if (!IsPowerOfTwo(n))
        {
            throw new ArgumentException($"FFT length must be a power of two, got {n}.", nameof(a));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        // Danielson-Lanczos butterflies; twiddles advanced by complex multiply per stage.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = (inverse ? 2.0 : -2.0) * Math.PI / len;
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            var half = len >> 1;
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < half; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + half] * w;
                    a[i + k] = u + v;
                    a[i + k + half] = u - v;
                    w *= wlen;
                }
            }
        }

        if (inverse)
        {
            var invN = 1.0 / n;
            for (var i = 0; i < n; i++)
            {
                a[i] *= invN;
            }
        }
    }
}
