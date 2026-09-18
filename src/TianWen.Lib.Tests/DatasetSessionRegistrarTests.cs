using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TianWen.Lib.Geometry;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// End-to-end coverage for <see cref="SessionRegistrar"/> (dataset builder P0/#39):
    /// measure + gate + register + warp + integrate one session on synthetic RGGB data
    /// (<see cref="RgbBayerSyntheticFixture"/>, the same 8 dithered lights + 2 darks the
    /// stacking synthetic test uses, so any registration/integration regression trips here
    /// too). The load-bearing dataset invariant is that every warped sub shares the master's
    /// exact pixel grid, that is what makes cell (i, j) of any two subs an N2N pair.
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
            return new ImagingSession(lightsDir, "synth/rggb", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);
        }

        private async Task<Calibrator> BuildDarkCalibratorAsync(CancellationToken ct)
        {
            var darksDir = Path.Combine(_dir, "DARK");
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);
            var darkMaster = await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct);
            return new Calibrator(Dark: darkMaster);
        }

        /// <summary>
        /// The half-master floor is per master strategy, because the two answer different constraints:
        /// a drizzled half needs per-Bayer-position R/B coverage (2x the strategy's own frame
        /// minimum), while a rejection-integrated half needs enough frames for a real rejector, which
        /// <c>StackingPipeline.BuildRejector</c> puts far lower. Measured over the current 50-session
        /// dataset the difference is 26 sessions qualifying versus 48, so collapsing the two to one
        /// number is not a rounding decision.
        /// </summary>
        [Fact]
        public void MinSubsForHalfMasters_IsPerStrategy_AndEachFloorSplitsEvenly()
        {
            var drizzled = SessionRegistrar.MinSubsForHalfMasters(drizzled: true);
            var rejected = SessionRegistrar.MinSubsForHalfMasters(drizzled: false);

            // Drizzle's floor IS twice its own coverage minimum, not a number of its own.
            drizzled.ShouldBe(2 * DrizzleStrategy.AutoSelectMinFrameCount);
            // A rejected half only needs a real rejector, so its floor is well below drizzle's, and
            // above the count at which BuildRejector gives up and returns null (no rejection at all,
            // the very defect that makes an uncalibrated drizzle unacceptable).
            rejected.ShouldBeLessThan(drizzled);
            StackingPipeline.BuildRejector(rejected / 2).ShouldNotBeNull();
            // Both split evenly, or one half is systematically deeper than the other and the pair is
            // no longer two samples of the same noise level.
            (drizzled % 2).ShouldBe(0);
            (rejected % 2).ShouldBe(0);
        }

        /// <summary>
        /// The per-session drizzle gate, both halves of it. The stacker's own
        /// <see cref="DrizzleStrategy.Evaluate"/> covers sensor pattern, frame count and RAM; the
        /// extra condition here is a MATCHED DARK, and it is not the stacker's business.
        ///
        /// <para>Why the dark is load-bearing: drizzle has no per-cell rejection, while the AHD path's
        /// sigma-clip washes hot pixels out across the whole session. Dark subtraction removes a hot
        /// pixel's offset, so a calibrated session is fine and an uncalibrated one would have
        /// uncorrected hot pixels deposited straight into the master, which is a worse master than the
        /// interpolated one it replaced. Falling back beats building a bad-pixel mask, because the mask
        /// would only reconstruct what the dark already carries.</para>
        /// </summary>
        [Fact]
        public async Task TryDrizzle_NeedsAMatchedDark_AndTheStrategysOwnGate()
        {
            var ct = TestContext.Current.CancellationToken;
            var calibrator = await BuildDarkCalibratorAsync(ct);
            var logger = new XunitLogger(output);
            Directory.CreateDirectory(_dir);

            IntegrationProbe Probe(int frameCount, SensorType sensor) => IntegrationProbe.Snapshot(
                frameCount: frameCount, frameWidth: 512, frameHeight: 512, channelCount: 3,
                canvasWidth: 520, canvasHeight: 520, stagingDir: _dir, sensorType: sensor);

            var deep = Probe(DrizzleStrategy.AutoSelectMinFrameCount, SensorType.RGGB);
            SessionRegistrar.TryDrizzle(deep, calibrator, logger, "deep+dark").ShouldBeTrue();

            // No calibration at all, and calibration that resolved a bias/flat but no dark: both are
            // the uncalibrated-hot-pixel case, and the second is the one that actually occurs (a
            // session whose darks are the wrong gain or temperature).
            SessionRegistrar.TryDrizzle(deep, null, logger, "deep+nocal").ShouldBeFalse();
            SessionRegistrar.TryDrizzle(deep, new Calibrator(Bias: calibrator.Dark), logger, "deep+nodark")
                .ShouldBeFalse();

            // The strategy's own gate still applies with a dark present: too few frames to fill the
            // per-Bayer-position R/B coverage, and a mono sensor has no CFA to drizzle at all.
            SessionRegistrar.TryDrizzle(Probe(8, SensorType.RGGB), calibrator, logger, "shallow").ShouldBeFalse();
            SessionRegistrar.TryDrizzle(Probe(DrizzleStrategy.AutoSelectMinFrameCount, SensorType.Monochrome),
                calibrator, logger, "mono").ShouldBeFalse();
        }

        /// <summary>
        /// The half-master pair: two integrations of DISJOINT halves of the same session, which is an
        /// N2N pair at the noise level a real master has (~1.41x) rather than the 2.96x the deepest
        /// sub pairing can reach. Gated on sub count, and the gate is the interesting half of the
        /// behaviour: below it the pair must be absent rather than degenerate, because a "half" of two
        /// frames is not a usable integration and a model trained on it would be learning a noise
        /// level that no deployment ever presents.
        ///
        /// <para>What this does NOT cover, deliberately: that the split is INTERLEAVED rather than
        /// first-half/second-half. The fixture's lights are near-identical by construction, so no
        /// observable output can distinguish the two, and manufacturing a drifting fixture to pin two
        /// lines of <c>i % 2</c> would test the fixture. The reason interleaving matters (seeing,
        /// transparency and focus drift monotonically through a real session, so contiguous halves
        /// disagree about the signal and an N2N pair teaches the model to average that away) is
        /// recorded at the split itself.</para>
        /// </summary>
        [Fact]
        public async Task Register_SplitsIntoAHalfMasterPair_OnlyWhenEnoughSubsRegistered()
        {
            var ct = TestContext.Current.CancellationToken;
            var session = WriteLightSession();
            var calibrator = await BuildDarkCalibratorAsync(ct);

            // No override: an 8-light fixture integrates by rejection, so the floor that applies is
            // the rejected one (40), and the pair must be absent rather than degenerate.
            SessionRegistrar.MinSubsForHalfMasters(drizzled: false)
                .ShouldBeGreaterThan(RgbBayerSyntheticFixture.LightCount);
            var withoutPair = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(_dir, "scratch-nopair"), minSubs: 4,
                logger: new XunitLogger(output), cancellationToken: ct);
            withoutPair.ShouldNotBeNull();
            withoutPair.HalfMasterA.ShouldBeNull();
            withoutPair.HalfMasterB.ShouldBeNull();

            var result = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(_dir, "scratch-pair"), minSubs: 4,
                minSubsForHalfMasters: 4, logger: new XunitLogger(output), cancellationToken: ct);

            result.ShouldNotBeNull();
            var halfA = result.HalfMasterA.ShouldNotBeNull();
            var halfB = result.HalfMasterB.ShouldNotBeNull();

            // Same grid as the master and each other, or a tile cut at one cell would not be the same
            // sky footprint in all three and the pair would not be a pair.
            foreach (var half in new[] { halfA, halfB })
            {
                half.Width.ShouldBe(result.CanvasWidth);
                half.Height.ShouldBe(result.CanvasHeight);
                half.ChannelCount.ShouldBe(result.Master.ChannelCount);
                float.IsFinite(half.MaxValue).ShouldBeTrue();
            }

            // Two independent integrations, not one image handed out twice.
            ReferenceEquals(halfA, halfB).ShouldBeFalse();
            var noiseA = NeighbourNoise(halfA);
            var noiseB = NeighbourNoise(halfB);
            var noiseMaster = NeighbourNoise(result.Master);
            output.WriteLine($"noise: master={noiseMaster:E3} halfA={noiseA:E3} halfB={noiseB:E3} " +
                $"(subs={result.RegisteredCount})");
            // Half the frames, so measurably noisier than the full master. This is the property that
            // makes the pair worth exporting at all, and it is also what would break if a half were
            // silently integrating every sub instead of its own subset.
            noiseA.ShouldBeGreaterThan(noiseMaster);
            noiseB.ShouldBeGreaterThan(noiseMaster);
        }

        /// <summary>Mean absolute difference between horizontally adjacent finite samples over a
        /// central box: a noise proxy that needs no sort and no knowledge of the stretch. Both frames
        /// carry the same stars, so the comparison between them is unaffected by structure.</summary>
        private static double NeighbourNoise(Image image)
        {
            var span = image.GetChannelSpan(0);
            var w = image.Width;
            var (x0, x1) = (image.Width / 4, image.Width * 3 / 4);
            var (y0, y1) = (image.Height / 4, image.Height * 3 / 4);
            var sum = 0.0;
            var n = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x + 1 < x1; x++)
                {
                    var a = span[y * w + x];
                    var b = span[y * w + x + 1];
                    if (float.IsFinite(a) && float.IsFinite(b))
                    {
                        sum += Math.Abs(a - b);
                        n++;
                    }
                }
            }
            n.ShouldBeGreaterThan(0);
            return sum / n;
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

        [Fact]
        public void TheDrizzleSkyReferenceIsEachColoursMedianOverTheSubs_NotAnySingleSub()
        {
            // Statue of Liberty's registration reference sat on a sky 2.1x the session's, and taking its
            // sky put the whole master on that pedestal. Each colour's median is taken on its own, so a
            // colour-shifted sub cannot drag the other colours with it.
            static Normalizer.CfaNormalizationStats Sky(float r, float g, float b)
                => new(new NormalizationStats([10f], [r]), new NormalizationStats([10f], [g]), new NormalizationStats([10f], [b]));

            var skies = new[] { Sky(300, 1000, 600), Sky(320, 900, 650), Sky(900, 2100, 1300), Sky(310, 950, 610), Sky(290, 1020, 700) };

            var median = SessionRegistrar.MedianSky(skies);

            median.Red.PerChannelMedian[0].ShouldBe(310f);
            median.Green.PerChannelMedian[0].ShouldBe(1000f);
            median.Blue.PerChannelMedian[0].ShouldBe(650f);
            median.Red.PerChannelFloor[0].ShouldBe(10f);
            SessionRegistrar.MedianSky([Sky(1, 2, 3), Sky(3, 6, 9)]).Green.PerChannelMedian[0].ShouldBe(4f, "an even count takes the mean of the middle two");
        }

        /// <summary>
        /// The two ways to a warped sub agree pixel for pixel: reading the scratch FITS the warp pass
        /// wrote, and warping the source again. That equality is what lets a drizzled session write no
        /// scratch at all, and it is not free -- the re-warp has to use the same calibration, the same
        /// demosaic, the same interpolation kernel and the same canvas, and each of those silently
        /// produces a plausible-but-different frame if it drifts (VNG against MHC is a measurable
        /// difference in flat-field bias alone).
        ///
        /// <para>Asserted on a STAGED session, because that is the only kind that has both routes
        /// available: it wrote the files, and the sub carries the transform a re-warp needs. The
        /// drizzled case is the one below, where there is nothing to compare against because there is
        /// nothing on disk.</para>
        /// </summary>
        [Fact]
        public async Task ARewarpedSubIsTheSubTheWarpPassWouldHaveWritten()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await DatasetSyntheticFixtures.RegisterAsync(_dir, ct);

            registered.MasterStrategy.ShouldBe(IntegrationStrategyKind.Float16Staged, "8 subs is far under drizzle's floor");
            registered.WarpedSubs.ShouldNotBeNull();
            var source = registered.WarpedSubs;

            var sub = registered.Subs[registered.Subs.Length - 1];
            sub.WarpedPath.ShouldNotBeNull("a staged session materialises every sub");
            File.Exists(sub.WarpedPath).ShouldBeTrue();

            var fromScratch = await source.LoadAsync(sub, ct);
            // The same sub with its path taken away is exactly what a drizzled session hands over.
            var rewarped = await source.LoadAsync(sub with { WarpedPath = null }, ct);

            rewarped.Width.ShouldBe(fromScratch.Width);
            rewarped.Height.ShouldBe(fromScratch.Height);
            rewarped.ChannelCount.ShouldBe(fromScratch.ChannelCount);

            var maxDiff = 0f;
            var compared = 0;
            for (var c = 0; c < fromScratch.ChannelCount; c++)
            {
                var a = fromScratch.GetChannelSpan(c);
                var b = rewarped.GetChannelSpan(c);
                a.Length.ShouldBe(b.Length);
                for (var i = 0; i < a.Length; i++)
                {
                    // Outside the source footprint both are NaN, which is equality here and not a
                    // comparison: NaN != NaN, so it has to be said rather than measured.
                    if (float.IsNaN(a[i]) || float.IsNaN(b[i]))
                    {
                        float.IsNaN(a[i]).ShouldBe(float.IsNaN(b[i]), $"channel {c} sample {i} is absent in one route only");
                        continue;
                    }
                    maxDiff = Math.Max(maxDiff, Math.Abs(a[i] - b[i]));
                    compared++;
                }
            }
            compared.ShouldBeGreaterThan(0);
            output.WriteLine($"compared {compared} samples, max |diff| = {maxDiff}");
            // A float32 FITS round trip is exact, and so is running the same kernels over the same
            // pixels, so this is equality rather than a tolerance; the epsilon is only there to keep
            // the failure message useful if a future writer ever quantises.
            maxDiff.ShouldBeLessThanOrEqualTo(1e-6f);
        }

        /// <summary>
        /// A drizzled session writes no warped scratch, because nothing in it reads one:
        /// <see cref="DrizzleStrategy"/> forward-projects the raw CFA (<c>RawBayerFrames</c>), both
        /// half-masters take the master's strategy, and the tiler re-warps what it needs. The cost
        /// avoided is the whole session at once: a canvas-sized float32 FITS per sub, ~117 MB each on
        /// a 24 MP OSC frame, which is what put the 861-sub ASI294MC eta Car session at ~120 GB and
        /// killed it mid-warp on a full disk.
        /// </summary>
        [Fact]
        public async Task ADrizzledSessionWritesNoWarpedScratch()
        {
            var ct = TestContext.Current.CancellationToken;
            var lightsDir = Path.Combine(_dir, "LIGHT");
            Directory.CreateDirectory(lightsDir);
            // The drizzle gate is a frame count, so the session has to clear it to BE a drizzled one.
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir, DrizzleStrategy.AutoSelectMinFrameCount);
            var calibrator = await BuildDarkCalibratorAsync(ct);
            var session = new ImagingSession(
                lightsDir, "synth/rggb-deep", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);
            var scratch = Path.Combine(_dir, "scratch");

            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, scratch, minSubs: 4, logger: new XunitLogger(output), cancellationToken: ct);

            registered.ShouldNotBeNull();
            registered.MasterStrategy.ShouldBe(IntegrationStrategyKind.BayerDrizzle);
            registered.Subs.ShouldAllBe(s => s.WarpedPath == null);
            Directory.GetFiles(scratch, "warped_*.fits", SearchOption.AllDirectories).ShouldBeEmpty();
            // The subs are still readable, which is the whole point: the tiler asks for them after
            // the master exists and gets them warped on demand.
            registered.WarpedSubs.ShouldNotBeNull();
            var rewarped = await registered.WarpedSubs.LoadAsync(registered.Subs[0], ct);
            rewarped.Width.ShouldBe(registered.CanvasWidth);
            rewarped.Height.ShouldBe(registered.CanvasHeight);
            rewarped.ChannelCount.ShouldBe(3);
        }

        /// <summary>
        /// The scratch requirement is answered before the first sub is warped, not discovered at the
        /// one that fills the disk. The session it was written for spent forty minutes measuring,
        /// registering and warping before dying at <c>warped_0572.fits</c> on "There is not enough
        /// space on the disk", an exception naming a frame and a path and nothing about the session
        /// being three times the size of the drive it was given.
        /// </summary>
        [Fact]
        public void TheScratchRequirementIsTheWarpedSubsThemselves()
        {
            Directory.CreateDirectory(_dir);

            // A canvas-sized float32 FITS per sub, three channels. Two subs of a 384x384 canvas is
            // about 3.5 MB, which fits on any drive a test runs on.
            SessionRegistrar.ScratchShortfall(_dir, subCount: 2, canvasWidth: 384, canvasHeight: 384).ShouldBeNull();

            // 100k subs of a 24 MP canvas is ~12 TB: refused, and the report carries the numbers the
            // log line needs rather than a bare bool, because "not enough space" without the
            // requirement beside it is what made the real failure unreadable.
            var shortfall = SessionRegistrar.ScratchShortfall(_dir, subCount: 100_000, canvasWidth: 6000, canvasHeight: 4000);
            shortfall.ShouldNotBeNull();
            shortfall.NeedBytes.ShouldBeGreaterThan(shortfall.FreeBytes);
            shortfall.NeedBytes.ShouldBeGreaterThan(100_000L * 6000 * 4000 * 3 * sizeof(float));
            shortfall.Drive.ShouldNotBeNullOrEmpty();

            // A path on no drive at all answers nothing rather than refusing the session: a drive that
            // will not report its free space is not evidence that the session is too big.
            SessionRegistrar.ScratchShortfall("\0not-a-path", subCount: 1, canvasWidth: 1, canvasHeight: 1).ShouldBeNull();
        }

        /// <summary>
        /// The flip is read off the registration, not off a PIERSIDE card: a sub's transform onto the
        /// reference says which way the field was lying, and a fifth of this archive carries no card
        /// at all. A quarter turn separates the two cases by a wide margin, because a flip is half a
        /// turn and a night's ordinary field rotation is a fraction of a degree.
        /// </summary>
        [Fact]
        public void TheFieldRotationSplitSeesHalfATurn_AndNothingLess()
        {
            static SessionRegistrar.RegisteredSub Sub(float degrees)
            {
                var m = Matrix3x2.CreateRotation(degrees * MathF.PI / 180f);
                return new SessionRegistrar.RegisteredSub(
                    new FrameInfo("x.fits", 1, 1, 1, BitDepth.Int16, new ImageMeta()), null, m, default);
            }

            // A night that never flipped: every frame within a degree of the first, however it drifted.
            var (first, second) = SessionRegistrar.SplitByFieldRotation(
                [Sub(0f), Sub(0.4f), Sub(-0.6f), Sub(0.2f)]);
            first.Length.ShouldBe(4);
            second.ShouldBeEmpty();

            // A night that did: the second half lies half a turn from the first, and the split follows
            // the FIELD rather than the frame order, so an out-of-order frame lands with its own side.
            (first, second) = SessionRegistrar.SplitByFieldRotation(
                [Sub(0f), Sub(0.3f), Sub(179.4f), Sub(-0.2f), Sub(180.5f), Sub(-179.7f)]);
            first.ShouldBe(new[] { 0, 1, 3 });
            second.ShouldBe(new[] { 2, 4, 5 });

            // Wrapping is not a flip: 359.5 degrees is half a degree the other way.
            (first, second) = SessionRegistrar.SplitByFieldRotation([Sub(0f), Sub(359.5f), Sub(-359.6f)]);
            first.Length.ShouldBe(3);
            second.ShouldBeEmpty();
        }

        /// <summary>
        /// A flipped session emits BOTH the combined master and one per side, and each side's
        /// all-frames intersection is LARGER than the combined session's: that rectangle is what the
        /// tiler samples cells from, and mixing two orientations on one canvas is what shrinks it.
        ///
        /// <para>The sides are the same sky as the session, which is why their ids carry the flip
        /// suffix and strip back to it -- <see cref="DatasetSplitWriter"/> hashes the stripped id, so
        /// a night cannot land in train and test at once.</para>
        /// </summary>
        [Fact]
        public async Task AFlippedSessionAlsoIntegratesEachSideOfTheFlip()
        {
            var ct = TestContext.Current.CancellationToken;
            var lightsDir = Path.Combine(_dir, "LIGHT");
            Directory.CreateDirectory(lightsDir);
            // Sixteen frames, the last eight showing the sky turned half a turn.
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir, 16, flipFrom: 8);
            var calibrator = await BuildDarkCalibratorAsync(ct);
            var session = new ImagingSession(
                lightsDir, "synth/rggb-flip", "SynthBayer", "SynthRgb", "", [.. ReadFrames(lightsDir, "light_*.fits")]);

            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(_dir, "scratch"), minSubs: 4,
                minSubsForHalfMasters: int.MaxValue, logger: new XunitLogger(output), cancellationToken: ct);

            registered.ShouldNotBeNull();
            registered.FlipSides.Length.ShouldBe(2, "the session holds two field orientations");
            var (sideA, sideB) = (registered.FlipSides[0], registered.FlipSides[1]);
            sideA.Subs.Length.ShouldBeGreaterThanOrEqualTo(4);
            sideB.Subs.Length.ShouldBeGreaterThanOrEqualTo(4);
            (sideA.Subs.Length + sideB.Subs.Length).ShouldBe(registered.Subs.Length, "every sub belongs to exactly one side");

            static long Area(PixelRect r) => (long)r.Width * r.Height;
            Area(sideA.StatsRect).ShouldBeGreaterThan(Area(registered.StatsRect));
            Area(sideB.StatsRect).ShouldBeGreaterThan(Area(registered.StatsRect));

            sideA.Master.Width.ShouldBe(registered.CanvasWidth);
            sideA.Master.ChannelCount.ShouldBe(3);
            sideA.Session.Id.ShouldBe(session.Id + "|flip=" + SessionRegistrar.FlipSideFirst);
            sideB.Session.Id.ShouldBe(session.Id + "|flip=" + SessionRegistrar.FlipSideSecond);
            DatasetSplitWriter.GroupIdOf(sideA.Session.Id).ShouldBe(session.Id);
            DatasetSplitWriter.GroupIdOf(sideB.Session.Id).ShouldBe(session.Id);
            registered.SelfAndFlipSides().Count().ShouldBe(3);
            output.WriteLine(
                $"combined {registered.Subs.Length} subs statsRect {registered.StatsRect}; " +
                $"sides {sideA.Subs.Length}/{sideB.Subs.Length} statsRects {sideA.StatsRect}/{sideB.StatsRect}");
        }
    }
}
