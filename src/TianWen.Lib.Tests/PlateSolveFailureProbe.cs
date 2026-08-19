using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Runs the real <see cref="CatalogPlateSolver"/> against a frame that failed to solve in the viewer,
/// with logging turned up, and reports WHY.
/// </summary>
/// <remarks>
/// <para>Env-gated on <c>TIANWEN_STAR_PROBE_FITS</c>, so it skips by default and holds no absolute
/// path.</para>
/// <para>It exists because the reason is not otherwise obtainable: <c>PlateSolverFactory</c> logs the
/// ATTEMPT at Information but every per-solver outcome ("returned no solution", "failed") at Debug,
/// and it throws on total failure, which the viewer catches into a transient status string. So a
/// successful solve is on the record and a failed one leaves nothing behind at the default level --
/// backwards, since failure is when the detail is wanted.</para>
/// </remarks>
[Collection("Imaging")]
public class PlateSolveFailureProbe(ITestOutputHelper output)
{
    private const string PathVar = "TIANWEN_STAR_PROBE_FITS";

    /// <summary>Bridges the solver's own <see cref="ILogger"/> into the test output.</summary>
    private sealed class OutputLogger(ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            output.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
            if (exception is not null)
            {
                output.WriteLine($"    {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    [Fact]
    public async Task ReportWhyTheFrameDidNotSolve()
    {
        var path = Environment.GetEnvironmentVariable(PathVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), $"{PathVar} not set");
        Assert.SkipUnless(System.IO.File.Exists(path), $"{PathVar} does not exist: {path}");

        var ct = TestContext.Current.CancellationToken;

        Image.TryReadFitsFile(path!, out var image, out var fileWcs);
        if (image is null)
        {
            output.WriteLine("could not read the frame");
            return;
        }

        var dim = image.GetImageDim();
        output.WriteLine($"file       {System.IO.Path.GetFileName(path)}");
        output.WriteLine($"imageDim   {dim}");
        output.WriteLine($"fileWcs    {(fileWcs is { } w ? $"RA={w.CenterRA:F4}h Dec={w.CenterDec:F3} hasCD={w.HasCDMatrix} scale={w.PixelScaleArcsec}" : "(none)")}");

        var stars = await image.FindStarsAsync(channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);
        output.WriteLine($"stars      {stars.Count}");

        var db = await SharedCatalogDB.InitAsync(ct);
        var solver = new CatalogPlateSolver(db, new OutputLogger(output));

        output.WriteLine($"supported  {await solver.CheckSupportAsync(ct)}");

        // The solver detects on channel 0. On an Ha-bright target that is the RED channel, which is
        // half nebula -- so try each channel and a luminance separately. If the others lock, the
        // solver is fine and the channel choice is the bug.
        foreach (var (label, candidate) in Variants(image))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await solver.SolveImageAsync(
                    candidate, dim, searchOrigin: fileWcs, cancellationToken: ct);
                sw.Stop();

                output.WriteLine(result.Solution is { } s
                    ? $"{label,-10} SOLVED in {sw.ElapsedMilliseconds}ms: RA={s.CenterRA:F4}h Dec={s.CenterDec:F3} scale={s.PixelScaleArcsec:F3}\"/px"
                    : $"{label,-10} NO SOLUTION after {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                output.WriteLine($"{label,-10} THREW after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>The original, each channel on its own, and a Rec.709 luminance.</summary>
    private static (string Label, Image Image)[] Variants(Image source)
    {
        if (source.ChannelCount < 3)
        {
            return [("as-read", source)];
        }

        var (_, width, height) = source.Shape;
        var luma = new float[height, width];
        var r = source.GetChannelArray(0);
        var g = source.GetChannelArray(1);
        var b = source.GetChannelArray(2);
        var lumaMax = 0f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = 0.2126f * r[y, x] + 0.7152f * g[y, x] + 0.0722f * b[y, x];
                luma[y, x] = v;
                if (v > lumaMax)
                {
                    lumaMax = v;
                }
            }
        }

        return
        [
            ("as-read", source),
            ("red", Single(source, 0)),
            ("green", Single(source, 1)),
            ("blue", Single(source, 2)),
            ("luma", new Image([luma], source.BitDepth, lumaMax, 0f, source.Pedestal, source.ImageMeta)),
        ];
    }

    private static Image Single(Image source, int channel)
    {
        var plane = source.GetChannelArray(channel);
        var max = 0f;
        foreach (var v in plane)
        {
            if (v > max)
            {
                max = v;
            }
        }
        return new Image([plane], source.BitDepth, max, 0f, source.Pedestal, source.ImageMeta);
    }
}
