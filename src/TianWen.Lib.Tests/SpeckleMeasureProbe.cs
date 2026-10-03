using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
/// <para>With <c>TIANWEN_SPECKLE_MASTERS</c> (the masters the plates were built from, <c>&lt;stem&gt;.fits</c>) each site and
/// null position is also classed by its MASTER's background texture (<see cref="TextureField"/>), rebuilt here with the
/// finder call the builder makes, at the luminance FWHM its plate statistics recorded (the store's
/// <c>stats/starless-plates.jsonl</c> beside <c>plates</c>), so existing plates are split without a rebuild. Each master's
/// rates go to <c>TIANWEN_SPECKLE_OUT</c> (a JSONL file) when set, and a summary over every master ends the output.</para>
/// </summary>
[Collection("Imaging")]
public sealed class SpeckleMeasureProbe(ITestOutputHelper output)
{
    // The last band's and class's upper edge is infinite, as in the plate store's own context.
    private static readonly JsonSerializerOptions JsonOut = new JsonSerializerOptions
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    [Fact]
    public async System.Threading.Tasks.Task ReadEveryPlatesSpecklesWithTheMeasureAsItIs()
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_PLATES");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "TIANWEN_SPECKLE_PLATES not set");
        var filter = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_FILTER") ?? "";
        var mastersDir = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_MASTERS");
        var outPath = Environment.GetEnvironmentVariable("TIANWEN_SPECKLE_OUT");
        var fwhmByStem = mastersDir is null ? new Dictionary<string, double>() : await ReadLuminanceFwhmAsync(dir!);
        var reports = new List<(string Stem, SpeckleReport Report)>();
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
            var (_, width, height) = plate.Shape;
            var lum = Luminance(plate);
            TextureField? texture = null;
            if (mastersDir is not null && fwhmByStem.TryGetValue(stem, out var fwhm)
                && Image.TryReadFitsFile(Path.Combine(mastersDir, stem + ".fits"), out var master))
            {
                var masterLum = Luminance(master);
                var absent = master.AbsentPixels();
                var (_, sky) = PointSourceFinder.Find(masterLum, width, height, absent, fwhm, new StarlessPlateOptions().DetectionSigma);
                texture = TextureField.FromSkyMap(sky, masterLum, width, height, absent, fwhm);
                master.Release();
            }
            var report = StarlessSpeckles.Measure(lum, width, height, plate.AbsentPixels(),
                [.. stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted).Select(static s => (s.X, s.Y, s.Significance))],
                [.. stars.Select(static s => (s.X, s.Y))], texture: texture);
            plate.Release();
            reports.Add((stem, report));
            output.WriteLine($"{stem.Split('_')[2],-24} {Cell(report)}");
            if (outPath is not null)
            {
                await File.AppendAllTextAsync(outPath, JsonSerializer.Serialize(new { Stem = stem, Report = report }, JsonOut) + "\n", TestContext.Current.CancellationToken);
            }
        }
        output.WriteLine("");
        output.WriteLine(Summary(reports));
    }

    private static float[] Luminance(Image image)
    {
        var (channels, width, height) = image.Shape;
        var lum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += plane[i] / channels;
            }
        }
        return lum;
    }

    // The luminance FWHM each plate was built at, from the store's statistics (the last record per master wins).
    private static async System.Threading.Tasks.Task<Dictionary<string, double>> ReadLuminanceFwhmAsync(string platesDir)
    {
        var store = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(platesDir).TrimEnd(Path.DirectorySeparatorChar)) ?? platesDir,
            "stats", "starless-plates.jsonl");
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(store))
        {
            using var doc = JsonDocument.Parse(line);
            var master = doc.RootElement.GetProperty("Master").GetString() ?? "";
            var fwhm = doc.RootElement.GetProperty("Statistics").GetProperty("FwhmPx");
            result[Path.GetFileNameWithoutExtension(master)] = fwhm[fwhm.GetArrayLength() - 1].GetDouble();
        }
        return result;
    }

    private static string P(SpeckleBand b) => b.Sites > 0 ? $"{100f * b.Rate:F1}" : "-";

    private static string Cell(SpeckleReport r)
        => string.Join(" / ", r.Bands.Select(P)) + $" (null {P(r.Null)})"
           + (r.Backgrounds.IsDefaultOrEmpty ? "" : "  by texture " + string.Join(" ; ", r.Backgrounds.Select(c => string.Join("/", c.Bands.Select(P)) + $" ({P(c.Null)})")));

    // Per band and background class: how many masters read at least 30 sites there, their median rate and median own
    // null, and how many sit over twice their class's null (the plan's gate), beside the same against the frame's null.
    private static string Summary(List<(string Stem, SpeckleReport Report)> reports)
    {
        var sb = new StringBuilder();
        var withClasses = reports.Where(static r => !r.Report.Backgrounds.IsDefaultOrEmpty).ToList();
        for (var b = 0; b < StarlessSpeckles.BandEdges.Length; b++)
        {
            var band = $"{StarlessSpeckles.BandEdges[b]}{(b + 1 < StarlessSpeckles.BandEdges.Length ? "-" + StarlessSpeckles.BandEdges[b + 1] : "+")}";
            var all = reports.Where(r => r.Report.Bands[b].Sites >= 30).ToList();
            sb.AppendLine($"band {band}: {all.Count} masters, over twice the FRAME null {all.Count(r => r.Report.Bands[b].Rate > Math.Max(2 * r.Report.Null.Rate, 0.01f))}");
            for (var c = 0; c < StarlessSpeckles.TextureEdges.Length; c++)
            {
                var rows = withClasses.Where(r => r.Report.Backgrounds[c].Bands[b].Sites >= 30 && r.Report.Backgrounds[c].Null.Sites >= 200).ToList();
                if (rows.Count == 0)
                {
                    continue;
                }
                static float Median(IEnumerable<float> v) { var a = v.Order().ToArray(); return a[a.Length / 2]; }
                var rate = Median(rows.Select(r => r.Report.Backgrounds[c].Bands[b].Rate));
                var nul = Median(rows.Select(r => r.Report.Backgrounds[c].Null.Rate));
                var over = rows.Count(r => r.Report.Backgrounds[c].Bands[b].Rate > Math.Max(2 * r.Report.Backgrounds[c].Null.Rate, 0.01f));
                var overFrame = rows.Count(r => r.Report.Backgrounds[c].Bands[b].Rate > Math.Max(2 * r.Report.Null.Rate, 0.01f));
                sb.AppendLine($"  texture {StarlessSpeckles.TextureEdges[c]}+: {rows.Count} masters, median rate {100 * rate:F1}% against its own null {100 * nul:F1}%; over twice its own null {over} (twice the frame null {overFrame})");
            }
        }
        return sb.ToString();
    }
}
