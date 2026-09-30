using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-belts</c> (docs/plans/planetary-restoration.md, R6 part 3): a capture, or a run of them de-rotated frame by frame,
/// stacked and projected onto the spheroid, its zonal albedo profile read along planetographic latitude
/// (<see cref="PlanetaryBelts"/>), and its belts' edges compared with a map's, OPAL's, blurred to the stack's own limb PSF. The
/// check on the projection's latitudes the plan moved here from R1: a belt's edge is where the planet itself puts a latitude.
/// </summary>
internal sealed class PlanetaryBeltsSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "One SER capture, or a run's, joined in time order.", Arity = ArgumentArity.OneOrMore };
        var mapOpt = new Option<string>("--map") { Description = "The global map to compare with (an OPAL FITS map, planetographic latitude).", Required = true };
        var channelOpt = new Option<string>("--channel") { Description = "A colour stack's channel read: r, g or b.", DefaultValueFactory = _ => "r" };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var keepOpt = new Option<double>("--keep") { Description = "The fraction of the frames stacked.", DefaultValueFactory = _ => 0.05 };
        var derotateOpt = new Option<bool>("--derotate") { Description = "Carry every frame to the run's middle first (R6 part 2)." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var outputOpt = new Option<string?>("--output") { Description = "Write both profiles as CSV here (latitude, stack, map)." };
        var kernelOpt = new Option<string>("--kernel") { Description = "What the map is blurred by: core (the limb fit's core alone, R6 part 3) or limb (its core and its halo, R7 part 3).", DefaultValueFactory = _ => "core" };

        var command = new Command("planetary-belts",
            "A capture or a run stacked and projected onto the spheroid, its zonal albedo profile along planetographic latitude and its belts' edges compared with a global map's (R6 part 3).")
        {
            Arguments = { capturesArg },
            Options = { mapOpt, channelOpt, planetOpt, keepOpt, derotateOpt, framesOpt, outputOpt, kernelOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var mapPath = parseResult.GetValue(mapOpt) ?? "";
            if (PlanetMap.ReadFits(mapPath) is not { } map)
            {
                consoleHost.WriteError($"{mapPath}: no map");
                return 1;
            }
            var paths = parseResult.GetValue(capturesArg) ?? [];
            using IPlanetaryFrameStream whole = paths.Length == 1 ? SerFrameStream.Open(paths[0]) : PlanetaryFrameSequence.OpenSer(paths);
            using var stream = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
            var options = new PlanetaryStackOptions
            {
                KeepFraction = parseResult.GetValue(keepOpt),
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                Derotation = parseResult.GetValue(derotateOpt) ? new PlanetaryDerotationOptions(planet) : null,
            };
            var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
            if ((result.Epoch ?? stream.MidCapture) is not { } when)
            {
                consoleHost.WriteError("the captures carry no timestamps");
                return 1;
            }
            var aspect = PhysicalEphemeris.Compute(planet, when);
            var channel = (parseResult.GetValue(channelOpt) ?? "r").ToLowerInvariant() switch { "g" => 1, "b" => 2, _ => 0 };
            var plane = result.Master.ChannelCount == 1 ? result.Master : result.Master.ChannelImage(channel);
            if (PlanetaryLimbFit.Fit(plane, PlanetaryLimbFit.OptionsFor(aspect)) is not { } fit)
            {
                consoleHost.WriteError("the stack's limb could not be fitted");
                return 1;
            }
            // The map at the stack's resolution: its limb PSF, in degrees of latitude at the disk's middle.
            var blurDeg = fit.PsfSigma / fit.EquatorialRadius * 180 / Math.PI;
            var withHalo = (parseResult.GetValue(kernelOpt) ?? "core").ToLowerInvariant() == "limb";
            var haloDeg = fit.HaloWidth / fit.EquatorialRadius * 180 / Math.PI;
            var reference = withHalo ? PlanetaryBelts.FromMap(map, blurDeg, fit.HaloFraction, haloDeg) : PlanetaryBelts.FromMap(map, blurDeg);
            if (withHalo)
            {
                consoleHost.WriteScrollable(string.Create(inv, $"    the map blurred by the limb fit's core and halo: {fit.HaloFraction:P1} in a halo of {haloDeg:0.00} deg"));
            }
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileNameWithoutExtension(paths[0])}{(paths.Length > 1 ? $" and {paths.Length - 1} more" : "")}: {result.FramesUsed} of {result.FramesGraded} frames at {when:yyyy-MM-dd HH:mm:ss} UTC, CM III {aspect.CentralMeridianIII:0.0}, sub-observer latitude {aspect.SubObserverLatitude:+0.00;-0.00}; disk R {fit.EquatorialRadius:0.00} px, PSF sigma {fit.PsfSigma:0.00} px ({blurDeg:0.00} deg), k {fit.LimbDarkening:0.000}; against {Path.GetFileName(mapPath)}"));

            // North: the run's own where its frames decided it; else both ends of the fitted axis, the map's agreement choosing, and
            // both said.
            var norths = result.North is { } decided
                ? [fit.NorthAngleDeg + (Math.Abs(Math.IEEERemainder(fit.NorthAngleDeg - decided.NorthAngleDeg, 360)) > 90 ? 180 : 0)]
                : new[] { fit.NorthAngleDeg, fit.NorthAngleDeg + 180 };
            var best = (Profile: default(ZonalProfile), North: double.NaN, Shift: double.NaN, Correlation: double.NegativeInfinity);
            foreach (var north in norths)
            {
                var projection = new PlanetaryProjection(aspect, new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, north));
                var profile = PlanetaryBelts.FromImage(plane.GetChannelSpan(0), plane.Width, plane.Height, projection, aspect.CentralMeridianIII, fit.LimbDarkening);
                var (shift, correlation) = PlanetaryBelts.Offset(profile, reference);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    north at {north % 360:0.0} deg{(result.North is null ? "" : " (the run's)")}: the slopes correlate {correlation:0.000} with the map's, best laid on them {shift:+0.00;-0.00} deg"));
                if (correlation > best.Correlation)
                {
                    best = (profile, north, shift, correlation);
                }
            }
            if (best.Profile is not { } chosen)
            {
                consoleHost.WriteError("no profile");
                return 1;
            }

            // Each of the map's edges against the stack's nearest of the same sense within 4 degrees.
            var mapEdges = PlanetaryBelts.Edges(reference);
            var stackEdges = PlanetaryBelts.Edges(chosen);
            consoleHost.WriteScrollable(string.Create(inv, $"    the belts' edges, planetographic latitude (map / stack / stack minus map), north at {best.North % 360:0.0} deg:"));
            var misses = new List<double>();
            foreach (var edge in mapEdges)
            {
                var match = stackEdges.Where(s => Math.Sign(s.Slope) == Math.Sign(edge.Slope) && Math.Abs(s.Latitude - edge.Latitude) <= 4)
                    .OrderBy(s => Math.Abs(s.Latitude - edge.Latitude)).Cast<BeltEdge?>().FirstOrDefault();
                var sense = edge.Slope > 0 ? "brighter north" : "darker north";
                if (match is { } m)
                {
                    misses.Add(m.Latitude - edge.Latitude);
                    consoleHost.WriteScrollable(string.Create(inv, $"      {edge.Latitude,7:+0.00;-0.00}  {m.Latitude,7:+0.00;-0.00}  {m.Latitude - edge.Latitude,6:+0.00;-0.00}   ({sense}, slope {edge.Slope:0.000} / {m.Slope:0.000})"));
                }
                else
                {
                    consoleHost.WriteScrollable(string.Create(inv, $"      {edge.Latitude,7:+0.00;-0.00}  (none within 4 deg)   ({sense}, slope {edge.Slope:0.000})"));
                }
            }
            if (misses.Count > 0)
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {misses.Count} of {mapEdges.Length} edges matched: largest miss {misses.Max(Math.Abs):0.00} deg, RMS {Math.Sqrt(misses.Average(d => d * d)):0.00} deg, {misses.Count(d => Math.Abs(d) <= 1)} within 1 deg; best single offset {best.Shift:+0.00;-0.00} deg"));
            }

            if (parseResult.GetValue(outputOpt) is { } output)
            {
                var csv = new StringBuilder("latitude,stack,map\n");
                for (var b = 0; b < chosen.Albedo.Length; b++)
                {
                    csv.Append(string.Create(inv, $"{PlanetaryBelts.LatitudeOf(b):0.00},{chosen.Albedo[b]:G6},{reference.Albedo[b]:G6}\n"));
                }
                File.WriteAllText(output, csv.ToString());
                consoleHost.WriteScrollable($"    wrote both profiles to {output}");
            }
            return 0;
        });
        return command;
    }
}
