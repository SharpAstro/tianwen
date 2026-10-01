using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-ceilings</c> (docs/plans/planetary-restoration.md, R8 part 1): on a synthetic capture whose truth and every frame's PSF
/// are known, what a sharpening of its stack could reach at best. On the stack: the best real gain per a trous band, the best isotropic
/// linear filter, and the truth's Fourier magnitude or phase swapped in. Over the frames: shift-and-add at the true shifts restored by
/// its own true transfer, against the multi-frame Wiener that weights each frame per frequency, on the stack's frames and on all of
/// them. Each is scored per band against the truth and on the limb's undershoot.
/// </summary>
internal sealed class PlanetaryCeilingsSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 4;

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A synthetic mono SER capture (planetary-degrade --psf-truth)." };
        var truthOpt = new Option<string>("--truth") { Description = "Its truth (planetary-degrade's .truth.fits).", Required = true };
        var psfOpt = new Option<string?>("--psf") { Description = "Its frames' PSFs (planetary-degrade --psf-truth's .psf; beside the capture by default)." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk the multi-frame bound is computed on, px, a power of two.", DefaultValueFactory = _ => 256 };

        var command = new Command("planetary-ceilings",
            "What a sharpening of a synthetic capture's stack could reach at best: per-band and per-ring oracle filters, the oracle swaps, and the multi-frame Wiener with every frame's true PSF against shift-and-add at the true shifts (R8 part 1).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, psfOpt, planetOpt, keepOpt, windowOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var keep = parseResult.GetValue(keepOpt);
            var windowSize = parseResult.GetValue(windowOpt);
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

            // The stack, as R7 part 4 made it, and the frames it kept, graded the same way.
            var options = new PlanetaryStackOptions
            {
                KeepFraction = keep,
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                QualityEstimator = new GradientEnergyEstimator(),
            };
            var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
            var grades = await new FrameGrader(options.QualityEstimator).GradeAllAsync(stream, cancellationToken: ct);
            var kept = FrameGrader.SelectBest(grades, keep).ToHashSet();
            var rows = new List<(string Name, float[] Plane)>();
            try
            {
                if (PlanetaryMeasureSubCommand.Register(result.Master, limbOptions, disk) is not { } stack)
                {
                    consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                    return 1;
                }
                consoleHost.WriteScrollable(string.Create(inv, $"{Path.GetFileName(input)}: {result.FramesUsed} of {result.FramesGraded} frames by the gradient ({kept.Count} graded again)"));
                rows.Add(("the truth itself", truthPlane));
                rows.Add(("the stack", stack.Plane));
                var (perBand, gains) = PlanetaryCeilings.PerBandOracle(stack.Plane, truthPlane, width, height, disk, Bands);
                consoleHost.WriteScrollable(string.Create(inv, $"    the per-band oracle's gains, bands 1 to {Bands}: {string.Join(", ", gains.Select(g => g.ToString("0.000", inv)))}"));
                rows.Add(("(1) per-band oracle gains", perBand));
                var (joint, jointGains) = PlanetaryCeilings.PerBandJointOracle(stack.Plane, truthPlane, width, height, disk, Bands);
                consoleHost.WriteScrollable(string.Create(inv, $"    the per-band gains fitted jointly, bands 1 to {Bands}: {string.Join(", ", jointGains.Select(g => g.ToString("0.000", inv)))}"));
                rows.Add(("(1') per-band gains fitted jointly", joint));
                rows.Add(("(2) per-ring oracle filter", PlanetaryCeilings.PerRingOracle(stack.Plane, truthPlane, width, height)));
                rows.Add(("(3) magnitude swap", PlanetaryCeilings.MagnitudeSwap(stack.Plane, truthPlane, width, height)));
                rows.Add(("(4) phase swap", PlanetaryCeilings.PhaseSwap(stack.Plane, truthPlane, width, height)));
            }
            finally
            {
                result.Master.Release();
            }

            // Every frame in turn through its own transfer: the model's check, and the two gatherings.
            var header = psfs.Header;
            var bound = new MultiFrameBound(header, windowSize, (int)Math.Round(disk.X) - (windowSize / 2), (int)Math.Round(disk.Y) - (windowSize / 2));
            var truthWindow = bound.Window(truth.Plane, width, height);
            var prior = bound.Prior(truth.Plane, width, height);
            var check = bound.NewModelCheck(truthWindow, Bands);
            var (subset, all) = (bound.NewSums(), bound.NewSums());
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
                check.Add(frame);
                all.Add(frame);
                if (kept.Contains(i))
                {
                    subset.Add(frame);
                }
            }
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the model, each frame against its transfer times the truth: residual over noise in bands 1 to {Bands} {string.Join(", ", check.ResidualOverNoise.Select(r => r.ToString("0.00", inv)))}; the truth's scale {check.Scale:0.0000}, after it {string.Join(", ", check.ScaledResidualOverNoise.Select(r => r.ToString("0.00", inv)))} ({(check.ScaledResidualOverNoise.Skip(1).All(r => r <= 1.5) ? "holds" : "FAILS: the multi-frame claims are not read")})"));

            float[] Restored(System.Numerics.Complex[] spectrum) => PlanetaryMetrics.Normalise(bound.ToDetector(spectrum, width, height), width, height, disk);
            rows.Add(($"5a shift-and-add at the true shifts, {subset.Count} frames, Wiener", Restored(subset.ShiftAndAdd(prior))));
            rows.Add(($"5b multi-frame Wiener, {subset.Count} frames", Restored(subset.MultiFrame(prior))));
            rows.Add(($"5a shift-and-add at the true shifts, {all.Count} frames, Wiener", Restored(all.ShiftAndAdd(prior))));
            rows.Add(($"5b multi-frame Wiener, {all.Count} frames", Restored(all.MultiFrame(prior))));
            rows.Add(($"   shift-and-add at the true shifts, {subset.Count} frames, unrestored", Restored(subset.Sum())));

            var errors = new Dictionary<string, double[]>();
            foreach (var (name, plane) in rows)
            {
                var fidelity = PlanetaryMetrics.Fidelity(plane, truthPlane, width, height, disk, Bands);
                errors[name] = [.. fidelity.Select(f => f.Error)];
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {name}: transfer {string.Join(", ", fidelity.Select(f => f.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", fidelity.Select(f => f.Error.ToString("0.000", inv)))} (sum {fidelity.Sum(f => f.Error):0.000}); undershoot {PlanetaryMetrics.LimbUndershoot(plane, width, height, disk):0.0000}"));
            }

            // The claims, as pre-registered.
            double Sum(string name, int from = 0) => errors[name].Skip(from).Sum();
            var multiGain = 1 - (Sum($"5b multi-frame Wiener, {subset.Count} frames") / Sum($"5a shift-and-add at the true shifts, {subset.Count} frames, Wiener"));
            var allGain = 1 - (Sum($"5b multi-frame Wiener, {all.Count} frames") / Sum($"5a shift-and-add at the true shifts, {all.Count} frames, Wiener"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    multi-frame over shift-and-add, error summed over bands 1 to {Bands}: {multiGain:P1} lower on the stack's {subset.Count} frames, {allGain:P1} on all {all.Count}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    per-band oracle over per-ring oracle, summed error: {Sum("(1) per-band oracle gains") / Sum("(2) per-ring oracle filter"):0.000}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    per-ring oracle over the magnitude swap, error summed over bands 2 to {Bands}: {Sum("(2) per-ring oracle filter", 1) / Sum("(3) magnitude swap", 1):0.000}"));
            return 0;
        });
        return command;
    }
}
