using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.AI.Imaging;

/// <summary>
/// R2's eval (docs/plans/star-remover-training.md, section 5): a star remover's outputs on a Stars-mode export's draws, read
/// against the stars the export injected (<see cref="DatasetDegradationExporter.InjectionManifestFileName"/>) and the plate
/// they were injected on. A draw is its plate, to the bit, everywhere its stars rendered no light (the Stars mode adds noise
/// only where a star does, and stops a star's render at a fiftieth of the sky's noise), so the pixels where the draw differs
/// from the plate ARE the injected footprints, a bright star's far wings included, and an output minus the plate is, on them,
/// what the model left of the stars and, off them, its own change to sky it was handed unchanged. Every measure is read on the
/// luminance in the draw's per-pixel noise (its <c>.sigma.f16</c>, the trainer's conditioning plane, divided by its
/// <see cref="Lib.Imaging.Degradation.StretchedNoise.PlaneScale"/>), inside the tile's stitched rim
/// (<see cref="AiNafnetInputs.StitchBorderPx"/>), and for two references from the same draws beside the model's: the input
/// itself (a remover that removes nothing) and the plate (one that removes everything and nothing else).
/// </summary>
public static class StarRemovalEval
{
    /// <summary>The model outputs to score, one row per draw, written beside them by the inference script.</summary>
    public const string OutputsFileName = "outputs.jsonl";

    /// <summary>The report written into the outputs directory.</summary>
    public const string ReportFileName = "starless-eval.json";

    /// <summary>A star counts as removed when the light left at its core is under this many sigma of the draw's noise there.</summary>
    public const double RemovedSigma = 1.0;

    /// <summary>The completeness bands' lower edges in the injected core's significance; a star under the first is counted
    /// but not rated, its core being under the noise in the input already.</summary>
    public static readonly ImmutableArray<float> CompletenessEdges = [1f, 5f, 20f, 100f, 1000f];

    /// <summary>One draw's output: the draw's tile as the export names it, and the output's path in the outputs directory.</summary>
    public sealed record OutputRow(string Tile, string Output);

    /// <summary>One significance band's completeness.</summary>
    public sealed record BandRow(string Band, int Stars, int Removed, double Rate);

    /// <summary>One band's speckle rate (<see cref="StarlessSpeckles"/>).</summary>
    public sealed record SpeckleRow(string Band, int Sites, int Speckled, double Rate);

    /// <summary>One output's (or reference's) scores.</summary>
    /// <param name="Name">The model's outputs, or a reference.</param>
    /// <param name="Completeness">Removed stars by significance band (<see cref="CompletenessEdges"/>, a band under the
    /// first counted and not rated), the saturated stars as a band of their own.</param>
    /// <param name="FootprintRms">The output minus the plate in sigma, RMS over the footprints (the pixels where the draw
    /// differs from the plate): what is left where stars were. The plan's gate is 1.</param>
    /// <param name="SkyRms">The same everywhere else: the model's change to sky it was handed unchanged. 0 for both
    /// references.</param>
    /// <param name="Speckles">Dark speckles at the injected sites by band, and the null at star-free places of the output.</param>
    public sealed record ArmScores(
        string Name, ImmutableArray<BandRow> Completeness, double FootprintRms, long FootprintPixels, double SkyRms, long SkyPixels,
        ImmutableArray<SpeckleRow> Speckles, SpeckleRow SpeckleNull);

    /// <summary>The whole report.</summary>
    public sealed record Report(string ExportRoot, string OutputsDir, int Draws, int Stars, ImmutableArray<ArmScores> Arms);

    /// <summary>The arms, in report order.</summary>
    public static readonly ImmutableArray<string> ArmNames = ["output", "input (removes nothing)", "plate (removes all)"];

    /// <summary>Scores the outputs listed in <paramref name="outputsDir"/>'s <see cref="OutputsFileName"/> against
    /// <paramref name="exportRoot"/>'s injections and plates, and writes <see cref="ReportFileName"/> beside them.</summary>
    public static async Task<Report> RunAsync(string exportRoot, string outputsDir, CancellationToken cancellationToken = default)
    {
        var outputs = await ReadRowsAsync(Path.Combine(outputsDir, OutputsFileName), StarRemovalEvalJsonContext.Default.OutputRow, cancellationToken);
        var injections = new Dictionary<string, DatasetDegradationExporter.InjectionRow>(StringComparer.Ordinal);
        foreach (var row in await ReadRowsAsync(Path.Combine(exportRoot, DatasetDegradationExporter.InjectionManifestFileName),
                     DatasetDegradationJsonContext.Default.InjectionRow, cancellationToken))
        {
            injections[row.Tile] = row;
        }

        var results = new DrawScores[outputs.Count];
        await Parallel.ForAsync(0, outputs.Count, cancellationToken, (i, ct) =>
        {
            var row = outputs[i];
            var injection = injections.TryGetValue(row.Tile, out var found)
                ? found
                : throw new InvalidDataException($"{row.Tile} is not in {exportRoot}'s {DatasetDegradationExporter.InjectionManifestFileName}");
            results[i] = ScoreDraw(exportRoot, outputsDir, row, injection);
            return ValueTask.CompletedTask;
        });

        var arms = ImmutableArray.CreateBuilder<ArmScores>(ArmNames.Length);
        for (var a = 0; a < ArmNames.Length; a++)
        {
            arms.Add(Summarise(ArmNames[a], results.Select(r => r.Arms[a])));
        }
        var report = new Report(exportRoot, outputsDir, results.Length, results.Sum(static r => r.Stars), arms.MoveToImmutable());
        await File.WriteAllTextAsync(Path.Combine(outputsDir, ReportFileName),
            JsonSerializer.Serialize(report, StarRemovalEvalJsonContext.Default.Report), cancellationToken);
        return report;
    }

