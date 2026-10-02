using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using Console.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// A planetary master scored as R3's metrics score a stack: against a synthetic capture's truth (band transfer and error inside 0.9 radii,
/// the limb's undershoot), a colour at a time for a colour master; or, with no truth, its limb's undershoot alone, the truth-free metric
/// that ranks stacks as the truth does (R3). Shared by <c>planetary-stack</c> and <c>planetary-sharpen</c>.
/// </summary>
internal static class PlanetaryMasterScore
{
    /// <summary>
    /// <paramref name="master"/> against <paramref name="truthPath"/> (a colour master against its truths .r, .g, .b beside it): its own limb
    /// fitted and moved onto the truth's disk, then the fidelity a band at a time and the undershoot, printed as <paramref name="what"/>.
    /// </summary>
    public static void AgainstTruth(IConsoleHost consoleHost, Image master, string truthPath, CatalogIndex planet, string what)
    {
        var inv = CultureInfo.InvariantCulture;
        var colour = master.ChannelCount == 3;
        foreach (var (channel, name) in colour ? new[] { (0, "r"), (1, "g"), (2, "b") } : [(0, "")])
        {
            var path = colour ? Path.ChangeExtension(truthPath, $".{name}.fits") : truthPath;
            if (PlanetaryMeasureSubCommand.ReadTruth(path, consoleHost) is not { } truth || truth.Time is not { } when)
            {
                consoleHost.WriteError($"[planetary] {path}: no truth with a time to score {what} against");
                return;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            var disk = truth.Disk with { AxisRatio = limbOptions.AxisRatio };
            var plane = colour ? master.ChannelImage(channel) : master;
            try
            {
                var label = colour ? $"{what}, {name}" : what;
                if (plane.Width * plane.Height != truth.Plane.Length)
                {
                    consoleHost.WriteError($"[planetary] {path}: {label} is {plane.Width} x {plane.Height}, the truth {truth.Plane.Length} px");
                    continue;
                }
                if (PlanetaryMeasureSubCommand.Register(plane, limbOptions, disk) is not { } fitted)
                {
                    consoleHost.WriteError($"[planetary] {label}: its limb could not be fitted");
                    continue;
                }
                var reference = PlanetaryMetrics.Normalise(truth.Plane, plane.Width, plane.Height, disk);
                var bands = PlanetaryMetrics.Fidelity(fitted.Plane, reference, plane.Width, plane.Height, disk);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"[planetary] {label} against the truth: transfer {string.Join(", ", bands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} (bands 1 to 4 {bands.Take(4).Sum(b => b.Error):0.000}); undershoot {PlanetaryMetrics.LimbUndershoot(fitted.Plane, plane.Width, plane.Height, disk):0.0000}; limb profile error {PlanetaryMetrics.LimbProfileError(fitted.Plane, reference, plane.Width, plane.Height, disk):0.0000}; rebound {PlanetaryMetrics.LimbRebound(fitted.Plane, plane.Width, plane.Height, disk):0.0000}"));
            }
            finally
            {
                if (colour)
                {
                    plane.Release();
                }
            }
        }
    }

    /// <summary><paramref name="master"/>'s limb undershoot below the sky, each channel on the disk fitted to the whole master, printed as <paramref name="what"/>.</summary>
    public static void Undershoot(IConsoleHost consoleHost, Image master, CatalogIndex planet, DateTimeOffset when, string what)
    {
        var inv = CultureInfo.InvariantCulture;
        var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
        if (PlanetaryLimbFit.Fit(master, limbOptions) is not { } fit)
        {
            consoleHost.WriteError($"[planetary] {what}: its limb could not be fitted");
            return;
        }
        var disk = MetricDisk.From(fit, limbOptions.AxisRatio);
        var planes = Enumerable.Range(0, master.ChannelCount).Select(c => PlanetaryMetrics.Normalise(master.GetChannelSpan(c), master.Width, master.Height, disk)).ToArray();
        var undershoots = planes.Select(p => PlanetaryMetrics.LimbUndershoot(p, master.Width, master.Height, disk));
        var rebounds = planes.Select(p => PlanetaryMetrics.LimbRebound(p, master.Width, master.Height, disk));
        consoleHost.WriteScrollable(string.Create(inv,
            $"[planetary] {what}: limb undershoot {string.Join(", ", undershoots.Select(u => u.ToString("0.0000", inv)))} of the disk; rebound outside the limb {string.Join(", ", rebounds.Select(u => u.ToString("0.0000", inv)))}"));
    }

    /// <summary>The telescope's pupil from the options <see cref="PupilOptions"/> made, or null when none was given.</summary>
    public static Pupil? PupilFrom(ParseResult parseResult, (Option<double?> ApertureMm, Option<double> Obstruction, Option<string?> Telescope) options)
    {
        if (parseResult.GetValue(options.ApertureMm) is { } apertureMm)
        {
            return new Pupil(apertureMm / 1000, ObstructionRatio: parseResult.GetValue(options.Obstruction));
        }
        return parseResult.GetValue(options.Telescope)?.ToLowerInvariant() switch
        {
            "newtonian" => PlanetaryGeometrySubCommands.NewtonianPupil,
            "maksutov" => PlanetaryGeometrySubCommands.MaksutovPupil,
            _ => null,
        };
    }

    /// <summary>The options a derived sharpening is told the telescope by.</summary>
    public static (Option<double?> ApertureMm, Option<double> Obstruction, Option<string?> Telescope) PupilOptions() => (
        new Option<double?>("--aperture-mm") { Description = "The telescope's aperture, mm: with it the sharpening is derived from the stack (R8), without it the PlanetaryDefault preset with the limb kept as stacked." },
        new Option<double>("--obstruction") { Description = "The central obstruction's diameter over the aperture's (0.25 for a typical Newtonian).", DefaultValueFactory = _ => 0 },
        new Option<string?>("--telescope") { Description = "A known telescope instead of --aperture-mm: newtonian (254 mm, 23 % obstructed, four vanes) or maksutov (102 mm, 30 %)." });

    /// <summary>The wavelengths of <c>--wavelength</c>, nm, one a channel; null and said when unreadable.</summary>
    public static double[]? Wavelengths(IConsoleHost consoleHost, string? text)
    {
        var parts = (text ?? "550").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || values[i] is <= 200 or >= 2000)
            {
                consoleHost.WriteError($"--wavelength {text}: a comma list of wavelengths in nm, one a channel");
                return null;
            }
        }
        return values;
    }
}
