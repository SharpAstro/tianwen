using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
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
/// <remarks>
/// <para><b>What R2's first reads could not see (2026-10-06), and the measures added for it.</b> The measures R2a and R2b
/// were registered on are kept exactly as they were; these sit beside them.</para>
/// <list type="bullet">
/// <item><b>Removal is one-sided.</b> <see cref="BandRow.Removed"/> counts a core left under a sigma above the plate, so a
/// core dug below the sky passes. <see cref="BandRow.Clean"/> is two-sided and judged against the truth, and
/// <see cref="BandRow.Dug"/> counts the digs.</item>
/// <item><b>The sky's change is one unsigned number on the luminance.</b> A level offset, a colour cast and a smoothed
/// noise all read alike there. <see cref="SkyDetail"/> takes it apart.</item>
/// <item><b>The footprint residue is pooled.</b> A few bad draws and a general failure read the same.
/// <see cref="FootprintDetail"/> and the per-session rows (<see cref="Report.Sessions"/>) tell them apart.</item>
/// <item><b>Two models were compared at whatever strength each was trained to.</b> The blended arms
/// (<see cref="BlendWeights"/>) turn one down, so arms can be read at a matched strength.</item>
/// </list>
/// </remarks>
public static class StarRemovalEval
{
    /// <summary>The model outputs to score, one row per draw, written beside them by the inference script.</summary>
    public const string OutputsFileName = "outputs.jsonl";

    /// <summary>The report written into the outputs directory.</summary>
    public const string ReportFileName = "starless-eval.json";

    /// <summary>A star counts as removed when the light left at its core is under this many sigma of the draw's noise there.</summary>
    public const double RemovedSigma = 1.0;

    /// <summary>A pixel within <see cref="StarlessSpeckles.CoreRadius"/> of a site this many sigma off the plate is a dig
    /// (under it) or a leftover (over it): the speckle test's threshold, read against the truth rather than against the
    /// ring round the site.</summary>
    public const float CorePixelSigma = StarlessSpeckles.SpeckleSigma;

    /// <summary>The weights the blended arms take the output at, <c>input + w (output - input)</c>: the same model turned
    /// down, so two models can be compared at a matched strength.</summary>
    public static readonly ImmutableArray<double> BlendWeights = [0.75, 0.5, 0.25];

    /// <summary>The worst share of draws <see cref="FootprintDetail.WorstShare"/> reads (at least one draw).</summary>
    public const double WorstDrawFraction = 0.01;

    /// <summary>How far round a source the plate kept, in the draw's PSF widths, its sky counts as near it
    /// (<see cref="PlateSourceDetail"/>).</summary>
    public const double NearSourceFwhm = 2.0;

    /// <summary>The PSF width the plate's own sources are found with when a draw injected no star to take it from.</summary>
    public const double FallbackFwhmPx = 2.5;

    /// <summary>The completeness bands' lower edges in the injected core's significance; a star under the first is counted
    /// but not rated, its core being under the noise in the input already.</summary>
    public static readonly ImmutableArray<float> CompletenessEdges = [1f, 5f, 20f, 100f, 1000f];

    /// <summary>One draw's output: the draw's tile as the export names it, and the output's path in the outputs directory.</summary>
    public sealed record OutputRow(string Tile, string Output);

    /// <summary>One significance band's completeness.</summary>
    /// <param name="Removed">Stars whose core's mean is left under <see cref="RemovedSigma"/> above the plate. One-sided, so a
    /// dug core counts: the measure R2a and R2b were registered on.</param>
    /// <param name="Rate"><paramref name="Removed"/> over the band's stars.</param>
    /// <param name="Clean">Stars removed and nothing else: the core's mean within <see cref="RemovedSigma"/> of the plate
    /// either way, and no pixel within <see cref="StarlessSpeckles.CoreRadius"/> more than <see cref="CorePixelSigma"/> off
    /// it.</param>
    /// <param name="Dug">Stars whose core went under the plate: its mean under minus <see cref="RemovedSigma"/>, or a pixel
    /// more than <see cref="CorePixelSigma"/> under it.</param>
    /// <param name="CoreMean">The core's mean left over the plate in sigma, signed, averaged over the band's stars.</param>
    /// <param name="CoreRms">The same, RMS.</param>
    public sealed record BandRow(
        string Band, int Stars, int Removed, double Rate, int Clean, double CleanRate, int Dug, double DugRate, double CoreMean, double CoreRms);

