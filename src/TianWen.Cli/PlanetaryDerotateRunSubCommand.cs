using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary derotate-run</c> (docs/plans/planetary-restoration.md, R6 part 2): a night's captures joined in time order and split
/// at the run's middle into two halves, each stacked as its frames were taken and with every frame carried to the run's middle
/// (6a). The halves are then compared three ways over the pixels all three cover: as taken, as finished stacks carried to one
/// epoch (6b, part 1's <c>planetary derotate</c>, both ends of the axis as north), and de-rotated frame by frame. With
/// <c>--whole</c>, the whole run stacked both ways too, and each wavelet band's power in the de-rotated stack against the stack as
/// taken: the same frames, so the same noise, and a rotation smeared along the belts is detail lost.
/// </summary>
internal sealed class PlanetaryDerotateRunSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "The run's SER captures, in any order: they are joined in time order.", Arity = ArgumentArity.OneOrMore };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var keepOpt = new Option<double>("--keep") { Description = "The fraction of each half's frames stacked.", DefaultValueFactory = _ => 0.05 };
        var methodOpt = new Option<string>("--method") { Description = "global (whole-disk registration) or ap (alignment points, each frame weighted as a whole).", DefaultValueFactory = _ => "global" };
        var spacingOpt = new Option<int>("--ap-spacing") { Description = "The alignment points' spacing (ap only).", DefaultValueFactory = _ => 12 };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The alignment points' patch, a power of two (ap only).", DefaultValueFactory = _ => 16 };
        var wholeOpt = new Option<bool>("--whole") { Description = "Stack the whole run both ways as well, and compare each band's power." };
        var outputOpt = new Option<string?>("--output") { Description = "Write every stack as FITS into this folder." };

        var command = new Command("derotate-run",
            "A run of captures split at its middle, each half stacked as taken and with every frame de-rotated to the run's middle (R6, 6a), and the halves compared as taken, as finished stacks carried to one epoch (6b) and de-rotated frame by frame.")
        {
            Arguments = { capturesArg },
            Options = { planetOpt, keepOpt, methodOpt, spacingOpt, patchOpt, wholeOpt, outputOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var method = (parseResult.GetValue(methodOpt) ?? "global").ToLowerInvariant();
            if (method is not ("global" or "ap"))
            {
                consoleHost.WriteError($"--method {method}: global or ap");
                return 1;
            }
            var inv = CultureInfo.InvariantCulture;
            using var run = PlanetaryFrameSequence.OpenSer(parseResult.GetValue(capturesArg) ?? []);
            if (run.MidCapture is not { } epoch || run.CaptureSpan is not { Earliest: var start, Latest: var end })
            {
                consoleHost.WriteError("the captures carry no timestamps");
                return 1;
            }
            var split = FirstAtOrAfter(run, epoch);
            consoleHost.WriteScrollable(string.Create(inv,
                $"{run.PartCount} captures, {run.FrameCount} frames, {start:HH:mm:ss} to {end:HH:mm:ss} UTC ({(end - start).TotalMinutes:0.00} min); halves of {split} and {run.FrameCount - split} frames either side of {epoch:HH:mm:ss.f}"));
            var options = new PlanetaryStackOptions
            {
                KeepFraction = parseResult.GetValue(keepOpt),
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                PerPointQualityWeighting = false,
                AlignmentPointSpacing = parseResult.GetValue(spacingOpt),
                AlignmentPatchSize = parseResult.GetValue(patchOpt),
            };
            var derotated = options with { Derotation = new PlanetaryDerotationOptions(planet) { Epoch = epoch } };
            var folder = parseResult.GetValue(outputOpt);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            using var first = new PlanetaryFrameWindow(run, 0, split);
            using var second = new PlanetaryFrameWindow(run, split, run.FrameCount - split);
            if (await StackAsync("first half, as taken", first, options, planet, method, folder, ct) is not { } firstTaken
                || await StackAsync("second half, as taken", second, options, planet, method, folder, ct) is not { } secondTaken
                || await StackAsync("first half, de-rotated", first, derotated, planet, method, folder, ct) is not { } firstTurned
                || await StackAsync("second half, de-rotated", second, derotated, planet, method, folder, ct) is not { } secondTurned)
            {
                return 1;
            }

            // Every comparison onto the second half's disk, with ONE north, the second's: one camera took the run, and two stacks
            // moved onto each other with each its own fitted north are turned by the fits' difference too (2.9 degrees between the
            // de-rotated halves' own, which alone put them 0.018 apart). As taken: the first moved there with no rotation. 6b:
            // carried from its epoch to the second's, each way round. 6a: both already at the run's middle, the first moved there.
            var k = secondTaken.Fit.LimbDarkening;
            var none = PlanetaryDerotation.Derotate(firstTaken.Master, secondTaken.Aspect, firstTaken.Placement with { NorthAngleDeg = secondTaken.Placement.NorthAngleDeg },
                secondTaken.Aspect, secondTaken.Placement, k);
            var frames = PlanetaryDerotation.Derotate(firstTurned.Master, secondTurned.Aspect, firstTurned.Placement with { NorthAngleDeg = secondTurned.Placement.NorthAngleDeg },
                secondTurned.Aspect, secondTurned.Placement, k);
            var stacks = new (double North, Derotation Carried)[2];
            for (var i = 0; i < 2; i++)
            {
                var to = secondTaken.Placement with { NorthAngleDeg = secondTaken.Placement.NorthAngleDeg + (180.0 * i) };
                var from = firstTaken.Placement with { NorthAngleDeg = to.NorthAngleDeg };
                stacks[i] = (to.NorthAngleDeg, PlanetaryDerotation.Derotate(firstTaken.Master, firstTaken.Aspect, from, secondTaken.Aspect, to, k));
            }
            var covered = new bool[none.Covered.Length];
            for (var i = 0; i < covered.Length; i++)
            {
                covered[i] = none.Covered[i] && frames.Covered[i] && stacks[0].Carried.Covered[i] && stacks[1].Carried.Covered[i];
            }
            var disk = secondTaken.Placement;
            var (noneRms, pixels) = PlanetaryDerotation.DifferenceRms(none.Image, secondTaken.Master, disk, covered);
            consoleHost.WriteScrollable(string.Create(inv,
                $"the halves over the {pixels} pixels inside 0.9 radii all three cover, each on its own disk level ({(secondTaken.Aspect.Utc - firstTaken.Aspect.Utc).TotalMinutes:0.00} min apart as taken):"));
            consoleHost.WriteScrollable(string.Create(inv, $"    as taken: {noneRms:0.00000} RMS"));
            foreach (var (north, carried) in stacks)
            {
                var rms = PlanetaryDerotation.DifferenceRms(carried.Image, secondTaken.Master, disk, covered).Rms;
                consoleHost.WriteScrollable(string.Create(inv, $"    finished stacks carried to one epoch (6b), north at {north:0.0} deg: {rms:0.00000}, {rms / noneRms:0.000} of as taken"));
            }
            var framesRms = PlanetaryDerotation.DifferenceRms(frames.Image, secondTurned.Master, disk, covered).Rms;
            consoleHost.WriteScrollable(string.Create(inv, $"    every frame carried to the run's middle (6a): {framesRms:0.00000}, {framesRms / noneRms:0.000} of as taken"));

            if (parseResult.GetValue(wholeOpt))
            {
                if (await StackAsync("the whole run, as taken", run, options, planet, method, folder, ct) is not { } wholeTaken
                    || await StackAsync("the whole run, de-rotated", run, derotated, planet, method, folder, ct) is not { } wholeTurned)
                {
                    return 1;
                }
                var (taken, turned) = (BandPower(wholeTaken), BandPower(wholeTurned));
                consoleHost.WriteScrollable("    each band's power inside 0.8 radii, de-rotated against as taken: "
                    + string.Join("  ", taken.Select((p, j) => string.Create(inv, $"{j + 1}: {turned[j] / p:0.000}"))));
            }
            return 0;
        });
        return command;
    }

    // One stack, its epoch (the run's middle when de-rotated, else its frames' middle) and its disk.
    private sealed record Stack(Image Master, PlanetAspect Aspect, LimbFit Fit, DiskPlacement Placement);

    private async Task<Stack?> StackAsync(string name, IPlanetaryFrameStream stream, PlanetaryStackOptions options, CatalogIndex planet, string method,
        string? folder, CancellationToken ct)
    {
        var stacker = new LuckyImagingStacker();
        var result = method == "ap" ? await stacker.StackAsync(stream, options, ct) : await stacker.StackGlobalAsync(stream, options, ct);
        if ((result.Epoch ?? stream.MidCapture) is not { } when)
        {
            consoleHost.WriteError($"{name}: no timestamps");
            return null;
        }
        var aspect = PhysicalEphemeris.Compute(planet, when);
        if (PlanetaryLimbFit.Fit(result.Master, PlanetaryLimbFit.OptionsFor(aspect)) is not { } fit)
        {
            consoleHost.WriteError($"{name}: the stack's limb could not be fitted");
            return null;
        }
        var inv = CultureInfo.InvariantCulture;
        var north = result.North is { } decided
            ? string.Create(inv, $"; its frames' north {decided.NorthAngleDeg:0.0} deg (the quarters {decided.AgreementAsFitted:0.00000} apart as fitted, {decided.AgreementTurnedOver:0.00000} turned over)")
            : "";
        consoleHost.WriteScrollable(string.Create(inv,
            $"{name}: {result.FramesUsed} of {result.FramesGraded} frames at {when:HH:mm:ss.f} UTC, CM III {aspect.CentralMeridianIII:0.00}; disk at {fit.CenterX:0.00}, {fit.CenterY:0.00}, R {fit.EquatorialRadius:0.00} px, north {fit.NorthAngleDeg:0.0} deg{north}"));
        if (folder is not null)
        {
            result.Master.WriteToFitsFile(Path.Combine(folder, name.Replace(", ", "-").Replace(' ', '-') + ".fits"));
        }
        // The stack's own disk and its own fit's north, at the end of the axis its frames decided where they did (a limb fit on
        // the stack cannot tell the ends apart either).
        var turnedOver = result.North is { } frames && Math.Abs(Math.IEEERemainder(fit.NorthAngleDeg - frames.NorthAngleDeg, 360)) > 90;
        var placement = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg + (turnedOver ? 180 : 0));
        return new Stack(result.Master, aspect, fit, placement);
    }

    // The first frame taken at or after `when`: the run's frames are in time order.
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

    // Each a trous band's power inside 0.8 radii of a stack's own disk, on its own disk level.
    private static double[] BandPower(Stack stack)
    {
        var (width, height) = (stack.Master.Width, stack.Master.Height);
        var disk = new MetricDisk(stack.Fit.CenterX, stack.Fit.CenterY, stack.Fit.EquatorialRadius);
        var luma = new float[width * height];
        for (var c = 0; c < stack.Master.ChannelCount; c++)
        {
            var channel = stack.Master.GetChannelSpan(c);
            for (var i = 0; i < luma.Length; i++)
            {
                luma[i] += channel[i] / stack.Master.ChannelCount;
            }
        }
        var bands = ATrousWaveletTransform.Decompose(PlanetaryMetrics.Normalise(luma, width, height, disk), width, height, PlanetaryMetrics.Bands);
        var power = new double[PlanetaryMetrics.Bands];
        var reach = 0.8 * disk.Radius;
        for (var j = 0; j < power.Length; j++)
        {
            var detail = bands.Detail(j);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (dx, dy) = (x - disk.X, y - disk.Y);
                    if ((dx * dx) + (dy * dy) < reach * reach)
                    {
                        power[j] += (double)detail[(y * width) + x] * detail[(y * width) + x];
                    }
                }
            }
        }
        return power;
    }
}
