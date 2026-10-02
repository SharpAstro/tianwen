using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using TianWen.Lib.Imaging.Sources;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The one fill the starless plate uses, and the one <see cref="StarlessFillProbe"/> measures: a push-pull pyramid per
/// channel (<see cref="PushPullFill"/>), then noise at the channel's local rms with its measured lag-1 correlation, so
/// a filled hole has the plate's grain. Deterministic: the noise is a counter-based hash of the seed, the channel and
/// the pixel.
/// </summary>
internal static class HoleFill
{
    /// <summary>Fills <paramref name="holes"/> in every plane in place; returns each channel's measured noise correlation.</summary>
    public static ImmutableArray<float> Fill(
        float[][] planes, int width, int height, BitMatrix holes, BitMatrix? absent, double fwhm, int seed, CancellationToken ct)
    {
        var correlation = new float[planes.Length];
        if (!holes.Any())
        {
            return correlation.ToImmutableArray();
        }
        var excluded = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                excluded[y, x] = holes[y, x] || (absent is { } a && a[y, x]);
            }
        }
        for (var c = 0; c < planes.Length; c++)
        {
            ct.ThrowIfCancellationRequested();
            var plane = planes[c];
            var skyMap = BackgroundMap.Estimate(plane, width, height, excluded, new BackgroundMapOptions(BlockSize: PointSourceFinder.SkyBlockFor(fwhm)));
            var rms = new float[plane.Length];
            skyMap.FillRms(rms);
            var rho = Math.Clamp(LagOneCorrelation(plane, width, height, rms, excluded), 0f, 0.7f);
            correlation[c] = rho;
            var k = rho > 1e-3f ? (1.0 - Math.Sqrt(1.0 - 2.0 * rho * rho)) / (2.0 * rho) : 0.0;
            var norm = 1.0 + 2.0 * k * k;
            PushPullFill.Fill(plane, width, height, holes, absent);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!holes[y, x] || (absent is { } a && a[y, x]))
                    {
                        continue;
                    }
                    double g = 0;
                    for (var j = -1; j <= 1; j++)
                    {
                        var ky = j == 0 ? 1.0 : k;
                        for (var i = -1; i <= 1; i++)
                        {
                            var kx = i == 0 ? 1.0 : k;
                            if (kx * ky != 0)
                            {
                                g += kx * ky * HashGaussian(seed, c, x + i, y + j);
                            }
                        }
                    }
                    plane[y * width + x] += (float)(rms[y * width + x] * g / norm);
                }
            }
        }
        return correlation.ToImmutableArray();
    }

    // 1 - var(first difference) / (2 var) over present, unexcluded sky, both robust and in the rms map's units.
    private static float LagOneCorrelation(float[] plane, int width, int height, float[] rms, BitMatrix excluded)
    {
        var diffs = new List<float>();
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x + 1 < width; x += 2)
            {
                if (excluded[y, x] || excluded[y, x + 1] || !(rms[y * width + x] > 0))
                {
                    continue;
                }
                diffs.Add((plane[y * width + x + 1] - plane[y * width + x]) / rms[y * width + x]);
            }
        }
        var s = PointSourceFinder.RobustSigma(diffs.ToArray(), null, 1);
        return float.IsFinite(s) ? 1f - s * s / 2f : 0f;
    }

    // A standard normal from a counter-based hash (SplitMix64 then Box-Muller): the same pixel draws the same value on
    // any thread.
    private static double HashGaussian(int seed, int channel, int x, int y)
    {
        var key = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)channel << 58) ^ ((ulong)(uint)y << 29) ^ (uint)x);
        var a = SplitMix(ref key);
        var b = SplitMix(ref key);
        var u1 = ((a >> 11) + 0.5) / (1UL << 53);
        var u2 = (b >> 11) / (double)(1UL << 53);
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static ulong SplitMix(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