    /// <summary>One band's speckle rate (<see cref="StarlessSpeckles"/>).</summary>
    public sealed record SpeckleRow(string Band, int Sites, int Speckled, double Rate);

    /// <summary>The sky's change taken apart; <see cref="ArmScores.SkyRms"/> is its total.</summary>
    /// <param name="ChannelMean">Each channel's signed mean change, in the luminance's sigma: a colour cast reads here.</param>
    /// <param name="Mean">The luminance's signed mean change.</param>
    /// <param name="LevelRms">The part of <see cref="ArmScores.SkyRms"/> that is each draw's own level shift: the RMS of the
    /// draws' signed means, weighted by their pixels.</param>
    /// <param name="AboutLevelRms">The rest, about each draw's level: the texture the model changed.
    /// <c>LevelRms^2 + AboutLevelRms^2 = SkyRms^2</c>.</param>
    /// <param name="NoiseRatio">The sky's differences between horizontal neighbours over the plate's, RMS, each in its
    /// pixel's sigma: under 1 the model smoothed the sky's noise, over 1 it added some.</param>
    public sealed record SkyDetail(ImmutableArray<double> ChannelMean, double Mean, double LevelRms, double AboutLevelRms, double NoiseRatio);

    /// <summary>How the footprint residue spreads over the draws; <see cref="ArmScores.FootprintRms"/> pools them.</summary>
    /// <param name="P50">The median draw's footprint RMS.</param>
    /// <param name="P90">The 90th percentile draw's.</param>
    /// <param name="P99">The 99th percentile draw's.</param>
    /// <param name="Max">The worst draw's.</param>
    /// <param name="WorstShare">The share of the pooled squared residue in the worst <see cref="WorstDrawFraction"/> of
    /// draws: near 1, a few draws ARE the pooled number.</param>
    public sealed record FootprintDetail(double P50, double P90, double P99, double Max, double WorstShare);

    /// <summary>
    /// The sky's change split by what the truth itself still holds: the point sources the plate kept
    /// (<see cref="PlateSources"/>, the faint stars its builder left, found on the plate's sky, off every injected footprint),
    /// the sky within <see cref="NearSourceFwhm"/> PSF widths of one, and the rest. A remover that takes the stars the plate
    /// kept moves the near sky down, and the far sky not at all.
    /// </summary>
    /// <param name="Sources">The plate's sources read.</param>
    /// <param name="NearPixels">Sky pixels near one.</param>
    /// <param name="NearMean">Their change, signed, in sigma.</param>
    /// <param name="NearRms">The same, RMS.</param>
    /// <param name="FarPixels">The rest of the sky.</param>
    /// <param name="FarMean">Its change, signed.</param>
    /// <param name="FarRms">The same, RMS.</param>
    /// <param name="Taken">Sources whose 3x3 core the candidate lowered by more than <see cref="RemovedSigma"/>.</param>
    /// <param name="TakenRate"><paramref name="Taken"/> over <paramref name="Sources"/>.</param>
    /// <param name="CoreMean">The sources' core change, signed, averaged.</param>
    public sealed record PlateSourceDetail(
        int Sources, long NearPixels, double NearMean, double NearRms, long FarPixels, double FarMean, double FarRms, int Taken,
        double TakenRate, double CoreMean);

    /// <summary>
    /// Where a candidate's squared error against the plate lives in the TILE's own units, inside the rim: the luminance's
    /// share of what a plain L2 against the plate (the trainer's pixel term) is made of. Never in sigma, so a bright core
    /// the stretch compressed weighs here what it weighs in the loss.
    /// </summary>
    /// <param name="Total">The squared error summed over the tile's pixels inside the rim, over every draw.</param>
    /// <param name="Footprints">The share on the injected footprints.</param>
    /// <param name="BrightCores">The share in the 3x3 cores of the injected stars of 100 sigma and over, saturated ones included
    /// (a part of <paramref name="Footprints"/>).</param>
    /// <param name="SkyNearSources">The share on the sky near a source the plate kept.</param>
    /// <param name="SkyFar">The share on the rest of the sky.</param>
    public sealed record LossShare(double Total, double Footprints, double BrightCores, double SkyNearSources, double SkyFar);

