using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-keeps</c> (docs/plans/planetary-restoration.md, "R4 keeps are scored after restoration", #1083): R4's keeps judged where
/// they are used. On a synthetic capture whose every frame's PSF is known (<c>planetary-degrade --psf-truth</c>), the frames are summed at
/// their true shifts at each keep (ranked by the gradient as the stacker ranks them) and scored raw and restored by the oracle Wiener with
/// the sum's own transfer; beside them the matched weights (each frame by its true transfer), Fourier burst accumulation's exponent sweep,
/// and the ceiling of per-frequency selection over whole-frame selection in band 1.
/// </summary>
internal sealed class PlanetaryKeepsSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 4;

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A synthetic mono SER capture (planetary-degrade --psf-truth)." };
        var truthOpt = new Option<string>("--truth") { Description = "Its truth (planetary-degrade's .truth.fits).", Required = true };
        var psfOpt = new Option<string?>("--psf") { Description = "Its frames' PSFs (planetary-degrade --psf-truth's .psf; beside the capture by default)." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var windowOpt = new Option<int>("--window") { Description = "The window about the disk, px, a power of two.", DefaultValueFactory = _ => 256 };
        var keepsOpt = new Option<string>("--keeps") { Description = "The keeps, shares of the frames by the gradient, a comma list.", DefaultValueFactory = _ => "0.01,0.02,0.05,0.1,0.2,0.5,1" };
        var fbaOpt = new Option<string>("--fba") { Description = "Fourier burst accumulation's exponents, a comma list (0 is a plain average).", DefaultValueFactory = _ => "0,1,2,4,8,11" };
        var ceilingKeepOpt = new Option<double>("--ceiling-keep") { Description = "The share of the frames the per-frequency ceiling keeps at each frequency.", DefaultValueFactory = _ => 0.05 };

        var command = new Command("planetary-keeps",
            "R4's keeps scored after restoration on a twin with known PSFs: each keep raw and restored, matched weights, Fourier burst accumulation and the ceiling of per-frequency selection (#1083).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, psfOpt, planetOpt, windowOpt, keepsOpt, fbaOpt, ceilingKeepOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var windowSize = parseResult.GetValue(windowOpt);
            double[] Numbers(string? list) => [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, inv))];
            var keeps = Numbers(parseResult.GetValue(keepsOpt));
            var exponents = Numbers(parseResult.GetValue(fbaOpt));
            if (PlanetaryMeasureSubCommand.ReadTruth(parseResult.GetValue(truthOpt) ?? "", consoleHost) is not { } truth)
            {
                return 1;
            }
            var psfPath = parseResult.GetValue(psfOpt) ?? SyntheticPsfFile.PathFor(input);
            using var psfs = SyntheticPsfFile.Reader.Open(psfPath);
            if (psfs is null)
            {
                consoleHost.WriteError($"{psfPath}: not a PSF file (planetary-degrade --psf-truth writes one)");
                return 1;
            }

            using var reader = SerReader.Open(input);
            using var stream = new SerFrameStream(reader, ownsReader: false);
            if (stream.MidCapture is not { } when)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            var (width, height) = (reader.Width, reader.Height);
            var disk = truth.Disk with { AxisRatio = limbOptions.AxisRatio };
            var truthPlane = PlanetaryMetrics.Normalise(truth.Plane, width, height, disk);

            // Each frame's rank by the gradient, as the stacker ranks them.
            var grades = await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(stream, cancellationToken: ct);
            var rank = new int[grades.Length];
            var sorted = FrameGrader.SortByQuality(grades);
            for (var r = 0; r < sorted.Length; r++)
            {
                rank[sorted[r].Index] = r;
            }
            int Count(double share) => Math.Max(1, (int)Math.Round(share * grades.Length));

            var header = psfs.Header;
            var bound = new MultiFrameBound(header, windowSize, (int)Math.Round(disk.X) - (windowSize / 2), (int)Math.Round(disk.Y) - (windowSize / 2));
            var prior = bound.Prior(truth.Plane, width, height);
            var keepSums = keeps.Select(k => (Keep: k, Count: Count(k), Sums: bound.NewSums())).ToArray();
            var matched = bound.NewWeightedSums((frame, i) => frame.Transfer[i].Magnitude);
            var fba = exponents.Select(p => (P: p, Sums: bound.NewWeightedSums((frame, i) => Math.Pow(frame.Spectrum[i].Magnitude, p)))).ToArray();
            var ceilingCount = Count(parseResult.GetValue(ceilingKeepOpt));
            var ceiling = bound.NewSelectionCeiling(0.25, 0.5, ceilingCount);

            var samples = new ushort[width * height];
            var psf = new double[header.PsfGrid * header.PsfGrid];
            for (var i = 0; i < reader.FrameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!psfs.TryRead(psf, out var shiftX, out var shiftY, out var brightness))
                {
                    consoleHost.WriteError($"{psfPath}: holds {i} frames, the capture {reader.FrameCount}");
                    return 1;
                }
                reader.ReadFrame16(i, samples);
                var frame = bound.Prepare(samples, width, height, psf, shiftX, shiftY, brightness);
                var back = bound.BackShift(frame);
                foreach (var (_, count, sums) in keepSums)
                {
                    if (rank[i] < count)
                    {
                        sums.Add(frame, back);
                    }
                }
                matched.Add(frame, back);
                foreach (var (_, sums) in fba)
                {
                    sums.Add(frame, back);
                }
                ceiling.Add(frame, rank[i] < ceilingCount);
            }

            float[] Restored(System.Numerics.Complex[] spectrum) => PlanetaryMetrics.Normalise(bound.ToDetector(spectrum, width, height), width, height, disk);
            BandRow Score(float[] plane) => new BandRow(PlanetaryMetrics.Fidelity(plane, truthPlane, width, height, disk, Bands));
            void Print(string name, BandRow row) => consoleHost.WriteScrollable(string.Create(inv,
                $"    {name}: transfer {string.Join(", ", row.Bands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", row.Bands.Select(b => b.Error.ToString("0.000", inv)))} (sum {row.Sum:0.000})"));

            consoleHost.WriteScrollable(string.Create(inv, $"{Path.GetFileName(input)}: {grades.Length} frames, a {windowSize} px window; each keep summed at the true shifts, raw and restored by the oracle Wiener"));
            var raw = new Dictionary<double, BandRow>();
            var restored = new Dictionary<double, BandRow>();
            foreach (var (keep, count, sums) in keepSums)
            {
                raw[keep] = Score(Restored(sums.Sum()));
                restored[keep] = Score(Restored(sums.ShiftAndAdd(prior)));
                Print(string.Create(inv, $"keep {keep:P0} ({count} frames), raw     "), raw[keep]);
                Print(string.Create(inv, $"keep {keep:P0} ({count} frames), restored"), restored[keep]);
            }
            var matchedRow = Score(Restored(matched.Restored(prior)));
            Print("matched weights, every frame, restored", matchedRow);
            foreach (var (p, sums) in fba)
            {
                Print(string.Create(inv, $"Fourier burst accumulation, p = {p:0.#}, restored"), Score(Restored(sums.Restored(prior))));
            }

            // The claims, as pre-registered with #1083.
            var bestRaw = raw.MinBy(kv => kv.Value.Bands[0].Error).Key;
            var bestRestored = restored.MinBy(kv => kv.Value.Bands[0].Error).Key;
            consoleHost.WriteScrollable(string.Create(inv,
                $"    band 1's best keep: raw {bestRaw:P0}, restored {bestRestored:P0} (claimed: half the frames or more once restored; {(bestRestored >= 0.5 ? "holds" : "FAILS")})"));
            var ratio = ceiling.Ratio;
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the ceiling of per-frequency selection in band 1, the best {ceilingCount} frames at each frequency over the best {ceilingCount} by the gradient: {ratio:0.000} ({ratio - 1:P1}; claimed under 15 %; {(ratio - 1 < 0.15 ? "holds" : "FAILS")})"));
            // Post hoc, not pre-registered: the same over the whole frames truly sharpest in band 1, which leaves out the gradient's ranking error.
            consoleHost.WriteScrollable(string.Create(inv,
                $"    post hoc: over the best {ceilingCount} whole frames by their true band 1 transfer: {ceiling.OverTheSharpestWholeFrames:0.000}"));
            return 0;
        });
        return command;
    }

    // One scored plane's bands, and their errors' sum.
    private sealed record BandRow(System.Collections.Immutable.ImmutableArray<BandFidelity> Bands)
    {
        public double Sum => Bands.Sum(b => b.Error);
    }
}
