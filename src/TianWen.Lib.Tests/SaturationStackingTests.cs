using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stacker writes the saturation sidecar SPCC's clip gate reads (#1991): per master pixel, how many lights had a
/// photosite at the ADC ceiling there, found on the RAW decode and carried through the registration warp.
/// </summary>
[Collection("Imaging")]
public class SaturationStackingTests(ITestOutputHelper output)
{
    private const float Ceiling = 4500f;
    private const int BlobX = 200;
    private const int BlobY = 150;

    /// <summary>The fixture's lights, with a star at (BlobX, BlobY) (moved by each frame's dither) clipped at the ceiling
    /// when <paramref name="clip"/>. The ceiling sits above anything the fixture renders (4096 + the pedestal), so only
    /// the blob can reach it.</summary>
    private static void WriteLights(string lightsDir, bool clip)
    {
        for (var i = 0; i < RgbBayerSyntheticFixture.LightCount; i++)
        {
            var (dx, dy) = RgbBayerSyntheticFixture.DitherOffsets[i];
            var mono = SyntheticStarFieldRenderer.Render(
                width: RgbBayerSyntheticFixture.FrameSize, height: RgbBayerSyntheticFixture.FrameSize, defocusSteps: 0,
                offsetX: dx, offsetY: dy, exposureSeconds: 10.0, skyBackground: 100.0, readNoise: 5.0,
                starCount: RgbBayerSyntheticFixture.StarCount, seed: 1337, hotPixelCount: 0, maxADU: 4096.0,
                noiseSeed: 1337 + i);
            var bayered = RgbBayerSyntheticFixture.BuildBayerMosaic(mono, RgbBayerSyntheticFixture.DarkLevel);
            if (clip)
            {
                var cx = (int)Math.Round(BlobX + dx);
                var cy = (int)Math.Round(BlobY + dy);
                for (var y = cy - 2; y <= cy + 2; y++)
                {
                    for (var x = cx - 2; x <= cx + 2; x++)
                    {
                        if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= 4)
                        {
                            bayered[y, x] = Ceiling;
                        }
                    }
                }
            }
            var meta = new ImageMeta
            {
                Instrument = "SynthBayer",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Light,
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
                SensorFullScaleAdu = Ceiling,
            };
            new Image([bayered], BitDepth.Int16, Ceiling, 0f, 0f, meta)
                .WriteToFitsFile(Path.Combine(lightsDir, $"light_{i:D2}.fits"));
        }
    }

    private async Task<(GroupResult Result, Image? Saturation)> StackAsync(bool clip, IntegrationStrategyKind? forced, DrizzleOptions? drizzle)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        WriteLights(workspace.LightsDir, clip);
        var options = new StackingOptions(
            DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir,
            ForcedStrategy: forced, DrizzleOptions: drizzle);
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }
        results.Count.ShouldBe(1);
        var result = results[0];
        result.SkipReason.ShouldBeEmpty();
        result.MasterFitsPath.ShouldNotBeNull();
        IntegrationFitsWriter.TryReadSaturationMap(result.MasterFitsPath, out var map);
        // The sidecar is read while the workspace is alive; detach the pixels from the file.
        return (result, map);
    }

    private static void ShouldCoverOnlyTheBlob(Image map, int frames)
    {
        var plane = map.GetChannelArray(0);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, nonzero = 0;
        float peak = 0;
        for (var y = 0; y < plane.GetLength(0); y++)
        {
            for (var x = 0; x < plane.GetLength(1); x++)
            {
                if (plane[y, x] > 0)
                {
                    nonzero++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    peak = Math.Max(peak, plane[y, x]);
                }
            }
        }
        nonzero.ShouldBeGreaterThan(0, "the clipped star must be counted");
        (maxX - minX).ShouldBeLessThan(14, "only the clipped star's neighbourhood (dither included) may be marked");
        (maxY - minY).ShouldBeLessThan(14);
        peak.ShouldBeLessThanOrEqualTo(frames);
        peak.ShouldBeGreaterThan(1f, "the star is clipped in every sub, so overlapping subs add up");
    }

    [Fact(Timeout = 300_000)]
    public async Task DefaultAhdPath_WritesASaturationSidecarOverTheClippedStar()
    {
        var (result, map) = await StackAsync(clip: true, forced: null, drizzle: null);
        map.ShouldNotBeNull("a master with clipped subs carries a saturation sidecar");
        ShouldCoverOnlyTheBlob(map, result.FramesMatched);
    }

    [Fact(Timeout = 300_000)]
    public async Task BayerDrizzlePath_WritesASaturationSidecarOverTheClippedStar()
    {
        var (result, map) = await StackAsync(clip: true, IntegrationStrategyKind.BayerDrizzle, new DrizzleOptions(MinFrameCount: 6));
        map.ShouldNotBeNull();
        ShouldCoverOnlyTheBlob(map, result.FramesMatched);
    }

    [Fact(Timeout = 300_000)]
    public async Task NoClippedPhotosite_WritesNoSaturationSidecar()
    {
        // Stated choice: a set with nothing at the ceiling writes NO sidecar (absence means "peak gate alone").
        var (_, map) = await StackAsync(clip: false, forced: null, drizzle: null);
        map.ShouldBeNull();
    }
}
