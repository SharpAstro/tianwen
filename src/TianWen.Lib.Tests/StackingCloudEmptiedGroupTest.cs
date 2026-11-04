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
/// A group whose lights all register but which the cloud filter empties is skipped for cloud or twilight, through the pipeline
/// (#1998): the frames are taken at 51 N near the June solstice, where the Sun never gets below -18 degrees, so every light grades as
/// twilight and <see cref="StackingOptions.RejectCloud"/> leaves them all out. With the filter off the same frames stack, so the
/// branch is not reached by a registration shortfall.
/// </summary>
[Collection("Imaging")]
public class StackingCloudEmptiedGroupTest(ITestOutputHelper output)
{
    private const int FrameSize = 512;
    private const int FrameCount = 8;

    private static readonly (double DX, double DY)[] DitherOffsets =
    [
        (0.0, 0.0), (1.3, 0.7), (-0.8, 1.5), (2.1, -1.1), (-1.7, -0.4), (0.6, 2.2), (-2.4, 0.3), (1.9, -1.8),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGroupTakenWhollyInTwilightIsSkippedForTwilightNotForRegistration(bool rejectCloud)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        WriteTwilightFrames(workspace.LightsDir);

        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir, RejectCloud: rejectCloud);
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1);
        var result = results[0];
        if (rejectCloud)
        {
            result.SkipReason.ShouldBe($"{FrameCount} of {FrameCount} matched frames left out for cloud or twilight, fewer than 2 remain");
            result.FramesMatched.ShouldBe(0);
        }
        else
        {
            result.SkipReason.ShouldBeEmpty($"with the filter off the frames register and stack: '{result.SkipReason}'");
            result.FramesMatched.ShouldBe(FrameCount);
        }
    }

    private static void WriteTwilightFrames(string lightsDir)
    {
        for (var i = 0; i < FrameCount; i++)
        {
            var (dx, dy) = DitherOffsets[i];
            var pixels = SyntheticStarFieldRenderer.Render(
                width: FrameSize, height: FrameSize, defocusSteps: 0, offsetX: dx, offsetY: dy, exposureSeconds: 1.0,
                skyBackground: 100.0, readNoise: 5.0, starCount: 60, seed: 42, hotPixelCount: 0, maxADU: 4096.0, noiseSeed: 42 + i);
            var meta = new ImageMeta
            {
                Instrument = "Synth",
                // Local midnight at 0 E on the solstice: the Sun sits near -15.5 degrees, twilight however the frames are spaced.
                ExposureStartTime = new DateTimeOffset(2026, 6, 21, 0, i, 0, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Light,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = SensorType.Monochrome,
                ObjectName = "SynthTarget",
                Gain = 100,
                Offset = 25,
                Latitude = 51f,
                Longitude = 0f,
            };
            var img = new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            img.WriteToFitsFile(Path.Combine(lightsDir, $"frame_{i:D2}.fits"));
        }
    }
}
