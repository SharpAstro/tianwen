using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R2's eval against synthetic Stars exports whose answers are known: a significant star injected on a plate (and one inside
/// the stitched rim), outputs that leave half the star, dig it, shift the sky's level in one channel or smooth its noise,
/// and the two references (the input, the plate).
/// </summary>
public sealed class StarRemovalEvalTests : IDisposable
{
    private const int Size = 256;
    private const int Channels = 3;
    private const float Sky = 0.2f;
    private const float Sigma = 0.01f;
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

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

    private static void WriteTile(string path, Func<int, float> value, int channels) => WriteTile(path, (_, i) => value(i), channels);

    private static void WriteTile(string path, Func<int, int, float> value, int channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = new byte[channels * Size * Size * 2];
        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < Size * Size; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(((c * Size * Size) + i) * 2, 2), (Half)value(c, i));
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    // The trainer's units for the noise plane, as the exporter writes it.
    private static float PlaneValue(float sigma) => sigma * (float)TianWen.Lib.Imaging.Degradation.StretchedNoise.PlaneScale;

    private static DatasetDegradationExporter.InjectedStarRow InjectedAt(double x, double y)
        => new DatasetDegradationExporter.InjectedStarRow(x, y, [1, 1, 1], [3, 3, 3], [2.8, 2.8, 2.8], 1, 0, false, null);

    // One draw of session `session` at cell (cellX, 0): its plate, its input, its noise plane and its injection row.
    private static string WriteDraw(string export, string session, int cellX, Func<int, int, float> plate, Func<int, int, float> input,
        IReadOnlyList<DatasetDegradationExporter.InjectedStarRow> stars, List<string> manifest)
    {
        var tile = $"tiles/{session}/x{cellX}_y0_deg000.f16";
        WriteTile(Path.Combine(export, "tiles", session, $"x{cellX}_y0_master.f16"), plate, Channels);
        WriteTile(Path.Combine(export, "tiles", session, $"x{cellX}_y0_deg000.f16"), input, Channels);
        WriteTile(Path.Combine(export, "tiles", session, $"x{cellX}_y0_deg000.sigma.f16"), _ => PlaneValue(Sigma), 1);
        var row = new DatasetDegradationExporter.InjectionRow(tile, session, cellX, 0, 0, "Random", "Field", 2, 2, false, [.. stars]);
        manifest.Add(JsonSerializer.Serialize(row, DatasetDegradationJsonContext.Default.InjectionRow));
        return tile;
    }

    private static async Task<StarRemovalEval.Report> ScoreAsync(string export, string outputs, List<string> manifest,
        IReadOnlyList<(string Tile, string Output)> rows, string? reportName = null)
    {
        Directory.CreateDirectory(outputs);
        await File.WriteAllLinesAsync(Path.Combine(export, DatasetDegradationExporter.InjectionManifestFileName), manifest,
            TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(outputs, StarRemovalEval.OutputsFileName),
            rows.Select(r => JsonSerializer.Serialize(new StarRemovalEval.OutputRow(r.Tile, r.Output), StarRemovalEvalJsonContext.Default.OutputRow)),
            TestContext.Current.CancellationToken);
        return await StarRemovalEval.RunAsync(export, outputs, reportName, TestContext.Current.CancellationToken);
    }

    private static StarRemovalEval.ArmScores Arm(StarRemovalEval.Report report, string name) => report.Arms.Single(a => a.Name == name);

    private static StarRemovalEval.BandRow Band(StarRemovalEval.ArmScores arm) => arm.Completeness.Single(b => b.Stars > 0);

