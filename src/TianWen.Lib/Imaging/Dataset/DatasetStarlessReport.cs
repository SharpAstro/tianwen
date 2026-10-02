using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Sources;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// R0's report (docs/plans/star-remover-training.md, "R0: the classical plate builder"): every named master through
/// <see cref="ClassicalStarRemover"/>, its plate and fill mask written beside each other, its measures appended to a JSONL
/// store as each master finishes and the markdown table re-rendered after each, so a stopped run keeps what it measured.
/// </summary>
public static class DatasetStarlessReport
{
    /// <summary>The store's file name under <c>&lt;out&gt;/stats/</c>.</summary>
    public const string StoreFileName = "starless-plates.jsonl";

    /// <summary>The markdown report's file name under <c>&lt;out&gt;/stats/</c>.</summary>
    public const string ReportFileName = "starless-plates.md";

    /// <summary>What to run.</summary>
    /// <param name="Masters">The master FITS files.</param>
    /// <param name="OutputRoot">Plates land in <c>plates/</c>, the store and the report in <c>stats/</c>.</param>
    /// <param name="WritePlates">Write each plate and its fill mask.</param>
    /// <param name="ProbeHoles">Holes per radius for the fill probe; 0 skips it.</param>
    /// <param name="Force">Measure a master already in the store again (the new record wins).</param>
    public sealed record RunOptions(ImmutableArray<string> Masters, string OutputRoot, bool WritePlates = true, int ProbeHoles = 100, bool Force = false);

    /// <summary>What a run did.</summary>
    public sealed record RunResult(int Measured, int Skipped, int Failed, string ReportPath);

    /// <summary>One master's record in the store.</summary>
    /// <param name="Master">The master's file name.</param>
    /// <param name="Statistics">The builder's measures.</param>
    /// <param name="Fill">The fill probe per hole radius.</param>
    /// <param name="CoreRadii">The plate's filled regions' equivalent radii: count, p50, p90, max.</param>
    public sealed record StarlessMasterRecord(
        string Master, int Width, int Height, int Channels, DateTimeOffset MeasuredUtc,
        int Found, int Subtracted, int Knots, int TooNarrow, int NoFit, int Merged, int Saturated, int SecondPass,
        StarlessPlateStatistics Statistics, ImmutableArray<FillProbeBand> Fill, ImmutableArray<float> CoreRadii);

