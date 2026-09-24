using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a rejecting drizzle's time goes, per frame, on real lights: the load, the calibration, the
/// per-colour sky offset, and each of the three deposit kernels (plain, moments, clipped). The dataset
/// bake streams every pass from the raw lights on the archive disk, so a pass is load + calibrate +
/// offset + kernel, and the question is which of those a faster rejection has to attack.
/// </summary>
/// <remarks>
/// <para>Set <c>TIANWEN_DRIZZLE_COST_PROBE</c> to a folder of RGGB lights (the first
/// <c>TIANWEN_DRIZZLE_COST_PROBE_FRAMES</c>, default 12, are used). The calibration is synthetic, a
/// copy of the first frame as dark and flat, because only its arithmetic is being timed; the transform
/// is a small rotation plus a dither step per frame, like a session's.</para>
/// </remarks>
[Collection("Imaging")]
public sealed class DrizzleCostProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task WhereARejectingDrizzlePassSpendsItsTime()
    {
        var root = Environment.GetEnvironmentVariable("TIANWEN_DRIZZLE_COST_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), "TIANWEN_DRIZZLE_COST_PROBE not set");
        var ct = TestContext.Current.CancellationToken;
        var count = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_DRIZZLE_COST_PROBE_FRAMES"), out var n) ? n : 12;

        var frames = Directory.EnumerateFiles(root, "*.fits")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(p => Image.TryReadFitsHeader(p, out var fi) ? fi : null)
            .OfType<FrameInfo>()
            .ToList();
        Assert.SkipWhen(frames.Count < 2, $"fewer than two readable frames under {root}");

        // The synthetic masters, loaded outside the timed loop.
        var dark = await frames[0].LoadFullAsync(ct);
        var flat = await frames[0].LoadFullAsync(ct);
        var calibrator = new Calibrator(Dark: dark, Flat: flat);
        var (w, h) = (dark.Width, dark.Height);
        var meta = dark.ImageMeta;
        var pattern = meta.SensorType.GetBayerPatternMatrix(meta.BayerOffsetX, meta.BayerOffsetY);
        const int Margin = 16;
        var canvasW = w + (2 * Margin);
        var canvasH = h + (2 * Margin);
        const float HalfP = 0.5f;
        var clip = new DrizzleClip(3f, 5f);
        var sourceRect = new PixelRect(0, 0, w, h);

        Matrix3x2 TransformFor(int i)
            => Matrix3x2.CreateRotation(0.0035f, new Vector2(w / 2f, h / 2f))
               * Matrix3x2.CreateTranslation(Margin + (0.37f * i), Margin + (0.23f * i));

        var load = new Stopwatch();
        var calibrate = new Stopwatch();
        var offset = new Stopwatch();
        var plain = new Stopwatch();
        var momentsWatch = new Stopwatch();
        var clipped = new Stopwatch();
        Normalizer.CfaNormalizationStats? reference = null;

        var flux = NewPlanes(canvasH, canvasW);
        var weight = NewPlanes(canvasH, canvasW);
        var moments = new DrizzleMoments(3, canvasH, canvasW);
        var prepared = new Image[frames.Count];

        for (var i = 0; i < frames.Count; i++)
        {
            load.Start();
            var raw = await frames[i].LoadFullAsync(ct);
            load.Stop();

            calibrate.Start();
            var calibrated = calibrator.Apply(raw);
            calibrate.Stop();

            offset.Start();
            var stats = Normalizer.ComputeCfaStats(calibrated);
            reference ??= stats;
            var shifted = Normalizer.OffsetCfaToReference(calibrated, stats, reference);
            offset.Stop();
            prepared[i] = shifted;

            plain.Start();
            DrizzleKernel.IterateAndDeposit(shifted, TransformFor(i), pattern, HalfP, flux, weight,
                0, canvasW, 0, canvasH, sourceRect, default, false);
            plain.Stop();

            momentsWatch.Start();
            DrizzleKernel.IterateAndAccumulateMoments(shifted, TransformFor(i), pattern, HalfP, moments,
                0, canvasW, 0, canvasH, sourceRect, default, false);
            momentsWatch.Stop();
        }

        var slope = Stopwatch.StartNew();
        moments.ComputeSlope();
        slope.Stop();

        var clippedFlux = NewPlanes(canvasH, canvasW);
        var clippedWeight = NewPlanes(canvasH, canvasW);
        long rejected = 0, total = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            clipped.Start();
            var (r, t) = DrizzleKernel.IterateAndDepositClipped(prepared[i], TransformFor(i), pattern, HalfP, moments, clip,
                clippedFlux, clippedWeight, 0, canvasW, 0, canvasH, sourceRect, default, false);
            clipped.Stop();
            rejected += r;
            total += t;
        }

        // The parallel kernels on the same prepared frames, into fresh accumulators.
        var pPlain = new Stopwatch();
        var pMoments = new Stopwatch();
        var pClipped = new Stopwatch();
        var pFlux = NewPlanes(canvasH, canvasW);
        var pWeight = NewPlanes(canvasH, canvasW);
        var pMomentsPlanes = new DrizzleMoments(3, canvasH, canvasW);
        for (var i = 0; i < frames.Count; i++)
        {
            pPlain.Start();
            DrizzleKernel.IterateAndDepositParallel(prepared[i], TransformFor(i), pattern, HalfP, pFlux, pWeight,
                canvasW, canvasH, default, false);
            pPlain.Stop();
            pMoments.Start();
            DrizzleKernel.IterateAndAccumulateMomentsParallel(prepared[i], TransformFor(i), pattern, HalfP, pMomentsPlanes,
                canvasW, canvasH, default, false);
            pMoments.Stop();
        }
        var pSlope = Stopwatch.StartNew();
        pMomentsPlanes.ComputeSlope();
        pSlope.Stop();
        var pcFlux = NewPlanes(canvasH, canvasW);
        var pcWeight = NewPlanes(canvasH, canvasW);
        for (var i = 0; i < frames.Count; i++)
        {
            pClipped.Start();
            DrizzleKernel.IterateAndDepositClippedParallel(prepared[i], TransformFor(i), pattern, HalfP, pMomentsPlanes, clip,
                pcFlux, pcWeight, canvasW, canvasH, default, false);
            pClipped.Stop();
        }
        output.WriteLine($"parallel, {Environment.ProcessorCount} cores, ms/frame: plain {pPlain.Elapsed.TotalMilliseconds / frames.Count:F0}, " +
                         $"moments {pMoments.Elapsed.TotalMilliseconds / frames.Count:F0}, clipped {pClipped.Elapsed.TotalMilliseconds / frames.Count:F0}; " +
                         $"slope {pSlope.Elapsed.TotalMilliseconds:F0} ms once");

        // The same frames again, now in the OS file cache: what is left of a load is the decode.
        var warmLoad = Stopwatch.StartNew();
        for (var i = 1; i < frames.Count; i++)
        {
            _ = await frames[i].LoadFullAsync(ct);
        }
        warmLoad.Stop();
        output.WriteLine($"warm reload (file cache): {warmLoad.Elapsed.TotalMilliseconds / (frames.Count - 1):F0} ms/frame");

        double PerFrame(Stopwatch s) => s.Elapsed.TotalMilliseconds / frames.Count;
        var loadMs = PerFrame(load);
        var calMs = PerFrame(calibrate);
        var offMs = PerFrame(offset);
        var plainMs = PerFrame(plain);
        var momMs = PerFrame(momentsWatch);
        var clipMs = PerFrame(clipped);
        var prepMs = loadMs + calMs + offMs;
        output.WriteLine($"{frames.Count} frames {w}x{h} ({new FileInfo(frames[0].Path).Length / 1e6:F1} MB) from {root}");
        output.WriteLine($"per frame, ms: load {loadMs:F0}, calibrate {calMs:F0}, sky offset {offMs:F0} (prepare {prepMs:F0});");
        output.WriteLine($"               kernel plain {plainMs:F0}, moments {momMs:F0}, clipped {clipMs:F0}; slope {slope.Elapsed.TotalMilliseconds:F0} ms once");
        output.WriteLine($"one pass before #346 (prepare + plain): {prepMs + plainMs:F0} ms/frame");
        output.WriteLine($"a rejecting master now (2 x prepare + moments + clipped): {(2 * prepMs) + momMs + clipMs:F0} ms/frame, " +
                         $"x{((2 * prepMs) + momMs + clipMs) / (prepMs + plainMs):F2}; of which prepare {2 * prepMs / ((2 * prepMs) + momMs + clipMs):P0}");
        output.WriteLine($"clip rejected {rejected} of {total} deposits ({(double)rejected / Math.Max(1, total):P3})");
    }

    private static float[][,] NewPlanes(int h, int w) => [new float[h, w], new float[h, w], new float[h, w]];
}
