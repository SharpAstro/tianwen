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
/// <c>planetary-inverses</c> (docs/plans/planetary-restoration.md, R8 part 2): a stack restored three regularised ways, a Wiener filter
/// with Conan's power-law prior, Richardson-Lucy with positivity at the sky, and an L1-L2 edge-preserving prior, each with the limb's
/// measured kernel and with a single Gaussian of its width, every one set to the same band 3 transfer (1.00 against a twin's truth, or
/// a given rise over the stack on a real capture) and scored on the limb's undershoot.
/// </summary>
internal sealed class PlanetaryInversesSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 4;

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): band 3 set to 1.00 against it, and the scores." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil whose diffraction a kernel is divided by.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk the restorations are made in, px.", DefaultValueFactory = _ => 256 };
        var liftOpt = new Option<double?>("--band3-lift") { Description = "Without a truth: the rise of band 3 over the stack each inverse is set to (what the twin's own setting gave)." };
        var maxStepsOpt = new Option<int>("--max-steps") { Description = "Richardson-Lucy's most steps.", DefaultValueFactory = _ => 80 };
        var gaussianBandOpt = new Option<int?>("--gaussian-band") { Description = "Fit the single Gaussian's width on this band alone (1 to 4) rather than on bands 1 to 4: one whose band 3 transfer is (b')'s can be set to band 3 at all." };

        var command = new Command("planetary-inverses",
            "A stack restored by Wiener with Conan's power-law prior, Richardson-Lucy with positivity at the sky and an L1-L2 edge-preserving prior, each with the limb's kernel and a single Gaussian of its width, all set to one band 3 transfer and scored on the limb (R8 part 2).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, liftOpt, maxStepsOpt, gaussianBandOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var size = parseResult.GetValue(windowOpt);
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            using var stream = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
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

                // Everything in a window about the disk.
                var (originX, originY) = ((int)Math.Round(stack.Disk.X) - (size / 2), (int)Math.Round(stack.Disk.Y) - (size / 2));
                var disk = stack.Disk with { X = stack.Disk.X - originX, Y = stack.Disk.Y - originY };
                var stackWindow = Crop(stack.Plane, width, height, originX, originY, size);
                var truthWindow = truth is { } tr ? Crop(PlanetaryMetrics.Normalise(tr.Plane, width, height, stack.Disk), width, height, originX, originY, size) : null;

                var scale = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;
                var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                var diffraction = PlanetaryInverse.Diffraction(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9, scale);
                Func<double, double> measured = f => diffraction.At(f) is var d && d > 0.02 ? Math.Clamp(wide.TransferAt(f) / d, 0, 1) : 0;
                // The single Gaussian whose band transfers on this stack are (b')'s.
                var bprimeTransfers = PlanetaryMetrics.Fidelity(PlanetaryInverse.Apply(stackWindow, size, size, measured), stackWindow, size, size, disk, Bands)
                    .Select(b => b.Transfer).ToImmutableArray();
                var gaussianBand = parseResult.GetValue(gaussianBandOpt);
                var sigma = PlanetaryBlurProbes.EquivalentGaussianSigma(bprimeTransfers, stackWindow, size, size, disk, gaussianBand ?? 1, gaussianBand ?? Bands);
                Func<double, double> gaussian = f => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);
                var noise = PlanetaryInverse.WhiteNoise(stackWindow, size, size);
                var pixelNoise = Math.Sqrt(noise / (size * size));
                var delta = pixelNoise * Math.Sqrt(2);

                consoleHost.WriteScrollable(string.Create(inv,
                    $"{Path.GetFileName(input)}: {result.FramesUsed} of {result.FramesGraded} frames by the gradient; {scale:0.0000}\"/px; (b') core {wide.CoreSigma:0.00} px with {wide.WingFraction:P1} in a {wide.WingScale:0.00} px wing, its equivalent Gaussian {sigma:0.000} px (bands {gaussianBand ?? 1} to {gaussianBand ?? Bands}); the stack's noise {pixelNoise:0.0000} of the disk a pixel"));

                double Band3(float[] restored) => truthWindow is { } tw
                    ? PlanetaryMetrics.Fidelity(restored, tw, size, size, disk, Bands)[2].Transfer
                    : PlanetaryMetrics.Fidelity(restored, stackWindow, size, size, disk, Bands)[2].Transfer;
                double target;
                if (truthWindow is { } truthPlane)
                {
                    target = 1.0;
                    var stackBands = PlanetaryMetrics.Fidelity(stackWindow, truthPlane, size, size, disk, Bands);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    the stack: transfer {string.Join(", ", stackBands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", stackBands.Select(b => b.Error.ToString("0.000", inv)))} (sum {stackBands.Sum(b => b.Error):0.000}); undershoot {PlanetaryMetrics.LimbUndershoot(stackWindow, size, size, disk):0.0000}; band 3 lift to 1.00: {1 / stackBands[2].Transfer:0.0000}"));
                }
                else if (parseResult.GetValue(liftOpt) is { } lift)
                {
                    target = lift;
                    consoleHost.WriteScrollable(string.Create(inv, $"    the stack: undershoot {PlanetaryMetrics.LimbUndershoot(stackWindow, size, size, disk):0.0000}; band 3 set to rise {lift:0.0000} over it"));
                }
                else
                {
                    consoleHost.WriteError($"{input}: without a truth, pass --band3-lift");
                    return 1;
                }

                var undershoots = new Dictionary<string, double>();
                foreach (var (kernelName, transfer) in new[] { ("(b')", measured), ("Gaussian", gaussian) })
                {
                    var (amplitude, exponent) = PlanetaryInverse.PowerLawPrior(stackWindow, size, size, transfer, noise);
                    consoleHost.WriteScrollable(string.Create(inv, $"    {kernelName}: the object's power law, A {amplitude:G3}, p {exponent:0.000}"));

                    // Each inverse's knob set so band 3 reaches the target, then scored.
                    var wienerScale = Bisect(Math.Log(1e-4), Math.Log(1e4), s => Band3(PlanetaryInverse.WienerPowerLaw(stackWindow, size, size, transfer, amplitude, exponent, noise, Math.Exp(s))), target, decreasing: true);
                    var wiener = PlanetaryInverse.WienerPowerLaw(stackWindow, size, size, transfer, amplitude, exponent, noise, Math.Exp(wienerScale));
                    Report($"Wiener with the power law, {kernelName}", $"noise scale {Math.Exp(wienerScale):G3}", wiener);

                    var (steps, rl) = RichardsonLucyTo(stackWindow, size, transfer, parseResult.GetValue(maxStepsOpt), Band3, target);
                    Report($"Richardson-Lucy at the sky, {kernelName}", $"{steps} steps", rl);

                    var logMu = Bisect(Math.Log(1e-7), Math.Log(10), m => Band3(PlanetaryInverse.L1L2(stackWindow, size, size, transfer, Math.Exp(m), delta)), target, decreasing: true, iterations: 14);
                    var l1l2 = PlanetaryInverse.L1L2(stackWindow, size, size, transfer, Math.Exp(logMu), delta);
                    Report($"L1-L2, {kernelName}", $"mu {Math.Exp(logMu):G3}, delta {delta:G3}", l1l2);
                }

                // The claims, as pre-registered.
                double U(string name) => undershoots[name];
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    with (b'), the undershoot over the Wiener's: Richardson-Lucy {U("Richardson-Lucy at the sky, (b')") / U("Wiener with the power law, (b')"):0.000}, L1-L2 {U("L1-L2, (b')") / U("Wiener with the power law, (b')"):0.000}"));
                foreach (var method in new[] { "Wiener with the power law", "Richardson-Lucy at the sky", "L1-L2" })
                {
                    consoleHost.WriteScrollable(string.Create(inv, $"    {method}: the Gaussian's undershoot over (b')'s {U($"{method}, Gaussian") / U($"{method}, (b')"):0.000}"));
                }
                return 0;

                void Report(string name, string knob, float[] restored)
                {
                    var undershoot = PlanetaryMetrics.LimbUndershoot(restored, size, size, disk);
                    undershoots[name] = undershoot;
                    if (truthWindow is { } tw)
                    {
                        var bands = PlanetaryMetrics.Fidelity(restored, tw, size, size, disk, Bands);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"    {name} ({knob}): transfer {string.Join(", ", bands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} (sum {bands.Sum(b => b.Error):0.000}); undershoot {undershoot:0.0000}"));
                    }
                    else
                    {
                        var gains = PlanetaryMetrics.Fidelity(restored, stackWindow, size, size, disk, Bands);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"    {name} ({knob}): rise over the stack {string.Join(", ", gains.Select(b => b.Transfer.ToString("0.000", inv)))}; undershoot {undershoot:0.0000}"));
                    }
                }
            }
            finally
            {
                stackImage.Release();
            }
        });
        return command;
    }

    // The knob in [lo, hi] (a log, bisected) at which `band3` meets `target`, band 3 falling as the knob rises when `decreasing`.
    private static double Bisect(double lo, double hi, Func<double, double> band3, double target, bool decreasing, int iterations = 24)
    {
        for (var i = 0; i < iterations; i++)
        {
            var mid = (lo + hi) / 2;
            var above = band3(mid) > target;
            if (above == decreasing)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) / 2;
    }

    // Richardson-Lucy with positivity at the sky (lifted a thousandth only so a division is defined), stopped at the first step whose
    // band 3 reaches the target, or the last.
    private static (int Steps, float[] Plane) RichardsonLucyTo(float[] plane, int size, Func<double, double> transfer, int maxSteps, Func<float[], double> band3, double target)
    {
        float[]? reached = null;
        var (at, last) = (maxSteps, plane);
        PlanetaryInverse.RichardsonLucy(plane, size, size, transfer, maxSteps, (step, p) =>
        {
            last = p;
            if (reached is null && band3(p) >= target)
            {
                (reached, at) = (p, step);
            }
        }, offset: 1e-3);
        return (at, reached ?? last);
    }

    private static float[] Crop(float[] plane, int width, int height, int originX, int originY, int size)
    {
        var window = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var fy = originY + y;
            for (var x = 0; x < size; x++)
            {
                var fx = originX + x;
                if (fx >= 0 && fy >= 0 && fx < width && fy < height)
                {
                    window[(y * size) + x] = plane[(fy * width) + fx];
                }
            }
        }
        return window;
    }
}
