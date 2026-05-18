using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Deterministic CI-runnable end-to-end test for <see cref="StackingPipeline"/>.
/// Generates a small synthetic dataset (8 mono frames, 256x256, ~25 stars
/// with Gaussian PSF, sub-pixel deterministic dither, per-frame random
/// noise, 2 frames carry an injected hot-pixel outlier) and runs the
/// full scan -> register -> integrate -> write pipeline against it.
///
/// <para>What this gives us that the manual SoL test doesn't:</para>
/// <list type="bullet">
///   <item>Runs in CI -- no external dataset dependency, completes in
///     a few seconds.</item>
///   <item>Deterministic geometry (star positions + dither offsets), so
///     assertions on the master canvas size and matched-frame count are
///     unambiguous regardless of host.</item>
///   <item>Random noise seeded per frame (<c>noiseSeed = 42 + frameIdx</c>),
///     so noise looks like real shot noise (uncorrelated between frames,
///     which is what the rejector needs) but stays reproducible across
///     test runs.</item>
///   <item>Hot pixels injected into 2 of 8 frames at a known location --
///     gives the rejector something concrete to throw out.</item>
/// </list>
/// </summary>
[Collection("Imaging")]
public class StackingPipelineSyntheticTest(ITestOutputHelper output)
{
    // Frame size has to give FindStarsAsync enough actual detections to
    // clear StackingPipeline.MinStarsForMatch (24). 512x512 with 60
    // synthetic stars consistently lands ~35-45 detections per frame
    // even with the per-frame noise variation, which clears the floor
    // with margin to spare. Total fixture footprint: 8 * 512x512x2B
    // = 4 MB on disk, ~30 MB peak RAM during integration.
    private const int FrameSize = 512;
    private const int FrameCount = 8;
    private const int StarCount = 60;

    /// <summary>
    /// Sub-pixel dither offsets per frame in (x, y). Frame 0 sits at the
    /// origin and ends up the registration reference (highest star count
    /// against itself). The other 7 sit at fractional offsets up to ~2.4
    /// pixels in either direction so the union bounding box grows
    /// modestly and the registrator has real shifts to detect.
    /// </summary>
    private static readonly (double DX, double DY)[] DitherOffsets =
    [
        ( 0.0,  0.0),
        ( 1.3,  0.7),
        (-0.8,  1.5),
        ( 2.1, -1.1),
        (-1.7, -0.4),
        ( 0.6,  2.2),
        (-2.4,  0.3),
        ( 1.9, -1.8),
    ];

    /// <summary>
    /// Frame indices that get a hot-pixel injected. Sigma-clip rejection
    /// should kill them on the strength of the other 6 inliers at the
    /// same canvas position.
    /// </summary>
    private static readonly int[] HotPixelFrames = [2, 5];

    [Fact]
    public async Task Stack_8Frames_DeterministicDither_RandomNoise_ProducesUsableMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();

        WriteSyntheticFrames(workspace.LightsDir);

        // No catalog DB -> plate-solve skipped, FITS written without WCS.
        // That's fine for this test: we assert on pixel-level + count
        // properties of the master, not on its sky coordinates.
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

        // 1) Exactly one light group survived (all frames share OBJECT +
        //    calibration signature).
        results.Count.ShouldBe(1, "expected a single integrated light group");
        var result = results[0];
        result.SkipReason.ShouldBeEmpty($"group should not have skipped: '{result.SkipReason}'");
        result.FramesAttempted.ShouldBe(FrameCount);
        result.FramesMatched.ShouldBe(FrameCount, "all 8 frames should register against frame 0");

        // 2) Master FITS landed at the expected path with a non-trivial
        //    integration result.
        result.MasterFitsPath.ShouldNotBeNull();
        File.Exists(result.MasterFitsPath).ShouldBeTrue($"master FITS missing at {result.MasterFitsPath}");
        result.Result.ShouldNotBeNull();
        result.Result.FrameCount.ShouldBe(FrameCount);

        // 3) Round-trip the master through the FITS reader and sanity-check
        //    its geometry. Union BB must be >= source frame size (dither
        //    can only grow the canvas).
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.Width.ShouldBeGreaterThanOrEqualTo(FrameSize);
        master.Height.ShouldBeGreaterThanOrEqualTo(FrameSize);

