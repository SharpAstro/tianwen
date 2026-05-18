using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// RGB Bayer counterpart to <see cref="StackingPipelineSyntheticTest"/>:
/// exercises the calibrate -> debayer -> register -> integrate chain end-
/// to-end on synthetic <see cref="SensorType.RGGB"/> data, plus a tiny
/// 2-frame master dark group so the Calibrator gets a real dark to
/// subtract. Catches regressions in the Bayer code path that the mono
/// test cannot see (debayer choice, RGGB Bayer pattern propagation,
/// 3-channel master output).
/// </summary>
[Collection("Imaging")]
public class StackingPipelineRgbBayerSyntheticTest(ITestOutputHelper output)
{
    // 384x384 leaves enough star detections per debayered channel to clear
    // StackingPipeline.MinStarsForMatch (24) consistently. Fixture footprint
    // for 4 lights + 2 darks: 6 * 384^2 * 2B = 1.7 MB on disk.
    private const int FrameSize = 384;
    private const int LightCount = 4;
    private const int DarkCount = 2;
    private const int StarCount = 50;
    private const float DarkLevel = 80.0f;

    /// <summary>Per-frame sub-pixel dither -- mirrors real-world capture
    /// (mount/atmosphere drift is never Bayer-aligned). Frame 0 sits at
    /// origin and becomes the registration reference. The high
    /// <c>exposureSeconds</c> in <see cref="WriteSyntheticLights"/> keeps
    /// star SNR high enough that debayer-interpolation centroid jitter
    /// stays inside the quad-match tolerance ladder.</summary>
    private static readonly (double DX, double DY)[] DitherOffsets =
    [
        ( 0.0,  0.0),
        ( 1.4,  0.6),
        (-1.1,  1.7),
        ( 2.0, -1.2),
    ];

    /// <summary>Light frames that receive an injected hot pixel. The
    /// rejector should kill them on the strength of the other 2 inliers
    /// at the same canvas position (we drop to 2 because LFC needs the
    /// rest of the dataset clean to identify the outlier).</summary>
    private static readonly int[] HotPixelFrames = [1, 3];

