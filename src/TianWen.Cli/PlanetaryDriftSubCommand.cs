using System;
using System.Collections.Immutable;
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
/// <c>planetary drift</c> (docs/plans/planetary-restoration.md, R6 part 3): a run's first and last thirds, each stacked with every
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
        var truthOpt = new Option<string?>("--truth")
        {
            Description = "A global map (OPAL's) rendered at each third's own disk and instant, blurred by its limb PSF, and measured the same way: a map that does not move, so what it reads is the measurement's own zero.",
        };
        var sameInstantOpt = new Option<bool>("--same-instant")
        {
            Description = "Instead of the first and last thirds, the captures beginning in the middle third taken alternately and both carried to one instant: two stacks of different frames at one epoch, whose shift is the stacks' own error.",
        };

        var command = new Command("drift",
            "A run's first and last thirds stacked, each de-rotated to its own middle, projected onto latitude and System III longitude, and the shift along each band of latitude between them read as the zonal wind (R6 part 3).")
        {
            Arguments = { capturesArg },
            Options = { planetOpt, channelOpt, keepOpt, outputOpt, truthOpt, sameInstantOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var channel = (parseResult.GetValue(channelOpt) ?? "r").ToLowerInvariant() switch { "g" => 1, "b" => 2, _ => 0 };
            PlanetMap? truth = null;
            if (parseResult.GetValue(truthOpt) is { } truthPath && (truth = PlanetMap.ReadFits(truthPath)) is null)
            {
                consoleHost.WriteError($"{truthPath}: no map");
                return 1;
            }
            using var run = PlanetaryFrameSequence.OpenSer(parseResult.GetValue(capturesArg) ?? []);
            // The run's span is its frames' earliest and latest times, never its first and last frames' (#1292).
            if (run.CaptureSpan is not (var start, var end))
            {
                consoleHost.WriteError("the captures carry no timestamps");
                return 1;
            }
            var third = (end - start) / 3;
            var options = new PlanetaryStackOptions
            {
                KeepFraction = parseResult.GetValue(keepOpt),
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                Derotation = new PlanetaryDerotationOptions(planet),
            };
            IPlanetaryFrameStream earlyStream, lateStream;
            if (parseResult.GetValue(sameInstantOpt))
            {
                // The captures that begin in the middle third, taken alternately, each set carried to the one instant halfway
                // through them: two stacks of different frames at one epoch, which nothing but the stacks themselves tells apart.
                var middle = Enumerable.Range(0, run.PartCount).Where(p => run.TimestampOf(run.StartOf(p)) is { } t && t >= start + third && t < end - third).ToArray();
                if (middle.Length < 2)
                {
                    consoleHost.WriteError("fewer than two captures begin in the run's middle third");
                    return 1;
                }
                var last = middle[^1];
                if (run.TimestampOf(run.StartOf(middle[0])) is not { } from || run.TimestampOf(run.StartOf(last) + run.CountOf(last) - 1) is not { } to)
                {
                    consoleHost.WriteError("the captures carry no timestamps");
                    return 1;
                }
                PlanetaryFrameSequence Alternate(int parity) => new(middle.Where((_, i) => i % 2 == parity).Select(p => new PlanetaryFrameWindow(run, run.StartOf(p), run.CountOf(p))));
                (earlyStream, lateStream) = (Alternate(0), Alternate(1));
                options = options with { Derotation = new PlanetaryDerotationOptions(planet) { Epoch = from + ((to - from) / 2) } };
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{run.PartCount} captures, {run.FrameCount} frames over {(end - start).TotalMinutes:0.0} min; the {middle.Length} beginning in the middle third taken alternately, {earlyStream.FrameCount} and {lateStream.FrameCount} frames, both carried to {from + ((to - from) / 2):HH:mm:ss.f} UTC"));
            }
            else
            {
                var firstEnd = FirstAtOrAfter(run, start + third);
                var lastStart = FirstAtOrAfter(run, end - third);
                consoleHost.WriteScrollable(string.Create(inv, $"{run.PartCount} captures, {run.FrameCount} frames over {(end - start).TotalMinutes:0.0} min; thirds of {firstEnd} and {run.FrameCount - lastStart} frames"));
                (earlyStream, lateStream) = (new PlanetaryFrameWindow(run, 0, firstEnd), new PlanetaryFrameWindow(run, lastStart, run.FrameCount - lastStart));
            }
            using var earlyOwned = earlyStream;
            using var lateOwned = lateStream;
            if (await ProjectAsync("the first", earlyStream, options, planet, channel, ct) is not { } early
                || await ProjectAsync("the second", lateStream, options, planet, channel, ct) is not { } late)
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
            // The zero: the map rendered where each third's stack puts the disk, at its instant and through its limb PSF, so the
            // same projection and correlation read a planet that does not move.
            ImmutableArray<(double ShiftDeg, double Correlation)>? zero = truth is { } still
                ? PlanetaryZonalDrift.Drift(
                    PlanetaryZonalDrift.Project(Rendered(still, early), early.Projection, early.Fit.LimbDarkening, minLatitude, rows, westLow, columns, StepDeg),
                    PlanetaryZonalDrift.Project(Rendered(still, late), late.Projection, late.Fit.LimbDarkening, minLatitude, rows, westLow, columns, StepDeg),
                    StepDeg)
                : null;
            // Two stacks at one instant have no time between them to read a wind over: their shift is only said.
            var apart = seconds >= 1;
            consoleHost.WriteScrollable(string.Create(inv,
                $"the stacks' middles {seconds / 60:0.00} min apart, over System III {westLow:0.0} to {westHigh:0.0} deg west; {(apart ? "the zonal wind" : "the shift")} by 2-degree band{(apart ? " (eastward positive)" : "")}{(zero is null ? "" : ", and a still map's rendered at both stacks' disks")}:"));

            var csv = new StringBuilder(zero is null ? "latitude,shift_deg,correlation\n" : "latitude,shift_deg,correlation,zero_shift_deg,zero_correlation\n");
            for (var r = 0; r < rows; r++)
            {
                csv.Append(string.Create(inv, $"{minLatitude + (r * StepDeg):0.00},{drift[r].ShiftDeg:G6},{drift[r].Correlation:G6}"));
                csv.Append(zero is { } z ? string.Create(inv, $",{z[r].ShiftDeg:G6},{z[r].Correlation:G6}\n") : "\n");
            }
            var perBand = (int)Math.Round(2 / StepDeg);
            csv.Append(zero is null ? "band_latitude,wind_ms,rows\n" : "band_latitude,wind_ms,rows,zero_wind_ms\n");
            for (var first = 0; first + perBand <= rows; first += perBand)
            {
                var shifts = Enumerable.Range(first, perBand).Select(r => drift[r].ShiftDeg).Where(double.IsFinite).ToArray();
                var middle = minLatitude + ((first + ((perBand - 1) / 2.0)) * StepDeg);
                if (shifts.Length < perBand / 2)
                {
                    continue;
                }
                var shift = shifts.Average();
                var wind = apart ? PlanetaryZonalDrift.WindOf(shift, seconds, middle) : double.NaN;
                // The shift on the first stack's disk, in pixels along the circle of latitude at the disk's middle.
                var pixels = shift * Math.PI / 180 * early.Fit.EquatorialRadius * Math.Cos(middle * Math.PI / 180);
                var spread = shifts.Length > 1 ? Math.Sqrt(shifts.Sum(s => (s - shift) * (s - shift)) / (shifts.Length - 1)) : double.NaN;
                var stillWind = zero is { } z2 && Enumerable.Range(first, perBand).Select(r => z2[r].ShiftDeg).Where(double.IsFinite).ToArray() is { Length: > 0 } zs
                    ? (apart ? PlanetaryZonalDrift.WindOf(zs.Average(), seconds, middle) : zs.Average())
                    : double.NaN;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {middle,6:+0.0;-0.0}  {(apart ? $"{wind,7:+0;-0} m/s  " : "")}(shift {shift:+0.000;-0.000} deg, {pixels:+0.00;-0.00} px, over {shifts.Length} rows, their spread {spread:0.000}){(zero is null ? "" : apart ? $"  still map {stillWind,5:+0;-0} m/s" : $"  still map {stillWind:+0.000;-0.000} deg")}"));
                csv.Append(string.Create(inv, $"{middle:0.00},{wind:G6},{shifts.Length}{(zero is null ? "" : $",{stillWind:G6}")}\n"));
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
    private sealed record Projected(Image Plane, PlanetAspect Aspect, LimbFit Fit, DiskPlacement Disk, PlanetaryProjection Projection);

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
        var disk = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, north);
        return new Projected(plane, aspect, fit, disk, new PlanetaryProjection(aspect, disk));
    }

    // `map` as the stack of `third` would show it with the planet still: rendered on the stack's disk at its instant, lit with its
    // limb darkening and blurred by its limb PSF.
    private static Image Rendered(PlanetMap map, Projected third)
    {
        var (width, height) = (third.Plane.Width, third.Plane.Height);
        var values = PlanetaryRender.Render(map, third.Aspect, third.Disk, width, height, third.Fit.LimbDarkening, supersample: 4);
        var plane = new float[height, width];
        Buffer.BlockCopy(values, 0, plane, 0, values.Length * sizeof(float));
        return Image.FromChannel(plane).GaussianBlur((float)third.Fit.PsfSigma);
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
