using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-ringing</c> (docs/plans/planetary-restoration.md, R8 follow-up 1): many restorations of twins' stacks, each read for
/// its largest band transfer against the truth, a linear one's composite kernel for its negative mass, and the limb's undershoot,
/// to test that a ring below the sky comes only with a transfer above one; and another program's sharpening fitted as a kernel of its
/// own stack, its residual read by region, then carried to the twins.
/// </summary>
internal sealed class PlanetaryRingingSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 6;

    private sealed record Row(string Twin, string Name, bool Linear, ImmutableArray<double> Transfers, double NegativeMass, double Undershoot)
    {
        public double Largest => Transfers.Max();
    }

    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "Synthetic captures (planetary-degrade's SERs), each with its truth.", Arity = ArgumentArity.OneOrMore };
        var truthsOpt = new Option<string[]>("--truth") { Description = "Each capture's truth (.truth.fits), in order.", AllowMultipleArgumentsPerToken = true, Required = true };
        var gainsOpt = new Option<string[]>("--gains") { Description = "Wavelet gains to read beside the presets, one set per capture in order, sets of a capture split by '/', each name=g1,g2,... (R8's derived gains, say).", AllowMultipleArgumentsPerToken = true };
        var otherStackOpt = new Option<string?>("--other-stack") { Description = "Another program's stack of a real capture (an AutoStakkert TIFF)." };
        var otherSharpenedOpt = new Option<string?>("--other-sharpened") { Description = "That program's sharpened copy, fitted as a kernel of its stack and carried to the twins." };
        var otherUtcOpt = new Option<DateTimeOffset?>("--other-utc") { Description = "When that capture was taken, for its disk's ellipse." };
        var radiusOpt = new Option<int>("--kernel-radius") { Description = "The fitted kernel's half width, taps.", DefaultValueFactory = _ => 7 };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px.", DefaultValueFactory = _ => 256 };

        var command = new Command("planetary-ringing",
            "Whether a ring below the sky comes only with a band transfer above one against the truth, over many restorations of twins' stacks, and another program's sharpening fitted as a kernel (R8 follow-up 1).")
        {
            Arguments = { capturesArg },
            Options = { truthsOpt, gainsOpt, otherStackOpt, otherSharpenedOpt, otherUtcOpt, radiusOpt, planetOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var captures = parseResult.GetValue(capturesArg) ?? [];
            var truths = parseResult.GetValue(truthsOpt) ?? [];
            if (captures.Length != truths.Length)
            {
                consoleHost.WriteError($"{captures.Length} captures and {truths.Length} truths");
                return 1;
            }
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var size = parseResult.GetValue(windowOpt);
            var radius = parseResult.GetValue(radiusOpt);
            var extraGains = parseResult.GetValue(gainsOpt) ?? [];

            // Another program's sharpening, read first: its kernel is carried to every twin.
            (double[] Kernel, int KernelRadius, double DiskRadius)? other = null;
            if (parseResult.GetValue(otherStackOpt) is { } otherStackPath && parseResult.GetValue(otherSharpenedOpt) is { } otherSharpenedPath)
            {
                if (parseResult.GetValue(otherUtcOpt) is not { } otherUtc)
                {
                    consoleHost.WriteError("--other-utc is needed with --other-stack");
                    return 1;
                }
                if (OtherKernel(otherStackPath, otherSharpenedPath, planet, otherUtc, radius) is not { } fitted)
                {
                    return 1;
                }
                other = fitted;
            }

            var rows = new List<Row>();
            for (var c = 0; c < captures.Length; c++)
            {
                var gainSets = c < extraGains.Length ? ParseGains(extraGains[c]) : [];
                if (await TwinAsync(captures[c], truths[c], gainSets, other, planet, size, parseResult.GetValue(keepOpt),
                    parseResult.GetValue(telescopeOpt) ?? "newtonian", parseResult.GetValue(wavelengthOpt), rows, ct) is not 0)
                {
                    return 1;
                }
            }

            // The claims, as pre-registered.
            var breaches = rows.Where(r => r.Undershoot > 0.003 && r.Largest <= 1.02).Select(r => $"{r.Twin}: {r.Name}")
                .Concat(rows.Where(r => r.Largest <= 1.00 && r.Undershoot > 0.001).Select(r => $"{r.Twin}: {r.Name}")).ToList();
            consoleHost.WriteScrollable(string.Create(inv,
                $"claim 1, a ring only with a transfer above one: {(breaches.Count == 0 ? "holds" : "FAILS on " + string.Join("; ", breaches))} ({rows.Count} restorations)"));
            var linear = rows.Where(r => r.Linear && double.IsFinite(r.NegativeMass)).ToArray();
            var rho = StatisticsHelper.Spearman([.. linear.Select(r => r.NegativeMass)], [.. linear.Select(r => r.Undershoot)]);
            consoleHost.WriteScrollable(string.Create(inv,
                $"claim 2, the composite's negative mass ranks the undershoots: Spearman {rho:0.000} over {linear.Length} linear restorations ({(rho >= 0.8 ? "holds" : "FAILS")})"));
            return 0;
        });
        return command;
    }

    // One twin: its stack, every restoration, and a row each.
    private async Task<int> TwinAsync(string input, string truthPath, IReadOnlyList<(string Name, double[] Gains)> gainSets, (double[] Kernel, int KernelRadius, double DiskRadius)? other,
        CatalogIndex planet, int size, double keep, string telescope, double wavelengthNm, List<Row> rows, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var twin = Path.GetFileNameWithoutExtension(input);
        using var reader = SerReader.Open(input);
        using var stream = new SerFrameStream(reader, ownsReader: false);
        if (stream.MidCapture is not { } when)
        {
            consoleHost.WriteError($"{input}: no timestamps");
            return 1;
        }
        var aspect = PhysicalEphemeris.Compute(planet, when);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var options = new PlanetaryStackOptions
        {
            KeepFraction = keep,
            WhitenedCorrelation = false,
            Interpolation = WarpInterpolation.Lanczos3,
            QualityEstimator = new GradientEnergyEstimator(),
        };
        var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
        var stackImage = result.Master;
        var shipped = new List<Image>();
        try
        {
            var (width, height) = (stackImage.Width, stackImage.Height);
            if (PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost) is not { } truth)
            {
                return 1;
            }
            var onto = truth.Disk with { AxisRatio = limbOptions.AxisRatio };
            if (PlanetaryMeasureSubCommand.Register(stackImage, limbOptions, onto) is not { } stack
                || PlanetaryLimbFit.Fit(stackImage, limbOptions) is not { } fit
                || PlanetaryLimbKernel.Fit(stackImage, fit, limbOptions) is not { } wide)
            {
                consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                return 1;
            }
            var own = MetricDisk.From(fit, limbOptions.AxisRatio);
            float[] Registered(ReadOnlySpan<float> plane) =>
                PlanetaryMetrics.Shift(PlanetaryMetrics.Normalise(plane, width, height, own), width, height, stack.Disk.X - own.X, stack.Disk.Y - own.Y);
            var (originX, originY) = ((int)Math.Round(stack.Disk.X) - (size / 2), (int)Math.Round(stack.Disk.Y) - (size / 2));
            var disk = stack.Disk with { X = stack.Disk.X - originX, Y = stack.Disk.Y - originY };
            float[] Window(float[] plane) => PlanetaryInversesSubCommand.Crop(plane, width, height, originX, originY, size);
            var stackWindow = Window(stack.Plane);
            var truthWindow = Window(PlanetaryMetrics.Normalise(truth.Plane, width, height, stack.Disk));

            var scale = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;
            var pupil = telescope.ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
            var diffraction = PlanetaryInverse.Diffraction(pupil, wavelengthNm * 1e-9, scale);
            Func<double, double> measured = f => diffraction.At(f) is var d && d > 0.02 ? Math.Clamp(wide.TransferAt(f) / d, 0, 1) : 0;
            // The stack's true transfer against the truth, ring by ring: the blur a linear filter's composite kernel is read through.
            var trueTransfer = PlanetaryInverse.Measure(stackWindow, truthWindow, size, size);

            consoleHost.WriteScrollable(string.Create(inv, $"{twin}: {result.FramesUsed} of {result.FramesGraded} frames by the gradient; {scale:0.0000}\"/px"));
            void Add(string name, float[] plane, Func<double, double, Complex>? filter)
            {
                var transfers = PlanetaryMetrics.Fidelity(plane, truthWindow, size, size, disk, Bands).Select(f => f.Transfer).ToImmutableArray();
                var negative = filter is null ? double.NaN : PlanetaryKernelFit.NegativeMass(filter, trueTransfer.At);
                var undershoot = PlanetaryMetrics.LimbUndershoot(plane, size, size, disk);
                var row = new Row(twin, name, filter is not null, transfers, negative, undershoot);
                rows.Add(row);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {name}: transfer {string.Join(", ", transfers.Select(t => t.ToString("0.000", inv)))} (largest {row.Largest:0.000}); negative mass {(double.IsNaN(negative) ? "-" : negative.ToString("0.0000", inv))}; undershoot {undershoot:0.0000}"));
            }
            Func<double, double, Complex> Wavelet(double[] gains) => (fx, fy) => PlanetaryKernelFit.WaveletTransfer(gains, fx, fy);

            Add("the stack", stackWindow, (_, _) => Complex.One);
            foreach (var (name, preset) in new[] { ("PlanetaryDefault", WaveletSharpenOptions.PlanetaryDefault), ("Bandpass", WaveletSharpenOptions.Bandpass), ("Combo", WaveletSharpenOptions.Combo) })
            {
                var sharpened = WaveletSharpen.Sharpen(stackImage, preset);
                shipped.Add(sharpened);
                Add($"{name} as shipped", Window(Registered(sharpened.GetChannelSpan(0))), null);
                foreach (var boost in new[] { 0.25, 0.5, 1.0 })
                {
                    var gains = preset.Gains.Select(g => 1 + (boost * (g - 1))).ToArray();
                    Add(string.Create(inv, $"{name} x{boost:0.00}, no thresholds"), PlanetaryWaveletGains.Apply(stackWindow, size, size, gains), Wavelet(gains));
                }
            }
            foreach (var (name, gains) in gainSets)
            {
                Add($"{name} [{string.Join(", ", gains.Select(g => g.ToString("0.00", inv)))}]", PlanetaryWaveletGains.Apply(stackWindow, size, size, gains), Wavelet(gains));
            }
            var (joint, jointGains) = PlanetaryCeilings.PerBandJointOracle(stackWindow, truthWindow, size, size, disk, 4);
            Add($"per-band gains fitted jointly to the truth [{string.Join(", ", jointGains.Select(g => g.ToString("0.00", inv)))}]", joint, Wavelet([.. jointGains]));

            // The Wiener filter with Conan's prior and (b'), at part 2's band 3 match and a decade either side.
            var noise = PlanetaryInverse.WhiteNoise(stackWindow, size, size);
            var (amplitude, exponent) = PlanetaryInverse.PowerLawPrior(stackWindow, size, size, measured, noise);
            double Band3(float[] p) => PlanetaryMetrics.Fidelity(p, truthWindow, size, size, disk, 4)[2].Transfer;
            var matched = Math.Exp(PlanetaryInversesSubCommand.Bisect(Math.Log(1e-4), Math.Log(1e4),
                s => Band3(PlanetaryInverse.WienerPowerLaw(stackWindow, size, size, measured, amplitude, exponent, noise, Math.Exp(s))), 1.0, decreasing: true));
            foreach (var factor in new[] { 0.1, 1.0, 10.0 })
            {
                var s = matched * factor;
                Add(string.Create(inv, $"Wiener with the power law, (b'), noise scale {s:G3}"), PlanetaryInverse.WienerPowerLaw(stackWindow, size, size, measured, amplitude, exponent, noise, s),
                    (fx, fy) => WienerGain(measured, amplitude, exponent, noise, s, Math.Sqrt((fx * fx) + (fy * fy))));
            }

            // Richardson-Lucy at the sky with (b'): not linear, read for its transfers and undershoot alone.
            var steps = new Dictionary<int, float[]>();
            PlanetaryInverse.RichardsonLucy(stackWindow, size, size, measured, 8, (step, p) =>
            {
                if (step is 2 or 4 or 8)
                {
                    steps[step] = (float[])p.Clone();
                }
            }, offset: 1e-3);
            foreach (var (step, plane) in steps.OrderBy(s => s.Key))
            {
                Add($"Richardson-Lucy at the sky, (b'), {step} steps", plane, null);
            }

            if (other is { } o)
            {
                // Its transfer read at our frequencies on its grid: one of our pixels is as many of its as its disk is wider than ours.
                var ratio = o.DiskRadius / fit.EquatorialRadius;
                Func<double, double, Complex> carried = (fx, fy) => PlanetaryKernelFit.TransferAt(o.Kernel, o.KernelRadius, fx / ratio, fy / ratio);
                Add(string.Create(inv, $"AutoStakkert's kernel, carried by {ratio:0.000}"), PlanetaryKernelFit.Filter(stackWindow, size, size, carried), carried);
            }
            return 0;
        }
        finally
        {
            foreach (var image in shipped)
            {
                image.Release();
            }
            stackImage.Release();
        }
    }

    // Another program's sharpening as the kernel of its stack: fitted over the whole frame, its residual read inside 0.9 radii, over
    // 0.9 to 1.3 and past 1.5, and the scale between its grid and the limb fit's.
    private (double[] Kernel, int KernelRadius, double DiskRadius)? OtherKernel(string stackPath, string sharpenedPath, CatalogIndex planet, DateTimeOffset when, int radius)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!Image.TryReadImageFile(stackPath, out var otherStack) || !Image.TryReadImageFile(sharpenedPath, out var otherSharpened))
        {
            consoleHost.WriteError($"{stackPath} or {sharpenedPath}: not readable");
            return null;
        }
        try
        {
            var (w, h) = (otherStack.Width, otherStack.Height);
            var source = otherStack.GetChannelSpan(0).ToArray();
            var target = otherSharpened.GetChannelSpan(0).ToArray();
            var (kernel, constant) = PlanetaryKernelFit.Fit(source, target, w, h, radius);
            var fitted = PlanetaryKernelFit.Apply(source, w, h, kernel, radius, constant);

            // The regions, by the stack's own limb fit (the ephemeris shapes the ellipse; the size is the fit's).
            var options = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            if (PlanetaryLimbFit.Fit(otherStack, options) is not { } fit)
            {
                consoleHost.WriteError($"{stackPath}: the limb could not be fitted");
                return null;
            }
            var disk = MetricDisk.From(fit, options.AxisRatio);
            double Rms(Func<double, bool> region)
            {
                double sum = 0;
                var count = 0;
                for (var y = radius; y < h - radius; y++)
                {
                    for (var x = radius; x < w - radius; x++)
                    {
                        if (region(disk.RadiiAt(x, y)))
                        {
                            var d = target[(y * w) + x] - fitted[(y * w) + x];
                            sum += d * d;
                            count++;
                        }
                    }
                }
                return count > 0 ? Math.Sqrt(sum / count) : double.NaN;
            }
            var (inside, annulus, sky) = (Rms(r => r < 0.9), Rms(r => r >= 0.9 && r < 1.3), Rms(r => r >= 1.5));
            var sumK = kernel.Sum();
            var negative = -kernel.Where(k => k < 0).Sum() / sumK;
            var transfers = new[] { 0.05, 0.1, 0.2, 0.3, 0.4, 0.5 }.Select(f => PlanetaryKernelFit.TransferAt(kernel, radius, f, 0).Real);
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(sharpenedPath)} as a {2 * radius + 1}x{2 * radius + 1} kernel of its stack ({w}x{h}, the disk {disk.Radius:0.0} px): sum {sumK:0.0000}, constant {constant:0.0}, its own negative mass {negative:0.000}; transfer along x at 0.05, 0.1, 0.2, 0.3, 0.4, 0.5 cycles a pixel of its grid {string.Join(", ", transfers.Select(t => t.ToString("0.000", inv)))}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the residual's RMS: inside 0.9 radii {inside:0.00}, 0.9 to 1.3 radii {annulus:0.00} ({annulus / inside:0.00} of inside), past 1.5 radii {sky:0.00} ({sky / inside:0.00}); a linear, shift-invariant filter: {(annulus <= 2 * inside && sky <= 2 * inside ? "yes" : "NO")}"));
            return (kernel, radius, disk.Radius);
        }
        finally
        {
            otherStack.Release();
            otherSharpened.Release();
        }
    }

    private static Complex WienerGain(Func<double, double> transfer, double amplitude, double exponent, double noise, double scale, double f)
    {
        var h = transfer(f);
        var prior = f > 0 ? amplitude * Math.Pow(f, -exponent) : double.PositiveInfinity;
        return f > 0 ? h / ((h * h) + (scale * noise / prior)) : (h > 0 ? 1 / h : 0);
    }

    // "name=g1,g2,.../name2=..." into named gain sets.
    private static List<(string Name, double[] Gains)> ParseGains(string text) =>
        [.. text.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(set =>
        {
            var parts = set.Split('=', 2);
            return (parts[0], parts[1].Split(',').Select(g => double.Parse(g, CultureInfo.InvariantCulture)).ToArray());
        })];
}
