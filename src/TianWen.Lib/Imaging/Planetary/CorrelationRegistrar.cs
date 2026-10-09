using System;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Registers square crops onto one target crop by their cross-correlation, and moves each by the Fourier shift theorem
/// (docs/plans/planetary-restoration.md, R4). The correlation is not whitened, so the disk's broad shape carries it and the
/// peak lands where a limb fit would put the disk; a phase correlation weights every frequency alike and on an 8-bit frame
/// follows the noise (R2: the stacker's aligner 0.45 px off a synthetic truth where a single frame's limb fit was 0.04 px).
/// A limb fit costs about three seconds of processor time a frame, this a few milliseconds. The target is fixed; any number of
/// crops can be registered on it at once.
/// </summary>
public sealed class CorrelationRegistrar
{
    private readonly Complex[] _target;

    /// <summary>A registrar onto <paramref name="target"/>, a crop of <paramref name="size"/> squared (a power of two), row-major.</summary>
    public CorrelationRegistrar(ReadOnlySpan<float> target, int size)
    {
        if (size < 8 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A crop's edge must be a power of two, at least 8.");
        }
        ArgumentOutOfRangeException.ThrowIfNotEqual(target.Length, size * size, nameof(target));
        Size = size;
        _target = Spectrum(target, size, out _);
    }

    /// <summary>The crop's edge.</summary>
    public int Size { get; }

    /// <summary>
    /// <paramref name="crop"/> moved onto the target, and where it lay relative to the target: the target's content sits at
    /// (x + <c>Dx</c>, y + <c>Dy</c>) in <paramref name="crop"/>. The move is circular, so a crop should have the same
    /// background on its opposite edges.
    /// </summary>
    public (float[] Moved, double Dx, double Dy) Register(ReadOnlySpan<float> crop)
    {
        var n = Size;
        ArgumentOutOfRangeException.ThrowIfNotEqual(crop.Length, n * n, nameof(crop));
        var spectrum = Spectrum(crop, n, out var mean);

        // c(s) = sum over x of target(x) crop(x + s): its peak is the crop's displacement. The whole-pixel peak and a parabola
        // through its neighbours start the search; the parabola alone is biased by hundredths of a pixel, because a disk's
        // correlation peaks in a rounded cone, so Newton's method then climbs the correlation itself, evaluated exactly
        // between the pixels from its spectrum.
        // The climb below reads the spectrum again, so the inverse transform runs on a second array, filled in the same pass.
        var cross = new Complex[n * n];
        var correlation = new Complex[n * n];
        for (var i = 0; i < cross.Length; i++)
        {
            correlation[i] = cross[i] = spectrum[i] * Complex.Conjugate(_target[i]);
        }
        Fft2D.Inverse(correlation, n, n);
        var (px, py, peak) = (0, 0, double.NegativeInfinity);
        for (var i = 0; i < correlation.Length; i++)
        {
            if (correlation[i].Real > peak)
            {
                (peak, px, py) = (correlation[i].Real, i % n, i / n);
            }
        }
        double At(int x, int y) => correlation[(((y % n) + n) % n * n) + (((x % n) + n) % n)].Real;
        var dx = Wrapped(px, n) + Vertex(At(px - 1, py), peak, At(px + 1, py));
        var dy = Wrapped(py, n) + Vertex(At(px, py - 1), peak, At(px, py + 1));
        (dx, dy) = PhaseCorrelation.ClimbPeak(cross, n, n, dx, dy);

        // moved(x) = crop(x + d), whose transform is the crop's times exp(+2 pi i k.d / n).
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                spectrum[(ky * n) + kx] *= Complex.FromPolarCoordinates(1, 2 * Math.PI * ((fx * dx) + (fy * dy)));
            }
        }
        Fft2D.Inverse(spectrum, n, n);
        var moved = new float[n * n];
        for (var i = 0; i < moved.Length; i++)
        {
            moved[i] = (float)(spectrum[i].Real + mean);
        }
        return (moved, dx, dy);
    }

    /// <summary>
    /// A square of <paramref name="size"/> from <paramref name="plane"/> (<paramref name="width"/> x <paramref name="height"/>),
    /// its corner at (<paramref name="x0"/>, <paramref name="y0"/>); a pixel outside the plane reads zero.
    /// </summary>
    public static float[] Crop(ReadOnlySpan<float> plane, int width, int height, int x0, int y0, int size)
    {
        var crop = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var sy = y + y0;
            if (sy < 0 || sy >= height)
            {
                continue;
            }
            for (var x = 0; x < size; x++)
            {
                var sx = x + x0;
                if (sx >= 0 && sx < width)
                {
                    crop[(y * size) + x] = plane[(sy * width) + sx];
                }
            }
        }
        return crop;
    }

    // The crop's transform, its mean taken out first so the background does not correlate with itself.
    private static Complex[] Spectrum(ReadOnlySpan<float> crop, int n, out double mean)
    {
        mean = 0;
        foreach (var v in crop)
        {
            mean += v;
        }
        mean /= crop.Length;
        var field = new Complex[n * n];
        for (var i = 0; i < field.Length; i++)
        {
            field[i] = crop[i] - mean;
        }
        Fft2D.Forward(field, n, n);
        return field;
    }

    private static int Wrapped(int k, int n) => k < n / 2 ? k : k - n;

    // The vertex of the parabola through three samples a pixel apart, relative to the middle one.
    private static double Vertex(double left, double middle, double right)
    {
        var curvature = left - (2 * middle) + right;
        return curvature < 0 ? 0.5 * (left - right) / curvature : 0;
    }
}
