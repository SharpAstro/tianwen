using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.IO;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary compare &lt;label=master.fits&gt;...</c>: several masters of one capture side by side in numbers
/// (<see cref="PlanetaryPicture"/>): the disk's contrast and detail and what is clipped, the light past the limb against the glow the
/// planet's own model through the pupil expects there, and the sky's noise, gradient, structure and outliers. A master the size of the
/// first is read on the FIRST's disk, model and moons: the limb is steep, so a refit 0.1 px larger moved several disk pixels' worth of
/// light out of the glow, and a sharpening's ring holds maxima the moon finder takes for moons (16 on the old live dials). A master of
/// another size, another program's crop, gets its own fit. The first master's sky noise is the one the glow's pixels are counted against
/// in every column, so a sharpening that lifts its own noise is not judged more leniently. Given one master it is that master's own report
/// (#1367, the measures the keep study took in Python): the sky's noise band by band and the grain, and with a telescope the limb's edge
/// over the pupil's transfer and the pupil's cutoff; <c>--json</c> writes it all (<see cref="PlanetaryPictureReport"/>).
/// </summary>
internal sealed class PlanetaryCompareSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "The masters, each label=path or a path (labelled by its file name); the first is the reference whose sky noise the glow is counted against.", Arity = ArgumentArity.OneOrMore };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter; read off the first master's file name when not given." };
        var utcOpt = new Option<string?>("--utc") { Description = "The instant the masters show the planet at (ISO 8601, UTC); the first master's DATE-OBS and EXPTIME's middle when not given." };
        var wavelengthOpt = new Option<string?>("--wavelength") { Description = "The filter's effective wavelength, nm, a comma list for a colour master's channels (550 when not given)." };
        var jsonOpt = new Option<string?>("--json") { Description = "Also write every master's numbers to this JSON file (PlanetaryPictureReport)." };
        var pupil = PlanetaryMasterScore.PupilOptions();

        var command = new Command("compare", "One master's numbers, or several masters of one capture side by side: contrast, the limb's light against the glow expected and its edge over the pupil's, the sky's noise band by band and the grain.")
        {
            Arguments = { mastersArg },
            Options = { planetOpt, utcOpt, wavelengthOpt, jsonOpt, pupil.ApertureMm, pupil.Obstruction, pupil.Telescope },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var items = (parseResult.GetValue(mastersArg) ?? []).Select(arg =>
            {
                var at = arg.IndexOf('=');
                return at > 0 ? (Label: arg[..at], Path: arg[(at + 1)..]) : (Label: Path.GetFileNameWithoutExtension(arg), Path: arg);
            }).ToArray();
            var masters = new List<(string Label, string Path, Image Image)>();
            try
            {
                foreach (var (label, path) in items)
                {
                    // Another program's result is read as it was saved, a PNG or JPEG as well as a FITS (Image.TryReadImageFile).
                    if (!Image.TryReadImageFile(path, out var image))
                    {
                        consoleHost.WriteError($"{path}: not a readable image");
                        return 1;
                    }
                    masters.Add((label, path, image));
                }
                var first = masters[0].Image;
                var planetName = parseResult.GetValue(planetOpt)?.ToLowerInvariant();
                var planet = PlanetaryGeometrySubCommands.ParsePlanet(planetName, items[0].Path);
                if (planet is not { } body)
                {
                    consoleHost.WriteError("name the planet (--planet jupiter)");
                    return 1;
                }
                if ((PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt)) ?? PlanetaryBestStack.InstantOf(first, epoch: null)) is not { } instant)
                {
                    consoleHost.WriteError($"{items[0].Path}: no time in its header; give --utc");
                    return 1;
                }
                if (PlanetaryMasterScore.Wavelengths(consoleHost, parseResult.GetValue(wavelengthOpt)) is not { } wavelengths)
                {
                    return 1;
                }
                var telescope = PlanetaryMasterScore.PupilFrom(parseResult, pupil);
                var aspect = PhysicalEphemeris.Compute(body, instant);
                var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{body} at {instant:yyyy-MM-dd HH:mm:ss} UTC; {(telescope is { } p ? $"the glow expected through a {p.DiameterM * 1000:0} mm pupil, {p.ObstructionRatio:P0} obstructed" : "no telescope given, so no glow expected")}"));

                // The first master's disk for every master its size, else a master's own, fitted once over its channels' mean.
                var fits = new List<(string Label, string Path, Image Image, LimbFit Fit, bool Own)>();
                foreach (var (label, path, image) in masters)
                {
                    ct.ThrowIfCancellationRequested();
                    var reference = fits.Count > 0 && (image.Width, image.Height) == (fits[0].Image.Width, fits[0].Image.Height) ? fits[0].Fit : (LimbFit?)null;
                    if ((reference ?? await Task.Run(() => PlanetaryLimbFit.Fit(image, limbOptions), ct)) is not { } fit)
                    {
                        consoleHost.WriteError($"{label}: the planet's limb could not be fitted; left out");
                        continue;
                    }
                    fits.Add((label, path, image, fit, reference is null));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"  {label}: {image.ChannelCount}ch {image.Width}x{image.Height}, {(reference is null ? "its own limb" : "the first's limb")} at ({fit.CenterX:0.0}, {fit.CenterY:0.0}), {fit.EquatorialRadius:0.0} px, north {fit.NorthAngleDeg:0.0} degrees"));
                }
                if (fits.Count == 0)
                {
                    return 1;
                }

                var channels = fits.Min(f => f.Image.ChannelCount);
                var reported = fits.Select(_ => ImmutableArray.CreateBuilder<PlanetaryChannelPicture>(channels)).ToArray();
                for (var c = 0; c < channels; c++)
                {
                    var wavelength = wavelengths[Math.Min(c, wavelengths.Length - 1)];
                    var pictures = new List<PlanetaryPicture>();
                    var edges = new List<ImmutableArray<double>>();
                    double? glowNoise = null;
                    float[]? sharedExpected = null;
                    var sharedMoons = ImmutableArray<(int X, int Y)>.Empty;
                    for (var m = 0; m < fits.Count; m++)
                    {
                        var (_, _, image, fit, own) = fits[m];
                        var disk = MetricDisk.From(fit, limbOptions);
                        var (expected, moons) = own || sharedExpected is null
                            ? (telescope is { } scope ? PlanetaryPicture.Diffracted(fit, limbOptions, aspect, scope, wavelength, image.Width, image.Height) : [],
                                PlanetaryPicture.MoonsOf(image.GetChannelSpan(c), image.Width, image.Height, disk))
                            : (sharedExpected, sharedMoons);
                        if (sharedExpected is null)
                        {
                            (sharedExpected, sharedMoons) = (expected, moons);
                        }
                        var picture = await Task.Run(() => PlanetaryPicture.Measure(image.GetChannelSpan(c), image.Width, image.Height, disk, expected, glowNoise, moons), ct);
                        glowNoise ??= picture.SkyNoise;
                        pictures.Add(picture);

                        // The limb's edge over the pupil's own transfer, and the pupil's cutoff at this master's scale, with a telescope.
                        var (edge, cutoff) = (ImmutableArray<double>.Empty, double.NaN);
                        if (telescope is { } optics)
                        {
                            var profile = await Task.Run(() => PlanetaryFinestBand.MasterEdge(image.GetChannelSpan(c), image.Width, image.Height, fit, limbOptions, aspect, optics, wavelength), ct);
                            edge = [.. PlanetaryChannelPicture.EdgeFrequencies.Select(profile.TransferAt)];
                            cutoff = PlanetaryFinishing.CutoffCyclesPerPixel(optics, wavelength, aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius);
                        }
                        edges.Add(edge);
                        var (skyLevel, diskScale) = PlanetaryMetrics.NormalisationLevels(image.GetChannelSpan(c), image.Width, image.Height, disk);
                        reported[m].Add(new PlanetaryChannelPicture(c, wavelength, skyLevel, diskScale, cutoff, edge, picture));
                    }
                    var cutoffs = reported.Select(r => r[^1].CutoffCyclesPerPixel).Where(double.IsFinite).ToArray();
                    var title = (channels > 1 ? string.Create(inv, $"channel {c} ({wavelength:0} nm)") : string.Create(inv, $"{wavelength:0} nm"))
                        + (cutoffs.Length > 0 ? string.Create(inv, $", the pupil's cutoff {cutoffs[0]:0.000} cycles a pixel") : "");
                    Report(title, [.. fits.Select(f => f.Label)], pictures, edges);
                }

                if (parseResult.GetValue(jsonOpt) is { } jsonPath)
                {
                    var report = new PlanetaryPictureReport(body.ToString(), instant.ToString("O", inv), telescope is { } t ? t.DiameterM * 1000 : double.NaN,
                        telescope?.ObstructionRatio ?? double.NaN,
                        [.. fits.Select((f, m) => new PlanetaryMasterPictures(f.Label, f.Path, f.Image.Width, f.Image.Height, f.Image.ChannelCount, f.Own,
                            f.Fit.CenterX, f.Fit.CenterY, f.Fit.EquatorialRadius, f.Fit.NorthAngleDeg, reported[m].ToImmutable()))]);
                    await SharedFile.WriteAsync(jsonPath, (stream, token) => JsonSerializer.SerializeAsync(stream, report, PlanetaryPictureJsonContext.Default.PlanetaryPictureReport, token), ct);
                    consoleHost.WriteScrollable($"written {jsonPath}");
                }
                return 0;
            }
            finally
            {
                foreach (var (_, _, image) in masters)
                {
                    image.Release();
                }
            }
        });
        return command;
    }

    // One table a channel: a row a measure, a column a master.
    private void Report(string title, string[] labels, List<PlanetaryPicture> pictures, List<ImmutableArray<double>> edges)
    {
        var inv = CultureInfo.InvariantCulture;
        const int nameWidth = 52;
        var width = Math.Max(14, labels.Max(l => l.Length) + 2);
        var lines = new List<string> { "", $"[planetary compare] {title}, every value in each master's own disk units (its sky 0, its disk 1)" };
        void RowAt(string name, Func<int, string> cell)
        {
            var row = new StringBuilder(name.PadRight(nameWidth));
            for (var m = 0; m < pictures.Count; m++)
            {
                row.Append(cell(m).PadLeft(width));
            }
            lines.Add(row.ToString());
        }
        void Row(string name, Func<PlanetaryPicture, string> cell) => RowAt(name, m => cell(pictures[m]));
        var header = new StringBuilder("".PadRight(nameWidth));
        foreach (var label in labels)
        {
            header.Append(label.PadLeft(width));
        }
        lines.Add(header.ToString());
        lines.Add("the disk, inside 0.9 radii");
        Row("  contrast (RMS over mean)", p => p.Contrast.ToString("0.0000", inv));
        for (var j = 0; j < pictures[0].BandRms.Length; j++)
        {
            var band = j;
            Row(string.Create(inv, $"  detail, band {band + 1} (RMS)"), p => p.BandRms[band].ToString("0.0000", inv));
        }
        Row("  pixels at the image's peak (inside the limb)", p => p.AtPeak.ToString(inv));
        Row("  pixels at or below the sky (inside 0.95 radii)", p => p.HeldAtSky.ToString(inv));
        lines.Add("the limb's glow, 1 to 1.5 radii (in disk pixels' worth)");
        Row("  light found", p => p.GlowFound.ToString("0.0", inv));
        Row("  light the planet's model through the pupil expects", p => p.GlowExpected.ToString("0.0", inv));
        Row("  of those, 1 to 1.1 radii: found / expected", p => string.Create(inv, $"{p.GlowNearFound:0.0}/{p.GlowNearExpected:0.0}"));
        Row("  light above the model / below it", p => string.Create(inv, $"{p.GlowExcess:0.0}/{p.GlowDeficit:0.0}"));
        Row("  pixels 3 noise above the model / below it", p => string.Create(inv, $"{p.GlowBrighter}/{p.GlowDarker}"));
        Row("  pixels read", p => p.GlowPixels.ToString(inv));
        if (edges.Any(e => !e.IsEmpty))
        {
            lines.Add("the limb's edge over the pupil's own transfer (1 where only the telescope blurs)");
            for (var k = 0; k < PlanetaryChannelPicture.EdgeFrequencies.Length; k++)
            {
                var at = k;
                RowAt(string.Create(inv, $"  at {PlanetaryChannelPicture.EdgeFrequencies[at]:0.0} cycles a pixel"),
                    m => edges[m].IsEmpty ? "" : edges[m][at].ToString("0.000", inv));
            }
        }
        lines.Add("the sky, past 2.5 radii, moons left out");
        Row("  noise (spread about its plane)", p => p.SkyNoise.ToString("0.00000", inv));
        for (var j = 0; j < pictures[0].SkyBandNoise.Length; j++)
        {
            var band = j;
            // A stack's sky is quiet in disk units (a few millionths in band 1), so these are printed to three figures, not places.
            Row(string.Create(inv, $"  noise, band {band + 1}"), p => p.SkyBandNoise[band].ToString("0.000E+0", inv));
        }
        Row("  grain (band 1's noise over the globe's median)", p => p.Grain.ToString("0.000E+0", inv));
        Row("  gradient across the frame", p => p.SkyGradient.ToString("0.00000", inv));
        Row("  block scatter over what its noise gives", p => p.SkyBlockScatter.ToString("0.00", inv));
        Row("  pixels 3 noise above / below its plane", p => string.Create(inv, $"{p.SkyBright}/{p.SkyDark}"));
        Row("  of those, a Gaussian expects (each side)", p => p.SkyOutliersExpected.ToString("0", inv));
        Row("  pixels read", p => p.SkyPixels.ToString(inv));
        Row("moons found", p => p.Moons.ToString(inv));
        consoleHost.WriteScrollable(string.Join(Environment.NewLine, lines));
    }
}
