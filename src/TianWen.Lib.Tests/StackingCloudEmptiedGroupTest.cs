using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
/// shortfall. A second test pins where the sky level is read (raw, not calibrated). (A group taken wholly in twilight no longer empties: a night with fewer than ten dark lights keeps its twilight lights.)
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
        var first = WriteFrames(workspace.LightsDir, FrameType.Light, skyBackground: 100.0);

        var external = new FakeExternal(output);
        await WriteClearReferenceAsync(external, first, nightDarkestSky: 1e-3f, ct);

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
    /// #2419: the forced-drizzle skip, through the pipeline. Twelve lights of a colour sensor all register; three were taken
    /// under a sky two and a half times the rig's clear night, so the cloud filter leaves those out and nine remain, under
    /// the twelve the forced drizzle is told to need. The skip says that the filter took the group under, with the pipeline's
    /// own counts: told that nothing was left out, it would say only "got 9" and send the reader to the registration.
    /// </summary>
    [Fact]
    public async Task AForcedDrizzleTheCloudFilterTookUnderItsMinimumSaysSoThroughThePipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        const int Lights = 12;
        const int Cloudy = 3;
        using var workspace = new TempStackingWorkspace();
        var first = WriteFrames(workspace.LightsDir, FrameType.Light, skyBackground: 100.0, count: Lights, SensorType.RGGB,
            skyOf: static i => i >= Lights - Cloudy ? 250.0 : 100.0);

        var external = new FakeExternal(output);
        await WriteClearReferenceAsync(external, first, nightDarkestSky: 80f, ct);

        // The star-quality filter is off, so that what is left out is the cloud filter's alone.
        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir, RejectCloud: true,
            QualityRejectSigma: 0f, ForcedStrategy: IntegrationStrategyKind.BayerDrizzle, DrizzleOptions: new DrizzleOptions(MinFrameCount: Lights));
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null, external: external);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        var result = results.ShouldHaveSingleItem();
        result.SkipReason.ShouldBe($"BayerDrizzle requires >= {Lights} matched frames (got {Lights - Cloudy}: {Cloudy} of {Lights} were left out for cloud or twilight; "
            + "--reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)");
        result.FramesMatched.ShouldBe(Lights - Cloudy);
    }

    /// <summary>
    /// The same skip when it is the STAR-QUALITY filter that took the group under (#2419's second note): two of twelve lights
    /// are out of focus, the filter leaves them out, and ten remain under the twelve the forced drizzle needs. The cloud filter
    /// is off here, so that what is left out is the quality filter's alone.
    /// </summary>
    [Fact]
    public async Task AForcedDrizzleTheQualityFilterTookUnderItsMinimumSaysSoThroughThePipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        const int Lights = 12;
        const int Soft = 2;
        using var workspace = new TempStackingWorkspace();
        WriteFrames(workspace.LightsDir, FrameType.Light, skyBackground: 100.0, count: Lights, SensorType.RGGB,
            defocusOf: static i => i >= Lights - Soft ? 40.0 : 0.0);

        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir, RejectCloud: false,
            ForcedStrategy: IntegrationStrategyKind.BayerDrizzle, DrizzleOptions: new DrizzleOptions(MinFrameCount: Lights));
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null, external: new FakeExternal(output));
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        var result = results.ShouldHaveSingleItem();
        result.SkipReason.ShouldBe($"BayerDrizzle requires >= {Lights} matched frames (got {Lights - Soft}: {Soft} of {Lights} were left out by the star-quality filter; "
            + "--quality-reject-sigma 0 keeps them, --drizzle-min-frames lowers the minimum)");
        result.FramesMatched.ShouldBe(Lights - Soft);
    }

    /// <summary>
    /// The grade reads its sky LEVEL off the raw light, not the calibrated one (#2003): the viewer's references and
    /// <see cref="LightGrading.NightSkyCloudFrom"/> are on the raw scale. Here a matched dark takes 60 counts off a sky of 160, and the
    /// rig's clear night is placed between the two scales: 160 against 80 is 2x, cloud, while the calibrated 100 against 80 is 1.25x,
    /// clear. So the group is left out only when the level is read raw; read calibrated, the cloud filter would keep every light (the
    /// star-quality filter still drops a couple, with or without it). The dark's use is asserted from the pipeline's log line.
    /// </summary>
    [Fact]
    public async Task TheReferenceIsComparedWithTheRawSkyNotTheCalibratedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var first = WriteFrames(workspace.LightsDir, FrameType.Light, skyBackground: 160.0);
        var darks = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darks);
        WriteFrames(darks, FrameType.Dark, skyBackground: 60.0);

        var external = new FakeExternal(output);
        await WriteClearReferenceAsync(external, first, nightDarkestSky: 80f, ct);

        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir, RejectCloud: true);
        var logger = new CapturingLogger(new XunitLogger(output));
        var pipeline = new StackingPipeline(options, logger, catalogDb: null, external: external);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        // Without a matched dark calibrated equals raw and the swap below is a no-op, so the match itself is asserted (from the
        // pipeline's own "dark master: <slug>" line, "NONE" when nothing matched), or a change to the dark matching key would
        // leave this test green while it no longer pins #2003.
        logger.Messages.ShouldContain(m => m.StartsWith("  dark master: dark_", StringComparison.Ordinal),
            $"the matched dark must be applied for the calibrated sky to differ from the raw one: {string.Join(" | ", logger.Messages.Where(m => m.Contains("dark master")))}");

        var lights = results.ShouldHaveSingleItem();
        // Every frame that registered is left out (how many register is the synthetic field's business, not this test's).
        var leftOut = CountLeftOut(lights.SkipReason);
        leftOut.ShouldBeGreaterThanOrEqualTo(2, $"the frames registered before the filter took them: '{lights.SkipReason}'");
        lights.SkipReason.ShouldBe($"{leftOut} of {leftOut} matched frames left out for cloud or twilight, fewer than 2 remain");
    }

    /// <summary>Forwards to an inner logger and keeps every formatted message, so a test can assert what the pipeline said it did.</summary>
    private sealed class CapturingLogger(ILogger inner) : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        public IReadOnlyCollection<string> Messages => [.. _messages];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>The N of an "N of N matched frames left out" skip reason, or 0 for any other reason.</summary>
    private static int CountLeftOut(string reason)
        => System.Text.RegularExpressions.Regex.Match(reason, @"^(\d+) of \1 matched frames left out for cloud or twilight") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>
    /// The rig's clear night, keyed as the pipeline keys the group (the frame's own grade key and rig, the site), from another night,
    /// with the darkest sky given.
    /// </summary>
    private static async Task WriteClearReferenceAsync(FakeExternal external, string firstFrame, float nightDarkestSky,
        System.Threading.CancellationToken ct)
    {
        Image.TryReadFitsFile(firstFrame, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        var reference = new LightGradeReference(LightGradeKey.Of(image) with { ObjectKey = string.Empty }, LightGrading.RigOf(image.ImageMeta),
            Latitude, Longitude, new DateOnly(2026, 11, 1), [], nightDarkestSky);
        var path = LightGradeReferenceStore.PathFor(external);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? external.AppDataFolder.FullName);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new LightGradeReferenceFile([reference]), LightGradeReferenceJsonContext.Default.LightGradeReferenceFile), ct);
        (await LightGradeReferenceStore.LoadAsync(external, ct)).Length.ShouldBe(1, "the reference must read back through the store");
    }

    /// <summary>
    /// Frames of one rig and night (lights carry 60 stars, darks none), ten unless <paramref name="count"/> says otherwise,
    /// the sky at <paramref name="skyBackground"/> counts or what <paramref name="skyOf"/> gives a frame.
    /// </summary>
    private static string WriteFrames(string dir, FrameType frameType, double skyBackground, int count = FrameCount,
        SensorType sensor = SensorType.Monochrome, Func<int, double>? skyOf = null, Func<int, double>? defocusOf = null)
    {
        var light = frameType == FrameType.Light;
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            // Past the ten offsets, the same ones again a third of a pixel further on: still a dither of its own.
            var (dx, dy) = DitherOffsets[i % DitherOffsets.Length];
            var further = 0.33 * (i / DitherOffsets.Length);
            var pixels = SyntheticStarFieldRenderer.Render(
                width: FrameSize, height: FrameSize, defocusSteps: defocusOf?.Invoke(i) ?? 0, offsetX: dx + further, offsetY: dy - further,
                exposureSeconds: 1.0, skyBackground: skyOf?.Invoke(i) ?? skyBackground, readNoise: 5.0, starCount: light ? 60 : 0, seed: 42, hotPixelCount: 0, maxADU: 4096.0,
                noiseSeed: 42 + i);
            var meta = new ImageMeta
            {
                Instrument = "Synth",
                // Midnight at 0 E in December: the Sun is far below -18 degrees, so no light is twilight.
                ExposureStartTime = new DateTimeOffset(2026, 12, 21, 0, i, 0, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = frameType,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = sensor,
                ObjectName = light ? "SynthTarget" : "",
                Gain = 100,
                Offset = 25,
                Latitude = Latitude,
                Longitude = Longitude,
            };
            var img = new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            var path = Path.Combine(dir, $"{(light ? "frame" : "dark")}_{i:D2}.fits");
            img.WriteToFitsFile(path);
            paths.Add(path);
        }
        return paths[0];
    }
}
