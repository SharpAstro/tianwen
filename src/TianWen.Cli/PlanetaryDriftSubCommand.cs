using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-drift</c> (docs/plans/planetary-restoration.md, R6 part 3): a run's first and last thirds, each stacked with every
/// frame carried to its own middle, projected onto planetographic latitude and System III longitude at their own instants
/// (<see cref="PlanetaryZonalDrift"/>), and the shift along each band of latitude between them: the zonal wind against System
/// III, which a de-rotation leaves in. Bands are 2 degrees, each the mean of its rows' shifts, and the wind is read from the
/// time between the two thirds' middles.
/// </summary>
internal sealed class PlanetaryDriftSubCommand(IConsoleHost consoleHost)
{
    private const double StepDeg = 0.25;

    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "The run's SER captures, joined in time order.", Arity = ArgumentArity.OneOrMore };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var channelOpt = new Option<string>("--channel") { Description = "A colour stack's channel read: r, g or b.", DefaultValueFactory = _ => "r" };
        var keepOpt = new Option<double>("--keep") { Description = "The fraction of each third's frames stacked.", DefaultValueFactory = _ => 0.05 };
        var outputOpt = new Option<string?>("--output") { Description = "Write each row's shift and the bands' winds as CSV here." };

        var command = new Command("planetary-drift",
            "A run's first and last thirds stacked, each de-rotated to its own middle, projected onto latitude and System III longitude, and the shift along each band of latitude between them read as the zonal wind (R6 part 3).")
        {
            Arguments = { capturesArg },
            Options = { planetOpt, channelOpt, keepOpt, outputOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var channel = (parseResult.GetValue(channelOpt) ?? "r").ToLowerInvariant() switch { "g" => 1, "b" => 2, _ => 0 };
            using var run = PlanetaryFrameSequence.OpenSer(parseResult.GetValue(capturesArg) ?? []);
            if (run.TimestampOf(0) is not { } start || run.TimestampOf(run.FrameCount - 1) is not { } end)
            {
                consoleHost.WriteError("the captures carry no timestamps");
                return 1;
            }
            var third = (end - start) / 3;
            var firstEnd = FirstAtOrAfter(run, start + third);
            var lastStart = FirstAtOrAfter(run, end - third);
            consoleHost.WriteScrollable(string.Create(inv, $"{run.PartCount} captures, {run.FrameCount} frames over {(end - start).TotalMinutes:0.0} min; thirds of {firstEnd} and {run.FrameCount - lastStart} frames"));
            using var earlyStream = new PlanetaryFrameWindow(run, 0, firstEnd);
            using var lateStream = new PlanetaryFrameWindow(run, lastStart, run.FrameCount - lastStart);
            var options = new PlanetaryStackOptions
            {
                KeepFraction = parseResult.GetValue(keepOpt),
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                Derotation = new PlanetaryDerotationOptions(planet),
            };
            if (await ProjectAsync("the first third", earlyStream, options, planet, channel, ct) is not { } early
                || await ProjectAsync("the last third", lateStream, options, planet, channel, ct) is not { } late)
            {
                return 1;
            }

            // One grid for both: planetographic latitude 40 S to 40 N, and the System III longitudes within 40 degrees of both
            // central meridians.
            var (minLatitude, rows) = (-40.0, (int)Math.Round(80 / StepDeg) + 1);
            var westLow = Math.Max(early.Aspect.CentralMeridianIII, Unwrap(late.Aspect.CentralMeridianIII, early.Aspect.CentralMeridianIII)) - 40;
            var westHigh = Math.Min(early.Aspect.CentralMeridianIII, Unwrap(late.Aspect.CentralMeridianIII, early.Aspect.CentralMeridianIII)) + 40;
            var columns = (int)Math.Floor((westHigh - westLow) / StepDeg) + 1;
            var mapEarly = PlanetaryZonalDrift.Project(early.Plane, early.Projection, early.Fit.LimbDarkening, minLatitude, rows, westLow, columns, StepDeg);
            var mapLate = PlanetaryZonalDrift.Project(late.Plane, late.Projection, late.Fit.LimbDarkening, minLatitude, rows, westLow, columns, StepDeg);
            var drift = PlanetaryZonalDrift.Drift(mapEarly, mapLate, StepDeg);
            var seconds = (late.Aspect.Utc - early.Aspect.Utc).TotalSeconds;
            consoleHost.WriteScrollable(string.Create(inv,
                $"the thirds' middles {seconds / 60:0.00} min apart, over System III {westLow:0.0} to {westHigh:0.0} deg west; the zonal wind by 2-degree band (eastward positive):"));

            var csv = new StringBuilder("latitude,shift_deg,correlation\n");
            for (var r = 0; r < rows; r++)
            {
                csv.Append(string.Create(inv, $"{minLatitude + (r * StepDeg):0.00},{drift[r].ShiftDeg:G6},{drift[r].Correlation:G6}\n"));
            }
            var perBand = (int)Math.Round(2 / StepDeg);
            csv.Append("band_latitude,wind_ms,rows\n");
            for (var first = 0; first + perBand <= rows; first += perBand)
            {
                var shifts = Enumerable.Range(first, perBand).Select(r => drift[r].ShiftDeg).Where(double.IsFinite).ToArray();
                var middle = minLatitude + ((first + ((perBand - 1) / 2.0)) * StepDeg);
                if (shifts.Length < perBand / 2)
                {
                    continue;
                }
                var shift = shifts.Average();
                var wind = PlanetaryZonalDrift.WindOf(shift, seconds, middle);
                var spread = shifts.Length > 1 ? Math.Sqrt(shifts.Sum(s => (s - shift) * (s - shift)) / (shifts.Length - 1)) : double.NaN;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {middle,6:+0.0;-0.0}  {wind,7:+0;-0} m/s  (shift {shift:+0.000;-0.000} deg over {shifts.Length} rows, their spread {spread:0.000})"));
                csv.Append(string.Create(inv, $"{middle:0.00},{wind:G6},{shifts.Length}\n"));
            }
            if (parseResult.GetValue(outputOpt) is { } output)
            {
                File.WriteAllText(output, csv.ToString());
                consoleHost.WriteScrollable($"    wrote the shifts and winds to {output}");
            }
            return 0;
        });
        return command;
    }

    // One third's stack, its channel, its disk and its projection at its own middle.
    private sealed record Projected(Image Plane, PlanetAspect Aspect, LimbFit Fit, PlanetaryProjection Projection);

    private async Task<Projected?> ProjectAsync(string name, IPlanetaryFrameStream stream, PlanetaryStackOptions options, CatalogIndex planet, int channel, CancellationToken ct)
    {
        var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
        if (result.Epoch is not { } when)
        {
            consoleHost.WriteError($"{name}: no epoch");
            return null;
        }
        var aspect = PhysicalEphemeris.Compute(planet, when);
        var plane = result.Master.ChannelCount == 1 ? result.Master : result.Master.ChannelImage(channel);
        if (PlanetaryLimbFit.Fit(plane, PlanetaryLimbFit.OptionsFor(aspect)) is not { } fit)
        {
            consoleHost.WriteError($"{name}: the stack's limb could not be fitted");
            return null;
        }
        // Its north at the end of the axis its frames decided, as the run's quarters told it.
        var north = fit.NorthAngleDeg + (result.North is { } d && Math.Abs(Math.IEEERemainder(fit.NorthAngleDeg - d.NorthAngleDeg, 360)) > 90 ? 180 : 0);
        consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {result.FramesUsed} of {result.FramesGraded} frames at {when:HH:mm:ss.f} UTC, CM III {aspect.CentralMeridianIII:0.00}; disk R {fit.EquatorialRadius:0.00} px, north {north:0.0} deg, k {fit.LimbDarkening:0.000}"));
        return new Projected(plane, aspect, fit, new PlanetaryProjection(aspect, new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, north)));
    }

    // `west` moved by whole turns to lie within half a turn of `near`.
    private static double Unwrap(double west, double near) => near + Math.IEEERemainder(west - near, 360);

    private static int FirstAtOrAfter(IPlanetaryFrameStream run, DateTimeOffset when)
    {
        var (low, high) = (0, run.FrameCount);
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (run.TimestampOf(mid) < when)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }
}
