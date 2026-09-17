using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reproduces the phase-locked 2x2 colour bias in <see cref="DrizzleStrategy"/> /
/// <see cref="TilePipelinedDrizzleStrategy"/> diagnosed on a real BayerDrizzle master
/// (10P/Tempel 2, 135 subs): neither strategy applies any per-frame normalization before
/// depositing, so a session-long sky trend combined with dither-driven uneven per-CFA-phase
/// frame weighting bakes a residual 2x2 checkerboard bias into each output channel plane, even
/// with no comet mask involved (see <c>docs/plans/comet-integration.md</c>, "A masked layer
/// needs a strategy that NORMALISES" -- the same root cause, minus the mask).
///
/// <para>The synthetic fixture below is a flat (no-star) RGGB set whose per-frame dither DRIFTS
/// monotonically with frame index (mirroring periodic tracking error / progressive dithering)
/// while the background level ALSO ramps monotonically with frame index (mirroring a session-long
/// sky trend). Both riding on the same time axis is what turns "uneven per-frame weighting" into
/// a systematic, sign-consistent bias rather than noise that averages out -- purely random
/// per-frame dither uncorrelated with the trend would NOT reproduce this reliably.</para>
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
        // session-long trend leaked into the master as a fixed 2x2 pattern. Measured against the
        // unfixed strategy: R 2.245, G 1.350, B 2.256 sigma, all well above the 0.75 bound below;
        // against the fixed one: R 0.241, G 0.171, B 0.354 sigma.
        for (var c = 0; c < 3; c++)
        {
            var channel = result.Master.GetChannelArray(c);
            var (spreadSigma, medians) = MeasurePhaseSpread(channel, RegionX0, RegionX1, RegionY0, RegionY1);

            // Noise floor: after per-frame normalisation removes the sky trend, the four phases
            // draw from statistically the same population and should agree to well under 1 sigma.
            spreadSigma.ShouldBeLessThan(0.75,
                $"channel {c}: phase-median spread {spreadSigma:F3} sigma (medians "
                    + $"[{medians[0]:F2}, {medians[1]:F2}, {medians[2]:F2}, {medians[3]:F2}]) -- "
                    + "DrizzleStrategy must normalise each frame's sky level before deposit so an "
                    + "uneven per-CFA-phase frame weighting cannot bake a session-long trend into "
                    + "a fixed checkerboard bias.");
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
            // fraction, for a clean margin) as the diagnosed 10P/Tempel 2 session.
            var bg = BgStart + (BgEnd - BgStart) * i / (FrameCount - 1);

            var plane = new float[FrameSize, FrameSize];
            for (var y = 0; y < FrameSize; y++)
            {
                for (var x = 0; x < FrameSize; x++)
                {
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
            StatsRect: Rectangle.Empty,
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
