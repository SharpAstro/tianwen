using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-spectral-ratio</c> (docs/plans/planetary-restoration.md, R7 part 1): a capture's spectral ratio
/// (<see cref="PlanetarySpectralRatio"/>), the frames' mean spectrum squared over their mean power, and the free air's r0 whose theory
/// fits it, the theory being the synthetic capture's own seeing model with the options <c>planetary-degrade</c> takes. With a still
/// layer given, the fit is made again with it in the theory, which says whether the ratio sees it.
/// </summary>
internal sealed class PlanetarySpectralRatioSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var planeOpt = new Option<string?>("--plane") { Description = "A colour capture's photosite colour: r, g, g2 or b." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windOpt = new Option<double>("--wind") { Description = "The wind carrying the free air, m/s.", DefaultValueFactory = _ => 10 };
        var outerScaleOpt = new Option<double?>("--outer-scale") { Description = "The turbulence's outer scale, m (von Karman; none for Kolmogorov)." };
        var exposureOpt = new Option<double>("--exposure-ms") { Description = "Each frame's exposure, ms.", DefaultValueFactory = _ => 0 };
        var defocusOpt = new Option<double>("--defocus-nm") { Description = "The telescope's own defocus, RMS wavefront error in nm.", DefaultValueFactory = _ => 0 };
        var localR0Opt = new Option<double?>("--local-r0") { Description = "A still layer at the telescope, its r0 at 500 nm in cm: the fit is made again with it in the theory." };
        var localOuterScaleOpt = new Option<double>("--local-outer-scale") { Description = "The still layer's outer scale, m.", DefaultValueFactory = _ => 0.25 };
        var localWindOpt = new Option<double>("--local-wind") { Description = "The still layer's drift, m/s.", DefaultValueFactory = _ => 0 };
        var offsetOpt = new Option<double>("--offset") { Description = "The camera's offset, ADU.", Required = true };
        var gainOpt = new Option<double>("--gain") { Description = "The camera's electrons an ADU.", Required = true };
        var readNoiseOpt = new Option<double>("--read-noise") { Description = "The camera's read noise, ADU.", Required = true };
        var minPowerOpt = new Option<double>("--min-power") { Description = "The least power over the noise's a ring is fitted at.", DefaultValueFactory = _ => 4 };
        var seedOpt = new Option<int>("--seed") { Description = "The theory's draws' seed.", DefaultValueFactory = _ => 1 };
        var outputOpt = new Option<string?>("--output") { Description = "Write the rings as CSV here." };

