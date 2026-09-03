using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The classical background fit against synthetic truth: a known gradient under noise, stars, a
/// light-pollution dome, a nebula blob, a NaN border, a multiplicative vignette, and a CFA mosaic whose
/// photosite colours carry different gradients. Every assertion is against the truth the plane was
/// built from, never against a previous output, so a change that makes the fit worse goes red for a
/// reason the message states.
/// </summary>
[Collection("Imaging")]
public class ClassicalBackgroundExtractorTests(ITestOutputHelper output)
{
    private const int W = 256;
    private const int H = 192;
    private const float Sky = 0.010f;
    private const float Noise = 2e-4f;

    /// <summary>A planar light-pollution ramp on a linear sky: 0.004 across, 0.002 down.</summary>
    private static float Ramp(int x, int y) => Sky + 0.004f * x / (W - 1) + 0.002f * y / (H - 1);

    /// <summary>A Gaussian dome at (0.7 W, 0.3 H), sigma 0.12 W, amplitude 0.003: wider than anything a quadratic can follow.</summary>
    private static float Dome(int x, int y)
    {
        var dx = x - 0.7f * W;
        var dy = y - 0.3f * H;
        var s = 0.12f * W;
        return 0.003f * MathF.Exp(-(dx * dx + dy * dy) / (2f * s * s));
    }

    /// <summary>A compact nebula blob at the frame centre, sigma 20 px, amplitude 0.02 (100 noise sigma).</summary>
    private static float Blob(int x, int y)
    {
        var dx = x - W / 2f;
        var dy = y - H / 2f;
        return 0.02f * MathF.Exp(-(dx * dx + dy * dy) / (2f * 20f * 20f));
    }

