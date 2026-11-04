using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A group whose lights all register but which the cloud filter empties is skipped for cloud or twilight, through the pipeline
/// (#1998). Ten dark lights of one rig and night are judged against that rig's clear-night reference (the viewer's store, #1976):
/// a reference sky far darker than the run's makes every light cloud, so the group is emptied, and the skip says so rather than
/// "fewer than 2 matched frames". With the filter off the same frames stack, so the branch is not reached by a registration
/// shortfall. (A group taken wholly in twilight no longer empties: a night with fewer than ten dark lights keeps its twilight lights.)
/// </summary>
[Collection("Imaging")]
public class StackingCloudEmptiedGroupTest(ITestOutputHelper output)
{
    private const int FrameSize = 512;
    private const int FrameCount = CloudRejection.MinGradedForCloud;
    private const float Latitude = 51f;
    private const float Longitude = 0f;

    /// <summary>StackingPipelineSyntheticTest's eight dither offsets, which all register, and two more of the same size.</summary>
    private static readonly (double DX, double DY)[] DitherOffsets =
    [
        (0.0, 0.0), (1.3, 0.7), (-0.8, 1.5), (2.1, -1.1), (-1.7, -0.4), (0.6, 2.2), (-2.4, 0.3), (1.9, -1.8), (0.9, -0.6), (-1.2, 1.1),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGroupTheReferenceCallsCloudyIsSkippedForCloudNotForRegistration(bool rejectCloud)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var first = WriteDarkFrames(workspace.LightsDir);

        var external = new FakeExternal(output);
        await WriteClearReferenceAsync(external, first, ct);

        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir, RejectCloud: rejectCloud);
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null, external: external);

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

    /// <summary>
    /// The rig's clear night, keyed as the pipeline keys the group (the frame's own grade key and rig, the site), from another night,
    /// with a darkest sky a hundredth of anything a synthetic frame here reads.
    /// </summary>
    private static async Task WriteClearReferenceAsync(FakeExternal external, string firstFrame, System.Threading.CancellationToken ct)
    {
        Image.TryReadFitsFile(firstFrame, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        var reference = new LightGradeReference(LightGradeKey.Of(image) with { ObjectKey = string.Empty }, LightGrading.RigOf(image.ImageMeta),
            Latitude, Longitude, new DateOnly(2026, 11, 1), [], NightDarkestSky: 1e-3f);
        var path = LightGradeReferenceStore.PathFor(external);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? external.AppDataFolder.FullName);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new LightGradeReferenceFile([reference]), LightGradeReferenceJsonContext.Default.LightGradeReferenceFile), ct);
        (await LightGradeReferenceStore.LoadAsync(external, ct)).Length.ShouldBe(1, "the reference must read back through the store");
    }

    private static string WriteDarkFrames(string lightsDir)
    {
        var paths = new List<string>();
        for (var i = 0; i < FrameCount; i++)
        {
            var pixels = SyntheticStarFieldRenderer.Render(
                width: FrameSize, height: FrameSize, defocusSteps: 0, offsetX: DitherOffsets[i].DX, offsetY: DitherOffsets[i].DY,
                exposureSeconds: 1.0, skyBackground: 100.0, readNoise: 5.0, starCount: 60, seed: 42, hotPixelCount: 0, maxADU: 4096.0,
                noiseSeed: 42 + i);
            var meta = new ImageMeta
            {
                Instrument = "Synth",
                // Midnight at 0 E in December: the Sun is far below -18 degrees, so no light is twilight.
                ExposureStartTime = new DateTimeOffset(2026, 12, 21, 0, i, 0, TimeSpan.Zero),
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
                Latitude = Latitude,
                Longitude = Longitude,
            };
            var img = new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            var path = Path.Combine(lightsDir, $"frame_{i:D2}.fits");
            img.WriteToFitsFile(path);
            paths.Add(path);
        }
        return paths[0];
    }
}
