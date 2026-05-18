using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises the Phase 8.2 two-pass strip-pipelined integrator on small
/// synthetic mono frames. Mono inputs short-circuit the debayer to a no-op so
/// the only thing under test is the strip-by-strip warp + per-strip integrate
/// + master-merge logic. Identity transforms make the warp a pure copy, so
/// the master is exactly the per-pixel combine of the inputs.
/// </summary>
[Collection("Imaging")]
public class TilePipelinedStrategyTests
{
    [Fact]
    public async Task ConstantFrames_MeanCombine_NoNormalization_MasterEqualsMean()
    {
        // 4 mono frames, each filled with a distinct constant. Identity warp +
        // no-op calibrator + no-op debayer (since SensorType == Monochrome).
        // Mean combiner -> master pixel = (0.1 + 0.2 + 0.3 + 0.4) / 4 = 0.25.
        var ct = TestContext.Current.CancellationToken;
        var tempDir = NewTempDir();
        try
        {
            var values = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
            const int width = 64;
            // Height crosses the 256-row strip boundary so the last-strip
            // clipping path is exercised. 300 = 256 + 44 -> 2 strips.
            const int height = 300;
            var sources = WriteMonoFitsFrames(tempDir, values, width, height);

            var strategy = new TilePipelinedStrategy();
            var job = BuildJob(sources, width, height, new IntegrationOptions(ApplyNormalization: false));

            var result = await strategy.RunAsync(job, ct);

            result.FrameCount.ShouldBe(values.Length);
            result.Master.Width.ShouldBe(width);
            result.Master.Height.ShouldBe(height);
            result.Master.ChannelCount.ShouldBe(1);

            var expectedMean = 0.25f;
            var masterCh = result.Master.GetChannelArray(0);
            // Sample a handful of points across both strips to confirm the
            // strip-row offset is correct -- a bug there would put zeros in
            // one strip and the expected mean in the other.
            (int y, int x)[] probes =
            {
                (0, 0), (0, width - 1),
                (128, width / 2),         // mid-strip 0
                (255, 0),                  // last row of strip 0
                (256, 0),                  // first row of strip 1
                (height - 1, width - 1),   // last pixel
            };
            foreach (var (y, x) in probes)
            {
                masterCh[y, x].ShouldBe(expectedMean, tolerance: 1e-6f,
                    $"master[{y}, {x}] != expected mean {expectedMean}");
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task SingleStrip_MatchesInRamAllFramesIntegrator()
    {
        // Same N frames + same canvas, but height kept under one strip
        // (256) so there's only one strip pass. Pre-normalize disabled.
        // Run both InRamAllFrames + TilePipelined and assert master pixels
        // match within tight float tolerance.
        var ct = TestContext.Current.CancellationToken;
        var tempDir = NewTempDir();
        try
        {
            var values = new[] { 0.10f, 0.50f, 0.90f };
            const int width = 48;
            const int height = 32;
            var sources = WriteMonoFitsFrames(tempDir, values, width, height);

            // Build identical warped frames in-memory for InRam (= raw values
            // since debayer is a no-op + warp is identity).
            var inRamFrames = new List<Image>(values.Length);
            foreach (var (path, _) in sources)
            {
                Image.TryReadFitsFile(path, out var img).ShouldBeTrue();
                inRamFrames.Add(img!);
            }
            var inRamResult = Integrator.Integrate(inRamFrames,
                new IntegrationOptions(ApplyNormalization: false));

            var strategy = new TilePipelinedStrategy();
            var job = BuildJob(sources, width, height, new IntegrationOptions(ApplyNormalization: false));
            var tileResult = await strategy.RunAsync(job, ct);

            tileResult.Master.Width.ShouldBe(inRamResult.Master.Width);
            tileResult.Master.Height.ShouldBe(inRamResult.Master.Height);

            var tileCh = tileResult.Master.GetChannelArray(0);
            var inRamCh = inRamResult.Master.GetChannelArray(0);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    tileCh[y, x].ShouldBe(inRamCh[y, x], tolerance: 1e-5f,
                        $"divergence at ({x}, {y}): tile={tileCh[y, x]}, inRam={inRamCh[y, x]}");
                }
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static IntegrationJob BuildJob(
        List<(string Path, Matrix3x2 Transform)> sources,
        int canvasW, int canvasH,
        IntegrationOptions opts)
    {
        var rawSources = new List<RawLightSource>(sources.Count);
        foreach (var (path, transform) in sources)
        {
            rawSources.Add(new RawLightSource(path, transform));
        }
        // TilePipelinedStrategy ignores WarpedFrames -- it loads from
        // RawLightSources directly. Supply an empty producer.
        return new IntegrationJob(
            WarpedFrames: _ => EmptyAsyncEnumerable<Image>(),
            ExpectedFrameCount: rawSources.Count,
            Options: opts,
            StagingDir: Path.GetTempPath(),
            StatsRect: Rectangle.Empty,
            RawLightSources: rawSources,
            Calibrator: new Calibrator(), // all-null masters: pass-through
            DebayerAlgorithm: DebayerAlgorithm.BilinearMono,
            CanvasWidth: canvasW,
            CanvasHeight: canvasH);
    }

    private static async System.Collections.Generic.IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static List<(string Path, Matrix3x2 Transform)> WriteMonoFitsFrames(
        string dir, float[] values, int width, int height)
    {
        // Mono Float32 FITS so TryReadFitsFile yields SensorType.Monochrome
        // and DebayerAsync short-circuits to a no-op.
        var sources = new List<(string, Matrix3x2)>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var arr = new float[height, width];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    arr[y, x] = values[i];
            var image = Image.FromChannel(arr, maxValue: 1f, minValue: 0f);
            var path = Path.Combine(dir, $"frame{i}.fits");
            image.WriteToFitsFile(path);
            sources.Add((path, Matrix3x2.Identity));
        }
        return sources;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"TianWen.Lib.Tests_TilePipelined_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