    [Fact]
    public async Task Stack_4Frames_RGGB_WithDarkMaster_ProducesThreeChannelMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);

        WriteSyntheticLights(workspace.LightsDir);
        WriteSyntheticDarks(darksDir);

        // No catalog DB -> plate-solve skipped. The Bayer/debayer path is
        // what we're verifying here, not the WCS pipeline.
        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir);
        var logger = new XunitLogger(output);
        var pipeline = new StackingPipeline(options, logger, catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        // 1) One light group survived; all 4 RGGB frames registered against
        //    the reference. If FrameType.Dark frames had leaked into the
        //    light enumeration we'd see a second group or a registration
        //    skip; if SensorType.RGGB had been ignored we'd get a
        //    single-channel master.
        results.Count.ShouldBe(1, "expected a single integrated light group");
        var result = results[0];
        result.SkipReason.ShouldBeEmpty($"group should not have skipped: '{result.SkipReason}'");
        result.FramesAttempted.ShouldBe(LightCount);
        // RGGB + sub-pixel dither stresses the quad-matcher more than the
        // mono path -- debayer interpolation at star edges drifts centroids
        // by ~0.1-0.3 px in a phase-dependent way that scales with dither.
        // The mono synthetic test matches 8/8 on the same dither magnitude
        // range; here we expect at least 3/4 (75%). A regression that broke
        // debayer or quad matching outright would drop below 2 and trip
        // the SkipReason check above.
        result.FramesMatched.ShouldBeGreaterThanOrEqualTo(3,
            $"expected at least 3/4 RGGB frames to register; got {result.FramesMatched}");

        // 2) Master FITS landed on disk + round-trips with a 3-channel
        //    shape. This is the key Bayer assertion: a missed debayer
        //    step would yield ChannelCount = 1.
        result.MasterFitsPath.ShouldNotBeNull();
        File.Exists(result.MasterFitsPath).ShouldBeTrue($"master FITS missing at {result.MasterFitsPath}");
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3, "RGGB lights should produce a 3-channel debayered master");
        master.Width.ShouldBeGreaterThanOrEqualTo(FrameSize);
        master.Height.ShouldBeGreaterThanOrEqualTo(FrameSize);

        // 3) Every channel carries signal. A debayer regression that left
        //    one channel zeroed (mismatched Bayer offset, dropped R or B
        //    interpolation step) would surface here as a near-zero mean.
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var (_, median, _) = master.GetPedestralMedianAndMADScaledToUnit(c);
            median.ShouldBeGreaterThan(1e-4f,
                $"channel {c} median {median:F6} is too close to zero -- debayer / calibration likely zeroed it");
        }

        // 4) Calibration master cache populated. The 2-dark group should
        //    have built one master_*.fits under output/masters/. Calibration
        //    masters are written by MasterFrameBuilder, NOT IntegrationFitsWriter,
        //    so they're deliberately NOT stamped with SWCREATE -- they live
        //    under masters/ which the wipe scan never touches.
        var mastersDir = Path.Combine(workspace.OutputDir, "masters");
        Directory.Exists(mastersDir).ShouldBeTrue();
        var cachedMasters = Directory.GetFiles(mastersDir, "*.fits");
        cachedMasters.Length.ShouldBe(1, "exactly one dark master should have been cached");

        // 5) The integrated master FITS at outputDir top-level IS stamped
        //    and would survive the wipe-on-rerun scan as one of our own.
        IntegrationFitsWriter.IsTianWenMaster(result.MasterFitsPath).ShouldBeTrue(
            "integrated master should round-trip through IsTianWenMaster (SWCREATE stamping)");
    }

    private static void WriteSyntheticLights(string lightsDir)
    {
        for (var i = 0; i < LightCount; i++)
        {
            var (dx, dy) = DitherOffsets[i];
            var hotCount = HotPixelFrames.Contains(i) ? 1 : 0;
            // SyntheticStarFieldRenderer renders mono Gaussian stars; we
            // hand the buffer in to BuildBayerMosaic to overlay an RGGB
            // colour signature so debayered R/G/B channels each have
            // non-trivial structure.
            // High SNR keeps star centroids stable across debayer
            // interpolation. With sky=100, readNoise=5, exposure=1
            // (the mono test's params), bayer interpolation artifacts at
            // star edges drift centroids enough that quad fingerprints
            // drift past qt=0.5 on 2 of 4 frames. Bumping exposure to 10s
            // pushes star SNR roughly 3.2x and stabilises the fit.
            var mono = SyntheticStarFieldRenderer.Render(
                width: FrameSize,
                height: FrameSize,
                defocusSteps: 0,
                offsetX: dx,
                offsetY: dy,
                exposureSeconds: 10.0,
                skyBackground: 100.0,
                readNoise: 5.0,
                starCount: StarCount,
                seed: 1337,           // fixed star field across all 4 frames
                hotPixelCount: hotCount,
                maxADU: 4096.0,
                noiseSeed: 1337 + i); // noise varies per frame

            var bayered = BuildBayerMosaic(mono, DarkLevel);

            var meta = new ImageMeta
            {
                Instrument = "SynthBayer",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, i, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Light,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = SensorType.RGGB,    // <-- the key bit driving the debayer path
                ObjectName = "SynthRgb",
                Gain = 100,
                Offset = 25,
            };
            var img = new Image(
                data: [bayered],
                bitDepth: BitDepth.Int16,
                maxValue: 4096f,
                minValue: 0f,
                pedestal: 0f,
                imageMeta: meta);
            img.WriteToFitsFile(Path.Combine(lightsDir, $"light_{i:D2}.fits"));
        }
    }

    private static void WriteSyntheticDarks(string darksDir)
    {
        // 2 noise-only frames at the same exposure / temperature / sensor
        // signature as the lights so MasterFrameBuilder produces a single
        // dark master that Calibrator.Apply uses on every light.
        for (var i = 0; i < DarkCount; i++)
        {
            var pixels = new float[FrameSize, FrameSize];
            var rng = new Random(7 + i);
            for (var y = 0; y < FrameSize; y++)
            {
                for (var x = 0; x < FrameSize; x++)
                {
                    pixels[y, x] = DarkLevel + (float)(rng.NextDouble() * 4.0); // 80..84 ADU
                }
            }

            var meta = new ImageMeta
            {
                Instrument = "SynthBayer",
                ExposureStartTime = new DateTimeOffset(2026, 5, 17, 0, 0, i, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Dark,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = SensorType.RGGB,
                ObjectName = "SynthRgb",
                Gain = 100,
                Offset = 25,
            };
            var img = new Image(
                data: [pixels],
                bitDepth: BitDepth.Int16,
                maxValue: 4096f,
                minValue: 0f,
                pedestal: 0f,
                imageMeta: meta);
            img.WriteToFitsFile(Path.Combine(darksDir, $"dark_{i:D2}.fits"));
        }
    }

    /// <summary>
    /// Overlays an RGGB colour signature on a mono star field + dark
    /// pedestal, returning the raw Bayer plane the camera would have
    /// produced. Star flux carries through; the per-Bayer-position
    /// multiplier (R=1.0, G=0.7, B=0.4) gives the debayer something
    /// non-uniform to interpolate so all three master channels end up
    /// with distinct signal levels.
    /// </summary>
    private static float[,] BuildBayerMosaic(float[,] mono, float darkPedestal)
    {
        var h = mono.GetLength(0);
        var w = mono.GetLength(1);
        var dst = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // RGGB pattern with offset (0, 0):
                //   even row + even col -> R
                //   even row + odd col  -> G
                //   odd  row + even col -> G
                //   odd  row + odd col  -> B
                var isEvenRow = (y & 1) == 0;
                var isEvenCol = (x & 1) == 0;
                float gain = (isEvenRow, isEvenCol) switch
                {
                    (true,  true)  => 1.0f, // R
                    (true,  false) => 0.7f, // G (row 0)
                    (false, true)  => 0.7f, // G (row 1)
                    (false, false) => 0.4f, // B
                };
                dst[y, x] = darkPedestal + mono[y, x] * gain;
            }
        }
        return dst;
    }
}