    /// <summary>Runs the report.</summary>
    public static async Task<RunResult> RunAsync(
        RunOptions options, ILogger? logger = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var statsDir = Path.Combine(options.OutputRoot, "stats");
        var platesDir = Path.Combine(options.OutputRoot, "plates");
        Directory.CreateDirectory(statsDir);
        if (options.WritePlates)
        {
            Directory.CreateDirectory(platesDir);
        }
        var storePath = Path.Combine(statsDir, StoreFileName);
        var reportPath = Path.Combine(statsDir, ReportFileName);
        var store = await ReadAsync(storePath, logger, cancellationToken);

        int measured = 0, skipped = 0, failed = 0;
        foreach (var path in options.Masters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!options.Force && store.ContainsKey(name))
            {
                skipped++;
                continue;
            }
            try
            {
                if (!Image.TryReadFitsFile(path, out var image, out var wcs) || image is null)
                {
                    progress?.Report($"[starless] {name}: not a readable image, skipped");
                    failed++;
                    continue;
                }
                var plate = await ClassicalStarRemover.BuildAsync(image, logger: logger, cancellationToken: cancellationToken);
                var fill = options.ProbeHoles > 0
                    ? StarlessFillProbe.Measure(plate, StarlessFillProbe.DefaultRadii, options.ProbeHoles, cancellationToken: cancellationToken)
                    : ImmutableArray<FillProbeBand>.Empty;
                var cores = StarlessFillProbe.CoreRadii(plate);
                if (options.WritePlates)
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    plate.Plate.WriteToFitsFile(Path.Combine(platesDir, stem + "_plate.fits"), wcs);
                    // The other half of the split the pipeline uses (stars = input - starless, RecombineMode.Additive):
                    // the two plates add back to the master exactly.
                    var starsOnly = StarsOnly(image, plate.Plate);
                    starsOnly.WriteToFitsFile(Path.Combine(platesDir, stem + "_stars.fits"), wcs);
                    starsOnly.Release();
                    SourceDetectionWriter.WriteMap(Path.Combine(platesDir, stem + "_plate.inpainted.fits.gz"), SourceDetectionWriter.MaskToImage(plate.Inpainted), "INPAINTED", wcs);
                    SourceDetectionWriter.WriteMap(Path.Combine(platesDir, stem + "_plate.subtracted.fits.gz"), SourceDetectionWriter.MaskToImage(plate.Subtracted), "SUBTRACTED", wcs);
                    await WriteStarsAsync(Path.Combine(platesDir, stem + "_plate.stars.csv"), plate.Stars, cancellationToken);
                }
                var (channels, width, height) = image.Shape;
                var stars = plate.Stars;
                var record = new StarlessMasterRecord(
                    name, width, height, channels, DateTimeOffset.UtcNow,
                    stars.Length,
                    stars.Count(static s => s.Outcome == StarFitOutcome.Subtracted),
                    stars.Count(static s => s.Outcome == StarFitOutcome.Knot),
                    stars.Count(static s => s.Outcome == StarFitOutcome.TooNarrow),
                    stars.Count(static s => s.Outcome == StarFitOutcome.NoFit),
                    stars.Count(static s => s.Outcome == StarFitOutcome.Merged),
                    stars.Count(static s => s.Saturated),
                    stars.Count(static s => s.SecondPass),
                    plate.Statistics, fill, Summarise(cores));
                await JsonLinesFile.AppendRecordAsync(storePath, record, DatasetStarlessJsonContext.Default.StarlessMasterRecord, cancellationToken);
                store[name] = record;
                image.Release();
                measured++;
                progress?.Report($"[starless] {name}: {record.Found} found, {record.Subtracted} subtracted, {record.Knots} knots, inpaint {plate.Statistics.InpaintFraction:P2}, {plate.Statistics.Seconds:F0} s");
                await WriteMarkdownAsync(reportPath, store.Values, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogError(ex, "Starless report: {Master} failed", name);
                progress?.Report($"[starless] {name}: failed ({ex.Message})");
                failed++;
            }
        }
        await WriteMarkdownAsync(reportPath, store.Values, cancellationToken);
        return new RunResult(measured, skipped, failed, reportPath);
    }

    private static Image StarsOnly(Image input, Image starless)
    {
        var (channels, width, height) = input.Shape;
        var planes = Image.CreateChannelData(channels, height, width);
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var a = input.GetChannelSpan(c);
            var b = starless.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var v = a[i] - b[i];
                    planes[c][y, x] = v;
                    if (float.IsFinite(v))
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
            }
        }
        if (min > max)
        {
            min = max = 0f;
        }
        return new Image(planes, BitDepth.Float32, max, min, 0f, input.ImageMeta);
    }

    // Every point source and what the fit made of it, for the review and for a miss to be looked up.
    private static async Task WriteStarsAsync(string path, ImmutableArray<FittedStar> stars, CancellationToken ct)
    {
        var sb = new StringBuilder("x,y,significance,amplitude,width,sky,sigma,outcome,saturated,inpainted,core_residual,core_bias,second_pass,hole_depth\n");
        foreach (var s in stars)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.X:F2},{s.Y:F2},{s.Significance:F1},{s.Amplitude:G5},{s.WidthScale:F3},{s.Sky:G5},{s.LocalSigma:G4},{s.Outcome},{(s.Saturated ? 1 : 0)},{(s.Inpainted ? 1 : 0)},{s.CoreResidual:F3},{s.CoreBias:F3},{(s.SecondPass ? 1 : 0)},{s.HoleDepth:F2}\n"));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    /// <summary>Reads the store, last record per master winning.</summary>
    public static Task<Dictionary<string, StarlessMasterRecord>> ReadAsync(string path, ILogger? logger = null, CancellationToken cancellationToken = default)
        => JsonLinesFile.ReadLastPerKeyAsync(path, DatasetStarlessJsonContext.Default.StarlessMasterRecord, static r => r.Master, "starless store", logger, cancellationToken);

    private static ImmutableArray<float> Summarise(ImmutableArray<float> sorted)
        => sorted.Length == 0
            ? ImmutableArray.Create(0f, float.NaN, float.NaN, float.NaN)
            : ImmutableArray.Create((float)sorted.Length, sorted[sorted.Length / 2], sorted[(int)(0.9 * (sorted.Length - 1))], sorted[^1]);

    private static async Task WriteMarkdownAsync(string path, IEnumerable<StarlessMasterRecord> records, CancellationToken ct)
    {
        static string F(float v, string format = "F2") => float.IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "-";
        var sb = new StringBuilder();
        sb.AppendLine("# Starless plates (R0)");
        sb.AppendLine();
        sb.AppendLine("docs/plans/star-remover-training.md, \"R0: the classical plate builder\". Residual: RMS of plate minus local sky");
        sb.AppendLine("within one FWHM of a subtracted, not inpainted star, in local sigma (pure noise reads 1), median per band.");
        sb.AppendLine("Leftover: point sources the finder still sees on the plate, as a fraction of the first pass's, per band; in");
        sb.AppendLine("brackets the share of those outside every subtracted star's footprint (never subtracted: a missed star, a spike).");
        sb.AppendLine("Fill: the signal the fill got wrong on known pixels, in local sigma with both noises out (0 is perfect),");
        sb.AppendLine("on smooth / structured holes; grain is the filled hole's scatter over the original's.");
        sb.AppendLine();
        sb.AppendLine("Holes: subtracted stars whose core sits more than 3 sigma over root n below their own sky, per band (5-20 / 20-100 / 100+).");
        sb.AppendLine();
        sb.AppendLine("| Master | ch | found | sub | knots | sat | holes | inpaint | resid 5-10 / 10-20 / 20-50 / 50-100 / 100+ | bias 5-20 / 100+ | leftover 5-10 / 10-20 / 20+ | faint centre / corners | FWHM, beta (field) | cores n / p50 / p90 / max px | fill r6 | fill r12 | fill r24 | grain r12 | s |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in records.OrderBy(static r => r.Master, StringComparer.OrdinalIgnoreCase))
        {
            var st = r.Statistics;
            var b = st.Bands;
            string Fill(int radius)
            {
                var f = r.Fill.FirstOrDefault(x => x.Radius == radius);
                return f.Holes == 0 ? "-" : $"{F(f.StructureErrorSmooth)} / {F(f.StructureErrorStructured)} ({f.SmoothHoles}/{f.StructuredHoles})";
            }
            var leftoverHigh = b.Skip(2).Sum(static x => x.Found) is var found && found > 0 ? (float)b.Skip(2).Sum(static x => x.Leftover) / found : float.NaN;
            var g12 = r.Fill.FirstOrDefault(x => x.Radius == 12).GrainRatio;
            sb.Append("| ").Append(r.Master).Append(" | ").Append(r.Channels)
                .Append(" | ").Append(r.Found).Append(" | ").Append(r.Subtracted).Append(" | ").Append(r.Knots + r.TooNarrow).Append(" | ").Append(r.Saturated)
                .Append(" | ").Append(b.Take(2).Sum(static x => x.Holes)).Append(" / ").Append(b.Skip(2).Take(2).Sum(static x => x.Holes)).Append(" / ").Append(b[^1].Holes)
                .Append(" | ").Append(F(st.InpaintFraction * 100f)).Append("%")
                .Append(" | ").Append(string.Join(" / ", b.Select(x => F(x.ResidualMedian))))
                .Append(" | ").Append(F(Median(b.Take(2).Select(static x => x.BiasMedian)))).Append(" / ").Append(F(b[^1].BiasMedian))
                .Append(" | ").Append(F(b[0].LeftoverFraction)).Append(" (").Append(F(Away(b[0]))).Append(") / ").Append(F(b[1].LeftoverFraction)).Append(" (").Append(F(Away(b[1]))).Append(") / ").Append(F(leftoverHigh))
                .Append(" | ").Append(F(st.FaintResidualCentre)).Append(" / ").Append(F(st.FaintResidualCorners))
                .Append(" | ").Append(F(st.FwhmPx[^1])).Append(", ").Append(F(st.MoffatBeta[^1])).Append(" (").Append(F(st.FieldBeta)).Append(")")
                .Append(" | ").Append(F(r.CoreRadii[0], "F0")).Append(" / ").Append(F(r.CoreRadii[1], "F1")).Append(" / ").Append(F(r.CoreRadii[2], "F1")).Append(" / ").Append(F(r.CoreRadii[3], "F1"))
                .Append(" | ").Append(Fill(6)).Append(" | ").Append(Fill(12)).Append(" | ").Append(Fill(24))
                .Append(" | ").Append(F(g12))
                .Append(" | ").Append(F((float)st.Seconds, "F0"))
                .AppendLine(" |");
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static float Away(StarlessBand band) => band.Leftover > 0 ? (float)band.LeftoverAway / band.Leftover : float.NaN;

    private static float Median(IEnumerable<float> values)
    {
        var arr = values.Where(float.IsFinite).OrderBy(static v => v).ToArray();
        return arr.Length == 0 ? float.NaN : arr[arr.Length / 2];
    }
}

// NaN is a legitimate value here (a band with no clean star has no median), so the named literals are allowed.
[JsonSerializable(typeof(DatasetStarlessReport.StarlessMasterRecord))]
[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class DatasetStarlessJsonContext : JsonSerializerContext;
