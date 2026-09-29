using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>One ring of an averaged power spectrum: its frequency, the mean power over frames, and that mean's standard error.</summary>
/// <param name="CyclesPerPixel">The ring's spatial frequency.</param>
/// <param name="Power">The mean power, in the plane's units squared per pixel: white noise of variance s^2 reads s^2.</param>
/// <param name="StandardError">The standard error of <paramref name="Power"/>, from the frames' spread.</param>
/// <param name="Samples">How many spectrum samples the ring averages in each frame.</param>
public readonly record struct SpectrumRing(double CyclesPerPixel, double Power, double StandardError, int Samples);

/// <summary>
/// The averaged power spectrum of a capture's frames, ring by ring (docs/plans/planetary-restoration.md, R1). It averages each
/// frame's POWER, never the power of an averaged frame, and that keeps the short exposures' speckle. Their transfer function
/// reaches the pupil's cutoff D / lambda and is exactly zero past it, so beyond the cutoff only the noise is left, and the
/// noise is flat. Each frame loses its sky (the mean of the tapered border, so the disk must sit in the window's flat middle)
/// and is tapered at its edges (a Tukey window) before the transform. The axes are left out, since row and column banding and
/// the taper's own leakage put power there.
/// </summary>
public sealed class PlanetaryPowerSpectrum
{
    private readonly int _width;
    private readonly int _height;
    private readonly float[] _windowX;
    private readonly float[] _windowY;
    private readonly double _windowEnergy;
    private readonly Complex[] _field;
    private Complex[]? _other;
    private readonly int[] _ringOf;
    private readonly int[] _ringSamples;
    private readonly double[] _frameRing;
    private readonly double[] _ringSum;
    private readonly double[] _ringSquareSum;

    /// <param name="width">The plane's width.</param>
    /// <param name="height">The plane's height.</param>
    /// <param name="taper">The fraction of each side the window tapers over.</param>
    public PlanetaryPowerSpectrum(int width, int height, double taper = 0.1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 8);
        _width = width;
        _height = height;
        var n = 1;
        while (n < Math.Max(width, height))
        {
            n <<= 1;
        }
        Size = n;
        _windowX = Tukey(width, taper);
        _windowY = Tukey(height, taper);
        _windowEnergy = SumOfSquares(_windowX) * SumOfSquares(_windowY);
        _field = new Complex[n * n];