    private static float[,] Plane(Func<int, int, float> truth)
    {
        var p = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                p[y, x] = truth(x, y);
            }
        }
        return p;
    }

    private static float Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private static void AddNoise(float[,] p, Random rng, float sigma)
    {
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                p[y, x] += sigma * Gaussian(rng);
            }
        }
    }

    private static void AddStar(float[,] p, float cx, float cy, float amp, float sigma)
    {
        var r = (int)MathF.Ceiling(4f * sigma);
        for (var y = Math.Max(0, (int)cy - r); y <= Math.Min(H - 1, (int)cy + r); y++)
        {
            for (var x = Math.Max(0, (int)cx - r); x <= Math.Min(W - 1, (int)cx + r); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                p[y, x] += amp * MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
            }
        }
    }

    private static (float X, float Y, float Amp)[] AddRandomStars(float[,] p, Random rng, int count)
    {
        var stars = new (float, float, float)[count];
        for (var i = 0; i < count; i++)
        {
            var cx = 10f + (float)rng.NextDouble() * (W - 20);
            var cy = 10f + (float)rng.NextDouble() * (H - 20);
            var amp = 0.02f + (float)rng.NextDouble() * 0.48f;
            AddStar(p, cx, cy, amp, 1.5f);
            stars[i] = (cx, cy, amp);
        }
        return stars;
    }

    private static Image Mono(float[,] p)
        => new Image([p], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, new ImageMeta { SensorType = SensorType.Monochrome });

    private static ReadOnlySpan<float> Flat(float[,] p) => MemoryMarshal.CreateReadOnlySpan(ref p[0, 0], p.Length);

    private static float RmsError(ReadOnlySpan<float> values, Func<int, int, float> truth, int w, int h)
    {
        var sumSq = 0.0;
        var n = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = values[y * w + x];
                if (!float.IsFinite(v))
                {
                    continue;
                }
                var d = v - truth(x, y);
                sumSq += (double)d * d;
                n++;
            }
        }
        return (float)Math.Sqrt(sumSq / n);
    }

    private static float Median(ReadOnlySpan<float> values)
    {
        var buf = new float[values.Length];
        var n = StatisticsHelper.CompactFinite(values, buf);
        return StatisticsHelper.MedianFast(buf.AsSpan(0, n));
    }

    private static float RegionMedian(ReadOnlySpan<float> values, int w, int x0, int x1, int y0, int y1)
    {
        var buf = new float[(x1 - x0) * (y1 - y0)];
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var v = values[y * w + x];
                if (float.IsFinite(v))
                {
                    buf[n++] = v;
                }
            }
        }
        return StatisticsHelper.MedianFast(buf.AsSpan(0, n));
    }

    private static BackgroundExtractionOptions WithSurface(BackgroundExtractionOptions o) => o with { SurfaceRefinement = true };

    [Fact]
    public async Task ARampUnderNoiseAndStarsIsRecoveredByTheStiffPolynomial()
    {
        var rng = new Random(1);
        var plane = Plane(Ramp);
        AddNoise(plane, rng, Noise);
        AddRandomStars(plane, rng, 60);
        var source = Mono(plane);

        var result = await new ClassicalBackgroundExtractor().ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        result.Cleaned.Shape.ShouldBe(source.Shape);
        result.Background.Shape.ShouldBe(source.Shape);
        var bg = result.Background.GetChannelSpan(0);
        var cleaned = result.Cleaned.GetChannelSpan(0);

        // The model is the ramp to well under the noise (block mean + many pixels average it down).
        RmsError(bg, Ramp, W, H).ShouldBeLessThan(1e-4f, "background model vs the true ramp");
        // The level survives: the level is the true sky's median (the STARLESS truth; the source's own median
        // sits 1.7e-4 higher because sixty stars push it up the ramp), and the cleaned frame sits at that level.
        var level = result.Planes[0].Level;
        Math.Abs(level - Median(Flat(Plane(Ramp)))).ShouldBeLessThan(1e-4f, "level is the true sky median");
        Math.Abs(Median(cleaned) - level).ShouldBeLessThan(1e-4f, "cleaned frame sits at the level");
        // The gradient is gone: left and right quarters sit at the same median.
        var left = RegionMedian(cleaned, W, 0, 32, 10, H - 10);
        var right = RegionMedian(cleaned, W, W - 32, W, 10, H - 10);
        Math.Abs(left - right).ShouldBeLessThan(1e-4f, "left vs right median after correction");

        result.Planes.Length.ShouldBe(1);
        var d = result.Planes[0];
        output.WriteLine($"ramp: {d}; model RMS error {RmsError(bg, Ramp, W, H):E2}");
        d.Converged.ShouldBeTrue();
        d.Iterations.ShouldBeInRange(1, BackgroundExtractionOptions.Default.MaxIterations);
        // What leaves the fit is exactly what should: every star lights its own block and the eight around it
        // above two sigma of the BLOCK-MEAN noise (a 1.5 px star spills 3 percent of its peak into the next
        // block, which is tens of that sigma), so 60 stars x 9 blocks = 17.6 percent of the 64 x 48 grid, plus
        // 2.3 percent of Gaussian tails. Measured 0.816. A bound of 0.9 here was the test's arithmetic being
        // wrong, and it is what found that structure protection had been turning stars into protected discs.
        d.KeptFraction.ShouldBeInRange(0.75f, 0.9f, "60 stars x 9 blocks + noise tails leave the fit, nothing more");
        d.ResidualSigma.ShouldBeInRange(0.2f * Noise, 1.5f * Noise, "the residual sigma is the block-mean noise, not the stars");
        d.Level.ShouldBeInRange(Sky, Sky + 0.006f);

        result.Cleaned.Release();
        result.Background.Release();
    }

    [Fact]
    public async Task StarsKeepTheirExcessOverTheSkyAndDoNotBiasTheModel()
    {
        var rng = new Random(2);
        var starless = Plane(Ramp);
        AddNoise(starless, rng, Noise);
        var withStars = (float[,])starless.Clone();
        var stars = AddRandomStars(withStars, new Random(3), 60);

        var extractor = new ClassicalBackgroundExtractor();
        var clean = await extractor.ExtractAsync(Mono(starless), BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);
        var starry = await extractor.ExtractAsync(Mono(withStars), BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        // Rejection makes the stars invisible to the model: the two backgrounds agree to a fraction of the noise.
        var bgClean = clean.Background.GetChannelSpan(0);
        var bgStarry = starry.Background.GetChannelSpan(0);
        var sumSq = 0.0;
        for (var i = 0; i < bgClean.Length; i++)
        {
            var d = bgClean[i] - bgStarry[i];
            sumSq += (double)d * d;
        }
        Math.Sqrt(sumSq / bgClean.Length).ShouldBeLessThan(5e-5, "background with stars vs without");

        // Photometric integrity: at every star's peak pixel the excess over the TRUE sky is unchanged.
        var cleaned = starry.Cleaned.GetChannelSpan(0);
        var level = starry.Planes[0].Level;
        foreach (var (cx, cy, _) in stars)
        {
            var px = (int)MathF.Round(cx);
            var py = (int)MathF.Round(cy);
            var excessBefore = withStars[py, px] - Ramp(px, py);
            var excessAfter = cleaned[py * W + px] - level;
            Math.Abs(excessAfter - excessBefore).ShouldBeLessThan(1.5e-4f, $"star at ({px},{py}) excess before {excessBefore:F5} after {excessAfter:F5}");
        }

        clean.Cleaned.Release(); clean.Background.Release();
        starry.Cleaned.Release(); starry.Background.Release();
    }

    [Fact]
    public async Task EachChannelKeepsItsOwnSkyLevelSoTheCorrectionIsNotANeutralisation()
    {
        var rng = new Random(4);
        float[] skies = [0.010f, 0.014f, 0.020f];
        float[] slopes = [0.004f, -0.003f, 0.002f];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var cc = c;
            planes[c] = Plane((x, y) => skies[cc] + slopes[cc] * x / (W - 1));
            AddNoise(planes[c], rng, Noise);
        }
        var source = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

        var result = await new ClassicalBackgroundExtractor().ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        result.Planes.Length.ShouldBe(3);
        var medians = new float[3];
        for (var c = 0; c < 3; c++)
        {
            var cc = c;
            RmsError(result.Background.GetChannelSpan(c), (x, y) => skies[cc] + slopes[cc] * x / (W - 1), W, H).ShouldBeLessThan(1e-4f, $"channel {c} model");
            medians[c] = Median(result.Cleaned.GetChannelSpan(c));
            // Each channel's cleaned median is ITS OWN sky at the frame centre (sky plus half the slope):
            // 0.0120, 0.0125, 0.0210. Neutralising them would be a different step, by design.
            Math.Abs(medians[c] - (skies[c] + slopes[c] / 2f)).ShouldBeLessThan(1e-4f, $"channel {c} level: {medians[c]:F5}");
        }
        (medians[2] - medians[0]).ShouldBeGreaterThan(0.008f, "the channel offsets survive the correction");

        result.Cleaned.Release();
        result.Background.Release();
    }

    [Fact]
    public async Task NanPixelsStayNanAndDoNotPoisonTheFit()
    {
        var rng = new Random(5);
        var plane = Plane(Ramp);
        AddNoise(plane, rng, Noise);
        const int border = 12;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                if (x < border || y < border || x >= W - border || y >= H - border)
                {
                    plane[y, x] = float.NaN;
                }
            }
        }

        var result = await new ClassicalBackgroundExtractor().ExtractAsync(Mono(plane), BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        var cleaned = result.Cleaned.GetChannelSpan(0);
        var bg = result.Background.GetChannelSpan(0);
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                float.IsNaN(cleaned[i]).ShouldBe(float.IsNaN(plane[y, x]), $"NaN pattern at ({x},{y})");
                float.IsFinite(bg[i]).ShouldBeTrue($"the model is finite everywhere, at ({x},{y}) too");
            }
        }
        RmsError(bg, Ramp, W, H).ShouldBeLessThan(1e-4f, "model vs truth including the extrapolated border");

        result.Cleaned.Release();
        result.Background.Release();
    }

    [Fact]
    public async Task DivideModeRecoversAMultiplicativeVignette()
    {
        var rng = new Random(6);
        static float Flatness(int x, int y)
        {
            var u = (x - (W - 1) / 2f) / (W / 2f);
            var v = (y - (H - 1) / 2f) / (H / 2f);
            return 1f - 0.3f * (u * u + v * v);
        }
        var plane = Plane((x, y) => Sky * Flatness(x, y));
        AddNoise(plane, rng, Noise);
        var options = BackgroundExtractionOptions.Default with { Correction = BackgroundCorrection.Divide };

        var result = await new ClassicalBackgroundExtractor().ExtractAsync(Mono(plane), options, TestContext.Current.CancellationToken);

        var cleaned = result.Cleaned.GetChannelSpan(0);
        var centre = RegionMedian(cleaned, W, W / 2 - 16, W / 2 + 16, H / 2 - 16, H / 2 + 16);
        var corner = RegionMedian(cleaned, W, 4, 36, 4, 36);
        Math.Abs(centre - corner).ShouldBeLessThan(1e-4f, $"vignette removed: centre {centre:F5} corner {corner:F5}");
        Math.Abs(Median(cleaned) - Median(Flat(plane))).ShouldBeLessThan(1e-4f, "level preserved in divide mode");

        result.Cleaned.Release();
        result.Background.Release();
    }

    [Fact]
    public async Task TheSurfaceRefinementFollowsADomeTheQuadraticCannot()
    {
        var rng = new Random(7);
        var plane = Plane((x, y) => Ramp(x, y) + Dome(x, y));
        AddNoise(plane, rng, Noise);
        AddRandomStars(plane, rng, 30);
        var source = Mono(plane);
        var extractor = new ClassicalBackgroundExtractor();

        var polyOnly = await extractor.ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);
        var refined = await extractor.ExtractAsync(source, WithSurface(BackgroundExtractionOptions.Default), TestContext.Current.CancellationToken);

        var polyErr = RmsError(polyOnly.Background.GetChannelSpan(0), (x, y) => Ramp(x, y) + Dome(x, y), W, H);
        var refinedErr = RmsError(refined.Background.GetChannelSpan(0), (x, y) => Ramp(x, y) + Dome(x, y), W, H);
        output.WriteLine($"dome: polynomial-only RMS error {polyErr:E2}, refined {refinedErr:E2}; refined {refined.Planes[0]}");
        refinedErr.ShouldBeLessThan(3e-4f, $"refined model error {refinedErr:E2}");
        refinedErr.ShouldBeLessThan(polyErr / 2f, $"refined {refinedErr:E2} vs polynomial-only {polyErr:E2}");
        refined.Planes[0].Converged.ShouldBeTrue();

        polyOnly.Cleaned.Release(); polyOnly.Background.Release();
        refined.Cleaned.Release(); refined.Background.Release();
    }

    [Fact]
    public async Task StructureProtectionKeepsTheSurfaceFromHollowingANebula()
    {
        var rng = new Random(8);
        var plane = Plane((x, y) => Ramp(x, y) + Blob(x, y));
        AddNoise(plane, rng, Noise);
        var source = Mono(plane);
        var extractor = new ClassicalBackgroundExtractor();
        var protectedOptions = WithSurface(BackgroundExtractionOptions.Default);
        var unprotectedOptions = protectedOptions with { ProtectStructure = false };

        var guarded = await extractor.ExtractAsync(source, protectedOptions, TestContext.Current.CancellationToken);
        var unguarded = await extractor.ExtractAsync(source, unprotectedOptions, TestContext.Current.CancellationToken);

        // Under the blob the model must stay on the ramp; the blob is signal, not background.
        static float TruthUnderBlob(int x, int y) => Ramp(x, y);
        float ErrorUnderBlob(ReadOnlySpan<float> bg)
        {
            var sumSq = 0.0; var n = 0;
            for (var y = H / 2 - 16; y < H / 2 + 16; y++)
            {
                for (var x = W / 2 - 16; x < W / 2 + 16; x++)
                {
                    var d = bg[y * W + x] - TruthUnderBlob(x, y);
                    sumSq += (double)d * d; n++;
                }
            }
            return (float)Math.Sqrt(sumSq / n);
        }
        var guardedErr = ErrorUnderBlob(guarded.Background.GetChannelSpan(0));
        var unguardedErr = ErrorUnderBlob(unguarded.Background.GetChannelSpan(0));
        guardedErr.ShouldBeLessThan(unguardedErr, $"protected {guardedErr:E2} vs unprotected {unguardedErr:E2} under the blob");

        // The blob's peak survives in the corrected frame.
        var peakBefore = plane[H / 2, W / 2] - Ramp(W / 2, H / 2);
        var peakAfter = guarded.Cleaned.GetChannelSpan(0)[H / 2 * W + W / 2] - guarded.Planes[0].Level;
        var peakUnguarded = unguarded.Cleaned.GetChannelSpan(0)[H / 2 * W + W / 2] - unguarded.Planes[0].Level;
        output.WriteLine($"blob: model error under it protected {guardedErr:E2} vs unprotected {unguardedErr:E2}; peak kept protected {peakAfter / peakBefore:P0} vs unprotected {peakUnguarded / peakBefore:P0}; {guarded.Planes[0]}");
        (peakAfter / peakBefore).ShouldBeGreaterThan(0.6f, $"blob peak kept {peakAfter / peakBefore:P0}");
        (peakAfter / peakBefore).ShouldBeGreaterThan(
            (unguarded.Cleaned.GetChannelSpan(0)[H / 2 * W + W / 2] - unguarded.Planes[0].Level) / peakBefore,
            "protection keeps more of the peak than no protection");

        guarded.Cleaned.Release(); guarded.Background.Release();
        unguarded.Cleaned.Release(); unguarded.Background.Release();
    }

    /// <summary>
    /// The G1 sweep over 67 real masters moved the model by exactly nothing between
    /// <see cref="BackgroundExtractionOptions.SurfaceStructureThresholdSigma"/> 5, 10, 20 and 40: identical to two
    /// decimals on every metric. That reads the same whether the threshold never binds on real data or is not wired
    /// at all, and the two call for opposite responses, so pin the wiring here. Against a blob the surface WOULD
    /// otherwise follow, a low threshold must mark it and a threshold no residual can reach must not.
    /// </summary>
    [Fact]
    public async Task TheSurfaceStructureThresholdBindsWhenItIsLowEnoughToReachTheResidual()
    {
        var rng = new Random(8);
        var plane = Plane((x, y) => Ramp(x, y) + Blob(x, y));
        AddNoise(plane, rng, Noise);
        var source = Mono(plane);
        var extractor = new ClassicalBackgroundExtractor();
        var options = WithSurface(BackgroundExtractionOptions.Default);

        var binding = await extractor.ExtractAsync(source, options with { SurfaceStructureThresholdSigma = 1.5f }, TestContext.Current.CancellationToken);
        var inert = await extractor.ExtractAsync(source, options with { SurfaceStructureThresholdSigma = 40f }, TestContext.Current.CancellationToken);

        var bindingBg = binding.Background.GetChannelSpan(0);
        var inertBg = inert.Background.GetChannelSpan(0);
        var maxDelta = 0f;
        for (var i = 0; i < bindingBg.Length; i++)
        {
            maxDelta = MathF.Max(maxDelta, MathF.Abs(bindingBg[i] - inertBg[i]));
        }

        var peakTruth = Blob(W / 2, H / 2);
        var peakBinding = (binding.Cleaned.GetChannelSpan(0)[H / 2 * W + W / 2] - binding.Planes[0].Level) / peakTruth;
        var peakInert = (inert.Cleaned.GetChannelSpan(0)[H / 2 * W + W / 2] - inert.Planes[0].Level) / peakTruth;
        output.WriteLine($"surface threshold 1.5 vs 40: max model delta {maxDelta:E2} ({maxDelta / Noise:F1} noise sigma); blob peak kept {peakBinding:P0} vs {peakInert:P0}");

        maxDelta.ShouldBeGreaterThan(Noise, $"the threshold moved the model by {maxDelta / Noise:F2} noise sigma, so it is not reaching the fit");
        peakBinding.ShouldBeGreaterThan(peakInert, "marking the blob as structure keeps more of its peak than letting the surface follow it");

        binding.Cleaned.Release(); binding.Background.Release();
        inert.Cleaned.Release(); inert.Background.Release();
    }

    [Fact]
    public async Task AnExclusionPolygonKeepsTheFitOutOfARegion()
    {
        var rng = new Random(9);
        var plane = Plane(Ramp);
        AddNoise(plane, rng, Noise);
        // A bright plateau: the rejection would find it on its own (anything above 2 sigma of the BLOCK-MEAN
        // noise does, which is 1e-4 here), so what the polygon buys is certainty, and what this pins is the
        // mechanism: the region is out of the valid set before any fit, and the diagnostics say so.
        const int x0 = 100, x1 = 160, y0 = 60, y1 = 100;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                plane[y, x] += 0.01f;
            }
        }
        var source = Mono(plane);
        var extractor = new ClassicalBackgroundExtractor();
        var withPolygon = BackgroundExtractionOptions.Default with
        {
            Exclusions = [ExclusionPolygon.Rectangle(x0, y0, x1, y1)],
        };

        var excluded = await extractor.ExtractAsync(source, withPolygon, TestContext.Current.CancellationToken);
        var plain = await extractor.ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        var area = (float)(x1 - x0) * (y1 - y0) / (W * H);
        excluded.Planes[0].ExcludedFraction.ShouldBeInRange(area - 0.01f, area + 0.01f,
            $"excluded {excluded.Planes[0].ExcludedFraction:F3} vs polygon area {area:F3}");
        plain.Planes[0].ExcludedFraction.ShouldBe(0f);
        excluded.Planes[0].KeptFraction.ShouldBeGreaterThan(0.9f, "with the plateau excluded up front, only noise tails leave");
        RmsError(excluded.Background.GetChannelSpan(0), Ramp, W, H).ShouldBeLessThan(1e-4f);
        RmsError(plain.Background.GetChannelSpan(0), Ramp, W, H).ShouldBeLessThan(1e-4f, "rejection alone also keeps the plateau out of the model");

        excluded.Cleaned.Release(); excluded.Background.Release();
        plain.Cleaned.Release(); plain.Background.Release();
    }

    [Fact]
    public void ExclusionPolygonUsesTheEvenOddRule()
    {
        var rect = ExclusionPolygon.Rectangle(10, 20, 30, 40);
        rect.Contains(20, 30).ShouldBeTrue();
        rect.Contains(5, 30).ShouldBeFalse();
        rect.Contains(20, 45).ShouldBeFalse();

        var triangle = new ExclusionPolygon([new Vector2(0, 0), new Vector2(10, 0), new Vector2(0, 10)]);
        triangle.Contains(2, 2).ShouldBeTrue();
        triangle.Contains(8, 8).ShouldBeFalse();

        Should.Throw<ArgumentException>(() => new ExclusionPolygon([new Vector2(0, 0), new Vector2(1, 1)]));
    }

    [Fact]
    public async Task ACfaMosaicIsFittedPerPhotositeColour()
    {
        // RGGB at offsets (0,0): R at (even, even), G at (odd, even) and (even, odd), B at (odd, odd).
        // Each colour has its own sky and its own slope; a single-plane fit could remove only their average.
        var rng = new Random(10);
        static int Colour(int x, int y) => (y & 1) == 0 ? ((x & 1) == 0 ? 0 : 1) : ((x & 1) == 0 ? 1 : 2);
        float[] skies = [0.010f, 0.015f, 0.008f];
        float[] slopes = [0.004f, 0f, -0.003f];
        float Truth(int x, int y) => skies[Colour(x, y)] + slopes[Colour(x, y)] * x / (W - 1);
        var plane = Plane(Truth);
        AddNoise(plane, rng, Noise);
        var source = new Image([plane], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.RGGB, BayerOffsetX = 0, BayerOffsetY = 0 });

        var result = await new ClassicalBackgroundExtractor().ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);

        result.Planes.Length.ShouldBe(4, "R, G1, G2, B");
        result.Cleaned.Shape.ShouldBe(source.Shape);
        result.Background.Shape.ShouldBe(source.Shape);
        var cleaned = result.Cleaned.GetChannelSpan(0);
        var bg = result.Background.GetChannelSpan(0);
        RmsError(bg, Truth, W, H).ShouldBeLessThan(1e-4f, "the mosaic background carries each colour's own ramp");

        for (var colour = 0; colour < 3; colour++)
        {
            var srcVals = new float[W * H];
            var leftVals = new float[W * H];
            var rightVals = new float[W * H];
            var allVals = new float[W * H];
            int ns = 0, nl = 0, nr = 0, na = 0;
            for (var y = 10; y < H - 10; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    if (Colour(x, y) != colour) continue;
                    srcVals[ns++] = plane[y, x];
                    allVals[na++] = cleaned[y * W + x];
                    if (x < 32) leftVals[nl++] = cleaned[y * W + x];
                    if (x >= W - 32) rightVals[nr++] = cleaned[y * W + x];
                }
            }
            var srcMedian = StatisticsHelper.MedianFast(srcVals.AsSpan(0, ns));
            var cleanedMedian = StatisticsHelper.MedianFast(allVals.AsSpan(0, na));
            Math.Abs(cleanedMedian - srcMedian).ShouldBeLessThan(1e-4f, $"colour {colour} level preserved");
            var left = StatisticsHelper.MedianFast(leftVals.AsSpan(0, nl));
            var right = StatisticsHelper.MedianFast(rightVals.AsSpan(0, nr));
            Math.Abs(left - right).ShouldBeLessThan(1.5e-4f, $"colour {colour}: left {left:F5} vs right {right:F5} after correction");
        }

        result.Cleaned.Release();
        result.Background.Release();
    }

    [Fact]
    public async Task RunsAreDeterministic()
    {
        var rng = new Random(11);
        var plane = Plane((x, y) => Ramp(x, y) + Dome(x, y));
        AddNoise(plane, rng, Noise);
        AddRandomStars(plane, rng, 20);
        var source = Mono(plane);
        var options = WithSurface(BackgroundExtractionOptions.Default);
        var extractor = new ClassicalBackgroundExtractor();

        var a = await extractor.ExtractAsync(source, options, TestContext.Current.CancellationToken);
        var b = await extractor.ExtractAsync(source, options, TestContext.Current.CancellationToken);

        a.Cleaned.GetChannelSpan(0).SequenceEqual(b.Cleaned.GetChannelSpan(0)).ShouldBeTrue();
        a.Background.GetChannelSpan(0).SequenceEqual(b.Background.GetChannelSpan(0)).ShouldBeTrue();
        a.Planes.ShouldBe(b.Planes);

        a.Cleaned.Release(); a.Background.Release();
        b.Cleaned.Release(); b.Background.Release();
    }

    [Fact]
    public async Task TheGradientCorrectorEntryPointsRunTheSameFit()
    {
        var rng = new Random(12);
        var plane = Plane(Ramp);
        AddNoise(plane, rng, Noise);
        var source = Mono(plane);
        IGradientCorrector corrector = new ClassicalBackgroundExtractor();
        var extractor = (IBackgroundExtractor)corrector;

        var direct = await extractor.ExtractAsync(source, BackgroundExtractionOptions.Default, TestContext.Current.CancellationToken);
        var enhanced = await corrector.EnhanceAsync(source, TestContext.Current.CancellationToken);
        var (corrected, background) = await corrector.EnhanceAndEstimateBackgroundAsync(source, TestContext.Current.CancellationToken);

        corrector.Name.ShouldContain("classical");
        enhanced.GetChannelSpan(0).SequenceEqual(direct.Cleaned.GetChannelSpan(0)).ShouldBeTrue();
        corrected.GetChannelSpan(0).SequenceEqual(direct.Cleaned.GetChannelSpan(0)).ShouldBeTrue();
        background.ShouldNotBeNull();
        background.GetChannelSpan(0).SequenceEqual(direct.Background.GetChannelSpan(0)).ShouldBeTrue();

        direct.Cleaned.Release(); direct.Background.Release();
        enhanced.Release(); corrected.Release(); background.Release();
    }

    [Fact]
    public void DefaultsAreTheReferenceDefaults()
    {
        var o = BackgroundExtractionOptions.Default;
        o.Downsample.ShouldBe(4);
        o.PolynomialDegree.ShouldBe(2);
        o.SurfaceRefinement.ShouldBeFalse();
        o.SurfaceScalePercent.ShouldBe(5f);
        o.SurfaceInpaintPasses.ShouldBe(10);
        o.SurfaceSmoothness.ShouldBe(1f);
        o.RejectBrightSigma.ShouldBe(2f);
        o.RejectDarkSigma.ShouldBe(4f);
        o.MaxIterations.ShouldBe(20);
        o.ConvergenceTolerance.ShouldBe(1e-4f);
        o.MinKeptFraction.ShouldBe(0.02f);
        o.ProtectStructure.ShouldBeTrue();
        o.StructureThresholdSigma.ShouldBe(3f);
        o.SurfaceStructureThresholdSigma.ShouldBe(10f);
        o.StructureAmount.ShouldBe(0.5f);
        o.Correction.ShouldBe(BackgroundCorrection.Subtract);
        o.PreserveLevel.ShouldBeTrue();
        o.Exclusions.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ValidateRefusesValuesOutsideTheFitsRange()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => (BackgroundExtractionOptions.Default with { PolynomialDegree = 7 }).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => (BackgroundExtractionOptions.Default with { Downsample = 0 }).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => (BackgroundExtractionOptions.Default with { StructureAmount = 1f }).Validate());
        Should.NotThrow(() => BackgroundExtractionOptions.Default.Validate());
    }

    [Fact]
    public void AddClassicalBackgroundExtractionPutsOneInstanceBehindBothRoles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClassicalBackgroundExtraction();
        using var provider = services.BuildServiceProvider();

        var corrector = provider.GetRequiredService<IGradientCorrector>();
        var extractor = provider.GetRequiredService<IBackgroundExtractor>();
        corrector.ShouldBeOfType<ClassicalBackgroundExtractor>();
        ReferenceEquals(corrector, extractor).ShouldBeTrue("one singleton, two interfaces");
    }
}
