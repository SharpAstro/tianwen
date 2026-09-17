using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reproduces the phase-locked 2x2 colour bias in <see cref="DrizzleStrategy"/> /
/// <see cref="TilePipelinedDrizzleStrategy"/> diagnosed on a real BayerDrizzle master
/// (10P/Tempel 2, 135 subs): neither strategy applied any per-frame normalization before
/// depositing, so a session-long sky trend combined with dither-driven uneven per-CFA-phase
/// frame weighting baked a residual 2x2 checkerboard bias into each output channel plane, even
/// with no comet mask involved (see <c>docs/plans/comet-integration.md</c>, "A masked layer
/// needs a strategy that NORMALISES" -- the same root cause, minus the mask).
///
/// <para>The synthetic fixture below is a flat (no-star) RGGB set whose per-frame dither DRIFTS
/// monotonically with frame index (mirroring periodic tracking error / progressive dithering)
/// while the background level ALSO ramps monotonically with frame index (mirroring a session-long
/// sky trend). Both riding on the same time axis is what turns "uneven per-frame weighting" into
/// a systematic, sign-consistent bias rather than noise that averages out -- purely random
/// per-frame dither uncorrelated with the trend would NOT reproduce this reliably.</para>
///
/// <para><b>A SECOND, smaller drift rides on top: R/G and B/G background ratios also drift a few
/// percent across the session</b> (matching what a real 10P/Tempel 2 session measured: R/G held
/// within ~2.3%, B/G ~3.0%, while G itself fell 46%). A single WHOLE-FRAME normalisation scalar is
/// dominated by green (2x the photosites of red or blue) and removes only the G-shaped trend, so
/// this per-colour residual survives even after the single-scalar fix
/// (<c>DrizzleStrategy</c>/<c>TilePipelinedDrizzleStrategy</c> commit "fix(stacking): drizzle
/// normalises per frame") and still bakes a (smaller) phase-locked bias into R and B. Per-CFA-colour
/// normalisation (<see cref="Normalizer.ComputeCfaStats"/> / <see cref="Normalizer.ApplyCfa"/>)
/// removes it because each colour is mapped onto the target independently.</para>
/// </summary>
[Collection("Imaging")]
public class DrizzlePerFrameNormalizationTests
{
    private const int FrameSize = 48;
    private const int FrameCount = 60;
    private const int Margin = 10;
    private const int CanvasSize = FrameSize + Margin * 2;
    private const float NoiseSigma = 8f;
    private const float BgStart = 1000f;
    private const float BgEnd = 1600f;

    /// <summary>R/G background ratio at the start/end of the session -- a 10% relative drift
    /// riding on top of the dominant (G-shaped) trend, several times the ~2-3% measured on the
    /// real session for a clean synthetic margin.</summary>
    private const float RgRatioStart = 0.30f;
    private const float RgRatioEnd = 0.33f;

    /// <summary>B/G background ratio at the start/end of the session -- drifts the opposite
    /// direction from R/G, same shape as the measured session (R/G and B/G did not move together).</summary>
    private const float BgRatioStart = 0.60f;
    private const float BgRatioEnd = 0.57f;

    /// <summary>
    /// Central sub-region, well inside every frame's footprint across the whole dither drift
    /// range (drift tops out at ~13 px in X, ~12 px in Y against a 10 px margin), so every probed
    /// pixel has full N-frame coverage and the measurement isolates the phase bias from any
    /// coverage-edge effect.
    /// </summary>
    private const int RegionX0 = Margin + 14;
    private const int RegionX1 = Margin + FrameSize - 14; // exclusive
    private const int RegionY0 = Margin + 14;
    private const int RegionY1 = Margin + FrameSize - 14; // exclusive

    [Fact]
    public async Task Drizzle_SkyTrendWithDriftingDither_MasterPhaseMediansStayAtNoiseFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = BuildFrames();

        var job = BuildJob(frames);
        var strategy = new DrizzleStrategy();
        var result = await strategy.RunAsync(job, ct);

        result.Master.ChannelCount.ShouldBe(3);

