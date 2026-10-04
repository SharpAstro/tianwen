using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R2's eval against a synthetic Stars export whose answers are known: one significant star injected on a flat plate and one
/// inside the stitched rim, an output that leaves half the star, and the two references (the input, the plate).
/// </summary>
public sealed class StarRemovalEvalTests : IDisposable
{
    private const int Size = 256;
    private const int Channels = 3;
    private const float Sky = 0.2f;
    private const float Sigma = 0.01f;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "starless-eval-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static float[] Star(double cx, double cy, double amplitude, double fwhm)
    {
        var plane = new float[Size * Size];
        var s = fwhm / 2.3548;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[(y * Size) + x] = (float)(amplitude * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / (2 * s * s)));
            }
        }
        return plane;
    }

    private static void WriteTile(string path, Func<int, float> value, int channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = new byte[channels * Size * Size * 2];
        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < Size * Size; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(((c * Size * Size) + i) * 2, 2), (Half)value(i));
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task TheEvalReadsCompletenessFootprintAndSkyBetweenItsTwoReferences()
    {
        var export = Path.Combine(_root, "export");
        var outputs = Path.Combine(_root, "outputs");
        Directory.CreateDirectory(outputs);
        const string dir = "tiles/S";
        var star = Star(128, 128, 1.0, 3.0);
        var rimStar = Star(8, 8, 1.0, 3.0);

        WriteTile(Path.Combine(export, "tiles", "S", "x0_y0_master.f16"), _ => Sky, Channels);
        WriteTile(Path.Combine(export, "tiles", "S", "x0_y0_deg000.f16"), i => Sky + star[i] + rimStar[i], Channels);
        // The plane in the trainer's units, as the exporter writes it.
        WriteTile(Path.Combine(export, "tiles", "S", "x0_y0_deg000.sigma.f16"), _ => Sigma * (float)TianWen.Lib.Imaging.Degradation.StretchedNoise.PlaneScale, 1);
        // An output that removes the rim star and half of the other: a leftover far over a sigma.
        WriteTile(Path.Combine(outputs, "out0.f16"), i => Sky + 0.5f * star[i], Channels);

        var injection = new DatasetDegradationExporter.InjectionRow(
            $"{dir}/x0_y0_deg000.f16", "S", 0, 0, 0, "Random", "Field", 2, 2, false,
            [
                new DatasetDegradationExporter.InjectedStarRow(128, 128, [1, 1, 1], [3, 3, 3], [2.8, 2.8, 2.8], 1, 0, false, null),
                new DatasetDegradationExporter.InjectedStarRow(8, 8, [1, 1, 1], [3, 3, 3], [2.8, 2.8, 2.8], 1, 0, false, null),
            ]);
        await File.WriteAllTextAsync(Path.Combine(export, DatasetDegradationExporter.InjectionManifestFileName),
            JsonSerializer.Serialize(injection, DatasetDegradationJsonContext.Default.InjectionRow) + "\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputs, StarRemovalEval.OutputsFileName),
            JsonSerializer.Serialize(new StarRemovalEval.OutputRow($"{dir}/x0_y0_deg000.f16", "out0.f16"), StarRemovalEvalJsonContext.Default.OutputRow) + "\n",
            TestContext.Current.CancellationToken);

        var report = await StarRemovalEval.RunAsync(export, outputs, TestContext.Current.CancellationToken);

        report.Draws.ShouldBe(1);
        report.Stars.ShouldBe(1, "the star inside the stitched rim is not read");
        File.Exists(Path.Combine(outputs, StarRemovalEval.ReportFileName)).ShouldBeTrue();

        var output = report.Arms.Single(a => a.Name == "output");
        var input = report.Arms.Single(a => a.Name.StartsWith("input", StringComparison.Ordinal));
        var plate = report.Arms.Single(a => a.Name.StartsWith("plate", StringComparison.Ordinal));
        // The star's 3x3 core holds about 0.6 of its unit peak, 60 sigma: the 20-100 band.
        static StarRemovalEval.BandRow Band(StarRemovalEval.ArmScores arm) => arm.Completeness.Single(b => b.Stars > 0);
        Band(plate).Band.ShouldBe("20-100 sigma");
        (Band(plate).Removed, Band(input).Removed, Band(output).Removed).ShouldBe((1, 0, 0));

        plate.FootprintRms.ShouldBe(0, 1e-6);
        plate.SkyRms.ShouldBe(0, 1e-6);
        input.SkyRms.ShouldBe(0, 1e-6, "the input is the plate off every footprint");
        output.SkyRms.ShouldBe(0, 1e-3);
        output.FootprintRms.ShouldBeGreaterThan(1.0);
        output.FootprintRms.ShouldBeLessThan(input.FootprintRms);
    }
}
