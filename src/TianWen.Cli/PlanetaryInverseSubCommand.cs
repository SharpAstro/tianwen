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
/// <c>planetary inverse</c> (docs/plans/planetary-restoration.md, R7 part 4): a stack restored by Richardson-Lucy with three kernels, the
/// oracle (the stack's own transfer against a twin's truth) and the limb's two (part 3's (b') and (b), each divided by the pupil's
/// diffraction transfer, since a limb kernel is the total blur and the truth is rendered through the diffraction limit), scored per band
/// against the truth and on the limb's undershoot. Without a truth, (b') alone at a given count, its undershoot the one check.
/// </summary>
internal sealed class PlanetaryInverseSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 4;

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary degrade's .truth.fits): the oracle and the scores." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var planeOpt = new Option<string?>("--plane") { Description = "A colour capture's photosite colour: r, g, g2 or b." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil whose diffraction a limb kernel is divided by.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var iterationsOpt = new Option<int>("--iterations") { Description = "Richardson-Lucy's steps: the most tried with a truth, the count used without one.", DefaultValueFactory = _ => 40 };

        var command = new Command("inverse",
            "A stack restored by Richardson-Lucy with the oracle's kernel and the limb's two, each limb kernel divided by the pupil's diffraction, scored per band against a twin's truth and on the limb's undershoot (R7 part 4).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, planeOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, iterationsOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            using var window = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
            var plane = PlanetaryGeometrySubCommands.ParsePlane(parseResult.GetValue(planeOpt));
            if (window.Layout == PlanetaryFrameLayout.SplitCfa && plane is null)
            {
                consoleHost.WriteError($"{input}: a colour capture; pass --plane r, g, g2 or b");
                return 1;
            }
            using var planeStream = plane is { } channel && window.Layout == PlanetaryFrameLayout.SplitCfa ? new CfaPlaneStream(window, channel) : null;
            IPlanetaryFrameStream stream = planeStream is null ? window : planeStream;
            if (stream.MidCapture is not { } when)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var aspect = PhysicalEphemeris.Compute(planet, when);
            var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
            var options = new PlanetaryStackOptions
            {
                KeepFraction = parseResult.GetValue(keepOpt),
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                QualityEstimator = new GradientEnergyEstimator(),
            };
            var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
            var stackImage = result.Master;
            try
            {
                var (width, height) = (stackImage.Width, stackImage.Height);
                var truth = parseResult.GetValue(truthOpt) is { } truthPath ? PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost) : null;
                if (parseResult.GetValue(truthOpt) is not null && truth is null)
                {
                    return 1;
                }
                MetricDisk? onto = truth is { } t ? t.Disk with { AxisRatio = limbOptions.AxisRatio } : null;
                if (PlanetaryMeasureSubCommand.Register(stackImage, limbOptions, onto) is not { } stack
                    || PlanetaryLimbFit.Fit(stackImage, limbOptions) is not { } fit
                    || PlanetaryLimbKernel.Fit(stackImage, fit, limbOptions) is not { } wide)
                {
                    consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                    return 1;
                }
                var disk = stack.Disk;
                var truthPlane = truth is { } tr ? PlanetaryMetrics.Normalise(tr.Plane, width, height, disk) : null;
                var scale = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;
                var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                var diffraction = PlanetaryInverse.Diffraction(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9, scale);
                // A limb kernel over the pupil's own transfer: what is left to restore toward the diffraction limit. Past the cutoff there
                // is nothing to restore, and the ratio is kept at most one.
                Func<double, double> OverDiffraction(Func<double, double> total) => f => diffraction.At(f) is var d && d > 0.02 ? Math.Clamp(total(f) / d, 0, 1) : 0;
                static double Gaussian(double sigma, double f) => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);
                var kernels = new List<(string Name, Func<double, double> Transfer)>
                {
                    ("(b') limb, core with the scatter's wing", OverDiffraction(f => wide.TransferAt(f))),
                    ("(b) limb, two Gaussians", OverDiffraction(f => ((1 - fit.HaloFraction) * Gaussian(fit.PsfSigma, f)) + (fit.HaloFraction * Gaussian(fit.HaloWidth, f)))),
                };
                if (truthPlane is not null)
                {
                    var measuredTransfer = PlanetaryInverse.Measure(stack.Plane, truthPlane, width, height);
                    kernels.Insert(0, ("the oracle, the stack's own transfer", measuredTransfer.At));
                }
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{Path.GetFileName(input)}: {result.FramesUsed} of {result.FramesGraded} frames by the gradient; {scale:0.0000}\"/px; limb (b) core {fit.PsfSigma:0.00} px with {fit.HaloFraction:P1} in {fit.HaloWidth:0.00} px, (b') core {wide.CoreSigma:0.00} px with {wide.WingFraction:P1} in a {wide.WingScale:0.00} px wing"));
                consoleHost.WriteScrollable(string.Create(inv, $"    the kernels at 0.1, 0.2, 0.3 c/px: {string.Join("; ", kernels.Select(k => $"{k.Name.Split(',')[0]} {k.Transfer(0.1):0.000} {k.Transfer(0.2):0.000} {k.Transfer(0.3):0.000}"))}"));

                var iterations = parseResult.GetValue(iterationsOpt);
                if (truthPlane is null)
                {
                    // A real capture: (b') alone at the count given, what it did to each band and to the limb.
                    float[]? restored = null;
                    PlanetaryInverse.RichardsonLucy(stack.Plane, width, height, kernels[0].Transfer, iterations, (step, p) => restored = step == iterations ? p : restored);
                    var gains = PlanetaryMetrics.Fidelity(restored ?? stack.Plane, stack.Plane, width, height, disk, Bands).Select(f => f.Transfer).ToArray();
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    RL with (b') at {iterations} steps: its gain over the stack in bands 1 to {Bands} {string.Join(", ", gains.Select(g => g.ToString("0.000", inv)))}; the limb's undershoot {PlanetaryMetrics.LimbUndershoot(stack.Plane, width, height, disk):0.0000} before, {PlanetaryMetrics.LimbUndershoot(restored ?? stack.Plane, width, height, disk):0.0000} after"));
                    return 0;
                }

                var stackTransfer = PlanetaryMetrics.Fidelity(stack.Plane, truthPlane, width, height, disk, Bands).Select(f => f.Transfer).ToArray();
                var stackUndershoot = PlanetaryMetrics.LimbUndershoot(stack.Plane, width, height, disk);
                // Every kernel's Richardson-Lucy, step by step: its transfer and error in each band, and the limb's undershoot.
                var runs = new List<(string Name, List<(double[] Transfer, double[] Error, double Undershoot)> Steps)>();
                foreach (var (name, transfer) in kernels)
                {
                    var steps = new List<(double[], double[], double)>();
                    PlanetaryInverse.RichardsonLucy(stack.Plane, width, height, transfer, iterations, (_, p) =>
                    {
                        var fidelity = PlanetaryMetrics.Fidelity(p, truthPlane, width, height, disk, Bands);
                        steps.Add(([.. fidelity.Select(f => f.Transfer)], [.. fidelity.Select(f => f.Error)], PlanetaryMetrics.LimbUndershoot(p, width, height, disk)));
                    });
                    runs.Add((name, steps));
                }
                var oracleSteps = runs[0].Steps;
                var best = Enumerable.Range(0, oracleSteps.Count).MinBy(i => oracleSteps[i].Error.Sum());
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the stack: transfer in bands 1 to {Bands} {string.Join(", ", stackTransfer.Select(v => v.ToString("0.000", inv)))}, undershoot {stackUndershoot:0.0000}; the oracle's best count {best + 1} of {iterations} steps"));
                var oracle = oracleSteps[best];
                foreach (var (name, steps) in runs)
                {
                    var at = steps[best];
                    var ratios = Enumerable.Range(0, Bands).Select(b => (at.Transfer[b] - stackTransfer[b]) / (oracle.Transfer[b] - stackTransfer[b])).ToArray();
                    var gate = at.Undershoot <= 1.25 * oracle.Undershoot && at.Undershoot < 0.05;
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    RL, {name}: transfer {string.Join(", ", at.Transfer.Select(v => v.ToString("0.000", inv)))}; error {string.Join(", ", at.Error.Select(v => v.ToString("0.000", inv)))}; of the oracle's gain {string.Join(", ", ratios.Select(v => v.ToString("0.00", inv)))}; undershoot {at.Undershoot:0.0000} ({(gate ? "passes" : "fails")} the gate)"));
                }

                // Wiener with (b') at the band 3 transfer the oracle's RL reaches, by a bisection on the log of its noise-to-signal.
                var target = oracle.Transfer[2];
                var (lo, hi) = (Math.Log(1e-6), Math.Log(10.0));
                float[] wiener = stack.Plane;
                for (var i = 0; i < 30; i++)
                {
                    var mid = (lo + hi) / 2;
                    wiener = PlanetaryInverse.Wiener(stack.Plane, width, height, kernels[1].Transfer, Math.Exp(mid));
                    var band3 = PlanetaryMetrics.Fidelity(wiener, truthPlane, width, height, disk, Bands)[2].Transfer;
                    (lo, hi) = band3 > target ? (mid, hi) : (lo, mid);
                }
                var w = PlanetaryMetrics.Fidelity(wiener, truthPlane, width, height, disk, Bands);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    Wiener with (b') at the oracle RL's band 3 transfer (noise-to-signal {Math.Exp((lo + hi) / 2):G3}): transfer {string.Join(", ", w.Select(f => f.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", w.Select(f => f.Error.ToString("0.000", inv)))}; undershoot {PlanetaryMetrics.LimbUndershoot(wiener, width, height, disk):0.0000}"));
                return 0;
            }
            finally
            {
                stackImage.Release();
            }
        });
        return command;
    }
}
