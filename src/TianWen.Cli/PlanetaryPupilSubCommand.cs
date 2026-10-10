using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Text;
using Console.Lib;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary pupil</c> (#1367): a telescope's diffraction transfer at fractions of its cutoff, and the gain that undoes it
/// under each Wiener floor (<see cref="PlanetaryFinishing.PupilTransfer"/>). The table #1366 was argued from, which a Python script
/// computed before; what undoing the telescope (<c>planetary sharpen --target aperture</c>) asks of each frequency.
/// </summary>
internal sealed class PlanetaryPupilSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var apertureOpt = new Option<double?>("--aperture-mm") { Description = "The telescope's aperture, mm." };
        var obstructionOpt = new Option<double>("--obstruction") { Description = "The central obstruction's diameter over the aperture's (0.25 for a typical Newtonian).", DefaultValueFactory = _ => 0 };
        var telescopeOpt = new Option<string?>("--telescope") { Description = "A known telescope instead of --aperture-mm: newtonian (254 mm, 23 % obstructed, four vanes) or maksutov (102 mm, 30 %)." };
        var wavelengthOpt = new Option<string?>("--wavelength") { Description = "The filter's effective wavelength, nm, a comma list to read several (550 when not given)." };
        var scaleOpt = new Option<double?>("--arcsec-per-px") { Description = "The plate scale, arcseconds a pixel." };
        var focalOpt = new Option<double?>("--focal-mm") { Description = "The focal length, mm: with --pixel-um, the plate scale in place of --arcsec-per-px." };
        var pixelOpt = new Option<double?>("--pixel-um") { Description = "The camera's pixel pitch, micrometres, with --focal-mm." };
        var fractionsOpt = new Option<string>("--fractions") { Description = "The fractions of the cutoff to read at, a comma list.", DefaultValueFactory = _ => "0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9" };

        var command = new Command("pupil", "A telescope's diffraction transfer at fractions of its cutoff, and the gain that undoes it under each Wiener floor.")
        {
            Options = { apertureOpt, obstructionOpt, telescopeOpt, wavelengthOpt, scaleOpt, focalOpt, pixelOpt, fractionsOpt },
        };

        command.SetAction(parseResult =>
        {
            var inv = CultureInfo.InvariantCulture;
            if (PlanetaryMasterScore.PupilFrom(parseResult, (apertureOpt, obstructionOpt, telescopeOpt)) is not { } pupil)
            {
                consoleHost.WriteError("name the telescope: --aperture-mm (with --obstruction) or --telescope newtonian|maksutov");
                return 1;
            }
            var scale = parseResult.GetValue(scaleOpt)
                ?? (parseResult.GetValue(focalOpt) is { } focal && parseResult.GetValue(pixelOpt) is { } pixel ? 206.264806 * pixel / focal : (double?)null);
            if (scale is not { } arcsecPerPixel || arcsecPerPixel <= 0)
            {
                consoleHost.WriteError("give the plate scale: --arcsec-per-px, or --focal-mm with --pixel-um");
                return 1;
            }
            if (PlanetaryMasterScore.Wavelengths(consoleHost, parseResult.GetValue(wavelengthOpt)) is not { } wavelengths)
            {
                return 1;
            }
            var fractions = new List<double>();
            foreach (var part in (parseResult.GetValue(fractionsOpt) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(part, NumberStyles.Float, inv, out var fraction) || fraction is < 0 or > 1.5)
                {
                    consoleHost.WriteError($"--fractions {part}: a fraction of the cutoff, 0 to 1.5");
                    return 1;
                }
                fractions.Add(fraction);
            }

            var lines = new List<string>();
            foreach (var wavelength in wavelengths)
            {
                var cutoff = PlanetaryFinishing.CutoffCyclesPerPixel(pupil, wavelength, arcsecPerPixel);
                lines.Add("");
                lines.Add(string.Create(inv,
                    $"[planetary pupil] a {pupil.DiameterM * 1000:0} mm pupil, {pupil.ObstructionRatio:P0} obstructed{(pupil.Vanes > 0 ? $", {pupil.Vanes} vanes" : "")}, at {wavelength:0} nm on {arcsecPerPixel:0.000}\"/px: the cutoff {cutoff:0.000} cycles a pixel{(cutoff > 0.5 ? " (past Nyquist: the camera undersamples it)" : "")}"));
                lines.Add($"{"fraction",9}{"cycles/px",11}{"transfer",10}{"gain, step",12}{"gain, Tikhonov",16}");
                foreach (var row in PlanetaryFinishing.PupilTransfer(pupil, wavelength, arcsecPerPixel, fractions))
                {
                    var line = new StringBuilder(string.Create(inv,
                        $"{row.Fraction,9:0.00}{row.CyclesPerPixel,11:0.000}{row.Transfer,10:0.0000}{row.StepGain,12:0.00}{row.TikhonovGain,16:0.00}"));
                    if (row.CyclesPerPixel > 0.5)
                    {
                        line.Append("  past Nyquist");
                    }
                    lines.Add(line.ToString());
                }
            }
            consoleHost.WriteScrollable(string.Join(Environment.NewLine, lines));
            return 0;
        });
        return command;
    }
}
