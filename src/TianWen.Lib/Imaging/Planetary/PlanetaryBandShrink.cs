using System;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One band's reading by <see cref="PlanetaryBandShrink"/>: its noise and signal spreads inside the disk and the soft threshold they set,
/// all in the plane's own units.
/// </summary>
public readonly record struct BandShrinkReading(double NoiseSigma, double SignalSigma, double Threshold);

/// <summary>A plane shrunk band by band (<see cref="PlanetaryBandShrink.Shrink"/>) and each band's reading, finest first.</summary>
public sealed record BandShrinkResult(float[] Shrunk, ImmutableArray<BandShrinkReading> Bands);

/// <summary>
/// BayesShrink in each a trous band (Chang, Yu and Vetterli 2000), the noise read off the master's own two halves (#1313): the halves hold
/// one planet with independent noise, so half their difference is the master's noise and nothing else, band by band, with no model of it.
/// In each band a coefficient is soft-thresholded at <c>T = sigma_n^2 / sigma_x</c>, with <c>sigma_x = sqrt(max(sigma_y^2 - sigma_n^2, 0))</c>,
/// both read inside <see cref="InsideRadii"/> of the disk; where the band holds no signal above its noise, <c>T</c> is its largest coefficient
/// and the band goes. The residual is kept. Parameter-free: nothing here is tuned.
/// </summary>
public static class PlanetaryBandShrink
{
    /// <summary>How far out, in the disk's radii, a band's noise and signal are read: the limb's blurred edge lies past it.</summary>
    public const double InsideRadii = 0.9;

    /// <summary>
    /// <paramref name="plane"/> shrunk in each of <paramref name="scales"/> a trous bands against the noise of half the difference between
    /// <paramref name="halfA"/> and <paramref name="halfB"/>, all three <paramref name="width"/> x <paramref name="height"/> on one grid and in
    /// one scale, <paramref name="disk"/> on that grid.
    /// </summary>
    public static BandShrinkResult Shrink(ReadOnlySpan<float> plane, ReadOnlySpan<float> halfA, ReadOnlySpan<float> halfB, int width, int height,
        MetricDisk disk, int scales = PlanetaryWaveletGains.Scales)
    {
        var n = width * height;
        if (plane.Length != n || halfA.Length != n || halfB.Length != n)
        {
            throw new ArgumentException($"The plane and both halves must be {width}x{height}.", nameof(plane));
        }

        var difference = new float[n];
        for (var i = 0; i < n; i++)
        {
            difference[i] = (halfA[i] - halfB[i]) / 2;
        }
        var bands = ATrousWaveletTransform.Decompose(plane, width, height, scales);
        var noise = ATrousWaveletTransform.Decompose(difference, width, height, scales);

        var inside = new bool[n];
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) < InsideRadii)
                {
                    inside[(y * width) + x] = true;
                    count++;
                }
            }
        }
        if (count == 0)
        {
            throw new ArgumentException("The disk holds no pixel of the plane.", nameof(disk));
        }

        var readings = new BandShrinkReading[scales];
        var thresholds = new float[scales];
        for (var j = 0; j < scales; j++)
        {
            var band = bands.Detail(j);
            var bandNoise = noise.Detail(j);
            double noisePower = 0, power = 0, peak = 0;
            for (var i = 0; i < n; i++)
            {
                peak = Math.Max(peak, Math.Abs(band[i]));
                if (inside[i])
                {
                    noisePower += (double)bandNoise[i] * bandNoise[i];
                    power += (double)band[i] * band[i];
                }
            }
            (noisePower, power) = (noisePower / count, power / count);
            var signalSigma = Math.Sqrt(Math.Max(power - noisePower, 0));
            var threshold = signalSigma > 0 ? noisePower / signalSigma : peak;
            readings[j] = new BandShrinkReading(Math.Sqrt(noisePower), signalSigma, threshold);
            thresholds[j] = (float)threshold;
        }

        var ones = Enumerable.Repeat(1f, scales).ToArray();
        return new BandShrinkResult(bands.Reconstruct(ones, thresholds), [.. readings]);
    }
}
