using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary dering</c> (docs/plans/planetary-restoration.md, R8 follow-up 2): the presets and R8's derived gains, each plain and with
/// four ways round a ring below the sky (a floor at the sky, the limb as its own channel, gains feathered at the limb, gains under a
/// non-negative composite), scored per band inside the disk, on the limb's undershoot and on the limb profile against a twin's truth.
/// </summary>
internal sealed class PlanetaryDeringSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    private const int Bands = 4;

    private sealed record Row(string Sharpening, string Fix, float[] Plane, double ErrorSum, ImmutableArray<double> Errors, ImmutableArray<double> Transfers, double Undershoot, double LimbError);

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary degrade's .truth.fits)." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px.", DefaultValueFactory = _ => 256 };
        var trueFloorOpt = new Option<double>("--true-floor") { Description = "Where the truth's power falls below this share of its second ring's, the true kernel reads zero (1e-3 as pre-registered; 1e-6, R8's, post hoc).", DefaultValueFactory = _ => 1e-3 };
        var panelOpt = new Option<string?>("--panel") { Description = "A PNG: PlanetaryDefault plain, floored, as a limb channel and feathered; then the derived gains plain and under a non-negative composite." };

        var command = new Command("dering",
            "The presets and R8's derived gains with a floor at the sky, the limb as its own channel, gains feathered at the limb, and gains under a non-negative composite (R8 follow-up 2).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, trueFloorOpt, panelOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var size = parseResult.GetValue(windowOpt);
            using var prepared = await PlanetaryWindowedStack.CreateAsync(consoleHost, input, parseResult.GetValue(truthOpt), planet, parseResult.GetValue(framesOpt),
                parseResult.GetValue(keepOpt), parseResult.GetValue(telescopeOpt) ?? "newtonian", parseResult.GetValue(wavelengthOpt), size, halves: true, ct);
            if (prepared is not { HalfA: { } halfA, HalfB: { } halfB })
            {
                return 1;
            }
            var (stack, disk, truth, diskTarget) = (prepared.Stack, prepared.Disk, prepared.Truth, prepared.DiskTarget);
            var measured = prepared.Measured;
            Func<double, double> total = prepared.Wide.TransferAt;

            // The derivation's inputs, as R8 part 3 read them.
            var power = PlanetaryWaveletGains.StackPower(stack, size, size, disk);
            var noise = PlanetaryWaveletGains.HalvesNoise(halfA, halfB, size, size, disk);
            ImmutableArray<double> Derive(Func<double, double> kernel) =>
                PlanetaryWaveletGains.Fit(power, PlanetaryWaveletGains.Wiener(power, noise, kernel), diskTarget, PlanetaryInverse.Apply(diskTarget, size, size, kernel), size, size, disk);
            ImmutableArray<double> DeriveNonNegative(Func<double, double> kernel) =>
                PlanetaryWaveletGains.FitNonNegative(power, PlanetaryWaveletGains.Wiener(power, noise, kernel), diskTarget, PlanetaryInverse.Apply(diskTarget, size, size, kernel), size, size, disk, kernel);

            // The presets' thresholds carried from the master's units to the window's (the sky zero, the disk one).
            var master = prepared.Result.Master.GetChannelSpan(0);
            var normalised = PlanetaryMetrics.Normalise(master, prepared.Width, prepared.Height, prepared.Own);
            var toWindow = Slope(master, normalised);

            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {prepared.Result.FramesUsed} of {prepared.Result.FramesGraded} frames by the gradient; {prepared.ArcsecPerPixel:0.0000}\"/px; a master unit is {toWindow:0.00} of the disk"));

            var sharpenings = new List<(string Name, double[] Gains, double[] Thresholds)>();
            foreach (var (name, preset) in new[] { ("PlanetaryDefault", WaveletSharpenOptions.PlanetaryDefault), ("Bandpass", WaveletSharpenOptions.Bandpass), ("Combo", WaveletSharpenOptions.Combo) })
            {
                sharpenings.Add((name, [.. preset.Gains.Select(g => (double)g)], preset.DenoiseThresholds.IsDefaultOrEmpty ? [] : [.. preset.DenoiseThresholds.Select(t => t * toWindow)]));
            }
            RadialTransfer? trueKernel = null;
            if (truth is { } tw)
            {
                trueKernel = PlanetaryDering.Smoothed(PlanetaryInverse.Measure(stack, tw, size, size, minPowerFraction: parseResult.GetValue(trueFloorOpt)));
                var control = PlanetaryKernelFit.NegativeMass((_, _) => Complex.One, trueKernel.At);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the control: the stack's own composite through the true kernel, smoothed, has a negative mass of {control:0.0000} ({(control <= 0.01 ? "the composite is read" : "NOT READ, the composite rows below mean nothing")})"));
                sharpenings.Add(("derived, the true kernel", [.. Derive(trueKernel.At)], []));
            }
            var bprime = Derive(measured);
            sharpenings.Add(("derived, (b')", [.. bprime], []));

            var rows = new List<Row>();
            void Add(string sharpening, string fix, float[] plane)
            {
                var undershoot = PlanetaryMetrics.LimbUndershoot(plane, size, size, disk);
                if (truth is { } t)
                {
                    var bands = PlanetaryMetrics.Fidelity(plane, t, size, size, disk, Bands);
                    var limb = PlanetaryMetrics.LimbProfileError(plane, t, size, size, disk);
                    var row = new Row(sharpening, fix, plane, bands.Sum(f => f.Error), [.. bands.Select(f => f.Error)], [.. bands.Select(f => f.Transfer)], undershoot, limb);
                    rows.Add(row);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    {sharpening}, {fix}: error {string.Join(", ", row.Errors.Select(e => e.ToString("0.000", inv)))} (sum {row.ErrorSum:0.000}); undershoot {undershoot:0.0000}; limb profile error {limb:0.0000}"));
                }
                else
                {
                    var rise = PlanetaryMetrics.Fidelity(plane, stack, size, size, disk, Bands);
                    rows.Add(new Row(sharpening, fix, plane, double.NaN, [], [.. rise.Select(f => f.Transfer)], undershoot, double.NaN));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    {sharpening}, {fix}: rise over the stack {string.Join(", ", rise.Select(f => f.Transfer.ToString("0.000", inv)))}; undershoot {undershoot:0.0000}"));
                }
            }

            Add("the stack", "plain", stack);
            var shipped = new List<Image>();
            try
            {
                foreach (var (name, preset) in new[] { ("PlanetaryDefault", WaveletSharpenOptions.PlanetaryDefault), ("Bandpass", WaveletSharpenOptions.Bandpass), ("Combo", WaveletSharpenOptions.Combo) })
                {
                    var sharpened = WaveletSharpen.Sharpen(prepared.Result.Master, preset);
                    shipped.Add(sharpened);
                    Add(name, "as shipped (the master, clamped at its zero)", prepared.Window(prepared.Registered(sharpened.GetChannelSpan(0))));
                }
            }
            finally
            {
                foreach (var image in shipped)
                {
                    image.Release();
                }
            }

            foreach (var (name, gains, thresholds) in sharpenings)
            {
                float[] Sharpen(float[] p) => PlanetaryDering.Sharpen(p, size, size, gains, thresholds);
                var plain = Sharpen(stack);
                Add(name, "plain", plain);
                Add(name, "floored at the sky", PlanetaryDering.Floor(plain));
                Add(name, "the limb as its own channel", PlanetaryDering.LimbChannel(stack, size, size, prepared.SharpDisk, total, prepared.Diffraction.At, Sharpen));
                Add(name, "feathered at the limb", PlanetaryDering.Feathered(stack, size, size, disk, gains, thresholds));
            }
            if (trueKernel is { } k)
            {
                var held = DeriveNonNegative(k.At);
                Add($"derived, the true kernel, non-negative [{Show(held)}]", "a non-negative composite", PlanetaryWaveletGains.Apply(stack, size, size, held.AsSpan()));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the composite's negative mass through the true kernel: derived {PlanetaryKernelFit.NegativeMass(Wavelet(sharpenings.First(s => s.Name == "derived, the true kernel").Gains), k.At):0.0000}, held non-negative {PlanetaryKernelFit.NegativeMass(Wavelet([.. held]), k.At):0.0000}"));
            }
            var heldPrime = DeriveNonNegative(measured);
            Add($"derived, (b'), non-negative [{Show(heldPrime)}]", "a non-negative composite", PlanetaryWaveletGains.Apply(stack, size, size, heldPrime.AsSpan()));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    derived with (b'): finest gain {bprime[0]:0.00} free, {heldPrime[0]:0.00} held non-negative ({heldPrime[0] / bprime[0]:0.00} of it)"));

            if (truth is not null)
            {
                Claims(rows, inv);
            }

            if (parseResult.GetValue(panelOpt) is { } panelPath)
            {
                float[]? Find(string sharpening, string fix) => rows.FirstOrDefault(r => r.Sharpening.StartsWith(sharpening, StringComparison.Ordinal) && r.Fix == fix)?.Plane;
                var panelRows = new[]
                {
                    new[] { truth, stack, Find("PlanetaryDefault", "plain"), Find("PlanetaryDefault", "floored at the sky"), Find("PlanetaryDefault", "the limb as its own channel"), Find("PlanetaryDefault", "feathered at the limb") },
                    new[] { Find("derived, the true kernel", "plain"), Find("derived, the true kernel, non-negative", "a non-negative composite"), Find("derived, (b')", "plain"),
                        Find("derived, (b'), non-negative", "a non-negative composite"), Find("derived, (b')", "the limb as its own channel"), Find("derived, (b')", "feathered at the limb") },
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
                consoleHost.WriteScrollable($"    wrote {panelPath}: truth, stack, PlanetaryDefault plain, floored, limb channel, feathered; below, derived (true kernel) plain and non-negative, derived (b') plain, non-negative, limb channel, feathered");
            }
            return 0;
        });
        return command;
    }

    // The claims, as pre-registered.
    private void Claims(List<Row> rows, CultureInfo inv)
    {
        Row Of(string sharpening, string fix) => rows.First(r => r.Sharpening == sharpening && r.Fix == fix);
        var sharpenings = rows.Where(r => r.Fix == "plain" && r.Sharpening != "the stack").Select(r => r.Sharpening).ToArray();
        var presets = new[] { "PlanetaryDefault", "Bandpass", "Combo" };
        string Verdict(bool holds) => holds ? "holds" : "FAILS";

        var floorRing = sharpenings.All(s => Of(s, "floored at the sky").Undershoot <= 0.001);
        var floorBands = sharpenings.All(s => Math.Abs(Of(s, "floored at the sky").ErrorSum / Of(s, "plain").ErrorSum - 1) <= 0.01);
        var floorLimb = presets.All(s => Of(s, "floored at the sky").LimbError <= Of(s, "plain").LimbError * 2 / 3);
        consoleHost.WriteScrollable(string.Create(inv,
            $"claim, the floor: every undershoot at most 0.001 {Verdict(floorRing)}; band errors within 1 % {Verdict(floorBands)}; the presets' limb profile error cut by a third {Verdict(floorLimb)}"));

        var channelRing = presets.All(s => Of(s, "the limb as its own channel").Undershoot < 0.02);
        var channelLimb = presets.All(s => Of(s, "the limb as its own channel").LimbError <= Of(s, "plain").LimbError / 2);
        var channelBands = presets.All(s => Math.Abs(Of(s, "the limb as its own channel").ErrorSum / Of(s, "plain").ErrorSum - 1) <= 0.05);
        consoleHost.WriteScrollable(string.Create(inv,
            $"claim, the limb channel: the presets' undershoot under 0.02 {Verdict(channelRing)}; their limb profile error halved {Verdict(channelLimb)}; band errors within 5 % {Verdict(channelBands)}"));

        var featherRing = sharpenings.All(s => Of(s, "feathered at the limb").Undershoot < 0.01);
        var featherBands = sharpenings.All(s => Of(s, "feathered at the limb").Errors[0] + Of(s, "feathered at the limb").Errors[1] <= 1.1 * (Of(s, "plain").Errors[0] + Of(s, "plain").Errors[1]));
        consoleHost.WriteScrollable(string.Create(inv,
            $"claim, the feathered gains: every undershoot under 0.01 {Verdict(featherRing)}; bands 1 and 2 at most 10 % worse {Verdict(featherBands)}"));

        if (rows.FirstOrDefault(r => r.Fix == "a non-negative composite" && r.Sharpening.StartsWith("derived, the true kernel", StringComparison.Ordinal)) is { } held)
        {
            var free = Of("derived, the true kernel", "plain");
            consoleHost.WriteScrollable(string.Create(inv,
                $"claim, the non-negative composite with the true kernel: undershoot {held.Undershoot:0.0000} at most 0.001 {Verdict(held.Undershoot <= 0.001)}; summed error {held.ErrorSum:0.000} against {free.ErrorSum:0.000}, at most 5 % more {Verdict(held.ErrorSum <= 1.05 * free.ErrorSum)}"));
        }
    }

    // The least-squares slope of `normalised` on `master`: how many disks a master unit is.
    private static double Slope(ReadOnlySpan<float> master, ReadOnlySpan<float> normalised)
    {
        double mx = 0, my = 0;
        for (var i = 0; i < master.Length; i++)
        {
            (mx, my) = (mx + master[i], my + normalised[i]);
        }
        (mx, my) = (mx / master.Length, my / master.Length);
        double sxy = 0, sxx = 0;
        for (var i = 0; i < master.Length; i++)
        {
            var dx = master[i] - mx;
            (sxy, sxx) = (sxy + (dx * (normalised[i] - my)), sxx + (dx * dx));
        }
        return sxy / sxx;
    }

    private static Func<double, double, Complex> Wavelet(double[] gains) => (fx, fy) => PlanetaryKernelFit.WaveletTransfer(gains, fx, fy);

    private static string Show(IEnumerable<double> gains) => string.Join(", ", gains.Select(g => g.ToString("0.00", CultureInfo.InvariantCulture)));
}
