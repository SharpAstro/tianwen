using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-derotate</c> (docs/plans/planetary-restoration.md, R6, 6b): two captures of one night stacked, the first carried
/// through the planet's rotation to the second's epoch and onto its disk (<see cref="PlanetaryDerotation"/>), and the two
/// compared inside 0.9 radii with the de-rotation and without it. The image's north is decided by the agreement itself: the
/// limb fit cannot tell north from south near opposition, and north the wrong way round turns the planet backwards.
/// </summary>
internal sealed class PlanetaryDerotateSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var firstArg = new Argument<string>("first") { Description = "The earlier SER capture, the one carried to the other's epoch." };
        var secondArg = new Argument<string>("second") { Description = "The later SER capture, whose epoch and disk the first is carried onto." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var keepOpt = new Option<double>("--keep") { Description = "The fraction of each capture's frames stacked.", DefaultValueFactory = _ => 0.05 };
        var framesOpt = new Option<int?>("--frames") { Description = "Only each capture's first frames." };
        var outputOpt = new Option<string?>("--output") { Description = "Write the two stacks and the first de-rotated, as FITS, into this folder." };

        var command = new Command("planetary-derotate",
            "Two captures stacked, the first de-rotated to the second's epoch through the oblate spheroid (R6, 6b), and the two compared with the de-rotation and without it, north decided by their agreement.")
        {
            Arguments = { firstArg, secondArg },
            Options = { planetOpt, keepOpt, framesOpt, outputOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var options = new PlanetaryStackOptions { KeepFraction = parseResult.GetValue(keepOpt), WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3 };
            var frames = parseResult.GetValue(framesOpt);
            if (await StackAsync(parseResult.GetValue(firstArg) ?? "", planet, options, frames, ct) is not { } first
                || await StackAsync(parseResult.GetValue(secondArg) ?? "", planet, options, frames, ct) is not { } second)
            {
                return 1;
            }
            var inv = CultureInfo.InvariantCulture;
            var rotation = Math.IEEERemainder(second.Aspect.CentralMeridianIII - first.Aspect.CentralMeridianIII, 360);
            consoleHost.WriteScrollable(string.Create(inv,
                $"{(second.Aspect.Utc - first.Aspect.Utc).TotalMinutes:0.00} minutes apart: the central meridian (System III) turned {rotation:0.00} degrees"));

            // With no rotation: the first moved onto the second's disk alone. Then de-rotated, each way round, each compared with
            // the unrotated one over the pixels its de-rotation covered. One camera took both, so both disks have ONE north, the
            // second's fit: with each its own, the two fits' difference (0.4 degrees on 2024-12-15) would turn the image too.
            var k = first.Fit.LimbDarkening;
            var unrotated = PlanetaryDerotation.Derotate(first.Master, second.Aspect, first.Placement with { NorthAngleDeg = second.Placement.NorthAngleDeg },
                second.Aspect, second.Placement, k);
            Image? best = null;
            var (bestRms, bestNone) = (double.PositiveInfinity, double.NaN);
            foreach (var flip in new[] { 0.0, 180.0 })
            {
                var to = second.Placement with { NorthAngleDeg = second.Placement.NorthAngleDeg + flip };
                var from = first.Placement with { NorthAngleDeg = to.NorthAngleDeg };
                var derotated = PlanetaryDerotation.Derotate(first.Master, first.Aspect, from, second.Aspect, to, k);
                var both = new bool[derotated.Covered.Length];
                for (var i = 0; i < both.Length; i++)
                {
                    both[i] = derotated.Covered[i] && unrotated.Covered[i];
                }
                var (rms, pixels) = PlanetaryDerotation.DifferenceRms(derotated.Image, second.Master, second.Placement, both);
                var (none, _) = PlanetaryDerotation.DifferenceRms(unrotated.Image, second.Master, second.Placement, both);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    north at {from.NorthAngleDeg:0.0} deg (the second stack's limb fit{(flip == 0 ? "" : ", turned over")}), over the {pixels} pixels inside 0.9 radii it covers: not de-rotated {none:0.00000} RMS, de-rotated {rms:0.00000}, {rms / none:0.000} of none"));
                if (rms / none < bestRms / bestNone || best is null)
                {
                    (best, bestRms, bestNone) = (derotated.Image, rms, none);
                }
            }
            consoleHost.WriteScrollable(string.Create(inv, $"de-rotation leaves {bestRms / bestNone:0.000} of the difference with no rotation taken out"));

            if (parseResult.GetValue(outputOpt) is { } folder && best is not null)
            {
                Directory.CreateDirectory(folder);
                first.Master.WriteToFitsFile(Path.Combine(folder, $"{first.Name}.stack.fits"));
                second.Master.WriteToFitsFile(Path.Combine(folder, $"{second.Name}.stack.fits"));
                best.WriteToFitsFile(Path.Combine(folder, $"{first.Name}.derotated-to-{second.Name}.fits"));
                consoleHost.WriteScrollable($"wrote the two stacks and the first de-rotated into {folder}");
            }
            return 0;
        });
        return command;
    }

    // One capture's stack, its epoch (the capture's middle, since frames are kept from all of it) and its disk.
    private sealed record Stack(string Name, Image Master, PlanetAspect Aspect, LimbFit Fit, DiskPlacement Placement);

    private async Task<Stack?> StackAsync(string path, CatalogIndex planet, PlanetaryStackOptions options, int? frames, CancellationToken ct)
    {
        using var reader = SerReader.Open(path);
        using var whole = new SerFrameStream(reader, ownsReader: false);
        using var stream = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, frames ?? whole.FrameCount));
        if (stream.MidCapture is not { } when)
        {
            consoleHost.WriteError($"{path}: no timestamps");
            return null;
        }
        var aspect = PhysicalEphemeris.Compute(planet, when);
        var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
        using var luma = Luma(result.Master);
        if (PlanetaryLimbFit.Fit(luma, PlanetaryLimbFit.OptionsFor(aspect)) is not { } fit)
        {
            consoleHost.WriteError($"{path}: the stack's limb could not be fitted");
            return null;
        }
        var name = Path.GetFileNameWithoutExtension(path);
        consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {result.FramesUsed} of {result.FramesGraded} frames at {when:HH:mm:ss.f} UTC, CM III {aspect.CentralMeridianIII:0.00}; disk at {fit.CenterX:0.00}, {fit.CenterY:0.00}, R {fit.EquatorialRadius:0.00} px, north {fit.NorthAngleDeg:0.0} deg, k {fit.LimbDarkening:0.000}"));
        return new Stack(name, result.Master, aspect, fit, new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg));
    }

    // The mean of a stack's channels as a mono image of its own, which the limb fit and the comparison read.
    private static RentedLuma Luma(Image image)
    {
        if (image.ChannelCount == 1)
        {
            return new RentedLuma(image, owns: false);
        }
        var (width, height) = (image.Width, image.Height);
        var plane = new float[height, width];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var channel = image.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] += channel[(y * width) + x] / image.ChannelCount;
                }
            }
        }
        return new RentedLuma(Image.FromChannel(plane, image.MaxValue, image.MinValue), owns: true);
    }

    // A luma image, released when it was made here and not when it is the stack itself.
    private readonly struct RentedLuma(Image image, bool owns) : IDisposable
    {
        public ReadOnlySpan<float> GetChannelSpan(int channel) => image.GetChannelSpan(channel);

        public static implicit operator Image(RentedLuma luma) => luma.Image;

        public Image Image => image;

        public void Dispose()
        {
            if (owns)
            {
                image.Release();
            }
        }
    }
}