        var command = new Command("planetary-spectral-ratio",
            "A capture's spectral ratio, abs(mean F)^2 over mean abs(F)^2 of its registered frames, and the free air's r0 whose theory, the synthetic capture's own seeing model, fits it (R7 part 1).")
        {
            Arguments = { inputArg },
            Options = { planetOpt, planeOpt, framesOpt, telescopeOpt, wavelengthOpt, windOpt, outerScaleOpt, exposureOpt, defocusOpt, localR0Opt, localOuterScaleOpt, localWindOpt, offsetOpt, gainOpt, readNoiseOpt, minPowerOpt, seedOpt, outputOpt },
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
            var camera = new CameraNoise(parseResult.GetValue(offsetOpt), parseResult.GetValue(gainOpt), parseResult.GetValue(readNoiseOpt));
            var progress = new Progress<string>(line => consoleHost.WriteScrollable("    " + line));
            if (await PlanetarySpectralRatio.MeasureAsync(stream, reader.MaxSampleValue, camera, progress, ct) is not { } measured)
            {
                consoleHost.WriteError($"{input}: no disk found");
                return 1;
            }
            try
            {
                // The detector's scale from the disk's fitted radius and the planet's size at the capture's middle, as a synthetic
                // capture takes it.
                var aspect = PhysicalEphemeris.Compute(planet, when);
                if (PlanetaryLimbFit.Fit(measured.Reference, PlanetaryLimbFit.OptionsFor(aspect)) is not { } limb)
                {
                    consoleHost.WriteError($"{input}: the reference's limb could not be fitted");
                    return 1;
                }
                var scale = aspect.AngularDiameterArcsec / 2 / limb.EquatorialRadius;
                var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                var freeAir = new DegradeOptions(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9)
                {
                    R0M = 0.1,
                    WindMps = parseResult.GetValue(windOpt),
                    OuterScaleM = parseResult.GetValue(outerScaleOpt) ?? double.PositiveInfinity,
                    ExposureSeconds = parseResult.GetValue(exposureOpt) / 1000,
                    DefocusNm = parseResult.GetValue(defocusOpt),
                    Seed = parseResult.GetValue(seedOpt),
                };
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{Path.GetFileName(input)}: {measured.Frames} frames in a {measured.WindowSize} px window, disk R {limb.EquatorialRadius:0.00} px ({scale:0.0000}\"/px), {freeAir.WavelengthM * 1e9:0} nm, wind {freeAir.WindMps:0} m/s, exposure {freeAir.ExposureSeconds * 1000:0.#} ms"));
                var minPower = parseResult.GetValue(minPowerOpt);
                if (PlanetarySpectralRatio.Fit(measured.Rings, freeAir, scale, minPower) is not { } fit)
                {
                    consoleHost.WriteError("too few rings stand above the noise to fit");
                    return 1;
                }
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the free air alone: r0 {fit.R0M * 100:0.00} cm at 500 nm ({fit.R0M * 100 * Math.Pow(freeAir.WavelengthM / 500e-9, 1.2):0.00} at {freeAir.WavelengthM * 1e9:0} nm), log RMS {fit.LogRms:0.000} over {fit.Rings.Length} rings to {fit.Rings[^1].CyclesPerPixel:0.000} c/px"));
                SpectralRatioFit? withStill = null;
                if (parseResult.GetValue(localR0Opt) is { } localR0)
                {
                    var still = freeAir with { LocalR0M = localR0 / 100, LocalOuterScaleM = parseResult.GetValue(localOuterScaleOpt), LocalWindMps = parseResult.GetValue(localWindOpt) };
                    withStill = PlanetarySpectralRatio.Fit(measured.Rings, still, scale, minPower);
                    if (withStill is { } s)
                    {
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"    with a still layer of {localR0:0.0} cm in the theory: r0 {s.R0M * 100:0.00} cm, log RMS {s.LogRms:0.000}"));
                    }
                }
                consoleHost.WriteScrollable("    ring (c/px), measured, power over noise, the free air's fitted theory:");
                foreach (var (f, value, theory) in fit.Rings.Where((_, i) => i % 4 == 0))
                {
                    var ring = measured.Rings.First(r => r.CyclesPerPixel == f);
                    consoleHost.WriteScrollable(string.Create(inv, $"      {f:0.000}  {value:0.0000}  {ring.PowerOverNoise,8:0.0}  {theory:0.0000}"));
                }
                if (parseResult.GetValue(outputOpt) is { } output)
                {
                    var csv = new StringBuilder("cycles_per_pixel,ratio,power_over_noise,samples\n");
                    foreach (var ring in measured.Rings)
                    {
                        csv.Append(string.Create(inv, $"{ring.CyclesPerPixel:G6},{ring.Ratio:G6},{ring.PowerOverNoise:G6},{ring.Samples}\n"));
                    }
                    csv.Append("fitted_cycles_per_pixel,measured,theory_free_air\n");
                    foreach (var (f, value, theory) in fit.Rings)
                    {
                        csv.Append(string.Create(inv, $"{f:G6},{value:G6},{theory:G6}\n"));
                    }
                    File.WriteAllText(output, csv.ToString());
                    consoleHost.WriteScrollable($"    wrote the rings to {output}");
                }
                return 0;
            }
            finally
            {
                measured.Reference.Release();
            }
        });
        return command;
    }
}
