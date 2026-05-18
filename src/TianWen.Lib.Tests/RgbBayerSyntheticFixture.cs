using System;
using System.IO;
using System.Linq;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests;

/// <summary>
/// Shared synthetic data factory for the RGGB Bayer pipeline tests
/// (<see cref="StackingPipelineRgbBayerSyntheticTest"/> for the debayer
/// path; <see cref="StackingPipelineRgbBayerDrizzleTest"/> for drizzle).
/// Same 4 lights + 2 darks fixture for both -- the strategy is the only
/// variable. Keeping the helpers in one place avoids drift between the
/// "calibrate-debayer-warp-stack" and "calibrate-skip-debayer-drizzle"
/// branches of the pipeline using subtly different inputs and producing
/// hard-to-compare results.
/// </summary>
internal static class RgbBayerSyntheticFixture
{
    public const int FrameSize = 384;
    // 8 lights matches the mono synthetic test -- gives drizzle enough
    // coverage that ~25% R-cells and ~25% B-cells each see at least 1-2
    // drops across the dither pattern. 4 lights left big uncovered swaths
    // (>50% of the canvas had no R or B sample under sub-pixel dither),
    // which produced NaN-riddled drizzle masters that couldn't be
    // histogrammed for stretch stats.
    public const int LightCount = 8;
    public const int DarkCount = 2;
    public const int StarCount = 50;
    public const float DarkLevel = 80.0f;

    /// <summary>Per-frame sub-pixel dither -- mirrors real-world capture
    /// (mount/atmosphere drift is never Bayer-aligned). Frame 0 sits at
    /// origin and becomes the registration reference. The high
    /// <c>exposureSeconds</c> in <see cref="WriteSyntheticLights"/> keeps
    /// star SNR high enough that debayer-interpolation centroid jitter
    /// stays inside the quad-match tolerance ladder.</summary>
    public static readonly (double DX, double DY)[] DitherOffsets =
    [
        ( 0.0,  0.0),
        ( 1.4,  0.6),
        (-1.1,  1.7),
        ( 2.0, -1.2),
        ( 0.7, -2.3),
        (-2.1, -0.5),
        ( 2.4,  1.9),
        (-0.9,  2.1),
    ];

    /// <summary>Light frames that get an injected hot pixel. The rejector
    /// (when used; not by drizzle) should kill them on the strength of the
    /// other inliers at the same canvas position.</summary>
    public static readonly int[] HotPixelFrames = [1, 3, 5];

    public static void WriteSyntheticLights(string lightsDir)
    {
        for (var i = 0; i < LightCount; i++)
        {
            var (dx, dy) = DitherOffsets[i];
            var hotCount = HotPixelFrames.Contains(i) ? 1 : 0;
            // SyntheticStarFieldRenderer renders mono Gaussian stars; we
            // hand the buffer to BuildBayerMosaic to overlay an RGGB colour
            // signature so debayered R/G/B channels each have non-trivial
            // structure (drizzle also reads the per-Bayer-position gain
            // directly without an interpolation step).
            // High SNR (10s exposure) keeps star centroids stable across
            // debayer interpolation; without it bayer artifacts at star
            // edges drift centroids past qt=0.5 on 2 of 4 frames.
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
                SensorType = SensorType.RGGB,    // drives both debayer + drizzle paths
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

    public static void WriteSyntheticDarks(string darksDir)
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
    /// multiplier (R=1.0, G=0.7, B=0.4) gives the debayer (or drizzle)
    /// something non-uniform to interpolate / project so all three master
    /// channels end up with distinct signal levels.
    /// </summary>
    public static float[,] BuildBayerMosaic(float[,] mono, float darkPedestal)
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
