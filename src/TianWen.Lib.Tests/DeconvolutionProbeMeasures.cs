using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Tests;

/// <summary>
/// The measurements the deconvolution probes share (<see cref="DeconvolutionOracleCeilingProbe"/> on
/// synthetic blur, <see cref="SeeingSplitPairProbe"/> on a real seeing split): plane extraction, the
/// deployed estimator's width, the profile fit, background statistics and the ringing observer. One
/// copy, so a number in one probe's table means what it means in the other's.
/// </summary>
internal static class DeconvolutionProbeMeasures
{
    public static float Median(List<float> v)
    {
        if (v.Count == 0) return float.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    public static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    public static double Percentile(List<double> sorted, double p)
        => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

    /// <summary>Background MAD of a plane: median of |v - median|, which stars are too sparse to move.</summary>
    public static (float Median, float Mad) BackgroundStats(float[] plane)
    {
        var copy = new List<float>(plane.Length);
        foreach (var v in plane)
        {
            if (float.IsFinite(v)) copy.Add(v);
        }

        var med = Median(copy);
        var dev = new List<float>(copy.Count);
        foreach (var v in copy)
        {
            dev.Add(Math.Abs(v - med));
        }

        return (med, Median(dev));
    }

    /// <summary>A centred square of one channel, NaN zeroed (a TianWen master's canvas ring is zero
    /// where no frame covered it anyway).</summary>
    public static float[] CropCentre(Image image, int channel, int side)
    {
        var (_, width, height) = image.Shape;
        return Cut(image, channel, (width - side) / 2, (height - side) / 2, side, side);
    }

    /// <summary>A rectangle of one channel at the given origin, NaN zeroed.</summary>
    public static float[] Cut(Image image, int channel, int x0, int y0, int width, int height)
    {
        var (_, fullWidth, _) = image.Shape;
        var src = image.GetChannelSpan(channel);
        var plane = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = src[((y0 + y) * fullWidth) + x0 + x];
                plane[(y * width) + x] = float.IsFinite(v) ? v : 0f;
            }
        }

        return plane;
    }

    /// <summary>The whole channel, NaN zeroed as <see cref="CropCentre"/> does, for the estimator's
    /// whole-frame fallback.</summary>
    public static float[] FullPlane(Image image, int channel)
    {
        var (_, width, height) = image.Shape;
        var src = image.GetChannelSpan(channel);
        var plane = new float[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            var v = src[i];
            plane[i] = float.IsFinite(v) ? v : 0f;
        }

        return plane;
    }

    /// <summary>
    /// A one-channel <see cref="Image"/> over a copy of the plane, NORMALISED to a peak of 1 so it is
    /// unit-referred. The star detector converts a unit-referred image to 16-bit counts for its noise
    /// model and takes anything else as ADU already (<c>aduScale = HasUnitScalePeak ? 65535 : 1</c>), so
    /// a crop wrapped with its own peak as <c>MaxValue</c> sits between the two conventions whenever
    /// that peak is not near 1: a TianWen master is unit-referred by convention (median 0.5) with star
    /// peaks to 49, and such a crop found 19 stars at 4.2 px where the same pixels at 1/49 found 60 at
    /// 2.9 (E2.10a's first run). Widths and counts are scale-free, so normalising costs nothing.
    /// </summary>
    public static Image Wrap(float[] plane, int width, int height)
    {
        var max = 0f;
        foreach (var v in plane)
        {
            if (v > max) max = v;
        }

        var inv = max > 0f ? 1f / max : 1f;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = plane[(y * width) + x] * inv;
            }
        }

        return new Image([data], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    /// <summary>
    /// Median FWHM the deployed estimator reads off a plane, or NaN when it found nothing. Deliberately
    /// the estimator and not a profile fit, so every probe's table and H5's are in the same units.
    /// </summary>
    public static Task<(float Fwhm, int Stars)> MeasuredFwhmAsync(float[] plane, int side, CancellationToken ct)
        => MeasuredFwhmAsync(plane, side, side, ct);

    public static async Task<(float Fwhm, int Stars)> MeasuredFwhmAsync(float[] plane, int width, int height, CancellationToken ct)
    {
        var image = Wrap(plane, width, height);
        try
        {
            var m = await new HfdPsfEstimator().MeasureRadiusPxAsync(image, ct);
            return (m.Stars == 0 ? float.NaN : m.RadiusPx * 2f, m.Stars);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>
    /// The frame's PSF shape as <see cref="PsfProfileFit"/> reads it from its OWN detections, which is
    /// what a deployed estimator has: no truth, no star list handed in. The fit is null when the plane
    /// cannot support one, and the diagnostics say which check refused and what it saw.
    /// </summary>
    public static async Task<(PsfProfileFit.Result? Fit, PsfProfileFit.Diagnostics Diagnostics)> FitStarProfileAsync(
        float[] plane, int width, int height, PsfProfileFit.StarSelection selection, float snrMin, int maxStars, CancellationToken ct)
    {
        var image = Wrap(plane, width, height);
        try
        {
            var stars = await image.FindStarsAsync(channel: 0, snrMin: snrMin, maxStars: maxStars, cancellationToken: ct);
            var fit = PsfProfileFit.Measure(image, 0, stars, out var diagnostics, selection: selection);
            return (fit, diagnostics);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>One line of refusal detail: the check that fired and the counts it tested.</summary>
    public static string Describe(PsfProfileFit.Diagnostics d)
        => $"{d.Refusal} (stars {d.StarsOffered}, band {d.InBrightnessBand}, stacked {d.Stacked}, bins {d.FitBins}"
            + (double.IsFinite(d.MoffatLogRms) ? $", rms {d.MoffatLogRms:F2}" : "") + ")";

    /// <summary>
    /// The deepest undershoot below local background in the annulus around each star, in MAD units, and
    /// the fraction of stars past 1 MAD.
    /// </summary>
    public static (float MedianUndershoot, double FractionOverOneMad) Ringing(
        float[] plane, int side, IReadOnlyCollection<ImagedStar> stars, float background, float mad)
    {
        if (mad <= 0f || stars.Count == 0)
        {
            return (float.NaN, double.NaN);
        }

        var depths = new List<float>(stars.Count);
        var over = 0;
        foreach (var star in stars)
        {
            var inner = Math.Max(2.0, star.StarFWHM * 1.2);
            var outer = Math.Max(inner + 2.0, star.StarFWHM * 2.5);
            var r = (int)Math.Ceiling(outer);
            var cx = (int)Math.Round(star.XCentroid);
            var cy = (int)Math.Round(star.YCentroid);
            if (cx - r < 0 || cy - r < 0 || cx + r >= side || cy + r >= side)
            {
                continue;
            }

            var min = float.PositiveInfinity;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    var d = Math.Sqrt((dx * dx) + (dy * dy));
                    if (d < inner || d > outer)
                    {
                        continue;
                    }

                    var v = plane[((cy + dy) * side) + cx + dx];
                    if (v < min) min = v;
                }
            }

            if (!float.IsFinite(min))
            {
                continue;
            }

            var depth = (background - min) / mad;
            depths.Add(depth);
            if (depth > 1f) over++;
        }

        return depths.Count == 0
            ? (float.NaN, double.NaN)
            : (Median(depths), (double)over / depths.Count);
    }
}
