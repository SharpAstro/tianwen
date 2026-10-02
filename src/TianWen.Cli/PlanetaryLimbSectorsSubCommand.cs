using System;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using Console.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-limb-sectors</c> (docs/plans/planetary-restoration.md, "The trough at the limb", #1171): a master's limb read sector by
/// sector against the limb fit's outline, the radius at which each sector's profile falls through half its level, and, given the master
/// sharpened, how deep its trough falls below the stack there; then the first and second harmonics of the radii (an outline off the
/// planet's centre, or misshapen) and where the deepest trough lies against the sector whose edge lies furthest inside the outline.
/// </summary>
internal sealed class PlanetaryLimbSectorsSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var masterArg = new Argument<string>("master") { Description = "A linear planetary master (planetary-stack's master_*.fits)." };
        var sharpenedOpt = new Option<string?>("--sharpened") { Description = "The master sharpened: each sector's trough below the stack." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var sectorsOpt = new Option<int>("--sectors") { Description = "How many position-angle sectors.", DefaultValueFactory = _ => 16 };

        var command = new Command("planetary-limb-sectors",
            "A master's limb read sector by sector against the limb fit's outline, and the trough of its sharpening there (#1171).")
        {
            Arguments = { masterArg },
            Options = { sharpenedOpt, planetOpt, sectorsOpt },
        };

        command.SetAction((parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var path = parseResult.GetValue(masterArg) ?? "";
            if (!Image.TryReadFitsFile(path, out var master))
            {
                consoleHost.WriteError($"{path}: not a readable FITS image");
                return System.Threading.Tasks.Task.FromResult(1);
            }
            Image? sharpened = null;
            try
            {
                if (parseResult.GetValue(sharpenedOpt) is { } sharpenedPath && !Image.TryReadFitsFile(sharpenedPath, out sharpened))
                {
                    consoleHost.WriteError($"{sharpenedPath}: not a readable FITS image");
                    return System.Threading.Tasks.Task.FromResult(1);
                }
                var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
                if (PlanetaryBestStack.InstantOf(master, null) is not { } instant)
                {
                    consoleHost.WriteError($"{path}: no time in its header");
                    return System.Threading.Tasks.Task.FromResult(1);
                }
                var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, instant));
                if (PlanetaryLimbFit.Fit(master, limbOptions) is not { } fit)
                {
                    consoleHost.WriteError($"{path}: its limb could not be fitted");
                    return System.Threading.Tasks.Task.FromResult(1);
                }
                var disk = MetricDisk.From(fit, limbOptions.AxisRatio);
                var sectors = parseResult.GetValue(sectorsOpt);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{System.IO.Path.GetFileName(path)}: the outline at ({fit.CenterX:0.00}, {fit.CenterY:0.00}), {fit.EquatorialRadius:0.00} px, axis {fit.AxisAngleDeg:0.0} deg, blur {fit.PsfSigma:0.00} px; {sectors} sectors"));
                for (var c = 0; c < master.ChannelCount; c++)
                {
                    var stack = PlanetaryMetrics.Normalise(master.GetChannelSpan(c), master.Width, master.Height, disk);
                    var radii = PlanetaryMetrics.SectorHalfLevelRadii(stack, master.Width, master.Height, disk, sectors);
                    var troughs = sharpened is not null
                        ? PlanetaryMetrics.SectorTroughs(PlanetaryMetrics.Normalise(sharpened.GetChannelSpan(c), master.Width, master.Height, disk), stack, master.Width, master.Height, disk, sectors)
                        : null;
                    var label = master.ChannelCount > 1 ? $" channel {c}" : "";
                    for (var k = 0; k < sectors; k++)
                    {
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"[planetary]{label} sector at {(k + 0.5) * 360.0 / sectors,5:0.0} deg: half level at {radii[k]:0.0000} radii{(troughs is not null ? $", trough {troughs[k]:0.0000}" : "")}"));
                    }
                    var (a1, p1) = PlanetaryMetrics.Harmonic(radii, 1);
                    var (a2, p2) = PlanetaryMetrics.Harmonic(radii, 2);
                    var finite = radii.Where(double.IsFinite).ToArray();
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"[planetary]{label} half level: mean {(finite.Length > 0 ? finite.Average() : double.NaN):0.0000} radii; first harmonic {a1:0.0000} radii ({a1 * fit.EquatorialRadius:0.00} px) largest at {p1:0.0} deg; second {a2:0.0000} radii ({a2 * fit.EquatorialRadius:0.00} px) largest at {p2:0.0} and {p2 + 180:0.0} deg"));
                    if (troughs is not null)
                    {
                        var deepest = Array.IndexOf(troughs, troughs.Max());
                        var innermost = Enumerable.Range(0, sectors).Where(k => double.IsFinite(radii[k])).OrderBy(k => radii[k]).FirstOrDefault();
                        var apart = Math.Abs(((((deepest - innermost) * 360.0 / sectors) + 540) % 360) - 180);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"[planetary]{label} the deepest trough ({troughs[deepest]:0.0000}) at {(deepest + 0.5) * 360.0 / sectors:0.0} deg; the edge furthest inside the outline at {(innermost + 0.5) * 360.0 / sectors:0.0} deg; {apart:0.0} deg apart"));
                    }
                }
                return System.Threading.Tasks.Task.FromResult(0);
            }
            finally
            {
                master.Release();
                sharpened?.Release();
            }
        });
        return command;
    }
}