    /// <summary>One output's (or reference's) scores.</summary>
    /// <param name="Name">The model's outputs, a blend of them with the input, or a reference.</param>
    /// <param name="Completeness">Removed stars by significance band (<see cref="CompletenessEdges"/>, a band under the
    /// first counted and not rated), the saturated stars as a band of their own.</param>
    /// <param name="FootprintRms">The output minus the plate in sigma, RMS over the footprints (the pixels where the draw
    /// differs from the plate): what is left where stars were. The plan's gate is 1.</param>
    /// <param name="SkyRms">The same everywhere else: the model's change to sky it was handed unchanged. 0 for both
    /// references.</param>
    /// <param name="Speckles">Dark speckles at the injected sites by band, and the null at star-free places of the output.</param>
    /// <param name="Sky">The sky's change taken apart.</param>
    /// <param name="Footprint">The footprint residue's spread over the draws.</param>
    /// <param name="PlateSources">The sky's change near the sources the plate kept, and away from them.</param>
    /// <param name="Loss">Where the squared error a plain L2 sees lives.</param>
    public sealed record ArmScores(
        string Name, ImmutableArray<BandRow> Completeness, double FootprintRms, long FootprintPixels, double SkyRms, long SkyPixels,
        ImmutableArray<SpeckleRow> Speckles, SpeckleRow SpeckleNull, SkyDetail Sky, FootprintDetail Footprint, PlateSourceDetail PlateSources,
        LossShare Loss);

    /// <summary>One session's scores: every arm over that session's draws alone, so a pooled number can be told from one
    /// night's.</summary>
    public sealed record SessionScores(string SessionId, int Draws, int Stars, ImmutableArray<ArmScores> Arms);

    /// <summary>The whole report.</summary>
    public sealed record Report(string ExportRoot, string OutputsDir, int Draws, int Stars, ImmutableArray<ArmScores> Arms,
        ImmutableArray<SessionScores> Sessions);

    /// <summary>The arms, in report order: the output and the two references first, as R2a and R2b read them, then the
    /// output blended with the input at each of <see cref="BlendWeights"/>.</summary>
    public static readonly ImmutableArray<string> ArmNames =
        ["output", "input (removes nothing)", "plate (removes all)",
         .. BlendWeights.Select(static w => string.Create(CultureInfo.InvariantCulture, $"output at {w:0.##}"))];

    /// <summary>Scores the outputs listed in <paramref name="outputsDir"/>'s <see cref="OutputsFileName"/> against
    /// <paramref name="exportRoot"/>'s injections and plates, and writes the report beside them, as
    /// <paramref name="reportName"/> (<see cref="ReportFileName"/> by default): a re-score under a name of its own never
    /// replaces a report a registration was read from.</summary>
    public static async Task<Report> RunAsync(string exportRoot, string outputsDir, string? reportName = null, CancellationToken cancellationToken = default)
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

