using System;
using System.Collections.Immutable;
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
    private static readonly double[] Frequencies = [0.1, 0.2, 0.3, 0.4, 0.45];

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
        var moonsOpt = new Option<double>("--moons") { Description = "Read every Galilean moon within this many radii of the centre as a near-point source (R8 follow-up 4, #1140); 0 for none." };
        var moonReachOpt = new Option<string>("--moon-reach") { Description = "The moon read's half side, px, a comma list to read at each.", DefaultValueFactory = _ => "16" };
        var moonQuadraticOpt = new Option<bool>("--moon-quadratic") { Description = "Take a quadratic surface off the moon read's rim, not a plane." };

        var command = new Command("planetary-finest-band",
            "A stack's finest band read off the limb's edge and off its spectrum against another year's map, beside a twin's oracle and the limb's kernel (R8 follow-up 3, #1139).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, mapOpt, killMapOpt, kOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, axisOpt, moonsOpt, moonReachOpt, moonQuadraticOpt },
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
                $"{Path.GetFileName(input)}: {prepared.Result.FramesUsed} of {prepared.Result.FramesGraded} frames by the gradient; {prepared.ArcsecPerPixel:0.0000}\"/px; disk R {fit.EquatorialRadius:0.00} px; the transfers over the pupil's diffraction at {string.Join(", ", Frequencies.Select(f => f.ToString("0.0#", inv)))} cycles a pixel"));
            if (prepared.Truth is { } truth)
            {
                var oracle = PlanetaryInverse.Measure(stack, truth, size, size);
                consoleHost.WriteScrollable($"    the oracle (the stack against its truth): {Row(oracle.At)}");
                // The stack is put on its truth by the limb fit's centre; registered by the correlation instead, any shift the limb fit
                // left is out of the oracle (a shift reads as blur at the finest frequencies).
                var (moved, shiftX, shiftY) = new CorrelationRegistrar(truth, size).Register(stack);
                var corrected = PlanetaryInverse.Measure(moved, truth, size, size);
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"      registered by correlation:            {Row(corrected.At)} (the stack lay {shiftX:+0.000;-0.000}, {shiftY:+0.000;-0.000} px off its truth)"));
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
                // Judged where it was pre-registered, 0.1 to 0.3 cycles a pixel (step 3); past that the edge is noise.
                var check = prepared.LimbEdge(truthPlane);
                var worst = Frequencies.Where(f => f <= 0.3).Max(f => Math.Abs(check.TransferAt(f) - 1));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"      its self-check, the truth's edge:     {Row(check.TransferAt)} ({(worst <= 0.05 ? "holds" : "FAILS")}, within {worst:0.000} of one to 0.3)"));
            }
            if (parseResult.GetValue(axisOpt) is { } along)
            {
                var (alongEdge, acrossEdge) = (prepared.LimbEdge(stack, along), prepared.LimbEdge(stack, along + 90));
                consoleHost.WriteScrollable(string.Create(inv, $"      along {along:0} degrees:                  {Row(alongEdge.TransferAt)}"));
                consoleHost.WriteScrollable(string.Create(inv, $"      across, {(along + 90) % 180:0} degrees:              {Row(acrossEdge.TransferAt)}"));
            }

            // (m) Each moon in the frame as a near-point source, where the ephemeris and the moons' own light put it.
            if (parseResult.GetValue(moonsOpt) is var within and > 0)
            {
                var reaches = (parseResult.GetValue(moonReachOpt) ?? "16").Split(',').Select(r => int.Parse(r, CultureInfo.InvariantCulture)).ToArray();
                ReadMoons(prepared, within, reaches, parseResult.GetValue(moonQuadraticOpt), Row);
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

    // Each Galilean moon within `within` radii: named and sized by the ephemeris at the frames' middle, placed by the disk turned the way
    // the moons themselves say, smeared by its drift over the frames, and read at each reach in the stack and its two halves (their
    // difference is the read's noise), with the read again under a limb darkened as mu^0.2; on a twin, also on its truth, which must
    // read one at every frequency and the light the twin put in.
    private void ReadMoons(PlanetaryWindowedStack prepared, double within, int[] reaches, bool quadratic, Func<Func<double, double>, string> row)
    {
        var inv = CultureInfo.InvariantCulture;
        var (width, height) = (prepared.Width, prepared.Height);
        var (moons, distance) = GalileanMoons.At(prepared.When);
        var (first, _) = GalileanMoons.At(prepared.Start);
        var (last, _) = GalileanMoons.At(prepared.End);
        var disks = moons.Select(m => new MoonDisk(m.X, m.Y, m.Radius, 1)).ToImmutableArray();
        var axis = new DiskPlacement(prepared.Target.X, prepared.Target.Y, prepared.Fit.EquatorialRadius, prepared.Fit.NorthAngleDeg);
        var placement = PlanetaryMoonProbe.Orient(prepared.FullStack, width, height, axis, disks);
        consoleHost.WriteScrollable(string.Create(inv,
            $"    (m), the moons at {distance:0.0000} AU; north {placement.NorthAngleDeg:0.0} deg{(placement.Mirrored ? ", mirrored" : "")} by the moons (the limb fit's axis end {prepared.Fit.NorthAngleDeg:0.0}):"));
        for (var i = 0; i < moons.Length; i++)
        {
            var moon = moons[i];
            var separation = Math.Sqrt((moon.X * moon.X) + (moon.Y * moon.Y));
            if (separation >= within)
            {
                continue;
            }
            var (x, y) = placement.ImagePoint(moon.X, moon.Y);
            var (x0, y0) = placement.ImagePoint(first[i].X, first[i].Y);
            var (x1, y1) = placement.ImagePoint(last[i].X, last[i].Y);
            var radius = moon.Radius * placement.EquatorialRadius;
            var (driftX, driftY) = (x1 - x0, y1 - y0);
            consoleHost.WriteScrollable(string.Create(inv,
                $"      {moon.Name}, {separation:0.00} radii, {2 * radius:0.00} px across, drifting {Math.Sqrt((driftX * driftX) + (driftY * driftY)):0.00} px, the background {(quadratic ? "a quadratic surface" : "a plane")}:"));
            foreach (var reach in reaches)
            {
                MoonRead? At(float[] plane, double atX, double atY, int search, double darkening = 0) =>
                    PlanetaryMoonProbe.Read(plane, width, height, atX, atY, radius, driftX, driftY, prepared.Diffraction.At, search, darkening, reach, quadratic);
                if (At(prepared.FullStack, x, y, 3) is not { } read)
                {
                    consoleHost.WriteScrollable(string.Create(inv, $"        within {reach} px, at {x:0.0}, {y:0.0}: outside the frame or not found"));
                    continue;
                }
                consoleHost.WriteScrollable(string.Create(inv,
                    $"        within {reach} px: found {read.X - x:+0.00;-0.00}, {read.Y - y:+0.00;-0.00} px from its place, flux {read.Flux:0.000}"));
                consoleHost.WriteScrollable($"          its read:                         {row(read.Transfer.At)}");
                if (prepared.FullHalfA is { } a && prepared.FullHalfB is { } b && At(a, read.X, read.Y, 0) is { } ha && At(b, read.X, read.Y, 0) is { } hb)
                {
                    consoleHost.WriteScrollable($"          its noise (half the halves' gap): {row(f => Math.Abs(ha.Transfer.At(f) - hb.Transfer.At(f)) / 2)}");
                }
                consoleHost.WriteScrollable($"          the model's own transfer:         {row(read.Model.At)}");
                if (At(prepared.FullStack, read.X, read.Y, 0, 0.2) is { } darkened)
                {
                    consoleHost.WriteScrollable($"          a limb darkened as mu^0.2 moves it: {row(f => darkened.Transfer.At(f) - read.Transfer.At(f))}");
                }
                if (prepared.FullTruth is { } truth && At(truth, read.X, read.Y, 0) is { } onTruth)
                {
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"          its self-check, on the truth:     {row(onTruth.Transfer.At)} (flux {onTruth.Flux:0.000})"));
                }
                // An anisotropic kernel's two directions: the frequencies along the planet's axis (across its belts, where its own power
                // is) and along its equator, each over the truth's read in the same sector where there is one.
                foreach (var (name, direction) in new[] { ("along the axis", placement.NorthAngleDeg), ("along the equator", placement.NorthAngleDeg + 90) })
                {
                    MoonRead? Sector(float[] plane) =>
                        PlanetaryMoonProbe.Read(plane, width, height, read.X, read.Y, radius, driftX, driftY, prepared.Diffraction.At, 0, 0, reach, quadratic, direction);
                    if (Sector(prepared.FullStack) is { } along)
                    {
                        var overTruth = prepared.FullTruth is { } t && Sector(t) is { } alongTruth
                            ? $", over the truth's {row(f => along.Transfer.At(f) / alongTruth.Transfer.At(f))}" : "";
                        consoleHost.WriteScrollable($"          {name,-18}:             {row(along.Transfer.At)}{overTruth}");
                    }
                }
            }
        }
    }
}
