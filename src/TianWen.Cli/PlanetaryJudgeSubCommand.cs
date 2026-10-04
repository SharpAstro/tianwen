using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-judge &lt;master.fits&gt; &lt;reference&gt;</c>: our master against another program's result of the same capture, the
/// <c>_stack</c> or <c>_post</c> beside it (<see cref="PlanetaryReferenceJudge"/>). The reference is placed on the master (scale, turn,
/// mirror), its tone matched, and each a trous band of the globe, and of Saturn's rings off it, read for what the two share: the correlation,
/// the master's energy over the reference's, and the master's gain on what they share. Then each disk's mean colour, the reference's decoded
/// from sRGB (a processor's curves make it approximate), and on request a picture of the two side by side at one geometry and one tone.
/// </summary>
internal sealed class PlanetaryJudgeSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var masterArg = new Argument<string>("master") { Description = "Our master (FITS): a stack or its sharpening." };
        var referenceArg = new Argument<string>("reference") { Description = "Another program's result of the same capture: its _stack or _post (PNG, JPEG, TIFF or FITS)." };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter or saturn; read off the master's file name when not given." };
        var utcOpt = new Option<string?>("--utc") { Description = "The instant the master shows the planet at (ISO 8601, UTC); its DATE-OBS and EXPTIME's middle when not given." };
        var pictureOpt = new Option<string?>("--picture") { Description = "Write the master beside the reference placed on it to this PNG, each of the reference's channels matched to the master's tone, both through one planetary stretch." };

        var command = new Command("planetary-judge", "Our master against another program's result of the same capture (its _stack or _post): placed, matched in tone, then read band by band for the detail both hold.")
        {
            Arguments = { masterArg, referenceArg },
            Options = { planetOpt, utcOpt, pictureOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var masterPath = parseResult.GetValue(masterArg) ?? "";
            var referencePath = parseResult.GetValue(referenceArg) ?? "";
            if (!Image.TryReadImageFile(masterPath, out var master))
            {
                consoleHost.WriteError($"{masterPath}: not a readable image");
                return 1;
            }
            if (!Image.TryReadImageFile(referencePath, out var reference))
            {
                master.Release();
                consoleHost.WriteError($"{referencePath}: not a readable image");
                return 1;
            }
            try
            {
                CatalogIndex? planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() switch
                {
                    null => PlanetaryCaptureName.Planet(masterPath),
                    "jupiter" => CatalogIndex.Jupiter,
                    "saturn" => CatalogIndex.Saturn,
                    _ => null,
                };
                if (planet is not { } body)
                {
                    consoleHost.WriteError("name the planet (--planet jupiter or saturn)");
                    return 1;
                }
                if ((PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt)) ?? PlanetaryBestStack.InstantOf(master, epoch: null)) is not { } instant)
                {
                    consoleHost.WriteError($"{masterPath}: no time in its header; give --utc");
                    return 1;
                }
                var options = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(body, instant));
                if (await Task.Run(() => PlanetaryLimbFit.Fit(master, options), ct) is not { } masterFit)
                {
                    consoleHost.WriteError($"{masterPath}: the planet's limb could not be fitted");
                    return 1;
                }
                if (await Task.Run(() => PlanetaryLimbFit.Fit(reference, options), ct) is not { } referenceFit)
                {
                    consoleHost.WriteError($"{referencePath}: the planet's limb could not be fitted");
                    return 1;
                }
                var masterDisk = MetricDisk.From(masterFit, options);
                var referenceDisk = MetricDisk.From(referenceFit, options);
                var (width, height) = (master.Width, master.Height);
                var ours = Luminance(master);
                var theirs = Luminance(reference);

                var placement = await Task.Run(() => PlanetaryReferenceJudge.Place(ours, width, height, masterDisk, theirs, reference.Width, reference.Height, referenceDisk), ct);
                var placed = PlanetaryReferenceJudge.Resample(theirs, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement);
                var matched = PlanetaryReferenceJudge.MatchTone(ours, placed, width, height, masterDisk);
                var judgement = PlanetaryReferenceJudge.Judge(ours, matched, width, height, masterDisk, placement);

                consoleHost.WriteScrollable(string.Create(inv,
                    $"{body} at {instant:yyyy-MM-dd HH:mm:ss} UTC; the master's disk R {masterFit.EquatorialRadius:0.0} px, the reference's {referenceFit.EquatorialRadius:0.0} px ({reference.Width}x{reference.Height})"));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"the reference placed at {placement.Scale:0.0000} master pixels a pixel, turned {placement.RotationDeg:0.0} deg{(placement.Mirrored ? ", mirrored" : "")}, shifted ({placement.ShiftX:0.0}, {placement.ShiftY:0.0}) px; detail correlation {placement.Correlation:0.000}{(placement.Correlation < 0.5 ? " (doubtful: check the picture)" : "")}"));
                Table("the globe, inside 0.9 radii", judgement.Globe, judgement.GlobePixels);
                if (judgement.Rings.Length > 0)
                {
                    Table("the rings, off the globe", judgement.Rings, judgement.RingPixels);
                }

                if (master.ChannelCount >= 3 && reference.ChannelCount >= 3)
                {
                    var oursColour = DiskColour(master.GetChannelSpan(0), master.GetChannelSpan(1), master.GetChannelSpan(2), width, height, masterDisk);
                    var (r, g, b, _, _) = PlanetaryColour.LinearFromSrgb(reference);
                    var theirsColour = DiskColour(
                        PlanetaryReferenceJudge.Resample(r, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement),
                        PlanetaryReferenceJudge.Resample(g, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement),
                        PlanetaryReferenceJudge.Resample(b, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement),
                        width, height, masterDisk);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"the disk's colour: the master R/G {oursColour.R / oursColour.G:0.000}, B/G {oursColour.B / oursColour.G:0.000}; the reference, decoded from sRGB, R/G {theirsColour.R / theirsColour.G:0.000}, B/G {theirsColour.B / theirsColour.G:0.000} (chromaticity {oursColour.ChromaDistance(theirsColour):0.0000} apart)"));
                }

                if (parseResult.GetValue(pictureOpt) is { } picture)
                {
                    var channels = Math.Min(master.ChannelCount, reference.ChannelCount);
                    const int gap = 8;
                    var planes = new float[channels][,];
                    var peak = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        var ourPlane = master.GetChannelSpan(c);
                        var theirPlane = PlanetaryReferenceJudge.MatchTone(ourPlane,
                            PlanetaryReferenceJudge.Resample(reference.GetChannelSpan(c), reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement),
                            width, height, masterDisk);
                        var plane = planes[c] = new float[height, (2 * width) + gap];
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var i = (y * width) + x;
                                plane[y, x] = ourPlane[i];
                                plane[y, width + gap + x] = float.IsFinite(theirPlane[i]) ? theirPlane[i] : 0;
                                peak = Math.Max(peak, Math.Max(plane[y, x], plane[y, width + gap + x]));
                            }
                        }
                    }
                    var both = new Image(planes, BitDepth.Float32, peak, 0, 0, new ImageMeta { SensorType = channels >= 3 ? SensorType.Color : SensorType.Monochrome });
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(picture)) ?? ".");
                        await previewRenderer.RenderPlanetaryAsync(both, picture, ct: ct);
                    }
                    finally
                    {
                        both.Release();
                    }
                    consoleHost.WriteScrollable($"wrote {picture} (the master left, the reference placed on it right)");
                }
                return 0;
            }
            finally
            {
                master.Release();
                reference.Release();
            }
        });
        return command;
    }

    private void Table(string title, System.Collections.Immutable.ImmutableArray<BandJudgement> bands, int pixels)
    {
        var inv = CultureInfo.InvariantCulture;
        consoleHost.WriteScrollable(string.Create(inv, $"{title} ({pixels} px): band, correlation, the master's energy over the reference's, the master's gain on what both hold"));
        foreach (var b in bands)
        {
            consoleHost.WriteScrollable(string.Create(inv, $"  band {b.Band}: {b.Correlation,7:0.000} {b.EnergyRatio,8:0.000} {b.SharedGain,8:0.000}"));
        }
    }

    // A picture's luminance, its channels' mean.
    private static float[] Luminance(Image image)
    {
        var result = new float[image.Width * image.Height];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var i = 0; i < result.Length; i++)
            {
                result[i] += plane[i] / image.ChannelCount;
            }
        }
        return result;
    }

    // A disk's mean colour, its sky taken off; a sky the picture does not reach (a reference cropped inside 2.5 radii) is its black, zero.
    private static LinearRgb DiskColour(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, int width, int height, MetricDisk disk)
    {
        var sky = PlanetaryColour.Sky(red, green, blue, width, height, disk);
        sky = new LinearRgb(double.IsFinite(sky.R) ? sky.R : 0, double.IsFinite(sky.G) ? sky.G : 0, double.IsFinite(sky.B) ? sky.B : 0);
        return PlanetaryColour.DiskMean(red, green, blue, width, height, disk, sky);
    }
}
