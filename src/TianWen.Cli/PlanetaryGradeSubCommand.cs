using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary grade</c> (docs/plans/planetary-restoration.md, R4): which frames a capture should keep. Every frame is scored by
/// each candidate estimator, and by its gain in each a trous band on the stack of every frame (the reference gain, registered by
/// the correlation). With a truth (a synthetic capture's), also by its true transfer in each band against it (and, where
/// planetary degrade wrote one, its Strehl): each estimator's ranking is compared with the true one (Spearman), and the frames
/// each selection keeps are stacked at the same counts and scored against the truth, whether a better selection reaches the
/// stack at all. Without one, the estimators are ranked against the reference gain, and each score's correlation from one frame
/// to the next says whether it follows the seeing, which is coherent over a few frames, or the noise, which is not.
/// </summary>
internal sealed partial class PlanetaryGradeSubCommand(IConsoleHost consoleHost)
{
    // The FFT bands, matched to the a trous bands 1 to 4 (band j carries about 1/2^(j+1) to 1/2^j cycles a pixel).
    private static readonly FrequencyBand[] FftBands = [new(0.25, 0.5), new(0.125, 0.25), new(0.0625, 0.125), new(0.03125, 0.0625)];

    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A mono SER capture of a planet, a synthetic one with its truth." };
        var truthOpt = new Option<string?>("--truth") { Description = "The truth planetary degrade wrote beside a synthetic capture; without it, the estimators are ranked against the reference gain and nothing is stacked." };
        var strehlOpt = new Option<string?>("--strehl") { Description = "The frames' Strehl ratios (planetary degrade's .frames.csv); by default the one beside the capture, where there is one." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var firstOpt = new Option<int>("--first") { Description = "The first frame measured.", DefaultValueFactory = _ => 0 };
        var framesOpt = new Option<int?>("--frames") { Description = "Only this many frames, from --first." };
        var keepOpt = new Option<string>("--keep") { Description = "The keep fractions to stack each selection at, a comma list.", DefaultValueFactory = _ => "0.02,0.05,0.1,0.2,0.5" };
        var cropOpt = new Option<int>("--crop") { Description = "The edge of the square about the disk each frame is measured in, a power of two.", DefaultValueFactory = _ => 256 };
        var pipelineOpt = new Option<string>("--pipeline") { Description = "The selections also stacked by the stacker's own aligner, a comma list of sources.", DefaultValueFactory = _ => "laplacian,strehl,every" };
        var outOpt = new Option<string?>("--out") { Description = "Write the per-frame scores to this CSV, and the stacks' fidelity beside it (.stacks.csv)." };
        var pointsOpt = new Option<bool>("--points") { Description = "Per point instead (R4 per-point, #1071): on a layered twin (planetary degrade --high-r0) with its truth, the share of each point's true quality that is its own, each local estimator ranked against it at every point, and each point's best frames stacked against the whole frames' best at the --keep fractions." };

        var command = new Command("grade",
            "Scores every frame of a capture by each quality estimator and by its gain on the stack of every frame; with a synthetic capture's truth, by its true transfer too, ranks the estimators against it and stacks each selection at the same counts (R4).")
        {
            Arguments = { captureArg },
            Options = { truthOpt, strehlOpt, planetOpt, firstOpt, framesOpt, keepOpt, cropOpt, pipelineOpt, outOpt, pointsOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(captureArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var first = Math.Clamp(parseResult.GetValue(firstOpt), 0, whole.FrameCount - 1);
            var frames = Math.Min(whole.FrameCount - first, parseResult.GetValue(framesOpt) ?? whole.FrameCount);
            using var stream = new PlanetaryFrameWindow(whole, first, frames);
            var (width, height) = (stream.Width, stream.Height);
            var size = parseResult.GetValue(cropOpt);
            if (size < 32 || (size & (size - 1)) != 0 || size > Math.Min(width, height))
            {
                consoleHost.WriteError($"--crop {size}: a power of two from 32 to the frame's smaller edge ({Math.Min(width, height)})");
                return 1;
            }

            (float[] Plane, MetricDisk Disk, DateTimeOffset? Time)? read = null;
            if (parseResult.GetValue(truthOpt) is { } truthPath && (read = PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost)) is null)
            {
                return 1;
            }
            if ((read?.Time ?? stream.MidCapture) is not { } when)
            {
                consoleHost.WriteError($"{input}: no truth with a DATE-OBS and no timestamps in the capture");
                return 1;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            var truth = read is { } r ? Target.Of(r.Plane, r.Disk with { AxisRatio = limbOptions.AxisRatio }, width, height, size) : null;

            if (parseResult.GetValue(pointsOpt))
            {
                if (truth is null)
                {
                    consoleHost.WriteError($"{input}: --points needs the layered twin's --truth");
                    return 1;
                }
                var pointKeeps = parseResult.GetValue(keepOpt)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(k => double.Parse(k, CultureInfo.InvariantCulture)).ToArray() ?? [];
                return await RunPointsAsync(input, stream, first, frames, truth, pointKeeps, ct);
            }

            var strehlPath = parseResult.GetValue(strehlOpt) ?? Path.ChangeExtension(input, ".frames.csv");
            var strehl = truth is not null && File.Exists(strehlPath) ? ReadColumn(strehlPath, "strehl", first, frames) : null;
            consoleHost.WriteScrollable($"{Path.GetFileName(input)}: frames {first} to {first + frames - 1}, measured in {size} px about the disk"
                + (truth is null ? "; no truth" : strehl is null ? "; no Strehl ratios" : $"; Strehl from {Path.GetFileName(strehlPath)}"));

            // The reference a pipeline has: every frame stacked on the Laplacian's best, its disk fitted.
            var grades = await new FrameGrader(new LaplacianEnergyEstimator()).GradeAllAsync(stream, cancellationToken: ct);
            var referenceIndex = FrameGrader.Reference(grades);
            var stacker = new LuckyImagingStacker();
            var everyFrame = ImmutableArray.CreateRange(Enumerable.Range(0, frames));
            var referenceStack = await stacker.StackPlanesAsync(stream, everyFrame, referenceIndex, ct);
            if (PlanetaryLimbFit.Fit(referenceStack, limbOptions) is not { } referenceFit)
            {
                consoleHost.WriteError("the stack of every frame: no limb fitted");
                return 1;
            }
            var referenceDisk = MetricDisk.From(referenceFit, limbOptions);
            var reference = Target.Of(referenceStack.GetChannelSpan(0), referenceDisk, width, height, size);
            referenceStack.Release();
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture, $"    the reference: every frame stacked on frame {referenceIndex}, the Laplacian's best; its disk at {referenceDisk.X:0.00}, {referenceDisk.Y:0.00}, R {referenceDisk.Radius:0.00} px"));

            // Every frame scored, in parallel: each estimator on the disk's bounding box, as the grader does, and the frame's
            // transfer against the reference and the truth, registered onto each.
            var scores = new FrameScores[frames];
            await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (i, token) =>
            {
                var frame = await stream.LoadAsync(i, token);
                try
                {
                    scores[i] = Score(frame, reference, truth);
                }
                finally
                {
                    frame.Release();
                }
            });
            consoleHost.WriteScrollable("    scored every frame");

            var sources = Sources(scores, strehl);
            WriteRanking(sources, scores, strehl);
            if (parseResult.GetValue(outOpt) is { } outPath)
            {
                await File.WriteAllTextAsync(outPath, FramesCsv(sources, scores, strehl), ct);
            }

            if (truth is null)
            {
                return 0;
            }

            // Each selection stacked: every source's best frames, at each keep, registered onto the truth by the correlation
            // (the oracle's alignment, so only the selection differs), and the pipeline's own stack for a few.
            var keeps = parseResult.GetValue(keepOpt)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => double.Parse(k, CultureInfo.InvariantCulture)).ToArray() ?? [];
            var stacks = await StackSelectionsAsync(stream, sources, keeps, truth, ct);
            var pipelineNames = (parseResult.GetValue(pipelineOpt) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var source in sources.Where(s => pipelineNames.Contains(s.Name)))
            {
                foreach (var keep in keeps)
                {
                    var selection = Select(source, keep);
                    var master = await stacker.StackPlanesAsync(stream, selection, referenceIndex, ct);
                    var (plane, _, _) = truth.Register(master.GetChannelSpan(0), width, height);
                    master.Release();
                    stacks.Add(new StackRow(source.Name, "pipeline", keep, selection.Length, truth.Fidelity(plane)));
                }
            }
            WriteStacks(stacks, keeps);
            if (parseResult.GetValue(outOpt) is { } stacksOut)
            {
                await File.WriteAllTextAsync(Path.ChangeExtension(stacksOut, ".stacks.csv"), StacksCsv(stacks), ct);
            }
            return 0;
        });
        return command;
    }

    // What frames are registered onto and scored against: a normalised plane's square of `size` about its disk, inside the
    // frame, and the disk in the square's coordinates.
    private sealed class Target
    {
        private readonly CorrelationRegistrar _registrar;
        private readonly int _x0, _y0;

        private Target(float[] crop, int x0, int y0, MetricDisk disk, int size)
        {
            (Crop, _x0, _y0, Disk, _registrar) = (crop, x0, y0, disk, new CorrelationRegistrar(crop, size));
        }

        public float[] Crop { get; }

        public MetricDisk Disk { get; }

        /// <summary>The square's corner in the frame.</summary>
        public int X0 => _x0;

        public int Y0 => _y0;

        public int Size => _registrar.Size;

        public static Target Of(ReadOnlySpan<float> plane, MetricDisk disk, int width, int height, int size)
        {
            var x0 = Math.Clamp((int)Math.Round(disk.X) - (size / 2), 0, width - size);
            var y0 = Math.Clamp((int)Math.Round(disk.Y) - (size / 2), 0, height - size);
            var crop = CorrelationRegistrar.Crop(PlanetaryMetrics.Normalise(plane, width, height, disk), width, height, x0, y0, size);
            return new Target(crop, x0, y0, disk with { X = disk.X - x0, Y = disk.Y - y0 }, size);
        }

        // A frame's square, moved onto this one and normalised on its disk, and where it lay.
        public (float[] Plane, double Dx, double Dy) Register(ReadOnlySpan<float> plane, int width, int height)
        {
            var (moved, dx, dy) = _registrar.Register(CorrelationRegistrar.Crop(plane, width, height, _x0, _y0, Size));
            return (PlanetaryMetrics.Normalise(moved, Size, Size, Disk), dx, dy);
        }

        public ImmutableArray<BandFidelity> Fidelity(float[] registered) => PlanetaryMetrics.Fidelity(registered, Crop, Size, Size, Disk);
    }

    // One frame's scores: the estimators', its gain in each band on the reference, its transfer in each against the truth (empty
    // without one), and where it lay against the truth (else the reference).
    private sealed record FrameScores(double Laplacian, double Gradient, BandPowers? Fft, ImmutableArray<BandFidelity> Truth,
        ImmutableArray<BandFidelity> Reference, double Dx, double Dy);

    private static FrameScores Score(Image frame, Target reference, Target? truth)
    {
        var region = PlanetaryDisk.BoundingBox(frame);
        var laplacian = new LaplacianEnergyEstimator().Score(frame, region);
        var gradient = new GradientEnergyEstimator().Score(frame, region);
        var fft = FftHighBandEstimator.Measure(frame, region, FftBands);

        var plane = frame.GetChannelSpan(0);
        var (onReference, dx, dy) = reference.Register(plane, frame.Width, frame.Height);
        var gains = reference.Fidelity(onReference);
        var transfer = ImmutableArray<BandFidelity>.Empty;
        if (truth is not null)
        {
            (var onTruth, dx, dy) = truth.Register(plane, frame.Width, frame.Height);
            transfer = truth.Fidelity(onTruth);
        }
        return new FrameScores(laplacian, gradient, fft, transfer, gains, dx, dy);
    }

    // A way of ranking the frames, higher better; `Oracle` for one that reads the truth.
    private sealed record Source(string Name, bool Oracle, double[] Score);

    private static List<Source> Sources(FrameScores[] scores, double[]? strehl)
    {
        var sources = new List<Source>
        {
            new("laplacian", false, [.. scores.Select(s => s.Laplacian)]),
            new("gradient", false, [.. scores.Select(s => s.Gradient)]),
        };
        for (var b = 0; b < FftBands.Length; b++)
        {
            var band = b;
            sources.Add(new($"fft{band + 1}", false, [.. scores.Select(s => s.Fft?.Detail(band, debias: false, normalizeBrightness: true) ?? double.NaN)]));
            sources.Add(new($"fft{band + 1}-debiased", false, [.. scores.Select(s => s.Fft?.Detail(band, debias: true, normalizeBrightness: true) ?? double.NaN)]));
        }
        for (var b = 0; b < PlanetaryMetrics.Bands; b++)
        {
            var band = b;
            sources.Add(new($"reference-gain{band + 1}", false, [.. scores.Select(s => s.Reference[band].Transfer)]));
        }
        // "every": every n-th frame, the selection that selects nothing, at the same count.
        sources.Add(new("every", false, [.. Enumerable.Range(0, scores.Length).Select(i => -(double)Spread(i, scores.Length))]));
        for (var b = 0; b < scores[0].Truth.Length; b++)
        {
            var band = b;
            sources.Add(new($"truth{band + 1}", true, [.. scores.Select(s => s.Truth[band].Transfer)]));
        }
        if (strehl is not null)
        {
            sources.Add(new("strehl", true, strehl));
        }
        return sources;
    }

    // A frame's place in an order that spreads any prefix of it evenly over the capture: the bit-reversal of its index.
    private static int Spread(int index, int count)
    {
        var bits = 0;
        while ((1 << bits) < count)
        {
            bits++;
        }
        var reversed = 0;
        for (var b = 0; b < bits; b++)
        {
            reversed |= ((index >> b) & 1) << (bits - 1 - b);
        }
        return reversed;
    }

    private static ImmutableArray<int> Select(Source source, double keep)
    {
        var order = Enumerable.Range(0, source.Score.Length)
            .Where(i => double.IsFinite(source.Score[i]))
            .OrderByDescending(i => source.Score[i]).ThenBy(i => i).ToArray();
        var count = Math.Clamp((int)Math.Round(keep * source.Score.Length, MidpointRounding.AwayFromZero), 1, order.Length);
        return [.. order.Take(count).Order()];
    }

    // The Strehl of frames `first` onwards, numbered from zero as the window numbers them.
    // One column of a synthetic capture's record (planetary degrade's .frames.csv) over the frames measured; NaN where a frame has
    // none, null when the record has no such column or is not there.
    private static double[]? ReadColumn(string path, string name, int first, int frames)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        var values = new double[frames];
        Array.Fill(values, double.NaN);
        var lines = File.ReadAllLines(path);
        var columns = lines[0].Split(',');
        var (frameColumn, valueColumn) = (Array.IndexOf(columns, "frame"), Array.IndexOf(columns, name));
        if (frameColumn < 0 || valueColumn < 0)
        {
            return null;
        }
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length > Math.Max(frameColumn, valueColumn) && int.TryParse(cells[frameColumn], CultureInfo.InvariantCulture, out var frame)
                && frame >= first && frame < first + frames)
            {
                values[frame - first] = double.Parse(cells[valueColumn], CultureInfo.InvariantCulture);
            }
        }
        return values;
    }

    // Each estimator's ranking of the frames against the true ones (with a truth) or the reference gain's (without), and each
    // score's correlation with itself one and two frames on.
    private void WriteRanking(List<Source> sources, FrameScores[] scores, double[]? strehl)
    {
        var inv = CultureInfo.InvariantCulture;
        var withTruth = scores[0].Truth.Length > 0;
        var yardsticks = new List<(string Name, double[] Score)>();
        for (var b = 0; b < PlanetaryMetrics.Bands; b++)
        {
            var band = b;
            yardsticks.Add(withTruth
                ? ($"transfer {band + 1}", [.. scores.Select(s => s.Truth[band].Transfer)])
                : ($"ref. gain {band + 1}", [.. scores.Select(s => s.Reference[band].Transfer)]));
        }
        if (strehl is not null)
        {
            yardsticks.Add(("Strehl", strehl));
        }
        var spread = new StringBuilder(withTruth ? "    the true transfer's spread over the frames, p5 / p50 / p95:" : "    the reference gain's spread over the frames, p5 / p50 / p95:");
        for (var b = 0; b < PlanetaryMetrics.Bands; b++)
        {
            var sorted = yardsticks[b].Score.Where(double.IsFinite).Order().ToArray();
            spread.Append(inv, $"  {b + 1}: {sorted[(int)(0.05 * (sorted.Length - 1))]:0.000} / {sorted[sorted.Length / 2]:0.000} / {sorted[(int)(0.95 * (sorted.Length - 1))]:0.000}");
        }
        consoleHost.WriteScrollable(spread.ToString());
        consoleHost.WriteScrollable(withTruth
            ? "ranking the frames (Spearman against each true quality), and each score against itself 1, 2 and 10 frames on:"
            : "ranking the frames (Spearman against the reference gain in each band), and each score against itself 1, 2 and 10 frames on:");
        consoleHost.WriteScrollable("    " + "estimator".PadRight(20) + string.Concat(yardsticks.Select(t => t.Name.PadLeft(12))) + "lag 1".PadLeft(9) + "lag 2".PadLeft(8) + "lag 10".PadLeft(8));
        foreach (var source in sources)
        {
            var row = new StringBuilder("    " + source.Name.PadRight(20));
            foreach (var (_, yardstick) in yardsticks)
            {
                var pairs = Enumerable.Range(0, yardstick.Length).Where(i => double.IsFinite(yardstick[i]) && double.IsFinite(source.Score[i])).ToArray();
                var rho = pairs.Length > 2 ? StatisticsHelper.Spearman([.. pairs.Select(i => source.Score[i])], [.. pairs.Select(i => yardstick[i])]) : double.NaN;
                row.Append(string.Create(inv, $"{rho,12:+0.000;-0.000}"));
            }
            row.Append(string.Create(inv, $"{Autocorrelation(source.Score, 1),9:+0.000;-0.000}{Autocorrelation(source.Score, 2),8:+0.000;-0.000}{Autocorrelation(source.Score, 10),8:+0.000;-0.000}"));
            consoleHost.WriteScrollable(row.ToString());
        }
    }

    // The correlation of a series with itself `lag` frames on (NaN where a frame has no score).
    private static double Autocorrelation(double[] series, int lag)
    {
        if (series.Any(v => !double.IsFinite(v)) || series.Length <= lag + 2)
        {
            return double.NaN;
        }
        var mean = series.Average();
        double product = 0, square = 0;
        for (var i = 0; i < series.Length; i++)
        {
            var d = series[i] - mean;
            square += d * d;
            if (i + lag < series.Length)
            {
                product += d * (series[i + lag] - mean);
            }
        }
        return square > 0 ? product / square : double.NaN;
    }

    // One selection's stack and its fidelity against the truth.
    private sealed record StackRow(string Source, string Alignment, double Keep, int Frames, ImmutableArray<BandFidelity> Fidelity);

    // Every source's selections at every keep stacked in one pass over the frames, each frame registered onto the truth once.
    private static async Task<List<StackRow>> StackSelectionsAsync(IPlanetaryFrameStream stream, List<Source> sources, double[] keeps,
        Target truth, CancellationToken ct)
    {
        var size = truth.Size;
        var selections = new List<(Source Source, double Keep, HashSet<int> Frames, double[] Sum)>();
        foreach (var source in sources)
        {
            foreach (var keep in keeps)
            {
                selections.Add((source, keep, [.. Select(source, keep)], new double[size * size]));
            }
        }
        var union = selections.SelectMany(s => s.Frames).Distinct().Order().ToArray();
        // Registered in parallel, in blocks, and summed in frame order, so every sum is the same from run to run.
        const int Block = 64;
        for (var start = 0; start < union.Length; start += Block)
        {
            var block = union.AsMemory(start, Math.Min(Block, union.Length - start));
            var moved = new float[block.Length][];
            await Parallel.ForAsync(0, block.Length, new ParallelOptions { CancellationToken = ct }, async (k, token) =>
            {
                var frame = await stream.LoadAsync(block.Span[k], token);
                try
                {
                    moved[k] = truth.Register(frame.GetChannelSpan(0), frame.Width, frame.Height).Plane;
                }
                finally
                {
                    frame.Release();
                }
            });
            for (var k = 0; k < block.Length; k++)
            {
                var index = block.Span[k];
                foreach (var selection in selections)
                {
                    if (selection.Frames.Contains(index))
                    {
                        var plane = moved[k];
                        for (var i = 0; i < plane.Length; i++)
                        {
                            selection.Sum[i] += plane[i];
                        }
                    }
                }
            }
        }
        var rows = new List<StackRow>();
        foreach (var (source, keep, frames, sum) in selections)
        {
            var mean = new float[sum.Length];
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] = (float)(sum[i] / frames.Count);
            }
            rows.Add(new StackRow(source.Name, "oracle", keep, frames.Count, truth.Fidelity(mean)));
        }
        return rows;
    }

    private void WriteStacks(List<StackRow> stacks, double[] keeps)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var alignment in stacks.Select(s => s.Alignment).Distinct())
        {
            consoleHost.WriteScrollable(alignment == "oracle"
                ? "stacks registered onto the truth by the correlation (the selection alone differs): transfer / error by band"
                : "stacks by the stacker's own aligner on the reference frame: transfer / error by band");
            foreach (var keep in keeps)
            {
                var rows = stacks.Where(s => s.Alignment == alignment && s.Keep == keep).ToArray();
                if (rows.Length == 0)
                {
                    continue;
                }
                consoleHost.WriteScrollable(string.Create(inv, $"  keep {keep:0.###} ({rows[0].Frames} frames)"));
                foreach (var row in rows)
                {
                    consoleHost.WriteScrollable("    " + row.Source.PadRight(20) + string.Join("  ", row.Fidelity.Select(f => string.Create(inv, $"{f.Band}: {f.Transfer:0.000} / {f.Error:0.000}"))));
                }
            }
        }
    }

    private static string FramesCsv(List<Source> sources, FrameScores[] scores, double[]? strehl)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder("frame,dx,dy," + string.Join(",", sources.Select(s => s.Name))
            + string.Concat(scores[0].Truth.Select(band => $",truth-error{band.Band}")) + "\n");
        for (var i = 0; i < scores.Length; i++)
        {
            text.Append(inv, $"{i},{scores[i].Dx:0.0000},{scores[i].Dy:0.0000}");
            foreach (var source in sources)
            {
                text.Append(inv, $",{source.Score[i]:G6}");
            }
            foreach (var band in scores[i].Truth)
            {
                text.Append(inv, $",{band.Error:G6}");
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string StacksCsv(List<StackRow> stacks)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder("source,alignment,keep,frames," + string.Join(",", Enumerable.Range(1, PlanetaryMetrics.Bands).Select(b => $"transfer{b},error{b}")) + "\n");
        foreach (var row in stacks)
        {
            text.Append(inv, $"{row.Source},{row.Alignment},{row.Keep},{row.Frames}");
            foreach (var band in row.Fidelity)
            {
                text.Append(inv, $",{band.Transfer:G6},{band.Error:G6}");
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
