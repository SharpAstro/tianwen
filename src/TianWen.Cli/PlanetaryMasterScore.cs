using System;
using System.Collections.Generic;
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
    public static void AgainstTruth(IConsoleHost consoleHost, Image master, string truthPath, CatalogIndex planet, string what, Image? stack = null)
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
            var disk = PlanetaryMeasureSubCommand.WithPlanet(truth.Disk, limbOptions);
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
                // The limb's trough (#1171), against the truth and, given the stack it was sharpened from, against that: the truth-free twin.
                var trough = PlanetaryMetrics.LimbTrough(fitted.Plane, reference, plane.Width, plane.Height, disk);
                var againstStack = double.NaN;
                if (stack is not null)
                {
                    var stackPlane = colour ? stack.ChannelImage(channel) : stack;
                    try
                    {
                        if (PlanetaryMeasureSubCommand.Register(stackPlane, limbOptions, disk) is { } stackFitted)
                        {
                            againstStack = PlanetaryMetrics.LimbTrough(fitted.Plane, stackFitted.Plane, plane.Width, plane.Height, disk);
                        }
                    }
                    finally
                    {
                        if (colour)
                        {
                            stackPlane.Release();
                        }
                    }
                }
                consoleHost.WriteScrollable(string.Create(inv, $"[planetary] {label}: the limb's trough below the truth {trough:0.0000}, below the stack {againstStack:0.0000}"));
                // The limb profile's error over the same reach in pixels on any planet, inside the limb (the sharpening) and outside it (what
                // is drawn there): 0.8 to 1.2 radii is 12 px across a 30 px Saturn and 19 across a 48 px Jupiter, so it does not compare them.
                var reach = PlanetaryMetrics.LimbReachPx / disk.Radius;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"[planetary] {label}: the limb profile's error within {PlanetaryMetrics.LimbReachPx:0} px inside it {PlanetaryMetrics.LimbProfileError(fitted.Plane, reference, plane.Width, plane.Height, disk, 1 - reach, 1):0.0000}, outside {PlanetaryMetrics.LimbProfileError(fitted.Plane, reference, plane.Width, plane.Height, disk, 1, 1 + reach):0.0000}"));
                // Saturn's rings (S4): their radial profile against the truth's, and what stands above the truth past their edge.
                if (disk.Rings is not null)
                {
                    var (ringError, pastTheEdge) = PlanetaryMetrics.RingProfileError(fitted.Plane, reference, plane.Width, plane.Height, disk);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"[planetary] {label}: the rings' profile error {ringError:0.0000}; past their edge at most {pastTheEdge:+0.0000;-0.0000} above the truth"));
                }
                // The moons where the truth has them (#1181): each one's peak and width here against the truth's.
                foreach (var (x, y) in PlanetaryMetrics.CompactSources(reference, plane.Width, plane.Height, disk))
                {
                    var (peak, wide) = PlanetaryMetrics.SourcePeak(fitted.Plane, plane.Width, plane.Height, x, y);
                    var (truthPeak, truthWide) = PlanetaryMetrics.SourcePeak(reference, plane.Width, plane.Height, x, y);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"[planetary] {label}, the moon at ({x}, {y}): peak {peak:0.0000} against the truth's {truthPeak:0.0000}, {wide} px above half against {truthWide}"));
                }
            }
            finally
            {
                if (colour)
                {
                    plane.Release();
                }
            }
        }
        if (colour)
        {
            ColourAgainstTruth(consoleHost, master, truthPath, planet, what);
        }
    }

    /// <summary>
    /// A colour master's colour against its truths (#1295): each channel's limb fitted and moved onto the truth's disk as
    /// <see cref="AgainstTruth"/> does, both normalised on the disk, then both carried onto the truth's own colour of the globe's core
    /// (inside 0.7 radii, clear of the rings), so a region's colour is its colour against the globe's, the truth's and the master's on one
    /// footing. For the globe's core, and on Saturn the ring ansae (the B and A rings off the globe) and the gap between the globe and the
    /// inner ring: the mean colour's OKLab chroma and hue against the truth's, the mean per-pixel OKLab distance from the truth, and the rings'
    /// chroma as a share of the globe's. The camera's planes are read as linear sRGB, the same for the master and the truth.
    /// </summary>
    public static void ColourAgainstTruth(IConsoleHost consoleHost, Image master, string truthPath, CatalogIndex planet, string what)
    {
        var inv = CultureInfo.InvariantCulture;
        var (width, height) = (master.Width, master.Height);
        var fitted = new float[3][];
        var normalised = new float[3][];
        var raw = new float[3][];
        MetricDisk disk = default;
        string[] names = ["r", "g", "b"];
        for (var c = 0; c < 3; c++)
        {
            var path = Path.ChangeExtension(truthPath, $".{names[c]}.fits");
            if (PlanetaryMeasureSubCommand.ReadTruth(path, consoleHost) is not { } truth || truth.Time is not { } when || truth.Plane.Length != width * height)
            {
                consoleHost.WriteError($"[planetary] {path}: no truth on {what}'s grid to read its colour against");
                return;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            disk = PlanetaryMeasureSubCommand.WithPlanet(truth.Disk, limbOptions);
            var plane = master.ChannelImage(c);
            try
            {
                if (PlanetaryMeasureSubCommand.Register(plane, limbOptions, disk) is not { } registered)
                {
                    consoleHost.WriteError($"[planetary] {what}, {names[c]}: its limb could not be fitted to read its colour");
                    return;
                }
                fitted[c] = registered.Plane;
            }
            finally
            {
                plane.Release();
            }
            normalised[c] = PlanetaryMetrics.Normalise(truth.Plane, width, height, disk);
            raw[c] = truth.Plane;
        }

        // The regions, on the truth's disk.
        var (globe, rings, gap) = (new List<int>(), new List<int>(), new List<int>());
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = disk.RadiiAt(x, y);
                var rho = disk.RingPlaneRadiiAt(x, y);
                if (r < 0.7 && !disk.RingTouched(x, y))
                {
                    globe.Add((y * width) + x);
                }
                else if (disk.Rings is { } ringSet && r > 1.1)
                {
                    if (rho >= 1.55 && rho <= ringSet.OuterRadii - 0.05)
                    {
                        rings.Add((y * width) + x);
                    }
                    else if (rho < 1.45)
                    {
                        gap.Add((y * width) + x);
                    }
                }
            }
        }
        if (globe.Count == 0)
        {
            return;
        }
        // Each channel carried from its disk-normalised units onto the truth's own units, by the truth's globe: the same factor for both.
        var scale = new double[3];
        for (var c = 0; c < 3; c++)
        {
            double rawSum = 0, normalisedSum = 0;
            foreach (var i in globe)
            {
                (rawSum, normalisedSum) = (rawSum + raw[c][i], normalisedSum + normalised[c][i]);
            }
            scale[c] = normalisedSum != 0 ? rawSum / normalisedSum : 1;
        }
        var parts = new List<string>();
        double? globeChroma = null, truthGlobeChroma = null;
        foreach (var (name, region) in new[] { ("globe", globe), ("rings", rings), ("gap", gap) })
        {
            if (region.Count == 0)
            {
                continue;
            }
            var (mean, truthMean) = (new double[3], new double[3]);
            double distance = 0;
            foreach (var i in region)
            {
                var (mr, mg, mb) = (fitted[0][i] * scale[0], fitted[1][i] * scale[1], fitted[2][i] * scale[2]);
                var (tr, tg, tb) = (normalised[0][i] * scale[0], normalised[1][i] * scale[1], normalised[2][i] * scale[2]);
                (mean[0], mean[1], mean[2]) = (mean[0] + mr, mean[1] + mg, mean[2] + mb);
                (truthMean[0], truthMean[1], truthMean[2]) = (truthMean[0] + tr, truthMean[1] + tg, truthMean[2] + tb);
                var (m, t) = (OkLab.FromLinearSrgb(Math.Max(mr, 0), Math.Max(mg, 0), Math.Max(mb, 0)), OkLab.FromLinearSrgb(Math.Max(tr, 0), Math.Max(tg, 0), Math.Max(tb, 0)));
                distance += Math.Sqrt(((m.L - t.L) * (m.L - t.L)) + ((m.A - t.A) * (m.A - t.A)) + ((m.B - t.B) * (m.B - t.B)));
            }
            var (ours, theirs) = (OkLab.FromLinearSrgb(mean[0] / region.Count, mean[1] / region.Count, mean[2] / region.Count),
                OkLab.FromLinearSrgb(truthMean[0] / region.Count, truthMean[1] / region.Count, truthMean[2] / region.Count));
            var (chroma, truthChroma) = (Math.Sqrt((ours.A * ours.A) + (ours.B * ours.B)), Math.Sqrt((theirs.A * theirs.A) + (theirs.B * theirs.B)));
            if (name == "globe")
            {
                (globeChroma, truthGlobeChroma) = (chroma, truthChroma);
            }
            var share = name == "rings" && globeChroma is { } ourGlobe && truthGlobeChroma is { } truthGlobe && ourGlobe > 0 && truthGlobe > 0
                ? string.Create(inv, $", as a share of the globe's {chroma / ourGlobe:0.00} against the truth's {truthChroma / truthGlobe:0.00}")
                : "";
            parts.Add(string.Create(inv,
                $"{name} chroma {chroma:0.0000} at {Math.Atan2(ours.B, ours.A) * 180 / Math.PI:0.0} deg against the truth's {truthChroma:0.0000} at {Math.Atan2(theirs.B, theirs.A) * 180 / Math.PI:0.0}{share}, {distance / region.Count:0.0000} from it pixel by pixel"));
        }
        consoleHost.WriteScrollable($"[planetary] {what}, its colour against the truth's: {string.Join("; ", parts)}");
    }

    /// <summary>
    /// The filter <paramref name="gains"/> make, as one line for <paramref name="what"/>: its transfer at fixed frequencies and its lowest
    /// point to Nyquist. A gain vector can swing below one and back while the filter it makes does not (#1251).
    /// </summary>
    public static string FilterWords(ReadOnlySpan<double> gains)
    {
        var inv = CultureInfo.InvariantCulture;
        double[] at = [0.05, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5];
        var values = new string[at.Length];
        for (var i = 0; i < at.Length; i++)
        {
            values[i] = PlanetaryWaveletGains.Transfer(gains, at[i]).ToString("0.00", inv);
        }
        var (lowest, lowestAt) = (double.PositiveInfinity, 0.0);
        for (var step = 0; step <= 100; step++)
        {
            var f = step * 0.005;
            if (PlanetaryWaveletGains.Transfer(gains, f) is var t && t < lowest)
            {
                (lowest, lowestAt) = (t, f);
            }
        }
        return string.Create(inv, $"their filter at 0.05, 0.1, 0.15, 0.2, 0.3, 0.4 and 0.5 cycles a pixel {string.Join(", ", values)}; lowest {lowest:0.00} at {lowestAt:0.000}");
    }

    /// <summary>
    /// The gains the truth itself asks of <paramref name="master"/> (a colour master's channels against their truths): one per scored a
    /// trous band, fitted JOINTLY to the truth inside 0.9 radii (<see cref="PlanetaryCeilings.PerBandJointOracle"/>, the coarser layers at
    /// one as the derived sharpening leaves them), their filter, and the band error they leave: what the derived gains are judged against
    /// (#1251).
    /// </summary>
    public static void TruthGains(IConsoleHost consoleHost, Image master, string truthPath, CatalogIndex planet)
    {
        var inv = CultureInfo.InvariantCulture;
        var colour = master.ChannelCount == 3;
        foreach (var (channel, name) in colour ? new[] { (0, "r"), (1, "g"), (2, "b") } : [(0, "")])
        {
            var path = colour ? Path.ChangeExtension(truthPath, $".{name}.fits") : truthPath;
            if (PlanetaryMeasureSubCommand.ReadTruth(path, consoleHost) is not { } truth || truth.Time is not { } when)
            {
                consoleHost.WriteError($"[planetary] {path}: no truth with a time to fit the truth's gains against");
                return;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            var disk = PlanetaryMeasureSubCommand.WithPlanet(truth.Disk, limbOptions);
            var plane = colour ? master.ChannelImage(channel) : master;
            try
            {
                var label = colour ? $"the truth's own gains, {name}" : "the truth's own gains";
                if (plane.Width * plane.Height != truth.Plane.Length || PlanetaryMeasureSubCommand.Register(plane, limbOptions, disk) is not { } fitted)
                {
                    consoleHost.WriteError($"[planetary] {label}: the master's limb could not be put on the truth's");
                    continue;
                }
                var reference = PlanetaryMetrics.Normalise(truth.Plane, plane.Width, plane.Height, disk);
                var (oracle, gains) = PlanetaryCeilings.PerBandJointOracle(fitted.Plane, reference, plane.Width, plane.Height, disk, PlanetaryWaveletGains.ScoredBands);
                var bands = PlanetaryMetrics.Fidelity(oracle, reference, plane.Width, plane.Height, disk);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"[planetary] {label}, fitted jointly to it: gains {string.Join(", ", gains.Select(g => g.ToString("0.00", inv)))}; {FilterWords(gains.AsSpan())}; error {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} (bands 1 to 4 {bands.Take(4).Sum(b => b.Error):0.000})"));
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
    public static void Undershoot(IConsoleHost consoleHost, Image master, CatalogIndex planet, DateTimeOffset when, string what, Image? stack = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
        if (PlanetaryLimbFit.Fit(master, limbOptions) is not { } fit)
        {
            consoleHost.WriteError($"[planetary] {what}: its limb could not be fitted");
            return;
        }
        var disk = MetricDisk.From(fit, limbOptions);
        var planes = Enumerable.Range(0, master.ChannelCount).Select(c => PlanetaryMetrics.Normalise(master.GetChannelSpan(c), master.Width, master.Height, disk)).ToArray();
        var undershoots = planes.Select(p => PlanetaryMetrics.LimbUndershoot(p, master.Width, master.Height, disk));
        var rebounds = planes.Select(p => PlanetaryMetrics.LimbRebound(p, master.Width, master.Height, disk));
        consoleHost.WriteScrollable(string.Create(inv,
            $"[planetary] {what}: limb undershoot {string.Join(", ", undershoots.Select(u => u.ToString("0.0000", inv)))} of the disk; rebound outside the limb {string.Join(", ", rebounds.Select(u => u.ToString("0.0000", inv)))}"));
        if (stack is not null && stack.Width == master.Width && stack.Height == master.Height && stack.ChannelCount == master.ChannelCount)
        {
            // The limb's trough below the stack it was sharpened from (#1171), the same disk, pixel for pixel.
            var troughs = Enumerable.Range(0, master.ChannelCount).Select(c => PlanetaryMetrics.LimbTrough(planes[c],
                PlanetaryMetrics.Normalise(stack.GetChannelSpan(c), master.Width, master.Height, disk), master.Width, master.Height, disk));
            consoleHost.WriteScrollable(string.Create(inv, $"[planetary] {what}: the limb's trough below the stack {string.Join(", ", troughs.Select(u => u.ToString("0.0000", inv)))}"));
        }
        // The moons each plane shows (#1181), as many as the sharpening looks for: each one's peak and width. Every channel's, since a
        // source one colour's glow alone puts up is one the sharpening keeps in that colour alone (#1301).
        string[] names = ["red", "green", "blue"];
        for (var c = 0; c < planes.Length; c++)
        {
            var channel = planes.Length == 1 ? "" : $" in {(c < names.Length ? names[c] : $"channel {c}")}";
            foreach (var (x, y) in PlanetaryMetrics.CompactSources(planes[c], master.Width, master.Height, disk, count: PlanetaryDering.MaxMoons))
            {
                var (peak, wide) = PlanetaryMetrics.SourcePeak(planes[c], master.Width, master.Height, x, y);
                consoleHost.WriteScrollable(string.Create(inv, $"[planetary] {what}, the moon at ({x}, {y}){channel}: peak {peak:0.0000}, {wide} px above half"));
            }
        }
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
