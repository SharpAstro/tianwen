using System;
using System.Collections.Generic;
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
/// <c>tianwen planetary look &lt;master.fits&gt; [--reference &lt;post&gt;]</c>: a colour look on a balanced planetary master (#1273,
/// <see cref="PlanetaryColourLook"/>), without stacking again. Given another program's result of the same capture it fits the look to it
/// (the reference's cast, then its chroma about grey at every quantile, read by <see cref="PlanetaryReferenceJudge.ReadColours"/>); without
/// one it applies <see cref="ColourLook.Boosted"/>'s S-curve, or one <c>--gain</c>, the tint as balanced. A look is a RENDERING of the
/// linear master, never a new master: it writes the planetary preview with the look, and a FITS with the look in its planes only on
/// <c>--fits</c> (no longer scene-linear, so never a master to process further). It reads both back, linear and as shown: the cast, the
/// spread, the chroma's quantiles and the rim against the master's own.
/// </summary>
internal sealed class PlanetaryLookSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var masterArg = new Argument<string>("master") { Description = "A balanced planetary master (FITS), as planetary stack writes its _sharpened.fits." };
        var referenceOpt = new Option<string?>("--reference") { Description = "Another program's result of the same capture (its _post), to fit the look to: its colour contrast and its tint." };
        var gainOpt = new Option<double?>("--gain") { Description = "With no reference, one gain on every pixel's chroma about grey (the boosted look's curve when not given)." };
        var gainsOpt = new Option<string?>("--chroma-gains") { Description = "With no reference, a gain on the chroma at each quantile of the colour reading's grid (1st, 5th, 10th ... 95th, 99th percentiles: 21 values, comma separated), in place of the boosted look's." };
        var postedOpt = new Option<bool>("--posted") { Description = "With no reference, the posts' look (#1305): every pixel's colour moved about the planet's own, by the planet's fit to outside observers' posts." };
        var spreadOpt = new Option<string?>("--spread") { Description = "With --mean and no reference, the posts' look at these values (for measuring, #1305): every pixel's saturation taken this many times as far from the planet's own. Comma separated, with --mean's, a grid: one preview each, <output>_s<spread>_m<mean>.png." };
        var meanOpt = new Option<string?>("--mean") { Description = "With --spread and no reference: the planet's own colour kept this much of itself (comma separated for a grid)." };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter or saturn; read off the master's file name when not given." };
        var utcOpt = new Option<string?>("--utc") { Description = "The instant the master shows the planet at (ISO 8601, UTC); its DATE-OBS and EXPTIME's middle when not given." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The preview PNG to write; <master>_look.png beside the master when not given." };
        var fitsOpt = new Option<bool>("--fits") { Description = "Also write the look's planes as a FITS beside the PNG (for measuring; not scene-linear, so not a master to process further)." };

        var command = new Command("look", "A colour look on a balanced planetary master (#1273): its chroma about grey raised in OKLab, keeping each colour's hue, or fitted to another program's result of the same capture.")
        {
            Arguments = { masterArg },
            Options = { referenceOpt, gainOpt, gainsOpt, postedOpt, spreadOpt, meanOpt, planetOpt, utcOpt, outputOpt, fitsOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var masterPath = parseResult.GetValue(masterArg) ?? "";
            if (!Image.TryReadImageFile(masterPath, out var master) || master.ChannelCount != 3)
            {
                consoleHost.WriteError($"{masterPath}: not a readable three-channel image");
                return 1;
            }
            Image? reference = null;
            Image? looked = null;
            try
            {
                var planet = PlanetaryGeometrySubCommands.ParsePlanet(parseResult.GetValue(planetOpt), masterPath);
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
                // The planet's disk, and a master left in the camera's colours balanced first: the one routine the viewer's colour
                // control makes a master ready with (#1277).
                var (prepared, refusal) = await Task.Run(() => PlanetaryColourLook.Prepare(master, body, instant), ct);
                if (prepared is not { } ready)
                {
                    consoleHost.WriteError($"{masterPath}: {refusal}");
                    return 1;
                }
                var (disk, options) = (ready.Disk, ready.Options);
                if (ready.Balance is { } balance)
                {
                    master.Release();
                    master = ready.Master;
                    consoleHost.WriteScrollable($"the master was in the camera's colours; {balance.Describe()}");
                }

                // The look: fitted to the reference when there is one, the contrast raised by the factor asked (the boosted look's) when not.
                ColourLook look;
                (MetricDisk Disk, ReferencePlacement Placement)? placed = null;
                if (parseResult.GetValue(referenceOpt) is { } referencePath)
                {
                    if (!Image.TryReadImageFile(referencePath, out var read) || read.ChannelCount < 3)
                    {
                        consoleHost.WriteError($"{referencePath}: not a readable colour picture");
                        return 1;
                    }
                    reference = read;
                    if (await Task.Run(() => PlanetaryLimbFit.Fit(read, options), ct) is not { } referenceFit)
                    {
                        consoleHost.WriteError($"{referencePath}: the planet's limb could not be fitted");
                        return 1;
                    }
                    var referenceDisk = MetricDisk.From(referenceFit, options);
                    var placement = await Task.Run(() => PlanetaryReferenceJudge.Place(PlanetaryLimbFit.Luminance(master), master.Width, master.Height, disk,
                        PlanetaryLimbFit.Luminance(read), read.Width, read.Height, referenceDisk), ct);
                    placed = (referenceDisk, placement);
                    var (_, theirs) = PlanetaryReferenceJudge.ReadColours(master, disk, read, referenceDisk, placement);
                    look = ColourLook.FittedTo(theirs);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"the reference placed at {placement.Scale:0.0000} master pixels a pixel, turned {placement.RotationDeg:0.0} deg{(placement.Mirrored ? ", mirrored" : "")}; detail correlation {placement.Correlation:0.000}{(placement.Correlation < 0.5 ? " (doubtful)" : "")}"));
                }
                else
                {
                    if (parseResult.GetValue(gainsOpt) is { } list)
                    {
                        var parts = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        var asked = new double[parts.Length];
                        for (var k = 0; k < parts.Length; k++)
                        {
                            if (!double.TryParse(parts[k], NumberStyles.Float, inv, out asked[k]) || !(asked[k] > 0))
                            {
                                consoleHost.WriteError($"--chroma-gains: '{parts[k]}' is not a positive number");
                                return 1;
                            }
                        }
                        if (asked.Length != PlanetaryColourReading.QuantileGrid.Length)
                        {
                            consoleHost.WriteError($"--chroma-gains takes {PlanetaryColourReading.QuantileGrid.Length} values, one a quantile of the grid; {asked.Length} given");
                            return 1;
                        }
                        look = new ColourLook { ChromaGains = [.. asked] };
                    }
                    else if (parseResult.GetValue(spreadOpt) is { } spreadList && parseResult.GetValue(meanOpt) is { } meanList)
                    {
                        if (Values(spreadList, "--spread") is not { } spreads || Values(meanList, "--mean") is not { } means)
                        {
                            return 1;
                        }
                        if (spreads.Length > 1 || means.Length > 1)
                        {
                            // A grid, for fitting the look through this routine (#1305): the master made ready once, one preview a pair.
                            var stem = parseResult.GetValue(outputOpt) is { } gridOut
                                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gridOut)) ?? ".", Path.GetFileNameWithoutExtension(gridOut))
                                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".", Path.GetFileNameWithoutExtension(masterPath) + "_look");
                            Directory.CreateDirectory(Path.GetDirectoryName(stem) ?? ".");
                            foreach (var spread in spreads)
                            {
                                foreach (var mean in means)
                                {
                                    var (one, _) = PlanetaryColourLook.Apply(master, disk, new ColourLook { AboutPlanet = (spread, mean) });
                                    try
                                    {
                                        await previewRenderer.RenderPlanetaryAsync(one, string.Create(inv, $"{stem}_s{spread:0.00}_m{mean:0.00}.png"), ct: ct);
                                    }
                                    finally
                                    {
                                        one.Release();
                                    }
                                }
                            }
                            consoleHost.WriteScrollable($"wrote {spreads.Length * means.Length} previews, {stem}_s<spread>_m<mean>.png");
                            return 0;
                        }
                        look = new ColourLook { AboutPlanet = (spreads[0], means[0]) };
                    }
                    else if (parseResult.GetValue(postedOpt))
                    {
                        look = ColourLook.Posted.For(body);
                    }
                    else
                    {
                        look = parseResult.GetValue(gainOpt) is { } gain ? ColourLook.Uniform(gain) : ColourLook.Boosted;
                    }
                }
                (looked, var gains) = PlanetaryColourLook.Apply(master, disk, look);
                consoleHost.WriteScrollable(look.Describe());
                if (look.AboutPlanet is null)
                {
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"the chroma's gain at the 10th, 50th and 90th percentiles: {GainAtQuantile(gains, 0.10):0.000}, {GainAtQuantile(gains, 0.50):0.000}, {GainAtQuantile(gains, 0.90):0.000}"));
                }

                var pngPath = parseResult.GetValue(outputOpt)
                    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".", Path.GetFileNameWithoutExtension(masterPath) + "_look.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? ".");
                await previewRenderer.RenderPlanetaryAsync(looked, pngPath, ct: ct);
                consoleHost.WriteScrollable($"wrote {pngPath}");
                if (parseResult.GetValue(fitsOpt))
                {
                    var fitsPath = Path.ChangeExtension(pngPath, ".fits");
                    looked.WriteToFitsFile(fitsPath, null, Cards(masterPath, ready.Balance, look));
                    consoleHost.WriteScrollable($"wrote {fitsPath} (the look in its planes, not scene-linear)");
                }

                // Read back: the master's own colour, the look's, and, fitted, the reference's on the look; each linear and as its preview
                // shows it (the preview's stretch shows a master at 1.7 to 2 times its linear chroma).
                Line("the master's colour, linear", Read(master, disk));
                Line("the master's colour as shown", PlanetaryReferenceJudge.ReadShown(master, disk));
                Line("the look's colour, linear", Read(looked, disk));
                Line("the look's colour as shown", PlanetaryReferenceJudge.ReadShown(looked, disk));
                if (reference is not null && placed is { } p)
                {
                    var (_, theirs) = PlanetaryReferenceJudge.ReadColours(looked, disk, reference, p.Disk, p.Placement);
                    Line("the reference's colour", theirs);
                }
                return 0;
            }
            finally
            {
                looked?.Release();
                reference?.Release();
                master.Release();
            }
        });
        return command;
    }

    // A comma-separated list of positive numbers, or null with its fault said.
    private double[]? Values(string list, string option)
    {
        var parts = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out values[k]) || !(values[k] > 0))
            {
                consoleHost.WriteError($"{option}: '{parts[k]}' is not a positive number");
                return null;
            }
        }
        return values.Length > 0 ? values : null;
    }

    private static ColourReading Read(Image image, in MetricDisk disk)
    {
        var r = image.GetChannelSpan(0);
        var g = image.GetChannelSpan(1);
        var b = image.GetChannelSpan(2);
        return PlanetaryColourReading.Read(r, g, b, image.Width, image.Height, disk, PlanetaryColour.SkyOrBlack(r, g, b, image.Width, image.Height, disk));
    }

    private void Line(string title, in ColourReading reading) => consoleHost.WriteScrollable($"{title} {reading.Describe()}");

    private static double GainAtQuantile(System.Collections.Immutable.ImmutableArray<double> gains, double q)
        => gains[PlanetaryColourReading.QuantileGrid.IndexOf(q)];

    // The look's FITS cards: the colour balance's (their CBALSAT is what marks a balanced master's one black point, #1229), and the look's.
    // The balance is the one Prepare made where it had to balance the master (`madeHere`), whose own file then carries no cards: copied from
    // the file alone, such a look read back as unbalanced, a black point a channel (the audit on #1343). Otherwise the master's are carried over.
    internal static Dictionary<string, (object Value, string Comment)> Cards(string masterPath, ColourBalance? madeHere, ColourLook look)
    {
        var cards = new Dictionary<string, (object Value, string Comment)>();
        if (madeHere is not null)
        {
            foreach (var (key, value) in madeHere.HeaderCards())
            {
                cards[key] = value;
            }
        }
        else
        {
            using var fits = Image.OpenFitsHeader(masterPath);
            if (fits.ReadFirstImageHduHeaderOnly()?.Header is { } header)
            {
                foreach (var key in ColourBalance.Cards)
                {
                    if (header.ContainsKey(key))
                    {
                        cards[key] = (header.GetDoubleValue(key), "colour balance, as the master carried it (#1212)");
                    }
                }
            }
        }
        foreach (var (key, value) in look.HeaderCards())
        {
            cards[key] = value;
        }
        return cards;
    }
}
