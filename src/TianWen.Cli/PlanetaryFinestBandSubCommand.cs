using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-finest-band</c> (docs/plans/planetary-restoration.md, R8 follow-up 3, #1139): a stack's transfer at 0.1, 0.2 and 0.3 cycles
/// a pixel read without a truth, off the limb's edge against the limb fit's sharp model through the diffraction, and off the disk's
/// spectrum against another year's map rendered at the capture's geometry; beside a twin's oracle and the limb fit's kernel (b').
/// </summary>
internal sealed class PlanetaryFinestBandSubCommand(IConsoleHost consoleHost)
{
    private static readonly double[] Frequencies = [0.1, 0.2, 0.3];

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): the oracle, and the edge's self-check." };
        var mapOpt = new Option<string?>("--map") { Description = "Another year's global map in the filter: the spectrum's object." };
        var killMapOpt = new Option<string?>("--kill-map") { Description = "The capture's own year's map, for the spectrum's kill line where there is no truth (on a twin the truth is it)." };
        var kOpt = new Option<double>("--k") { Description = "Minnaert's exponent for the maps' filter.", DefaultValueFactory = _ => 0.999 };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient (each half its own share of its frames).", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil every transfer here is over.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px.", DefaultValueFactory = _ => 256 };
        var axisOpt = new Option<double?>("--axis") { Description = "Also read the edge along this direction (degrees from +x toward +y) and across it: an elongated kernel's two profiles." };

        var command = new Command("planetary-finest-band",
            "A stack's finest band read off the limb's edge and off its spectrum against another year's map, beside a twin's oracle and the limb's kernel (R8 follow-up 3, #1139).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, mapOpt, killMapOpt, kOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, axisOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var size = parseResult.GetValue(windowOpt);
            var truthPath = parseResult.GetValue(truthOpt);
            var telescope = parseResult.GetValue(telescopeOpt) ?? "newtonian";
            var wavelengthNm = parseResult.GetValue(wavelengthOpt);
            using var prepared = await PlanetaryWindowedStack.CreateAsync(consoleHost, input, truthPath, planet, parseResult.GetValue(framesOpt),
                parseResult.GetValue(keepOpt), telescope, wavelengthNm, size, halves: true, ct);
            if (prepared is not { HalfA: { } halfA, HalfB: { } halfB })
            {
                return 1;
            }
            var (disk, fit, stack) = (prepared.Disk, prepared.Fit, prepared.Stack);
            string Row(Func<double, double> transfer) => string.Join(" ", Frequencies.Select(f => transfer(f).ToString("0.000", inv)));
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {prepared.Result.FramesUsed} of {prepared.Result.FramesGraded} frames by the gradient; {prepared.ArcsecPerPixel:0.0000}\"/px; disk R {fit.EquatorialRadius:0.00} px; the transfers over the pupil's diffraction at {string.Join(", ", Frequencies.Select(f => f.ToString("0.0", inv)))} cycles a pixel"));
            if (prepared.Truth is { } truth)
            {
                var oracle = PlanetaryInverse.Measure(stack, truth, size, size);
                consoleHost.WriteScrollable($"    the oracle (the stack against its truth): {Row(oracle.At)}");
            }
            consoleHost.WriteScrollable($"    (b'), the limb fit's kernel:            {Row(prepared.Measured)}");

            // (b) The edge, each pixel divided by the zonal brightness at its limb point's latitude, the stack's and the model's each its own.
            var edge = prepared.LimbEdge(stack);
            consoleHost.WriteScrollable(string.Create(inv,
                $"    (b), the limb's edge:                   {Row(edge.TransferAt)} ({edge.Counts.Sum()} pixels, {edge.Counts.Min()} to {edge.Counts.Max()} a bin)"));
            // (a) The physical kernel fitted to that edge to 0.35 cycles a pixel, carried on to the cutoff.
            var physical = PlanetaryFinestBand.FitPhysical(edge, prepared.Cutoff);
            consoleHost.WriteScrollable(string.Create(inv,
                $"    (a), the physical kernel on the edge:   {Row(physical.TransferAt)} (D/r0 {physical.ApertureOverR0:0.00}, sigma {physical.SigmaPx:0.00} px, halo {physical.Halo:0.000} of {physical.HaloWidthPx:0.0} px; the cutoff {prepared.Cutoff:0.000} cycles a pixel)"));
            if (prepared.Truth is { } truthPlane)
            {
                var check = prepared.LimbEdge(truthPlane);
                var worst = Frequencies.Max(f => Math.Abs(check.TransferAt(f) - 1));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"      its self-check, the truth's edge:     {Row(check.TransferAt)} ({(worst <= 0.05 ? "holds" : "FAILS")}, within {worst:0.000} of one)"));
            }
            if (parseResult.GetValue(axisOpt) is { } along)
            {
                var (alongEdge, acrossEdge) = (prepared.LimbEdge(stack, along), prepared.LimbEdge(stack, along + 90));
                consoleHost.WriteScrollable(string.Create(inv, $"      along {along:0} degrees:                  {Row(alongEdge.TransferAt)}"));
                consoleHost.WriteScrollable(string.Create(inv, $"      across, {(along + 90) % 180:0} degrees:              {Row(acrossEdge.TransferAt)}"));
            }

            // (c) The spectrum: the stack's texture power less its halves' noise, over another year's map at the capture's geometry.
            if (parseResult.GetValue(mapOpt) is { } mapPath)
            {
                var k = parseResult.GetValue(kOpt);
                var pupil = telescope.ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                // Where and when the truth was rendered, on a twin; the stack's own disk and middle otherwise.
                var (placement, when) = truthPath is not null && PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost) is { } read && read.Time is { } readTime
                    ? (new DiskPlacement(read.Disk.X, read.Disk.Y, read.Disk.Radius, read.Disk.AxisAngleDeg), readTime)
                    : (new DiskPlacement(prepared.Target.X, prepared.Target.Y, fit.EquatorialRadius, fit.NorthAngleDeg), prepared.When);
                float[]? Rendered(string path)
                {
                    if (PlanetMap.ReadFits(path) is not { } map)
                    {
                        consoleHost.WriteError($"{path}: not a global map");
                        return null;
                    }
                    var render = PlanetaryRender.RenderDiffracted(map, PhysicalEphemeris.Compute(planet, when), placement, prepared.Width, prepared.Height, k, pupil,
                        wavelengthNm * 1e-9, prepared.ArcsecPerPixel);
                    return prepared.Window(PlanetaryMetrics.Normalise(render, prepared.Width, prepared.Height, prepared.Target));
                }
                if (Rendered(mapPath) is not { } other)
                {
                    return 1;
                }
                var objectPower = PlanetaryFinestBand.TexturePower(other, size, size, disk);
                var noise = PlanetaryWaveletGains.HalvesNoise(halfA, halfB, size, size, disk);
                var spectrum = PlanetaryFinestBand.Spectrum(PlanetaryFinestBand.TexturePower(stack, size, size, disk), noise, objectPower);
                consoleHost.WriteScrollable($"    (c), the spectrum over {Path.GetFileName(mapPath)}: {Row(spectrum.At)}");
                var own = prepared.Truth ?? (parseResult.GetValue(killMapOpt) is { } killPath ? Rendered(killPath) : null);
                if (own is not null)
                {
                    var ownPower = PlanetaryFinestBand.TexturePower(own, size, size, disk);
                    var ratios = new[] { (0.1, 0.15), (0.15, 0.2), (0.2, 0.25), (0.25, 0.3) }.Select(b => PlanetaryFinestBand.PowerRatio(objectPower, ownPower, b.Item1, b.Item2)).ToArray();
                    var worst = ratios.Max(r => Math.Abs(r - 1));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"      its kill line, that map's power over the capture's own year's, 0.1 to 0.3 by 0.05: {string.Join(" ", ratios.Select(r => r.ToString("0.000", inv)))} ({(worst <= 0.2 ? "holds" : "FIRES")}, {worst:P0} at worst)"));
                }
            }
            return 0;
        });
        return command;
    }
}