        var sessions = results
            .GroupBy(static r => r.SessionId, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => new SessionScores(g.Key, g.Count(), g.Sum(static r => r.Stars), SummariseArms([.. g])))
            .ToImmutableArray();
        var report = new Report(exportRoot, outputsDir, results.Length, results.Sum(static r => r.Stars), SummariseArms(results), sessions);
        await File.WriteAllTextAsync(Path.Combine(outputsDir, reportName ?? ReportFileName),
            JsonSerializer.Serialize(report, StarRemovalEvalJsonContext.Default.Report), cancellationToken);
        return report;
    }

    private static ImmutableArray<ArmScores> SummariseArms(IReadOnlyList<DrawScores> draws)
    {
        var arms = ImmutableArray.CreateBuilder<ArmScores>(ArmNames.Length);
        for (var a = 0; a < ArmNames.Length; a++)
        {
            arms.Add(Summarise(ArmNames[a], draws.Select(r => r.Arms[a])));
        }
        return arms.MoveToImmutable();
    }

    // Completeness bands: under the first edge, each edge's band, then the saturated stars. Speckle bands are StarlessSpeckles'.
    private static readonly ImmutableArray<string> BandNames =
        [$"under {CompletenessEdges[0]:0} sigma", .. EdgeNames(CompletenessEdges), "saturated"];

    private static readonly ImmutableArray<string> SpeckleBandNames = EdgeNames(StarlessSpeckles.BandEdges);

    private static ImmutableArray<string> EdgeNames(ImmutableArray<float> edges)
        => [.. edges.Select((e, i) => i + 1 < edges.Length ? $"{e:0}-{edges[i + 1]:0} sigma" : $"{e:0}+ sigma")];

    // One arm's sums, over one draw (ScoreDraw) or over many (Summarise).
    private sealed class ArmAccumulator(int channels)
    {
        public int[] Stars { get; } = new int[BandNames.Length];
        public int[] Removed { get; } = new int[BandNames.Length];
        public int[] Clean { get; } = new int[BandNames.Length];
        public int[] Dug { get; } = new int[BandNames.Length];
        public double[] CoreSum { get; } = new double[BandNames.Length];
        public double[] CoreSq { get; } = new double[BandNames.Length];
        public double FootprintSq { get; set; }
        public long FootprintPixels { get; set; }
        public double SkySq { get; set; }
        public long SkyPixels { get; set; }
        public double SkySum { get; set; }
        public double[] SkyChannelSum { get; } = new double[channels];
        // The draws' level shifts, each draw's SkySum^2 / SkyPixels, summed: the level's part of SkySq.
        public double SkyLevelSq { get; set; }
        public double SkyDiffSq { get; set; }
        public double PlateDiffSq { get; set; }
        public int PlateSourceCount { get; set; }
        public int PlateSourcesTaken { get; set; }
        public double PlateSourceCoreSum { get; set; }
        public double NearSum { get; set; }
        public double NearSq { get; set; }
        public long NearPixels { get; set; }
        public double FarSum { get; set; }
        public double FarSq { get; set; }
        public long FarPixels { get; set; }
        public double RawFootprintSq { get; set; }
        public double RawBrightCoreSq { get; set; }
        public double RawNearSq { get; set; }
        public double RawFarSq { get; set; }
        public int[] SpeckleSites { get; } = new int[StarlessSpeckles.BandEdges.Length];
        public int[] Speckled { get; } = new int[StarlessSpeckles.BandEdges.Length];
        public int NullSites { get; set; }
        public int NullSpeckled { get; set; }
    }

    private sealed record DrawScores(string SessionId, int Stars, ArmAccumulator[] Arms);

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
        var platePlanes = ReadPlanes(Path.Combine(exportRoot, Native(cleanRel)), channels, out _);
        var inputPlanes = ReadPlanes(Path.Combine(exportRoot, Native(row.Tile)), channels, out _);
        var outputPlanes = ReadPlanes(Path.Combine(outputsDir, Native(row.Output)), channels, out _);
        var plate = Luminance(platePlanes);
        var input = Luminance(inputPlanes);
        var output = Luminance(outputPlanes);

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

        // The sources the truth itself kept: the plate's own, found as its builder finds them at the injected stars' width,
        // on its sky (off every injected footprint) with a 3x3 core inside the rim; and the sky near any of them.
        var fwhm = PlateFwhm(stars);
        var plateSources = new List<(int X, int Y)>();
        foreach (var (sx, sy, _) in PlateSources.Find(plate, size, size, absent, fwhm))
        {
            var cx = (int)Math.Round(sx);
            var cy = (int)Math.Round(sy);
            if (cx - 1 < rim || cy - 1 < rim || cx + 1 >= size - rim || cy + 1 >= size - rim)
            {
                continue;
            }
            var i = (cy * size) + cx;
            if (input[i] == plate[i] && sigma[i] > 0)
            {
                plateSources.Add((cx, cy));
            }
        }
        var near = new BitMatrix(size, size);
        var reach = (int)Math.Ceiling(NearSourceFwhm * fwhm);
        var reach2 = NearSourceFwhm * fwhm * NearSourceFwhm * fwhm;
        foreach (var (cx, cy) in plateSources)
        {
            for (var y = Math.Max(0, cy - reach); y <= Math.Min(size - 1, cy + reach); y++)
            {
                for (var x = Math.Max(0, cx - reach); x <= Math.Min(size - 1, cx + reach); x++)
                {
                    if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= reach2)
                    {
                        near[y, x] = true;
                    }
                }
            }
        }

        // The bright stars' cores (100 sigma and over, saturated ones too), whose share of a plain L2 the loss readout reads.
        var brightCore = new BitMatrix(size, size);
        foreach (var (x, y, significance, saturated) in sites)
        {
            if (!saturated && CompletenessBand(significance) < CompletenessEdges.IndexOf(100f) + 1)
            {
                continue;
            }
            var cx = (int)Math.Round(x);
            var cy = (int)Math.Round(y);
            for (var yy = cy - 1; yy <= cy + 1; yy++)
            {
                for (var xx = cx - 1; xx <= cx + 1; xx++)
                {
                    brightCore[yy, xx] = true;
                }
            }
        }

        var arms = new ArmAccumulator[ArmNames.Length];
        for (var a = 0; a < arms.Length; a++)
        {
            var (candidate, candidatePlanes) = a switch
            {
                0 => (output, outputPlanes),
                1 => (input, inputPlanes),
                2 => (plate, platePlanes),
                _ => Blend(inputPlanes, outputPlanes, BlendWeights[a - 3]),
            };
            var acc = new ArmAccumulator(channels);
            foreach (var (x, y, significance, saturated) in sites)
            {
                var cx = (int)Math.Round(x);
                var cy = (int)Math.Round(y);
                var band = saturated ? BandNames.Length - 1 : CompletenessBand(significance);
                acc.Stars[band]++;
                var s = sigma[(cy * size) + cx];
                var core = Core(candidate, plate, size, cx, cy);
                if (core < RemovedSigma * s)
                {
                    acc.Removed[band]++;
                }
                var (over, under) = CorePixelsOff(candidate, plate, sigma, absent, size, x, y);
                if (Math.Abs(core) < RemovedSigma * s && !over && !under)
                {
                    acc.Clean[band]++;
                }
                if (core < -RemovedSigma * s || under)
                {
                    acc.Dug[band]++;
                }
                acc.CoreSum[band] += core / s;
                acc.CoreSq[band] += core / s * (core / s);
            }
            for (var i = 0; i < plate.Length; i++)
            {
                if (absent[i / size, i % size] || !(sigma[i] > 0))
                {
                    continue;
                }
                var z = (candidate[i] - plate[i]) / sigma[i];
                var raw = (double)(candidate[i] - plate[i]) * (candidate[i] - plate[i]);
                if (input[i] != plate[i])
                {
                    acc.FootprintSq += z * z;
                    acc.FootprintPixels++;
                    acc.RawFootprintSq += raw;
                    if (brightCore[i / size, i % size])
                    {
                        acc.RawBrightCoreSq += raw;
                    }
                }
                else
                {
                    if (near[i / size, i % size])
                    {
                        acc.RawNearSq += raw;
                    }
                    else
                    {
                        acc.RawFarSq += raw;
                    }
                    acc.SkySq += z * z;
                    acc.SkyPixels++;
                    acc.SkySum += z;
                    if (near[i / size, i % size])
                    {
                        acc.NearSum += z;
                        acc.NearSq += z * z;
                        acc.NearPixels++;
                    }
                    else
                    {
                        acc.FarSum += z;
                        acc.FarSq += z * z;
                        acc.FarPixels++;
                    }
                    for (var c = 0; c < channels; c++)
                    {
                        acc.SkyChannelSum[c] += (candidatePlanes[c][i] - platePlanes[c][i]) / sigma[i];
                    }
                    // The noise ratio: this pixel and its right neighbour, both sky, both inside the rim.
                    var j = i + 1;
                    if ((i % size) + 1 < size - rim && sigma[j] > 0 && input[j] == plate[j])
                    {
                        var dc = (candidate[j] - candidate[i]) / sigma[i];
                        var dp = (plate[j] - plate[i]) / sigma[i];
                        acc.SkyDiffSq += dc * dc;
                        acc.PlateDiffSq += dp * dp;
                    }
                }
            }
            acc.SkyLevelSq = acc.SkyPixels > 0 ? acc.SkySum * acc.SkySum / acc.SkyPixels : 0;
            foreach (var (cx, cy) in plateSources)
            {
                var z = Core(candidate, plate, size, cx, cy) / sigma[(cy * size) + cx];
                acc.PlateSourceCount++;
                acc.PlateSourceCoreSum += z;
                if (z < -RemovedSigma)
                {
                    acc.PlateSourcesTaken++;
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
        return new DrawScores(injection.SessionId, sites.Count, arms);
    }

    // The PSF width the plate's own sources are found with: the injected stars' median, each the mean of its channels'
    // (the stars take the master's own profile), or the fallback for a draw with none.
    private static double PlateFwhm(IReadOnlyList<DatasetDegradationExporter.InjectedStarRow> stars)
    {
        var widths = stars
            .Where(static s => s.FwhmPx.Length > 0)
            .Select(static s => s.FwhmPx.Average())
            .Where(static w => w > 0 && double.IsFinite(w))
            .Order()
            .ToArray();
        return widths.Length > 0 ? widths[widths.Length / 2] : FallbackFwhmPx;
    }

    // The input taken toward the output by weight w, per channel, and its luminance.
    private static (float[] Luminance, float[][] Planes) Blend(float[][] input, float[][] output, double w)
    {
        var weight = (float)w;
        var planes = new float[input.Length][];
        for (var c = 0; c < input.Length; c++)
        {
            var plane = new float[input[c].Length];
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = input[c][i] + (weight * (output[c][i] - input[c][i]));
            }
            planes[c] = plane;
        }
        return (Luminance(planes), planes);
    }

    // Whether any pixel within the speckle test's core radius of a site is more than CorePixelSigma over the plate, and
    // whether any is more than that under it, each in its own pixel's sigma; the rim is not read.
    private static (bool Over, bool Under) CorePixelsOff(float[] candidate, float[] plate, float[] sigma, BitMatrix absent, int size, float cx, float cy)
    {
        var r = (int)Math.Ceiling(StarlessSpeckles.CoreRadius);
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        bool over = false, under = false;
        for (var y = Math.Max(0, y0 - r); y <= Math.Min(size - 1, y0 + r); y++)
        {
            for (var x = Math.Max(0, x0 - r); x <= Math.Min(size - 1, x0 + r); x++)
            {
                if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) > StarlessSpeckles.CoreRadius * StarlessSpeckles.CoreRadius || absent[y, x])
                {
                    continue;
                }
                var i = (y * size) + x;
                if (!(sigma[i] > 0))
                {
                    continue;
                }
                var z = (candidate[i] - plate[i]) / sigma[i];
                over |= z > CorePixelSigma;
                under |= z < -CorePixelSigma;
            }
        }
        return (over, under);
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
        var list = draws.ToList();
        var total = new ArmAccumulator(list.Count > 0 ? list.Max(static d => d.SkyChannelSum.Length) : 0);
        var footprintDraws = new List<(double Rms, double Sq)>(list.Count);
        foreach (var d in list)
        {
            for (var b = 0; b < BandNames.Length; b++)
            {
                total.Stars[b] += d.Stars[b];
                total.Removed[b] += d.Removed[b];
                total.Clean[b] += d.Clean[b];
                total.Dug[b] += d.Dug[b];
                total.CoreSum[b] += d.CoreSum[b];
                total.CoreSq[b] += d.CoreSq[b];
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
            total.SkySum += d.SkySum;
            total.SkyLevelSq += d.SkyLevelSq;
            for (var c = 0; c < d.SkyChannelSum.Length; c++)
            {
                total.SkyChannelSum[c] += d.SkyChannelSum[c];
            }
            total.SkyDiffSq += d.SkyDiffSq;
            total.PlateDiffSq += d.PlateDiffSq;
            total.PlateSourceCount += d.PlateSourceCount;
            total.PlateSourcesTaken += d.PlateSourcesTaken;
            total.PlateSourceCoreSum += d.PlateSourceCoreSum;
            total.NearSum += d.NearSum;
            total.NearSq += d.NearSq;
            total.NearPixels += d.NearPixels;
            total.FarSum += d.FarSum;
            total.FarSq += d.FarSq;
            total.FarPixels += d.FarPixels;
            total.RawFootprintSq += d.RawFootprintSq;
            total.RawBrightCoreSq += d.RawBrightCoreSq;
            total.RawNearSq += d.RawNearSq;
            total.RawFarSq += d.RawFarSq;
            total.NullSites += d.NullSites;
            total.NullSpeckled += d.NullSpeckled;
            if (d.FootprintPixels > 0)
            {
                footprintDraws.Add((Math.Sqrt(d.FootprintSq / d.FootprintPixels), d.FootprintSq));
            }
        }
        static double Rate(int n, int of) => of > 0 ? (double)n / of : double.NaN;
        static double PerPixel(double sum, long pixels) => pixels > 0 ? sum / pixels : double.NaN;

        // The band under the noise is counted, not rated: its cores are under a sigma in the input already.
        var bands = BandNames.Select((band, b) =>
        {
            var rated = b != 0;
            var n = total.Stars[b];
            return new BandRow(band, n,
                total.Removed[b], rated ? Rate(total.Removed[b], n) : double.NaN,
                total.Clean[b], rated ? Rate(total.Clean[b], n) : double.NaN,
                total.Dug[b], rated ? Rate(total.Dug[b], n) : double.NaN,
                n > 0 ? total.CoreSum[b] / n : double.NaN, n > 0 ? Math.Sqrt(total.CoreSq[b] / n) : double.NaN);
        });

        var sky = new SkyDetail(
            [.. total.SkyChannelSum.Select(s => PerPixel(s, total.SkyPixels))],
            PerPixel(total.SkySum, total.SkyPixels),
            Math.Sqrt(PerPixel(total.SkyLevelSq, total.SkyPixels)),
            Math.Sqrt(Math.Max(0.0, PerPixel(total.SkySq - total.SkyLevelSq, total.SkyPixels))),
            total.PlateDiffSq > 0 ? Math.Sqrt(total.SkyDiffSq / total.PlateDiffSq) : double.NaN);

        return new ArmScores(
            name,
            [.. bands],
            total.FootprintPixels > 0 ? Math.Sqrt(total.FootprintSq / total.FootprintPixels) : double.NaN, total.FootprintPixels,
            total.SkyPixels > 0 ? Math.Sqrt(total.SkySq / total.SkyPixels) : double.NaN, total.SkyPixels,
            [.. Enumerable.Range(0, total.SpeckleSites.Length).Select(b =>
                new SpeckleRow(SpeckleBandNames[b], total.SpeckleSites[b], total.Speckled[b], Rate(total.Speckled[b], total.SpeckleSites[b])))],
            new SpeckleRow("null", total.NullSites, total.NullSpeckled, Rate(total.NullSpeckled, total.NullSites)),
            sky,
            Spread(footprintDraws, total.FootprintSq),
            new PlateSourceDetail(
                total.PlateSourceCount,
                total.NearPixels, PerPixel(total.NearSum, total.NearPixels), Math.Sqrt(PerPixel(total.NearSq, total.NearPixels)),
                total.FarPixels, PerPixel(total.FarSum, total.FarPixels), Math.Sqrt(PerPixel(total.FarSq, total.FarPixels)),
                total.PlateSourcesTaken, Rate(total.PlateSourcesTaken, total.PlateSourceCount),
                total.PlateSourceCount > 0 ? total.PlateSourceCoreSum / total.PlateSourceCount : double.NaN),
            Shares(total));
    }

    // The squared error's shares by region, in the tile's own units.
    private static LossShare Shares(ArmAccumulator total)
    {
        var sum = total.RawFootprintSq + total.RawNearSq + total.RawFarSq;
        double Of(double part) => sum > 0 ? part / sum : double.NaN;
        return new LossShare(sum, Of(total.RawFootprintSq), Of(total.RawBrightCoreSq), Of(total.RawNearSq), Of(total.RawFarSq));
    }

    // The footprint residue's spread over the draws: nearest-rank percentiles of the draws' RMS, and the share of the pooled
    // squared residue the worst WorstDrawFraction of draws hold.
    private static FootprintDetail Spread(List<(double Rms, double Sq)> draws, double pooledSq)
    {
        if (draws.Count == 0)
        {
            return new FootprintDetail(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }
        var rms = draws.Select(static d => d.Rms).Order().ToArray();
        double At(double q) => rms[(int)Math.Round(q * (rms.Length - 1))];
        var worst = Math.Max(1, (int)Math.Ceiling(WorstDrawFraction * draws.Count));
        var worstSq = draws.Select(static d => d.Sq).OrderDescending().Take(worst).Sum();
        return new FootprintDetail(At(0.5), At(0.9), At(0.99), rms[^1], pooledSq > 0 ? worstSq / pooledSq : double.NaN);
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
