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
using TianWen.Lib.Imaging.Stacking;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-gains</c> (docs/plans/planetary-restoration.md, R8, #1055): the wavelet gains a stack's own power, its two halves'
/// noise, a kernel and the limb fit's disk give (<see cref="PlanetaryWaveletGains"/>), against <c>PlanetaryDefault</c>, <c>Bandpass</c>
/// and <c>Combo</c> as shipped and at the derived gains' noise, scored per band against a twin's truth and on the limb.
/// </summary>
internal sealed class PlanetaryGainsSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    private const int Bands = 4;

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): the scores, and the oracle's kernel." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient (each half its own share of its frames).", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil whose diffraction the limb's kernel is divided by.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px.", DefaultValueFactory = _ => 256 };
        var panelOpt = new Option<string?>("--panel") { Description = "A PNG of the disk: the truth (a twin's), the stack, the derived gains with (b') and the presets as shipped, then the presets at matched noise below." };

        var command = new Command("planetary-gains",
            "Wavelet gains derived from a stack's own power, its halves' noise, the limb's kernel and its disk, against the presets as shipped and at matched noise (R8, #1055).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, panelOpt },
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
            var stacker = new LuckyImagingStacker();
            var result = await stacker.StackGlobalAsync(stream, options, ct);
            using var halfAFrames = PlanetaryFrameSubset.Half(stream, 0);
            using var halfBFrames = PlanetaryFrameSubset.Half(stream, 1);
            var halfA = await stacker.StackGlobalAsync(halfAFrames, options, ct);
            var halfB = await stacker.StackGlobalAsync(halfBFrames, options, ct);
            var stackImage = result.Master;
            var shipped = new List<Image>();
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
                if (PlanetaryMeasureSubCommand.Register(halfA.Master, limbOptions, stack.Disk) is not { } a
                    || PlanetaryMeasureSubCommand.Register(halfB.Master, limbOptions, stack.Disk) is not { } b)
                {
                    consoleHost.WriteError($"{input}: a half's limb could not be fitted");
                    return 1;
                }

                // Everything in a window about the disk, moved as the stack was: normalised on its own disk, then onto the target.
                var own = MetricDisk.From(fit, limbOptions.AxisRatio);
                float[] Registered(ReadOnlySpan<float> plane) =>
                    PlanetaryMetrics.Shift(PlanetaryMetrics.Normalise(plane, width, height, own), width, height, stack.Disk.X - own.X, stack.Disk.Y - own.Y);
                var (originX, originY) = ((int)Math.Round(stack.Disk.X) - (size / 2), (int)Math.Round(stack.Disk.Y) - (size / 2));
                var disk = stack.Disk with { X = stack.Disk.X - originX, Y = stack.Disk.Y - originY };
                float[] Window(float[] plane) => PlanetaryInversesSubCommand.Crop(plane, width, height, originX, originY, size);
                var stackWindow = Window(stack.Plane);
                var (aWindow, bWindow) = (Window(a.Plane), Window(b.Plane));
                var truthWindow = truth is { } tr ? Window(PlanetaryMetrics.Normalise(tr.Plane, width, height, stack.Disk)) : null;

                var scale = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;
                var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                var diffraction = PlanetaryInverse.Diffraction(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9, scale);
                Func<double, double> measured = f => diffraction.At(f) is var d && d > 0.02 ? Math.Clamp(wide.TransferAt(f) / d, 0, 1) : 0;

                // The disk's model: the limb fit's sharp disk, moved as the stack was and taken through the diffraction, as the truth is.
                var diskTarget = PlanetaryInverse.Apply(Window(Registered(PlanetaryLimbFit.SharpModel(fit, limbOptions, width, height))), size, size, diffraction.At);
                var power = PlanetaryWaveletGains.StackPower(stackWindow, size, size, disk);
                var noise = PlanetaryWaveletGains.HalvesNoise(aWindow, bWindow, size, size, disk);
                var white = PlanetaryInverse.WhiteNoise(PlanetaryWaveletGains.Interior(stackWindow, size, size, disk), size, size);
                var whiteNoise = ImmutableArray.CreateRange(Enumerable.Repeat(white, noise.Length));
                ImmutableArray<double> Derive(Func<double, double> kernel, ImmutableArray<double> n) =>
                    PlanetaryWaveletGains.Fit(power, PlanetaryWaveletGains.Wiener(power, n, kernel), diskTarget, PlanetaryInverse.Apply(diskTarget, size, size, kernel), size, size, disk);

                var halfNoise = noise.Skip(noise.Length / 8).Take(noise.Length / 4).Average();
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{Path.GetFileName(input)}: {result.FramesUsed} of {result.FramesGraded} frames by the gradient, halves {halfA.FramesUsed} and {halfB.FramesUsed}; {scale:0.0000}\"/px; (b') core {wide.CoreSigma:0.00} px with {wide.WingFraction:P1} in a {wide.WingScale:0.00} px wing; the halves' noise over the white floor, 0.125 to 0.375 cycles a pixel: {halfNoise / white:0.00}"));

                var inside = Enumerable.Range(0, size * size).Where(i => disk.RadiiAt(i % size, i / size) < ScoredRadii).ToArray();
                var halfDifference = aWindow.Zip(bWindow, (x, y) => (x - y) / 2).ToArray();
                double NoiseOf(ReadOnlySpan<double> gains)
                {
                    var p = PlanetaryWaveletGains.Apply(halfDifference, size, size, gains);
                    return Math.Sqrt(inside.Average(i => (double)p[i] * p[i]));
                }

                var rows = new List<(string Name, float[] Plane)> { ("the stack", stackWindow) };
                var derived = Derive(measured, noise);
                var derivedNoise = NoiseOf(derived.AsSpan());
                rows.Add(($"derived, (b') [{Show(derived)}]", PlanetaryWaveletGains.Apply(stackWindow, size, size, derived.AsSpan())));
                var derivedWhite = Derive(measured, whiteNoise);
                rows.Add(($"derived, (b'), white noise [{Show(derivedWhite)}]", PlanetaryWaveletGains.Apply(stackWindow, size, size, derivedWhite.AsSpan())));
                float[]? jointPlane = null;
                if (truthWindow is { } tw)
                {
                    var oracle = PlanetaryInverse.Measure(stackWindow, tw, size, size);
                    var derivedOracle = Derive(oracle.At, noise);
                    rows.Add(($"derived, the true kernel [{Show(derivedOracle)}]", PlanetaryWaveletGains.Apply(stackWindow, size, size, derivedOracle.AsSpan())));
                    var (joint, jointGains) = PlanetaryCeilings.PerBandJointOracle(stackWindow, tw, size, size, disk, Bands);
                    jointPlane = joint;
                    rows.Add(($"per-band gains fitted jointly to the truth [{Show(jointGains)}]", joint));
                }

                var presets = new[] { ("PlanetaryDefault", WaveletSharpenOptions.PlanetaryDefault), ("Bandpass", WaveletSharpenOptions.Bandpass), ("Combo", WaveletSharpenOptions.Combo) };
                var shippedPlanes = new List<float[]>();
                var matchedPlanes = new List<float[]>();
                foreach (var (name, preset) in presets)
                {
                    var sharpened = WaveletSharpen.Sharpen(stackImage, preset);
                    shipped.Add(sharpened);
                    var shippedPlane = Window(Registered(sharpened.GetChannelSpan(0)));
                    shippedPlanes.Add(shippedPlane);
                    rows.Add(($"{name} as shipped", shippedPlane));

                    // Thresholds dropped and the boost scaled until the halves' half difference carries the derived gains' noise.
                    var boosts = preset.Gains.Select(g => (double)g).ToArray();
                    double[] Scaled(double s) => [.. boosts.Select(g => 1 + (s * (g - 1)))];
                    var (lo, hi) = (0.0, 3.0);
                    for (var i = 0; i < 40; i++)
                    {
                        var mid = (lo + hi) / 2;
                        if (NoiseOf(Scaled(mid)) < derivedNoise)
                        {
                            lo = mid;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }
                    var matched = Scaled((lo + hi) / 2);
                    var matchedPlane = PlanetaryWaveletGains.Apply(stackWindow, size, size, matched);
                    matchedPlanes.Add(matchedPlane);
                    rows.Add(($"{name} at matched noise (boost x{(lo + hi) / 2:0.000}) [{Show(matched)}]", matchedPlane));
                }

                consoleHost.WriteScrollable(string.Create(inv, $"    the noise inside 0.9 radii through the derived gains, (b'): {derivedNoise:G3} of the disk, the stack's {NoiseOf([1, 1, 1, 1, 1, 1]):G3}"));
                var scores = new Dictionary<string, (double Error, double Undershoot)>();
                foreach (var (name, plane) in rows)
                {
                    var undershoot = PlanetaryMetrics.LimbUndershoot(plane, size, size, disk);
                    if (truthWindow is { } tw2)
                    {
                        var bands = PlanetaryMetrics.Fidelity(plane, tw2, size, size, disk, Bands);
                        scores[name] = (bands.Sum(f => f.Error), undershoot);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"    {name}: transfer {string.Join(", ", bands.Select(f => f.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", bands.Select(f => f.Error.ToString("0.000", inv)))} (sum {bands.Sum(f => f.Error):0.000}); undershoot {undershoot:0.0000}"));
                    }
                    else
                    {
                        var rise = PlanetaryMetrics.Fidelity(plane, stackWindow, size, size, disk, Bands);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"    {name}: rise over the stack {string.Join(", ", rise.Select(f => f.Transfer.ToString("0.000", inv)))}; undershoot {undershoot:0.0000}"));
                    }
                }

                if (truthWindow is not null)
                {
                    // The claims, as pre-registered.
                    var d = scores.First(s => s.Key.StartsWith("derived, (b') [", StringComparison.Ordinal)).Value;
                    var o = scores.First(s => s.Key.StartsWith("derived, the true kernel", StringComparison.Ordinal)).Value;
                    var j = scores.First(s => s.Key.StartsWith("per-band gains fitted jointly", StringComparison.Ordinal)).Value;
                    var beaten = scores.Where(s => s.Key.StartsWith("PlanetaryDefault", StringComparison.Ordinal) || s.Key.StartsWith("Bandpass", StringComparison.Ordinal) || s.Key.StartsWith("Combo", StringComparison.Ordinal))
                        .Select(s => $"{s.Key.Split(' ')[0]} {(s.Key.Contains("shipped") ? "shipped" : "matched")} {s.Value.Error / d.Error:0.00}");
                    var gate = Math.Max(1.25 * j.Undershoot, 0.001);
                    consoleHost.WriteScrollable(string.Create(inv, $"    each preset's error over the derived gains': {string.Join(", ", beaten)}"));
                    consoleHost.WriteScrollable(string.Create(inv, $"    the ringing gate: undershoot {d.Undershoot:0.0000} against {gate:0.0000} ({(d.Undershoot <= gate ? "passes" : "FAILS")})"));
                    consoleHost.WriteScrollable(string.Create(inv, $"    over the jointly fitted oracle: the true kernel {o.Error / j.Error:0.000} (within 1.10?), (b') {d.Error / j.Error:0.000} (within 1.25?)"));
                }

                if (parseResult.GetValue(panelOpt) is { } panelPath)
                {
                    var panelRows = new[]
                    {
                        new[] { truthWindow, stackWindow, rows[1].Plane }.Concat(shippedPlanes).ToArray(),
                        new float[]?[] { null, jointPlane, null }.Concat(matchedPlanes).ToArray(),
                    };
                    var panel = PlanetaryInversesSubCommand.Panel(panelRows, size, disk);
                    try
                    {
                        await previewRenderer.RenderPlanetaryAsync(panel, panelPath, gamma: 1, ct: ct);
                    }
                    finally
                    {
                        panel.Release();
                    }
                    consoleHost.WriteScrollable($"    wrote {panelPath}: truth, stack, derived with (b'), then PlanetaryDefault, Bandpass and Combo as shipped; below, the joint oracle and the presets at matched noise");
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
                halfA.Master.Release();
                halfB.Master.Release();
            }
        });
        return command;
    }

    // PlanetaryMetrics' scored region, inside 0.9 radii.
    private const double ScoredRadii = 0.9;

    private static string Show(IEnumerable<double> gains) => string.Join(", ", gains.Select(g => g.ToString("0.00", CultureInfo.InvariantCulture)));
}