    // Completeness bands: under the first edge, each edge's band, then the saturated stars. Speckle bands are StarlessSpeckles'.
    private static readonly ImmutableArray<string> BandNames =
        [$"under {CompletenessEdges[0]:0} sigma", .. EdgeNames(CompletenessEdges), "saturated"];

    private static readonly ImmutableArray<string> SpeckleBandNames = EdgeNames(StarlessSpeckles.BandEdges);

    private static ImmutableArray<string> EdgeNames(ImmutableArray<float> edges)
        => [.. edges.Select((e, i) => i + 1 < edges.Length ? $"{e:0}-{edges[i + 1]:0} sigma" : $"{e:0}+ sigma")];

    private sealed class ArmAccumulator
    {
        public int[] Stars { get; } = new int[BandNames.Length];
        public int[] Removed { get; } = new int[BandNames.Length];
        public double FootprintSq { get; set; }
        public long FootprintPixels { get; set; }
        public double SkySq { get; set; }
        public long SkyPixels { get; set; }
        public int[] SpeckleSites { get; } = new int[StarlessSpeckles.BandEdges.Length];
        public int[] Speckled { get; } = new int[StarlessSpeckles.BandEdges.Length];
        public int NullSites { get; set; }
        public int NullSpeckled { get; set; }
    }

    private sealed record DrawScores(int Stars, ArmAccumulator[] Arms);

    private static DrawScores ScoreDraw(string exportRoot, string outputsDir, OutputRow row, DatasetDegradationExporter.InjectionRow injection)
    {
        var sigmaRel = DatasetDegradationExporter.SigmaPathFor(row.Tile);
        var sigma = ReadPlanes(Path.Combine(exportRoot, Native(sigmaRel)), channels: 1, out var size)[0];
        for (var i = 0; i < sigma.Length; i++)
        {
            sigma[i] /= (float)Lib.Imaging.Degradation.StretchedNoise.PlaneScale;
        }
        var dir = row.Tile[..row.Tile.LastIndexOf('/')];
        var cleanRel = $"{dir}/x{injection.CellX}_y{injection.CellY}_{DatasetDegradationExporter.FrameClean}{DatasetTileExporter.TileExtension}";
        var channels = (int)(new FileInfo(Path.Combine(exportRoot, Native(row.Tile))).Length / (2L * size * size));
        var plate = Luminance(ReadPlanes(Path.Combine(exportRoot, Native(cleanRel)), channels, out _));
        var input = Luminance(ReadPlanes(Path.Combine(exportRoot, Native(row.Tile)), channels, out _));
        var output = Luminance(ReadPlanes(Path.Combine(outputsDir, Native(row.Output)), channels, out _));

        var rim = AiNafnetInputs.StitchBorderPx;
        var absent = new BitMatrix(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                absent[y, x] = x < rim || y < rim || x >= size - rim || y >= size - rim;
            }
        }

        var stars = injection.Stars;

        // The sites: inside the rim with a 3x3 core, each star's significance its injected core over the noise there.
        var sites = new List<(float X, float Y, float Significance, bool Saturated)>();
        foreach (var star in stars)
        {
            var cx = (int)Math.Round(star.X);
            var cy = (int)Math.Round(star.Y);
            if (cx - 1 < rim || cy - 1 < rim || cx + 1 >= size - rim || cy + 1 >= size - rim)
            {
                continue;
            }
            var s = sigma[(cy * size) + cx];
            if (!(s > 0))
            {
                continue;
            }
            sites.Add(((float)star.X, (float)star.Y, (float)(Core(input, plate, size, cx, cy) / s), star.Saturated));
        }
        var sources = stars.Select(static s => ((float)s.X, (float)s.Y)).ToArray();

