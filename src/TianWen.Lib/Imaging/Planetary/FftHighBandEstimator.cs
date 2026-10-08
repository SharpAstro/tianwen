using System;
using System.Numerics;
using TianWen.Lib.Geometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A band of spatial frequency, in cycles a pixel, from <paramref name="Low"/> (inclusive) to <paramref name="High"/>.</summary>
public readonly record struct FrequencyBand(double Low, double High);

/// <summary>
/// One frame's power in some bands of spatial frequency (<see cref="FftHighBandEstimator.Measure"/>): each band's share of the
/// windowed luminance's mean square, the noise variance a pixel read off the frequency plane's corners, and the mean luminance.
/// </summary>
/// <param name="Power">Each band's power, in the frame's units squared.</param>
/// <param name="NoiseShare">The share of each band a white noise of one unit variance a pixel puts there.</param>
/// <param name="CornerNoise">A pixel's noise variance, read off the corners of the frequency plane (past 0.5 cycles a pixel).</param>
/// <param name="Mean">The region's mean luminance.</param>
public readonly record struct BandPowers(double[] Power, double[] NoiseShare, double CornerNoise, double Mean)
{
    /// <summary>
    /// Band <paramref name="band"/>'s detail: its power less the corners' noise when <paramref name="debias"/>, over the mean
    /// luminance squared when <paramref name="normalizeBrightness"/>.
    /// </summary>
    public double Detail(int band, bool debias, bool normalizeBrightness)
    {
        var detail = Power[band] - (debias ? CornerNoise * NoiseShare[band] : 0);
        return normalizeBrightness ? detail / ((Mean * Mean) + 1e-12) : detail;
    }
}

/// <summary>
/// A frame's detail in one band of spatial frequency: the power of its luminance over the region in that band (Hann-windowed,
/// its mean taken out), less the white noise's share of it when debiased, over the mean luminance squared
/// (docs/plans/planetary-restoration.md, R4). Seeing takes power from the fine frequencies first, so a band where the planet's
/// detail stands above the noise ranks frames by their blur, where the Laplacian's finest scale is mostly noise on an 8-bit
/// capture.
/// <para>
/// The noise is read off the frame itself: the corners of its frequency plane, past 0.5 cycles a pixel, taken as noise alone.
/// That holds where the telescope's cutoff or the seeing leaves nothing there; where it does not, it takes a little of the
/// frame's own detail with it. On a capture whose brightness and noise are steady the debiasing subtracts nearly the same from
/// every frame and hardly changes a ranking.
/// </para>
/// </summary>
/// <param name="band">The band, in cycles a pixel.</param>
/// <param name="debias">Take the corners' noise out of the band's power.</param>
/// <param name="normalizeBrightness">Divide by the mean luminance squared, as <see cref="LaplacianEnergyEstimator"/> does.</param>
public sealed class FftHighBandEstimator(FrequencyBand band, bool debias = false, bool normalizeBrightness = true) : IFrameQualityEstimator
{
    /// <summary>Where the corners begin, in cycles a pixel: the frequency plane past this radius is read as noise.</summary>
    public const double CornerRadius = 0.5;

    /// <inheritdoc/>
    public string CacheKey => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"fft {band.Low:R} {band.High:R}{(debias ? " debiased" : "")}{(normalizeBrightness ? "" : " raw")}");

    /// <inheritdoc/>
    public float Score(Image frame, PixelRect region)
    {
        return Measure(frame, region, [band]) is { } powers ? (float)powers.Detail(0, debias, normalizeBrightness) : 0f;
    }

    /// <summary>
    /// Every band's power in <paramref name="frame"/>'s luminance over <paramref name="region"/> (the whole frame when empty),
    /// from one transform. Null when the region is too small to hold a band.
    /// </summary>
    public static BandPowers? Measure(Image frame, PixelRect region, ReadOnlySpan<FrequencyBand> bands)
    {
        if (region.IsEmpty)
        {
            region = LumaProxy.FullFrame(frame);
        }
        var (rw, rh) = (region.Width, region.Height);
        if (rw < 8 || rh < 8)
        {
            return null;
        }

        var luma = new float[rw * rh];
        LumaProxy.Fill(frame, region, luma);
        double mean = 0;
        foreach (var v in luma)
        {
            mean += v;
        }
        mean /= luma.Length;

        var n = 1;
        while (n < Math.Max(rw, rh))
        {
            n <<= 1;
        }
        var field = new Complex[n * n];
        double windowEnergy = 0;
        for (var y = 0; y < rh; y++)
        {
            var wy = 0.5 - (0.5 * Math.Cos(2 * Math.PI * (y + 0.5) / rh));
            for (var x = 0; x < rw; x++)
            {
                var w = wy * (0.5 - (0.5 * Math.Cos(2 * Math.PI * (x + 0.5) / rw)));
                field[(y * n) + x] = (luma[(y * rw) + x] - mean) * w;
                windowEnergy += w * w;
            }
        }
        Fft2D.Forward(field, n, n);

        // A white noise of variance s a pixel puts s times the window's energy into every frequency; Parseval puts the windowed
        // mean square at the sum of |F|^2 over n^2 and that energy. So a band's power is its |F|^2 over both, and the noise's
        // share of it is the band's count of frequencies over n^2.
        var sums = new double[bands.Length];
        var counts = new long[bands.Length];
        double corner = 0;
        long cornerCount = 0;
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                var c = field[(ky * n) + kx];
                var p = (c.Real * c.Real) + (c.Imaginary * c.Imaginary);
                for (var b = 0; b < bands.Length; b++)
                {
                    if (f >= bands[b].Low && f < bands[b].High)
                    {
                        sums[b] += p;
                        counts[b]++;
                    }
                }
                if (f >= CornerRadius)
                {
                    corner += p;
                    cornerCount++;
                }
            }
        }
        var grid = (double)n * n;
        var power = new double[bands.Length];
        var share = new double[bands.Length];
        for (var b = 0; b < bands.Length; b++)
        {
            power[b] = sums[b] / grid / windowEnergy;
            share[b] = counts[b] / grid;
        }
        var cornerNoise = cornerCount > 0 ? corner / windowEnergy / cornerCount : 0;
        return new BandPowers(power, share, cornerNoise, mean);
    }
}
