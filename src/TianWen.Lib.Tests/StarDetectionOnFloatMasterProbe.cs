using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Measures star detection on a real INTEGRATED MASTER (float pixels), as opposed to the single
/// camera sub-frames the detector was tuned on.
/// </summary>
/// <remarks>
/// <para>Env-gated on <c>TIANWEN_STAR_PROBE_FITS</c> (a path to the frame) so it skips by default and
/// carries no absolute path in source. Point it at the file that motivated the investigation.</para>
/// <para>It reports both error directions, because "false positives" and "missed bright stars" have
/// opposite causes and the eye conflates them: a detection sitting on flat sky is scored against its
/// own neighbourhood, and separately the brightest local maxima in the frame are checked for having
/// been found at all. A detector that is merely mis-thresholded shows one; a detector reading the
/// wrong scale shows both.</para>
/// </remarks>
[Collection("Imaging")]
public class StarDetectionOnFloatMasterProbe(ITestOutputHelper output)
{
    private const string PathVar = "TIANWEN_STAR_PROBE_FITS";

    // What the viewer itself asks for (AstroImageDocument.DetectStarsAsync), so the probe measures
    // the behaviour that was actually reported rather than a tuning of its own.
    private const float ViewerSnrMin = 10f;
    private const int ViewerMaxStars = 2000;

    [Fact]
    public async Task ReportDetectionQualityOnARealMaster()
    {
        var path = Environment.GetEnvironmentVariable(PathVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), $"{PathVar} not set");
        Assert.SkipUnless(System.IO.File.Exists(path), $"{PathVar} does not exist: {path}");

        var ct = TestContext.Current.CancellationToken;
        Image.TryReadFitsFile(path!, out var image).ShouldBeTrueForProbe(output, "could not read the frame");
        if (image is null)
        {
            return;
        }

        var (channels, width, height) = image.Shape;
        output.WriteLine($"file      {System.IO.Path.GetFileName(path)}");
        output.WriteLine($"shape     {width}x{height} x{channels}  bitDepth={image.BitDepth}");
        // Round-trip precision, NOT G6. The histogram's float path is gated on MaxValue <= 1.0f, so
        // a value a single ulp over one behaves completely differently from one -- and prints the same.
        output.WriteLine($"range     min={image.MinValue:R} max={image.MaxValue:R} unitScaleDivisor={image.UnitScaleDivisor:R}");
        output.WriteLine($"overOne   {(image.MaxValue > 1.0f ? "yes" : "no")}  unitScaled={image.IsUnitScaledFloat}");
        output.WriteLine($"meta      sensor={image.ImageMeta.SensorType} fullScaleAdu={image.ImageMeta.SensorFullScaleAdu?.ToString() ?? "(none)"}");