        // 4) Rejection happened: the synthetic hot-pixel frames carry
        //    one outlier each, so the rejector should have killed some
        //    pixels. We can't pin the exact count (StreamingIntegrator's
        //    asymmetric kappa for the synthetic noise level is a soft
        //    threshold) but the rate should be non-zero, and should
        //    stay well below "rejected most of the data".
        result.Result.TotalRejections.ShouldBeGreaterThan(0,
            "two synthetic frames inject hot pixels; rejector should fire on them");
        result.Result.MeanRejectionRate.ShouldBeLessThan(0.05,
            $"rejection rate {result.Result.MeanRejectionRate:P2} is implausibly high " +
            "for clean synthetic frames with two outliers");
    }

    /// <summary>
    /// Lays down 8 synthetic mono FITS files in <paramref name="lightsDir"/>.
    /// Star field is fixed (seed=42); per-frame dither + noise vary per the
    /// arrays at the top of the class.
    /// </summary>
    private static void WriteSyntheticFrames(string lightsDir)
    {
        for (var i = 0; i < FrameCount; i++)
        {
            var (dx, dy) = DitherOffsets[i];
            var hotPixelCount = HotPixelFrames.Contains(i) ? 1 : 0;
            var pixels = SyntheticStarFieldRenderer.Render(
                width: FrameSize,
                height: FrameSize,
                defocusSteps: 0,
                offsetX: dx,
                offsetY: dy,
                exposureSeconds: 1.0,
                skyBackground: 100.0,
                readNoise: 5.0,
                starCount: StarCount,
                seed: 42,                // star positions: same across all 8 frames
                hotPixelCount: hotPixelCount,
                maxADU: 4096.0,
                noiseSeed: 42 + i);      // shot + read noise: varies per frame

            // ImageMeta needs the fields that LightGroupKey.FromFrame
            // reads from the FITS header -- otherwise the 8 frames could
            // end up in 8 different light groups. ObjectName, sensor
            // shape, exposure, gain, offset, temperature all match.
            var meta = new ImageMeta
            {
                Instrument = "Synth",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, i, TimeSpan.Zero),
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
            };
            var img = new Image(
                data: [pixels],
                bitDepth: BitDepth.Int16,
                maxValue: 4096f,
                minValue: 0f,
                pedestal: 0f,
                imageMeta: meta);
            var path = Path.Combine(lightsDir, $"frame_{i:D2}.fits");
            img.WriteToFitsFile(path);
        }
    }
}

/// <summary>
/// Scratch directory tree mirroring the layout the pipeline expects:
/// a root with the lights folder inside, plus a separate output dir. The
/// pipeline recursively scans the root, so the output dir MUST sit outside
/// (or be filtered out explicitly via the pipeline's own path-prefix skip,
/// which we still get for free since the output dir lives under the root).
///
/// <para>Pattern: <c>&lt;tmp&gt;/StackingPipelineSyntheticTest_&lt;guid&gt;/{root,output}/</c>.
/// Cleaned up on Dispose; if the test crashes the dir leaks under
/// <c>%TEMP%</c> but is uniquely named so subsequent runs don't collide.</para>
/// </summary>
internal sealed class TempStackingWorkspace : IDisposable
{
    public string RootDir { get; }
    public string LightsDir { get; }
    public string OutputDir { get; }
    private readonly string _baseDir;

    public TempStackingWorkspace()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"StackingPipelineSyntheticTest_{Guid.NewGuid():N}");
        RootDir = Path.Combine(_baseDir, "data");
        LightsDir = Path.Combine(RootDir, "LIGHT");
        OutputDir = Path.Combine(RootDir, "output");
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LightsDir);
        Directory.CreateDirectory(OutputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); }
        catch { /* best-effort; if file handles linger, leak to %TEMP% */ }
    }
}

/// <summary>
/// Forwards <see cref="ILogger"/> calls to xUnit's <see cref="ITestOutputHelper"/>
/// so the pipeline's per-stage <c>LogInformation</c> chatter lands in the
/// test output. <see cref="NullLogger.Instance"/> would also work, but
/// makes debugging a failing assertion harder.
/// </summary>
internal sealed class XunitLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        if (string.IsNullOrEmpty(msg) && exception is null) return;
        try
        {
            output.WriteLine($"[{logLevel,5}] {msg}{(exception is null ? "" : $" -- {exception.Message}")}");
        }
        catch (InvalidOperationException)
        {
            // xUnit closes the output helper after the test finishes; the
            // pipeline's background-style logging may try to write after
            // that and we don't want to throw out of ILogger.Log.
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
