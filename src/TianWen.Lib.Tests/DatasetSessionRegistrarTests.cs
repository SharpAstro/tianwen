using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// End-to-end coverage for <see cref="SessionRegistrar"/> (dataset builder P0/#39):
    /// measure + gate + register + warp + integrate one session on synthetic RGGB data
    /// (<see cref="RgbBayerSyntheticFixture"/> — the same 8 dithered lights + 2 darks the
    /// stacking synthetic test uses, so any registration/integration regression trips here
    /// too). The load-bearing dataset invariant is that every warped sub shares the master's
    /// exact pixel grid — that is what makes cell (i, j) of any two subs an N2N pair.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetSessionRegistrarTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sessreg-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static List<FrameInfo> ReadFrames(string dir, string pattern)
        {
            var frames = new List<FrameInfo>();
            foreach (var path in Directory.GetFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal))
            {
                Image.TryReadFitsFile(path, out var img).ShouldBeTrue();
                frames.Add(new FrameInfo(path, img!.Width, img.Height, img.ChannelCount, img.BitDepth, img.ImageMeta));
                img.Release();
            }
            return frames;
        }

        private ImagingSession WriteLightSession()
        {
            var lightsDir = Path.Combine(_dir, "LIGHT");
            Directory.CreateDirectory(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);
            return new ImagingSession(lightsDir, "synth/rggb", "SynthBayer", "SynthRgb", [.. ReadFrames(lightsDir, "light_*.fits")]);
        }

        private async Task<Calibrator> BuildDarkCalibratorAsync(CancellationToken ct)
        {
            var darksDir = Path.Combine(_dir, "DARK");
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);
            var darkMaster = await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct);
            return new Calibrator(Dark: darkMaster);
        }

        [Fact]
        public async Task Register_RGGB_WithDark_ProducesCanvasAlignedSubsAndMaster()
        {
            var ct = TestContext.Current.CancellationToken;
            var session = WriteLightSession();
            var calibrator = await BuildDarkCalibratorAsync(ct);
            var scratch = Path.Combine(_dir, "scratch");

            var result = await SessionRegistrar.RegisterAsync(
                session, calibrator, scratch, minSubs: 4, logger: new XunitLogger(output), cancellationToken: ct);

            result.ShouldNotBeNull();

            // The 8 dithered lights are near-identical (same star field, per-frame noise only),
            // so all survive the session-relative gate.
            result.GatedCount.ShouldBe(RgbBayerSyntheticFixture.LightCount);
            // RGGB debayer-interpolation centroid jitter costs a couple of quad fits at the
            // wider dither offsets (the stacking synthetic test sees the same 6/8 floor).
            result.RegisteredCount.ShouldBeGreaterThanOrEqualTo(6,
                $"expected >= 6/8 RGGB subs to register; got {result.RegisteredCount}");
            result.Subs.Length.ShouldBe(result.RegisteredCount);
            result.SkippedCount.ShouldBe(result.GatedCount - result.RegisteredCount);

            // Master shares the union-canvas grid and is 3-channel (a missed debayer would be 1).
            result.CanvasWidth.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);
            result.CanvasHeight.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);
            result.Master.ChannelCount.ShouldBe(3, "RGGB lights should integrate to a 3-channel master");
            result.Master.Width.ShouldBe(result.CanvasWidth);
            result.Master.Height.ShouldBe(result.CanvasHeight);

            // THE dataset invariant: every warped scratch sub is on the master's exact grid,
            // so cell (i, j) is a fixed sky footprint across the whole session (N2N pairing).
            foreach (var sub in result.Subs)
            {
                File.Exists(sub.WarpedPath).ShouldBeTrue($"warped scratch missing: {sub.WarpedPath}");
                Image.TryReadFitsFile(sub.WarpedPath, out var warped).ShouldBeTrue();
                warped!.Width.ShouldBe(result.CanvasWidth);
                warped.Height.ShouldBe(result.CanvasHeight);
                warped.ChannelCount.ShouldBe(3);
                warped.Release();
            }

            // Stats rect is a valid non-empty sub-rectangle of the canvas.
            result.StatsRect.Width.ShouldBeGreaterThan(0);
            result.StatsRect.Height.ShouldBeGreaterThan(0);
            result.StatsRect.Right.ShouldBeLessThanOrEqualTo(result.CanvasWidth);
            result.StatsRect.Bottom.ShouldBeLessThanOrEqualTo(result.CanvasHeight);

            // Every channel carries signal after calibrate + debayer + integrate, and the three
            // channels are genuinely distinct (a collapsed / broadcast debayer would make them
            // identical). Measured over the raw canvas in native units (NaN-aware). We deliberately
            // do NOT assert the baked R=1.0 / G=0.7 / B=0.4 gain ratio here: the integrator applies
            // per-channel median normalisation, which by design scrambles the inter-channel ratio
            // (colour balance is restored downstream by SPCC / white-balance in display rendering,
            // which registration does not do). Ratio fidelity is the stacking pipeline's test, not
            // the registrar's; here the load-bearing facts are "3 aligned channels, all carrying
            // signal, not a broadcast".
            var means = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var ch = result.Master.GetChannelArray(c);
                double sum = 0;
                long n = 0;
                for (var y = 0; y < ch.GetLength(0); y++)
                {
                    for (var x = 0; x < ch.GetLength(1); x++)
                    {
                        var v = ch[y, x];
                        if (!float.IsNaN(v))
                        {
                            sum += v;
                            n++;
                        }
                    }
                }
                n.ShouldBeGreaterThan(0);
                means[c] = sum / n;
                output.WriteLine($"channel {c} finite-mean = {means[c]:F4}");
                means[c].ShouldBeGreaterThan(0.0, $"channel {c} collapsed to zero");
            }
            var spread = (means.Max() - means.Min()) / means.Max();
            spread.ShouldBeGreaterThan(0.05,
                $"debayer should yield three distinct channels, not a broadcast; means=[{means[0]:F3},{means[1]:F3},{means[2]:F3}]");
        }

        [Fact]
        public async Task Register_NullCalibrator_StillProducesMaster()
        {
            // Calibration is optional to the registration mechanics (the CLI always supplies
            // one for real N2N validity, but the seam must work without it for tests / uncalibrated
            // archives).
            var ct = TestContext.Current.CancellationToken;
            var session = WriteLightSession();

            var result = await SessionRegistrar.RegisterAsync(
                session, calibrator: null, Path.Combine(_dir, "scratch"), minSubs: 4, cancellationToken: ct);

            result.ShouldNotBeNull();
            result.Master.ChannelCount.ShouldBe(3);
            result.RegisteredCount.ShouldBeGreaterThanOrEqualTo(6);
        }

        [Fact]
        public async Task Register_FewerSurvivorsThanMin_ReturnsNull()
        {
            // 8 lights survive the gate; demanding 20 leaves the session too small to build a
            // meaningful master, so the registrar skips it cleanly (null, not an exception).
            var ct = TestContext.Current.CancellationToken;
            var session = WriteLightSession();

            var result = await SessionRegistrar.RegisterAsync(
                session, calibrator: null, Path.Combine(_dir, "scratch"), minSubs: 20, cancellationToken: ct);

            result.ShouldBeNull();
        }
    }
}