        // Per-channel phase-median spread, in units of the region's own robust (MAD) sigma -- the
        // same normalisation the real-master diagnosis used. A spread near 0 means the four
        // (y%2, x%2) sub-medians agree (no residual CFA-phase structure); a large spread means a
        // trend leaked into the master as a fixed 2x2 pattern. Measured against the single-scalar
        // fix ("fix(stacking): drizzle normalises per frame", a pooled whole-frame scalar
        // dominated by green): R 2.287, G 1.414, B 1.850 sigma, all well above the 0.75 bound --
        // the per-colour ratio drift this fixture adds survives a pooled scalar (G's own trend is
        // fully removed, but R and B's OWN drift relative to G is not). Measured against the
        // per-colour fix (Normalizer.ComputeCfaStats/ApplyCfa, this commit): R 0.231, G 0.183,
        // B 0.198 sigma -- all three channels pass.
        for (var c = 0; c < 3; c++)
        {
            var channel = result.Master.GetChannelArray(c);
            var (spreadSigma, medians) = MeasurePhaseSpread(channel, RegionX0, RegionX1, RegionY0, RegionY1);

            // Noise floor: after per-frame, per-CFA-colour normalisation removes both the dominant
            // trend and each colour's own drift, the four phases draw from statistically the same
            // population and should agree to well under 1 sigma.
            spreadSigma.ShouldBeLessThan(0.75,
                $"channel {c}: phase-median spread {spreadSigma:F3} sigma (medians "
                    + $"[{medians[0]:F2}, {medians[1]:F2}, {medians[2]:F2}, {medians[3]:F2}]) -- "
                    + "DrizzleStrategy must normalise each CFA colour's sky level independently "
                    + "before deposit so a per-colour drift riding on the dominant (green) trend "
                    + "cannot bake a fixed checkerboard bias into R or B.");
        }
    }

    private static List<RawBayerFrame> BuildFrames()
    {
        var frames = new List<RawBayerFrame>(FrameCount);
        // Fixed seed: deterministic per-pixel noise so the test is reproducible run to run.
        var rng = new Random(20260917);

        for (var i = 0; i < FrameCount; i++)
        {
            // Monotonic sub-pixel drift correlated with frame index -- the mechanism that spreads
            // deposit weight unevenly across the 2x2 CFA phases (see class remarks).
            var dx = Margin + 0.055f * i;
            var dy = Margin + 0.035f * i;

            // Session-long sky trend: a monotonic ramp across the run, same shape (if a larger
            // fraction, for a clean margin) as the diagnosed 10P/Tempel 2 session. Dominant
            // (G-weighted) level, plus each colour's OWN small ratio drift on top (see class
            // remarks) -- a single pooled scalar removes only the G-shaped part of this.
            var bgG = BgStart + (BgEnd - BgStart) * i / (FrameCount - 1);
            var t = (float)i / (FrameCount - 1);
            var rgRatio = RgRatioStart + (RgRatioEnd - RgRatioStart) * t;
            var bgRatio = BgRatioStart + (BgRatioEnd - BgRatioStart) * t;
            var bgR = bgG * rgRatio;
            var bgB = bgG * bgRatio;

            var plane = new float[FrameSize, FrameSize];
            for (var y = 0; y < FrameSize; y++)
            {
                var isEvenRow = (y & 1) == 0;
                for (var x = 0; x < FrameSize; x++)
                {
                    // RGGB, offset (0, 0): R at (even, even), G at (even, odd) / (odd, even), B at
                    // (odd, odd) -- the same convention SensorType.GetBayerPatternMatrix(0, 0) and
                    // Image.CfaPhaseStarts share.
                    var isEvenCol = (x & 1) == 0;
                    var bg = (isEvenRow, isEvenCol) switch
                    {
                        (true, true) => bgR,
                        (true, false) => bgG,
                        (false, true) => bgG,
                        (false, false) => bgB,
                    };
                    plane[y, x] = bg + NextGaussian(rng) * NoiseSigma;
                }
            }

            var meta = new ImageMeta { Instrument = "synth-drizzle-norm", SensorType = SensorType.RGGB };
            var img = new Image([plane], BitDepth.Float32, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            frames.Add(new RawBayerFrame(img, Matrix3x2.CreateTranslation(dx, dy)));
        }

        return frames;
    }

    private static IntegrationJob BuildJob(List<RawBayerFrame> frames)
    {
        async IAsyncEnumerable<RawBayerFrame> RawBayerFramesProducer(
            [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        static async IAsyncEnumerable<Image> EmptyWarpedFrames(
            [EnumeratorCancellation] CancellationToken token)
        {
            // DrizzleStrategy never reads WarpedFrames -- only RawBayerFrames -- but the record
            // field is non-nullable, so a producer that yields nothing satisfies the contract.
            await Task.CompletedTask;
            yield break;
        }

        return new IntegrationJob(
            WarpedFrames: EmptyWarpedFrames,
            ExpectedFrameCount: frames.Count,
            Options: new IntegrationOptions(),
            StagingDir: Path.GetTempPath(),
            StatsRect: PixelRect.Empty,
            RawBayerFrames: RawBayerFramesProducer,
            DrizzleOptions: new DrizzleOptions(),
            CanvasWidth: CanvasSize,
            CanvasHeight: CanvasSize);
    }

    /// <summary>Box-Muller standard normal sample from a seeded <see cref="Random"/>.</summary>
    private static float NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
    }

    /// <summary>
    /// Mirrors the diagnosis script's per-block method: split the region into its four
    /// (y % 2, x % 2) sub-grids, take each sub-grid's median, and report the max-min spread of
    /// those four medians in units of the whole region's robust (1.4826 * MAD) sigma.
    /// </summary>
    private static (double SpreadSigma, double[] PhaseMedians) MeasurePhaseSpread(
        float[,] channel, int x0, int x1, int y0, int y1)
    {
        var all = new List<double>();
        var phases = new List<double>[4];
        for (var p = 0; p < 4; p++) phases[p] = [];

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var v = channel[y, x];
                if (float.IsNaN(v)) continue;
                all.Add(v);
                var phaseIdx = (y & 1) * 2 + (x & 1);
                phases[phaseIdx].Add(v);
            }
        }

        all.ShouldNotBeEmpty("probed region has no finite pixels -- coverage gap in the fixture");
        var overallMedian = Median(all);
        var mad = Median(all.ConvertAll(v => Math.Abs(v - overallMedian)));
        var sigma = 1.4826 * mad;
        sigma.ShouldBeGreaterThan(0.0, "MAD sigma degenerated to zero -- fixture has no pixel noise");

        var medians = new double[4];
        for (var p = 0; p < 4; p++)
        {
            phases[p].ShouldNotBeEmpty($"phase {p} has no finite pixels in the probed region");
            medians[p] = Median(phases[p]);
        }

        var spread = (medians[Array.IndexOf(medians, Max(medians))] - medians[Array.IndexOf(medians, Min(medians))]) / sigma;
        return (spread, medians);
    }

    private static double Max(double[] values)
    {
        var m = values[0];
        for (var i = 1; i < values.Length; i++) if (values[i] > m) m = values[i];
        return m;
    }

    private static double Min(double[] values)
    {
        var m = values[0];
        for (var i = 1; i < values.Length; i++) if (values[i] < m) m = values[i];
        return m;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