    [Fact]
    public async Task TheEvalReadsCompletenessFootprintAndSkyBetweenItsTwoReferences()
    {
        var root = _folders.Create("starless-eval-").FullName;
        var export = Path.Combine(root, "export");
        var outputs = Path.Combine(root, "outputs");
        var star = Star(128, 128, 1.0, 3.0);
        var rimStar = Star(8, 8, 1.0, 3.0);
        var manifest = new List<string>();
        var tile = WriteDraw(export, "S", 0, (_, _) => Sky, (_, i) => Sky + star[i] + rimStar[i], [InjectedAt(128, 128), InjectedAt(8, 8)], manifest);
        // An output that removes the rim star and half of the other: a leftover far over a sigma.
        WriteTile(Path.Combine(outputs, "out0.f16"), i => Sky + 0.5f * star[i], Channels);

        var report = await ScoreAsync(export, outputs, manifest, [(tile, "out0.f16")]);

        report.Draws.ShouldBe(1);
        report.Stars.ShouldBe(1, "the star inside the stitched rim is not read");
        File.Exists(Path.Combine(outputs, StarRemovalEval.ReportFileName)).ShouldBeTrue();

        var output = Arm(report, "output");
        var input = Arm(report, "input (removes nothing)");
        var plate = Arm(report, "plate (removes all)");
        // The star's 3x3 core holds about 0.6 of its unit peak, 60 sigma: the 20-100 band.
        Band(plate).Band.ShouldBe("20-100 sigma");
        (Band(plate).Removed, Band(input).Removed, Band(output).Removed).ShouldBe((1, 0, 0));
        (Band(plate).Clean, Band(input).Clean, Band(output).Clean).ShouldBe((1, 0, 0));
        (Band(plate).Dug, Band(input).Dug, Band(output).Dug).ShouldBe((0, 0, 0));
        Band(output).CoreMean.ShouldBeGreaterThan(20, "half of a 60 sigma core is left");

        plate.FootprintRms.ShouldBe(0, 1e-6);
        plate.SkyRms.ShouldBe(0, 1e-6);
        input.SkyRms.ShouldBe(0, 1e-6, "the input is the plate off every footprint");
        output.SkyRms.ShouldBe(0, 1e-3);
        output.FootprintRms.ShouldBeGreaterThan(1.0);
        output.FootprintRms.ShouldBeLessThan(input.FootprintRms);
        report.Sessions.Single().SessionId.ShouldBe("S");
    }

    [Fact]
    public async Task ADugCoreIsRemovedOneSidedButNeverCleanAndTheBlendsTurnTheOutputDown()
    {
        var root = _folders.Create("starless-eval-").FullName;
        var export = Path.Combine(root, "export");
        var outputs = Path.Combine(root, "outputs");
        var star = Star(128, 128, 1.0, 3.0);
        var manifest = new List<string>();
        var tile = WriteDraw(export, "S", 0, (_, _) => Sky, (_, i) => Sky + star[i], [InjectedAt(128, 128)], manifest);
        // Over-subtracted by half the star: a hole of about 30 sigma at the core.
        WriteTile(Path.Combine(outputs, "out0.f16"), i => Sky - 0.5f * star[i], Channels);

        var report = await ScoreAsync(export, outputs, manifest, [(tile, "out0.f16")], "re-score.json");

        File.Exists(Path.Combine(outputs, "re-score.json")).ShouldBeTrue();
        File.Exists(Path.Combine(outputs, StarRemovalEval.ReportFileName)).ShouldBeFalse("a named re-score leaves the registered report alone");
        var output = Band(Arm(report, "output"));
        (output.Removed, output.Clean, output.Dug).ShouldBe((1, 0, 1), "the one-sided test passes a hole; the two-sided one does not");
        output.CoreMean.ShouldBeLessThan(-20);
        // input + w (output - input) = sky + (1 - 1.5 w) star: -0.125 at 0.75, +0.25 at 0.5, +0.625 at 0.25.
        var at075 = Band(Arm(report, "output at 0.75"));
        var at05 = Band(Arm(report, "output at 0.5"));
        var at025 = Band(Arm(report, "output at 0.25"));
        (at075.Removed, at075.Clean, at075.Dug).ShouldBe((1, 0, 1));
        (at05.Removed, at05.Clean, at05.Dug).ShouldBe((0, 0, 0));
        (at025.Removed, at025.Clean, at025.Dug).ShouldBe((0, 0, 0));
        at05.CoreMean.ShouldBeLessThan(at025.CoreMean);
        var plate = Band(Arm(report, "plate (removes all)"));
        (plate.Clean, plate.Dug).ShouldBe((1, 0));
    }