        _ringOf = new int[n * n];
        var rings = 0;
        for (var ky = 0; ky < n; ky++)
        {
            var sy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var sx = kx < n / 2 ? kx : kx - n;
                if (Math.Abs(sx) <= 1 || Math.Abs(sy) <= 1)
                {
                    _ringOf[(ky * n) + kx] = -1;
                    continue;
                }
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                _ringOf[(ky * n) + kx] = ring;
                rings = Math.Max(rings, ring + 1);
            }
        }
        _ringSamples = new int[rings];
        foreach (var ring in _ringOf)
        {
            if (ring >= 0)
            {
                _ringSamples[ring]++;
            }
        }
        _frameRing = new double[rings];
        _ringSum = new double[rings];
        _ringSquareSum = new double[rings];
    }

    /// <summary>The transform's side, the smallest power of two that holds the plane: a ring is 1 / Size cycles a pixel wide.</summary>
    public int Size { get; }

    /// <summary>How many frames have been added.</summary>
    public int Frames { get; private set; }

    /// <summary>Adds one frame's plane (row-major, the width and height this was made for).</summary>
    public void Add(ReadOnlySpan<float> plane)
    {
        Transform(plane, _field);

        Array.Clear(_frameRing);
        for (var i = 0; i < _field.Length; i++)
        {
            var ring = _ringOf[i];
            if (ring >= 0)
            {
                var value = _field[i];
                _frameRing[ring] += (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
            }
        }
        for (var ring = 0; ring < _frameRing.Length; ring++)
        {
            if (_ringSamples[ring] > 0)
            {
                var mean = _frameRing[ring] / (_ringSamples[ring] * _windowEnergy);
                _ringSum[ring] += mean;
                _ringSquareSum[ring] += mean * mean;
            }
        }
        Frames++;
    }

    /// <summary>
    /// The cross-spectrum of two stacks of the same capture from DISJOINT frames, ring by ring: the real part of
    /// <c>A conj(B)</c>, which the two stacks' independent noise leaves zero in expectation, so what stands above zero is detail
    /// both halves hold, with no floor to estimate (docs/plans/planetary-restoration.md, T2). A ring's standard error comes
    /// from the scatter of its own samples, counted as independent only once per Hermitian pair and per resolution cell of the
    /// plane, which a transform padded past the plane oversamples. Does not touch the averaged spectrum's frames.
    /// </summary>
    public ImmutableArray<SpectrumRing> Cross(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var other = _other ??= new Complex[_field.Length];
        Transform(a, _field);
        Transform(b, other);

        var rings = _ringSamples.Length;
        var sum = new double[rings];
        var squareSum = new double[rings];
        for (var i = 0; i < _field.Length; i++)
        {
            var ring = _ringOf[i];
            if (ring >= 0)
            {
                var p = _field[i];
                var q = other[i];
                var c = ((p.Real * q.Real) + (p.Imaginary * q.Imaginary)) / _windowEnergy;
                sum[ring] += c;
                squareSum[ring] += c * c;
            }
        }

        var independence = (double)_width * _height / (2.0 * Size * Size);
        var builder = ImmutableArray.CreateBuilder<SpectrumRing>(rings);
        for (var ring = 0; ring < rings; ring++)
        {
            var samples = _ringSamples[ring];
            if (samples < 2)
            {
                continue;
            }
            var mean = sum[ring] / samples;
            var variance = Math.Max(0, (squareSum[ring] / samples) - (mean * mean));
            var effective = Math.Max(1, samples * independence);
            builder.Add(new SpectrumRing((double)ring / Size, mean, Math.Sqrt(variance / effective), samples));
        }
        return builder.ToImmutable();
    }

    // Sky-subtracts, tapers and transforms one plane into the transform-sized destination.
    private void Transform(ReadOnlySpan<float> plane, Complex[] destination)
    {
        var count = _width * _height;
        ArgumentOutOfRangeException.ThrowIfNotEqual(plane.Length, count);

        // The sky is the mean of the tapered border: whatever constant is left there after the subtraction is multiplied by the
        // window and leaks the window's own spectrum into every ring. A low percentile (tried first) left 1.6 sigma of it, which
        // read as signal out to 0.1 cycles a pixel on pure white noise; and on an 8-bit sky clipped at black it is the MEAN, not
        // the median, that the clipped samples average to.
        var skySum = 0.0;
        var skyCount = 0;
        for (var y = 0; y < _height; y++)
        {
            var row = plane.Slice(y * _width, _width);
            var edgeRow = _windowY[y] < 1f;
            for (var x = 0; x < _width; x++)
            {
                if (edgeRow || _windowX[x] < 1f)
                {
                    skySum += row[x];
                    skyCount++;
                }
            }
        }
        var sky = (float)(skySum / Math.Max(1, skyCount));

        var n = Size;
        Array.Clear(destination);
        for (var y = 0; y < _height; y++)
        {
            var wy = _windowY[y];
            var row = plane.Slice(y * _width, _width);
            for (var x = 0; x < _width; x++)
            {
                destination[(y * n) + x] = new Complex((row[x] - sky) * _windowX[x] * wy, 0);
            }
        }
        Fft2D.Forward(destination, n, n);
    }

    /// <summary>The rings so far, lowest frequency first, each with the mean power over frames and its standard error.</summary>
    public ImmutableArray<SpectrumRing> Rings()
    {
        var builder = ImmutableArray.CreateBuilder<SpectrumRing>(_ringSum.Length);
        for (var ring = 0; ring < _ringSum.Length; ring++)
        {
            if (_ringSamples[ring] == 0 || Frames == 0)
            {
                continue;
            }
            var mean = _ringSum[ring] / Frames;
            var variance = Math.Max(0, (_ringSquareSum[ring] / Frames) - (mean * mean));
            var standardError = Frames > 1 ? Math.Sqrt(variance / (Frames - 1)) : double.PositiveInfinity;
            builder.Add(new SpectrumRing((double)ring / Size, mean, standardError, _ringSamples[ring]));
        }
        return builder.ToImmutable();
    }

    private static float[] Tukey(int length, double taper)
    {
        var window = new float[length];
        for (var i = 0; i < length; i++)
        {
            var t = (i + 0.5) / length;
            var edge = Math.Min(t, 1 - t);
            window[i] = edge >= taper ? 1f : (float)(0.5 * (1 - Math.Cos(Math.PI * edge / taper)));
        }
        return window;
    }

    private static double SumOfSquares(ReadOnlySpan<float> values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            sum += v * v;
        }
        return sum;
    }
}