        // HOW FAR over one, and how many pixels are there? A handful of outliers and a whole image
        // scaled slightly differently are the same MaxValue and need opposite fixes -- a robust
        // statistic in the first case, a wider tolerance in the second.
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            var over = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] > 1.0f)
                {
                    over++;
                }
            }

            var sorted = span.ToArray();
            Array.Sort(sorted);
            float Pct(double p) => sorted[(int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1)];
            output.WriteLine(
                $"dist[{c}]   >1.0: {over} px ({100.0 * over / span.Length:F6}%)  " +
                $"p50={Pct(0.50):R} p99={Pct(0.99):R} p99.9={Pct(0.999):R} p99.99={Pct(0.9999):R} max={sorted[^1]:R}");
        }

        // What the VIEWER measures. AstroImageDocument.AdoptImageAsync normalises whenever MaxValue
        // exceeds 1, so a probe reading the file raw is not looking at the same pixels the reported
        // star list came from.
        if (!image.HasUnitScalePeak)
        {
            image = image.ScaleFloatValuesToUnitInPlace();
            output.WriteLine($"normalised to match the viewer: max={image.MaxValue:R}");
        }
        else
        {
            output.WriteLine("left as read -- unit-referred, so no normalisation pass is needed");
        }

        for (var c = 0; c < channels; c++)
        {
            var (bg, starLevel, noise, threshold) = image.Background(c);
            output.WriteLine($"bg[{c}]     background={bg:G6} starLevel={starLevel:G6} noise={noise:G6} histThreshold={threshold:G6}");
        }

        var stars = await image.FindStarsAsync(
            channel: 0, snrMin: ViewerSnrMin, maxStars: ViewerMaxStars, cancellationToken: ct);
        output.WriteLine($"detected  {stars.Count} stars (snrMin={ViewerSnrMin}, maxStars={ViewerMaxStars})");

        var pixels = image.GetChannelSpan(0);

        // --- How many detections sit on something that is actually a peak? ---
        // Scored against the detection's OWN neighbourhood, so it needs no catalog and no threshold
        // borrowed from the detector under test.
        var contrasts = new List<float>();
        var flat = 0;
        foreach (var star in stars)
        {
            var xi = (int)MathF.Round(star.XCentroid);
            var yi = (int)MathF.Round(star.YCentroid);
            if (xi < 8 || yi < 8 || xi + 8 >= width || yi + 8 >= height)
            {
                continue;
            }

            var peak = LocalMax(pixels, width, xi, yi, 2);
            var ring = RingMedian(pixels, width, xi, yi, 6, 8);
            var ringSpread = RingSpread(pixels, width, xi, yi, 6, 8, ring);
            var contrast = ringSpread > 0f ? (peak - ring) / ringSpread : float.PositiveInfinity;
            contrasts.Add(contrast);
            if (contrast < 3f)
            {
                flat++;
            }
        }

        contrasts.Sort();
        if (contrasts.Count > 0)
        {
            output.WriteLine($"contrast  median={contrasts[contrasts.Count / 2]:F2} sigma  p10={contrasts[contrasts.Count / 10]:F2}  min={contrasts[0]:F2}");
            output.WriteLine($"flat      {flat} of {contrasts.Count} detections sit under 3 sigma over their own ring (probable false positives)");
        }

        // --- How many of the frame's brightest peaks were NOT detected? ---
        var brightest = FindBrightPeaks(pixels, width, height, count: 60);
        var missed = 0;
        foreach (var (px, py, value) in brightest)
        {
            var found = stars.Any(s => MathF.Abs(s.XCentroid - px) < 6f && MathF.Abs(s.YCentroid - py) < 6f);
            if (!found)
            {
                missed++;
                if (missed <= 10)
                {
                    output.WriteLine($"  missed peak at ({px},{py}) value={value:G6}");
                }
            }
        }
        output.WriteLine($"missed    {missed} of {brightest.Count} brightest peaks have no detection within 6 px");
    }

    private static float LocalMax(ReadOnlySpan<float> pixels, int width, int cx, int cy, int r)
    {
        var max = float.NegativeInfinity;
        for (var y = cy - r; y <= cy + r; y++)
        {
            for (var x = cx - r; x <= cx + r; x++)
            {
                var v = pixels[y * width + x];
                if (v > max)
                {
                    max = v;
                }
            }
        }
        return max;
    }

    private static float RingMedian(ReadOnlySpan<float> pixels, int width, int cx, int cy, int rInner, int rOuter)
    {
        var vals = new List<float>();
        for (var y = cy - rOuter; y <= cy + rOuter; y++)
        {
            for (var x = cx - rOuter; x <= cx + rOuter; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d2 = dx * dx + dy * dy;
                if (d2 >= rInner * rInner && d2 <= rOuter * rOuter)
                {
                    vals.Add(pixels[y * width + x]);
                }
            }
        }
        if (vals.Count == 0)
        {
            return 0f;
        }
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private static float RingSpread(ReadOnlySpan<float> pixels, int width, int cx, int cy, int rInner, int rOuter, float median)
    {
        var devs = new List<float>();
        for (var y = cy - rOuter; y <= cy + rOuter; y++)
        {
            for (var x = cx - rOuter; x <= cx + rOuter; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d2 = dx * dx + dy * dy;
                if (d2 >= rInner * rInner && d2 <= rOuter * rOuter)
                {
                    devs.Add(MathF.Abs(pixels[y * width + x] - median));
                }
            }
        }
        if (devs.Count == 0)
        {
            return 0f;
        }
        devs.Sort();
        // MAD scaled to a Gaussian sigma.
        return devs[devs.Count / 2] * 1.4826f;
    }

    /// <summary>The N brightest well-separated local maxima, as a detector-independent ground truth
    /// for "this is obviously a star".</summary>
    private static List<(int X, int Y, float Value)> FindBrightPeaks(
        ReadOnlySpan<float> pixels, int width, int height, int count)
    {
        var candidates = new List<(int X, int Y, float Value)>();
        const int Margin = 12;
        for (var y = Margin; y < height - Margin; y++)
        {
            for (var x = Margin; x < width - Margin; x++)
            {
                var v = pixels[y * width + x];
                var isPeak = true;
                for (var dy = -2; dy <= 2 && isPeak; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        if ((dx != 0 || dy != 0) && pixels[(y + dy) * width + x + dx] > v)
                        {
                            isPeak = false;
                            break;
                        }
                    }
                }
                if (isPeak)
                {
                    candidates.Add((x, y, v));
                }
            }
        }

        candidates.Sort((a, b) => b.Value.CompareTo(a.Value));
        var kept = new List<(int X, int Y, float Value)>();
        foreach (var c in candidates)
        {
            if (kept.Count >= count)
            {
                break;
            }
            if (kept.All(k => MathF.Abs(k.X - c.X) > 20 || MathF.Abs(k.Y - c.Y) > 20))
            {
                kept.Add(c);
            }
        }
        return kept;
    }
}

internal static class ProbeAssertions
{
    public static void ShouldBeTrueForProbe(this bool value, ITestOutputHelper output, string message)
    {
        if (!value)
        {
            output.WriteLine($"SKIP: {message}");
        }
    }
}
