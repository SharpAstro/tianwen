using System;
using System.IO;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: the speckle rate (<see cref="StarlessSpeckles"/>) of every starless plate in a folder, read with the measure
/// as it is now, so plates built by different builders, or measured before the measure changed, are compared on one
/// footing. Set <c>TIANWEN_SPECKLE_PLATES</c> to a <c>starless-plates</c> output's <c>plates</c> folder (each
/// <c>&lt;stem&gt;_plate.fits</c> beside its <c>&lt;stem&gt;_plate.stars.csv</c>); optionally <c>TIANWEN_SPECKLE_FILTER</c>
/// to a substring of the stems to read.
/// </summary>
[Collection("Imaging")]
public sealed class SpeckleMeasureProbe(ITestOutputHelper output)
{
    [Fact]
    public async System.Threading.Tasks.Task ReadEveryPlatesSpecklesWithTheMeasureAsItIs()
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_PLATES");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "TIANWEN_SPECKLE_PLATES not set");
        var filter = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_FILTER") ?? "";
        foreach (var platePath in Directory.EnumerateFiles(dir!, "*_plate.fits").Order(StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileName(platePath)[..^"_plate.fits".Length];
            if (!stem.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var cataloguePath = StarlessCatalogue.PathFor(dir!, stem);
            if (!File.Exists(cataloguePath) || !Image.TryReadFitsFile(platePath, out var plate))
            {
                continue;
            }
            var stars = await StarlessCatalogue.ReadAsync(cataloguePath, TestContext.Current.CancellationToken);
            var (channels, width, height) = plate.Shape;
            var lum = new float[width * height];
            for (var c = 0; c < channels; c++)
            {
                var plane = plate.GetChannelSpan(c);
                for (var i = 0; i < lum.Length; i++)
                {
                    lum[i] += plane[i] / channels;
                }
            }
            var report = StarlessSpeckles.Measure(lum, width, height, plate.AbsentPixels(),
                [.. stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted).Select(static s => (s.X, s.Y, s.Significance))],
                [.. stars.Select(static s => (s.X, s.Y))]);
            static string P(SpeckleBand b) => b.Sites > 0 ? $"{100f * b.Rate:F1}" : "-";
            output.WriteLine($"{stem.Split('_')[2],-24} speckles {string.Join(" / ", report.Bands.Select(P))} (null {P(report.Null)})");
            plate.Release();
        }
    }
}