        var arms = new ArmAccumulator[ArmNames.Length];
        for (var a = 0; a < arms.Length; a++)
        {
            var candidate = a switch { 0 => output, 1 => input, _ => plate };
            var acc = new ArmAccumulator();
            foreach (var (x, y, significance, saturated) in sites)
            {
                var cx = (int)Math.Round(x);
                var cy = (int)Math.Round(y);
                var band = saturated ? BandNames.Length - 1 : CompletenessBand(significance);
                acc.Stars[band]++;
                if (Core(candidate, plate, size, cx, cy) < RemovedSigma * sigma[(cy * size) + cx])
                {
                    acc.Removed[band]++;
                }
            }
            for (var i = 0; i < plate.Length; i++)
            {
                if (absent[i / size, i % size] || !(sigma[i] > 0))
                {
                    continue;
                }
                var z = (candidate[i] - plate[i]) / sigma[i];
                if (input[i] != plate[i])
                {
                    acc.FootprintSq += z * z;
                    acc.FootprintPixels++;
                }
                else
                {
                    acc.SkySq += z * z;
                    acc.SkyPixels++;
                }
            }
            var speckles = StarlessSpeckles.Measure(candidate, size, size, absent,
                [.. sites.Select(static s => (s.X, s.Y, s.Significance))], sources);
            for (var b = 0; b < speckles.Bands.Length; b++)
            {
                acc.SpeckleSites[b] += speckles.Bands[b].Sites;
                acc.Speckled[b] += speckles.Bands[b].Speckled;
            }
            acc.NullSites += speckles.Null.Sites;
            acc.NullSpeckled += speckles.Null.Speckled;
            arms[a] = acc;
        }
        return new DrawScores(sites.Count, arms);
    }

    // The mean of a candidate minus the plate over the 3x3 about a pixel.
    private static double Core(float[] candidate, float[] plate, int size, int cx, int cy)
    {
        var sum = 0.0;
        for (var y = cy - 1; y <= cy + 1; y++)
        {
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                sum += candidate[(y * size) + x] - plate[(y * size) + x];
            }
        }
        return sum / 9.0;
    }

    // 0 under the first completeness edge, then 1 + the edge's index; the saturated band is the caller's.
    private static int CompletenessBand(float significance)
    {
        for (var b = CompletenessEdges.Length - 1; b >= 0; b--)
        {
            if (significance >= CompletenessEdges[b])
            {
                return b + 1;
            }
        }
        return 0;
    }

    private static ArmScores Summarise(string name, IEnumerable<ArmAccumulator> draws)
    {
        var total = new ArmAccumulator();
        foreach (var d in draws)
        {
            for (var b = 0; b < BandNames.Length; b++)
            {
                total.Stars[b] += d.Stars[b];
                total.Removed[b] += d.Removed[b];
            }
            for (var b = 0; b < total.SpeckleSites.Length; b++)
            {
                total.SpeckleSites[b] += d.SpeckleSites[b];
                total.Speckled[b] += d.Speckled[b];
            }
            total.FootprintSq += d.FootprintSq;
            total.FootprintPixels += d.FootprintPixels;
            total.SkySq += d.SkySq;
            total.SkyPixels += d.SkyPixels;
            total.NullSites += d.NullSites;
            total.NullSpeckled += d.NullSpeckled;
        }
        static double Rate(int n, int of) => of > 0 ? (double)n / of : double.NaN;
        return new ArmScores(
            name,
            // The band under the noise is counted, not rated: its cores are under a sigma in the input already.
            [.. BandNames.Select((band, b) => new BandRow(band, total.Stars[b], total.Removed[b], b == 0 ? double.NaN : Rate(total.Removed[b], total.Stars[b])))],
            total.FootprintPixels > 0 ? Math.Sqrt(total.FootprintSq / total.FootprintPixels) : double.NaN, total.FootprintPixels,
            total.SkyPixels > 0 ? Math.Sqrt(total.SkySq / total.SkyPixels) : double.NaN, total.SkyPixels,
            [.. Enumerable.Range(0, total.SpeckleSites.Length).Select(b =>
                new SpeckleRow(SpeckleBandNames[b], total.SpeckleSites[b], total.Speckled[b], Rate(total.Speckled[b], total.SpeckleSites[b])))],
            new SpeckleRow("null", total.NullSites, total.NullSpeckled, Rate(total.NullSpeckled, total.NullSites)));
    }

    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    // A tile's planes, CHW fp16 as every exporter writes them; its side from the file's size.
    private static float[][] ReadPlanes(string path, int channels, out int size)
    {
        var bytes = File.ReadAllBytes(path);
        size = (int)Math.Round(Math.Sqrt(bytes.Length / (2.0 * channels)));
        if ((long)channels * size * size * 2 != bytes.Length)
        {
            throw new InvalidDataException($"{path} is {bytes.Length} bytes, not {channels} square fp16 planes");
        }
        var halfs = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        var planes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size * size];
            var src = halfs.Slice(c * size * size, size * size);
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = (float)src[i];
            }
            planes[c] = plane;
        }
        return planes;
    }

    // The channels' mean, the luminance the noise plane and the speckle measure read.
    private static float[] Luminance(float[][] planes)
    {
        var lum = new float[planes[0].Length];
        foreach (var plane in planes)
        {
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += plane[i];
            }
        }
        for (var i = 0; i < lum.Length; i++)
        {
            lum[i] /= planes.Length;
        }
        return lum;
    }

    private static async Task<List<T>> ReadRowsAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (line.Length > 0 && JsonSerializer.Deserialize(line, info) is { } row)
            {
                rows.Add(row);
            }
        }
        return rows;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(StarRemovalEval.OutputRow))]
[JsonSerializable(typeof(StarRemovalEval.Report))]
internal sealed partial class StarRemovalEvalJsonContext : JsonSerializerContext
{
}