    [Fact]
    public async Task TheSkysChangeIsTakenApartPerSessionIntoLevelColourAndNoise()
    {
        var root = _folders.Create("starless-eval-").FullName;
        var export = Path.Combine(root, "export");
        var outputs = Path.Combine(root, "outputs");
        // Each channel's noise is the luminance's times root 3, so the plane (the luminance's noise) is Sigma.
        var channelSigma = Sigma * MathF.Sqrt(Channels);
        float[][] Noise(int seed)
        {
            var rng = new Random(seed);
            return [.. Enumerable.Range(0, Channels).Select(_ =>
                Enumerable.Range(0, Size * Size).Select(_ =>
                    Sky + (channelSigma * (float)(Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())))).ToArray())];
        }
        var plateA = Noise(1);
        var plateB = Noise(2);
        var star = Star(128, 128, 1.0, 3.0);
        var manifest = new List<string>();
        var tileA = WriteDraw(export, "A", 0, (c, i) => plateA[c][i], (c, i) => plateA[c][i] + star[i], [InjectedAt(128, 128)], manifest);
        var tileB = WriteDraw(export, "B", 1, (c, i) => plateB[c][i], (c, i) => plateB[c][i], [], manifest);
        // Session A: the star removed and red lifted by 0.9 of the luminance's sigma, a luminance shift of 0.3.
        WriteTile(Path.Combine(outputs, "outA.f16"), (c, i) => plateA[c][i] + (c == 0 ? 0.9f * Sigma : 0f), Channels);
        // Session B: the sky's noise smoothed by a 3x3 box in every channel, its level kept.
        WriteTile(Path.Combine(outputs, "outB.f16"), (c, i) =>
        {
            int x = i % Size, y = i / Size;
            if (x == 0 || y == 0 || x == Size - 1 || y == Size - 1)
            {
                return plateB[c][i];
            }
            var sum = 0f;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    sum += plateB[c][((y + dy) * Size) + x + dx];
                }
            }
            return sum / 9f;
        }, Channels);

        var report = await ScoreAsync(export, outputs, manifest, [(tileA, "outA.f16"), (tileB, "outB.f16")]);

        report.Sessions.Select(static s => (s.SessionId, s.Draws)).ShouldBe([("A", 1), ("B", 1)]);
        var a = report.Sessions[0].Arms.Single(static x => x.Name == "output").Sky;
        a.ChannelMean[0].ShouldBe(0.9, 0.05);
        a.ChannelMean[1].ShouldBe(0, 0.05);
        a.ChannelMean[2].ShouldBe(0, 0.05);
        a.Mean.ShouldBe(0.3, 0.03);
        a.LevelRms.ShouldBe(0.3, 0.03, "one draw: its level shift is the whole level part");
        a.AboutLevelRms.ShouldBeLessThan(0.1, "a level shift changes no texture");
        a.NoiseRatio.ShouldBe(1, 0.05, "a level shift leaves the noise as it was");

        var b = report.Sessions[1].Arms.Single(static x => x.Name == "output").Sky;
        b.Mean.ShouldBe(0, 0.05);
        b.AboutLevelRms.ShouldBeInRange(0.8, 1.05, "a 3x3 box takes sqrt(8/9) of the noise away from the plate");
        b.NoiseRatio.ShouldBeLessThan(0.4, "the box smooths the sky's pixel-to-pixel noise");

        var pooled = Arm(report, "output");
        (pooled.Sky.LevelRms * pooled.Sky.LevelRms + pooled.Sky.AboutLevelRms * pooled.Sky.AboutLevelRms)
            .ShouldBe(pooled.SkyRms * pooled.SkyRms, 1e-9, "the level and the texture make the whole sky change");
        pooled.Footprint.WorstShare.ShouldBe(1, 1e-9, "only session A's draw has a footprint");
        Arm(report, "input (removes nothing)").Sky.NoiseRatio.ShouldBe(1, 1e-9);
    }
}
