using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary colour &lt;label=master.fits&gt;... --composite &lt;png&gt; --opal &lt;dir&gt;</c>: #1212's measurement, read against
/// the rule set before it (docs/plans/planetary-restoration.md, "The rule, set before measuring"). Each colour master's disk-mean
/// colour and latitude chroma spread, against two targets: (A) an sRGB composite (Wikipedia's OPAL 2024 picture of Jupiter), decoded to linear;
/// (B) OPAL's reflectance maps taken through the CIE observer under D65 (<see cref="PlanetaryColour"/>). Says whether A and B agree
/// (rule 1), whether OPAL's apparitions agree (rule 2), the gains each target asks of each master, and the chroma spreads (rule 3),
/// each target blurred to the master's resolution. Jupiter or Saturn (<c>--planet</c>), Saturn's globe read where its rings leave it clear.
/// </summary>
internal sealed class PlanetaryColourSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    // The rule's thresholds (docs/plans/planetary-restoration.md, "The rule, set before measuring").
    private const double ChromaRule = 0.010;
    private const double SpreadLow = 0.9;
    private const double SpreadHigh = 1.1;

    public Command Build()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "The colour masters, each label=path or a path (labelled by its file name).", Arity = ArgumentArity.ZeroOrMore };
        var compositeOpt = new Option<string?>("--composite") { Description = "An sRGB picture of Jupiter to take as target A (Wikipedia's Jupiter_OPAL_2024.png)." };
        var compositeUtcOpt = new Option<string?>("--composite-utc") { Description = "The composite's instant (ISO 8601, UTC); OPAL's 2024 visit ran from 5 January 20:46 to 6 January 15:43 UTC.", DefaultValueFactory = _ => "2024-01-06T06:00:00Z" };
        var opalOpt = new Option<string?>("--opal") { Description = "The folder of OPAL's global maps (hlsp_opal_hst_wfc3-uvis_<planet>-<epoch>_<filter>_v1_globalmap.fits) and each apparition's readme, whose table gives every filter's I/F factor and Minnaert k: target B.", Required = true };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn: the planet the masters and the maps are of. Saturn's globe is read where its rings leave it clear (S6, #1235).", DefaultValueFactory = _ => "jupiter" };
        var compositeBinOpt = new Option<int>("--composite-bin") { Description = "Box-average the composite by this on each axis before fitting its limb.", DefaultValueFactory = _ => 4 };
        var previewOpt = new Option<string?>("--preview") { Description = "Write, into this folder, each master's planetary preview as captured and balanced to the planet's colour (PlanetaryColourBalance) at each --saturation: the sharpened master beside it (<name>_sharpened.fits) when there is one, as the Best stack shows it." };
        var saturationOpt = new Option<string>("--saturation") { Description = "The saturation factors the previews are balanced at, a comma list.", DefaultValueFactory = _ => "1,1.4,2" };
        var cameraOpt = new Option<string?>("--camera") { Description = "The camera every master here was taken with (e.g. ASI462MC, Uranus-C): its colour matrix, from its sensor's QE and the Sony CFA curves, is read after the gains beside the gains alone (#1279, rule E), and the previews add it." };

        var command = new Command("colour", "Planetary colour (#1212): each colour master's disk-mean colour and chroma spread against an sRGB composite and against OPAL's reflectance through the CIE observer, read against the rule set before measuring.")
        {
            Arguments = { mastersArg },
            Options = { compositeOpt, compositeUtcOpt, opalOpt, planetOpt, compositeBinOpt, previewOpt, saturationOpt, cameraOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var named = PlanetaryGeometrySubCommands.ParsePlanet(parseResult.GetValue(planetOpt), path: null);
            if (named is not { } planet)
            {
                consoleHost.WriteError("--planet: jupiter or saturn");
                return 1;
            }
            // The camera's colour matrix (#1279, rule E): camera RGB, white-balanced, to linear sRGB.
            float[]? cameraMatrix = null;
            if (parseResult.GetValue(cameraOpt) is { Length: > 0 } camera)
            {
                await FilterCurveDatabase.LoadAsync(ct);
                if (!FilterCurveDatabase.TryComputeCameraToSrgbMatrix(camera, out var matrix))
                {
                    consoleHost.WriteError($"--camera {camera}: no sensor curve resolves for it");
                    return 1;
                }
                cameraMatrix = matrix;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{camera}'s colour matrix, camera RGB to linear sRGB, by row: {string.Join("; ", Enumerable.Range(0, 3).Select(row => string.Join(", ", Enumerable.Range(0, 3).Select(col => matrix[(row * 3) + col].ToString("0.000", inv)))))}"));
            }
            var apparitions = OpalMaps.ReadApparitions(parseResult.GetValue(opalOpt) ?? "", planet);
            if (apparitions.Length == 0)
            {
                consoleHost.WriteError($"no OPAL maps of {planet}'s visible filters, with their apparition's readme, in --opal");
                return 1;
            }
            foreach (var apparition in apparitions)
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"OPAL {apparition.Year}: {string.Join(", ", apparition.Filters.Select((f, i) => $"{f.Name} ({apparition.Maps[i].Length} rotation{(apparition.Maps[i].Length == 1 ? "" : "s")})"))}"));
            }

            // Target A, the composite, and the comparison of the targets at its geometry (rules 1 and 2).
            Composite? composite = null;
            if (parseResult.GetValue(compositeOpt) is { } compositePath)
            {
                if (planet != CatalogIndex.Jupiter)
                {
                    // OPAL publishes no true-colour composite of Saturn, and the composite's reader knows no rings (S6, #1235).
                    consoleHost.WriteError("--composite: target A is Jupiter's OPAL composite; Saturn has none");
                    return 1;
                }
                if (PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(compositeUtcOpt)) is not { } compositeUtc)
                {
                    consoleHost.WriteError("--composite-utc is not an ISO 8601 time");
                    return 1;
                }
                if (await Task.Run(() => ReadComposite(compositePath, compositeUtc, Math.Max(1, parseResult.GetValue(compositeBinOpt))), ct) is not { } read)
                {
                    consoleHost.WriteError($"{compositePath}: not a readable sRGB picture, or no disk found in it");
                    return 1;
                }
                composite = read;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"composite: {read.Width}x{read.Height} after binning, the disk at ({read.Placement.CenterX:0.0}, {read.Placement.CenterY:0.0}), R {read.Placement.EquatorialRadius:0.0} px; disk mean {Describe(read.DiskMean)}"));

                consoleHost.WriteScrollable("");
                consoleHost.WriteScrollable(string.Create(inv, $"Rule 1 and rule 2, at the composite's geometry ({compositeUtc:yyyy-MM-dd HH:mm} UTC), each apparition's maps averaged over a rotation:"));
                var expected = new List<(int Year, ExpectedColour Colour)>();
                foreach (var apparition in apparitions)
                {
                    var colour = await Task.Run(() => PlanetaryColour.ExpectedDiskColour(apparition, read.Aspect), ct);
                    expected.Add((apparition.Year, colour));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"  OPAL {apparition.Year}: I/F {string.Join(", ", apparition.Filters.Select((f, i) => $"{f.Name} {colour.IfMeans[i]:0.0000}"))}; " +
                        $"rotation changes a filter's disk mean by up to {colour.RotationRange:P1}"));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    linear join {Describe(colour.Linear)}, cubic {Describe(colour.Cubic)}; target B {Describe(colour.Value)}, method uncertainty {colour.Uncertainty:0.0000}"));
                }
                // Rule 1: the composite against the apparition nearest its instant.
                var nearest = expected.OrderBy(e => Math.Abs(e.Year - compositeUtc.Year)).First();
                var distance = read.DiskMean.ChromaDistance(nearest.Colour.Value);
                var allowed = ChromaRule + nearest.Colour.Uncertainty;
                consoleHost.WriteScrollable(string.Create(inv,
                    $"  rule 1: the composite against OPAL {nearest.Year}, (r, g) {read.DiskMean.ChromaR:0.0000}, {read.DiskMean.ChromaG:0.0000} against {nearest.Colour.Value.ChromaR:0.0000}, {nearest.Colour.Value.ChromaG:0.0000}: " +
                    $"{distance:0.0000} apart, {(distance <= allowed ? "within" : "PAST")} {allowed:0.0000} -> target {(distance <= allowed ? "A, the composite" : "B, OPAL's reflectance")}"));
                for (var i = 0; i < expected.Count; i++)
                {
                    for (var j = i + 1; j < expected.Count; j++)
                    {
                        var apart = expected[i].Colour.Value.ChromaDistance(expected[j].Colour.Value);
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"  rule 2: OPAL {expected[i].Year} against {expected[j].Year}: {apart:0.0000} apart, {(apart <= ChromaRule ? "within" : "PAST")} {ChromaRule:0.000} -> " +
                            $"{(apart <= ChromaRule ? "one colour serves every apparition" : "each capture's target from its own apparition")}"));
                    }
                }
            }

            // Each master against both targets.
            var items = (parseResult.GetValue(mastersArg) ?? []).Select(arg =>
            {
                var at = arg.IndexOf('=');
                return at > 0 ? (Label: arg[..at], Path: arg[(at + 1)..]) : (Label: Path.GetFileNameWithoutExtension(arg), Path: arg);
            }).ToArray();

            // Rule 2 with no composite, at the first master's geometry: whether one colour serves every apparition. Saturn's season turns
            // a different hemisphere to us from year to year, so it is read, never assumed (S6, #1235).
            if (composite is null && items.Length > 0 && Image.TryReadFitsFile(items[0].Path, out var first)
                && PlanetaryBestStack.InstantOf(first, epoch: null) is { } firstInstant)
            {
                first.Release();
                var at = PhysicalEphemeris.Compute(planet, firstInstant);
                consoleHost.WriteScrollable("");
                consoleHost.WriteScrollable(string.Create(inv, $"Rule 2, at {items[0].Label}'s geometry ({firstInstant:yyyy-MM-dd HH:mm} UTC), each apparition's maps averaged over a rotation:"));
                var expected = new List<(int Year, ExpectedColour Colour)>();
                foreach (var apparition in apparitions)
                {
                    var colour = await Task.Run(() => PlanetaryColour.ExpectedDiskColour(apparition, at), ct);
                    expected.Add((apparition.Year, colour));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"  OPAL {apparition.Year}: target B {Describe(colour.Value)}, method uncertainty {colour.Uncertainty:0.0000}, rotation range {colour.RotationRange:P1}"));
                }
                var (closest, farthest) = (double.PositiveInfinity, 0.0);
                for (var i = 0; i < expected.Count; i++)
                {
                    for (var j = i + 1; j < expected.Count; j++)
                    {
                        var apart = expected[i].Colour.Value.ChromaDistance(expected[j].Colour.Value);
                        (closest, farthest) = (Math.Min(closest, apart), Math.Max(farthest, apart));
                    }
                }
                consoleHost.WriteScrollable(string.Create(inv,
                    $"  rule 2: apparitions {closest:0.0000} to {farthest:0.0000} apart, {(farthest <= ChromaRule ? "within" : "PAST")} {ChromaRule:0.000} -> " +
                    $"{(farthest <= ChromaRule ? "one colour serves every apparition" : "each capture's target from its own apparition")}"));
            }
            foreach (var (label, path) in items)
            {
                ct.ThrowIfCancellationRequested();
                consoleHost.WriteScrollable("");
                if (!Image.TryReadFitsFile(path, out var image) || image.ChannelCount != 3)
                {
                    consoleHost.WriteError($"{label}: {path} is not a readable three-channel FITS master; left out");
                    continue;
                }
                if (PlanetaryBestStack.InstantOf(image, epoch: null) is not { } instant)
                {
                    consoleHost.WriteError($"{label}: no time in its header; left out");
                    continue;
                }
                var aspect = PhysicalEphemeris.Compute(planet, instant);
                var options = PlanetaryLimbFit.OptionsFor(aspect);
                if (await Task.Run(() => PlanetaryLimbFit.Fit(image, options), ct) is not { } fit)
                {
                    consoleHost.WriteError($"{label}: its limb could not be fitted; left out");
                    continue;
                }
                var (width, height) = (image.Width, image.Height);
                var (red, green, blue) = (image.GetChannelSpan(0).ToArray(), image.GetChannelSpan(1).ToArray(), image.GetChannelSpan(2).ToArray());
                var disk = MetricDisk.From(fit, options);
                var placement = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg);
                var sky = PlanetaryColour.Sky(red, green, blue, width, height, disk);
                var mean = PlanetaryColour.DiskMean(red, green, blue, width, height, disk, sky);
                var (bands, counts) = PlanetaryColour.RgbBands(red, green, blue, sky, width, height, disk, new PlanetaryProjection(aspect, placement));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{label} ({instant:yyyy-MM-dd HH:mm} UTC): disk R {fit.EquatorialRadius:0.0} px, blur sigma {fit.PsfSigma:0.00} px; disk mean {Describe(mean)}, chroma spread {PlanetaryColour.ChromaSpread(bands, counts):0.0000} as captured"));

                // Target B from the apparition nearest the capture, at the capture's own geometry and blur.
                var apparition = apparitions.OrderBy(a => Math.Abs(a.Year - instant.Year)).First();
                var expectedDisk = await Task.Run(() => PlanetaryColour.ExpectedDiskColour(apparition, aspect), ct);
                // The target at three resolutions: none, the limb fit's core, and its core with its halo (which sits at its bound, so
                // whether it is this master's blur is what the comparison shows).
                PlanetaryBlur[] blurs = [PlanetaryBlur.None, PlanetaryBlur.Of(fit, withHalo: false), PlanetaryBlur.Of(fit, withHalo: true)];
                var expectedBands = await Task.Run(() => PlanetaryColour.ExpectedBands(apparition, aspect, placement, width, height, disk, blurs), ct);
                Report(label, "B, OPAL " + apparition.Year.ToString(inv), mean, bands, counts, expectedDisk.Value,
                    [.. expectedBands.Select(e => PlanetaryColour.ChromaSpread(e.Bands, e.Counts))]);

                if (composite is { } a)
                {
                    var spreads = await Task.Run(() => blurs.Select(blur => a.SpreadAt(fit.EquatorialRadius, blur)).ToArray(), ct);
                    Report(label, "A, the composite", mean, bands, counts, a.DiskMean, spreads);
                }

                if (parseResult.GetValue(previewOpt) is { } previewFolder)
                {
                    if (Saturations(parseResult.GetValue(saturationOpt)) is not { } saturations)
                    {
                        consoleHost.WriteError("--saturation is a comma list of positive numbers");
                        return 1;
                    }
                    Directory.CreateDirectory(previewFolder);
                    var sharpenedPath = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "_sharpened.fits");
                    var shown = File.Exists(sharpenedPath) && Image.TryReadFitsFile(sharpenedPath, out var sharpened) && sharpened.ChannelCount == 3 ? sharpened : image;
                    // The planet's measured colour (#1212, S6 #1235), else the target this capture's apparition gives.
                    var target = PlanetaryColourBalance.DiskColourOf(planet) ?? expectedDisk.Value;
                    var (gains, shownSky) = PlanetaryColourBalance.GainsFor(shown, disk, target);
                    var stem = Path.Combine(previewFolder, label);
                    await previewRenderer.RenderPlanetaryAsync(shown, stem + "_captured.png", ct: ct);
                    ReportRenderedSky("as captured", await Task.Run(() => PlanetaryColour.RenderedSky(shown, shown.ComputePlanetaryStretchUniforms(), disk), ct));
                    // Saturated about grey (every colour's departure from white) and about the planet's own colour (the belts' and zones'
                    // departure from the disk, which keeps the disk's mean on the target).
                    (string Name, LinearRgb About)[] centres = [("grey", new LinearRgb(1, 1, 1)), ("disk", target)];
                    foreach (var saturation in saturations)
                    {
                        foreach (var (name, about) in centres)
                        {
                            var balanced = await Task.Run(() => PlanetaryColourBalance.Apply(shown, gains, shownSky, saturation, about), ct);
                            await previewRenderer.RenderPlanetaryAsync(balanced, string.Create(inv, $"{stem}_balanced_{name}_s{saturation:0.0#}.png"), ct: ct);
                            ReportRenderedSky(string.Create(inv, $"about {name} at saturation {saturation:0.0#}"),
                                await Task.Run(() => PlanetaryColour.RenderedSky(balanced, balanced.ComputePlanetaryStretchUniforms(), disk), ct));
                        }
                    }
                    if (cameraMatrix is { } matrix)
                    {
                        // The camera's matrix after its own gains, about the disk's colour (#1279, rule E).
                        var (matrixGains, _) = PlanetaryColourBalance.GainsFor(shown, disk, target, matrix);
                        foreach (var saturation in saturations)
                        {
                            var balanced = await Task.Run(() => PlanetaryColourBalance.Apply(shown, matrixGains, shownSky, saturation, target, matrix), ct);
                            await previewRenderer.RenderPlanetaryAsync(balanced, string.Create(inv, $"{stem}_balanced_matrix_s{saturation:0.0#}.png"), ct: ct);
                        }
                    }
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"  previews of the {(ReferenceEquals(shown, image) ? "stacked" : "sharpened")} master: as captured, and balanced (gains R {gains.R:0.000}, B {gains.B:0.000}) at saturation {string.Join(", ", saturations.Select(s => s.ToString("0.0#", inv)))}, about grey and about the disk's colour{(cameraMatrix is null ? "" : ", and through the camera's matrix about the disk's colour")}"));
                }
            }
            return 0;

            // The sky's tint as a preview renders it (#1229): each channel's mean in levels of 255, and their spread.
            void ReportRenderedSky(string how, LinearRgb sky)
            {
                var spread = Math.Max(sky.R, Math.Max(sky.G, sky.B)) - Math.Min(sky.R, Math.Min(sky.G, sky.B));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"  sky as rendered, {how}: R {255 * sky.R:0.00}, G {255 * sky.G:0.00}, B {255 * sky.B:0.00} of 255, spread {255 * spread:0.00}"));
            }

            // A target's gains for the master, and the master's balanced chroma spread against the target's at each of the three blurs.
            void Report(string label, string target, in LinearRgb mean, LinearRgb[] bands, int[] counts, in LinearRgb targetMean, double[] targetSpreads)
            {
                var gains = mean.GainsTo(targetMean);
                var balanced = bands.Select(b => new LinearRgb(b.R * gains.R, b.G, b.B * gains.B)).ToArray();
                var spread = PlanetaryColour.ChromaSpread(balanced, counts);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"  target {target}: {Describe(targetMean)}; gains R {gains.R:0.000}, G 1, B {gains.B:0.000}; balanced chroma spread {spread:0.0000}"));
                string[] blurNames = ["unblurred", "the core's blur", "core and halo"];
                for (var k = 0; k < targetSpreads.Length; k++)
                {
                    var ratio = spread / targetSpreads[k];
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    the target's through {blurNames[k]}: {targetSpreads[k]:0.0000}, ratio {ratio:0.00} -> " +
                        $"{(ratio is >= SpreadLow and <= SpreadHigh ? "no saturation factor" : $"saturation factor {1 / ratio:0.00}")}"));
                }
                // Today's default saturation about the target, and the camera's matrix at none (#1279, rule E).
                var aboutColour = targetMean;
                var saturated = balanced.Select(b => PlanetaryColourBalance.Saturate(b, PlanetaryColourBalance.EyeSaturation, aboutColour)).ToArray();
                var saturatedSpread = PlanetaryColour.ChromaSpread(saturated, counts);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"  the gains at saturation {PlanetaryColourBalance.EyeSaturation:0.0#}: chroma spread {saturatedSpread:0.0000}, ratios {string.Join(", ", targetSpreads.Select(t => (saturatedSpread / t).ToString("0.00", inv)))} ({string.Join(", ", blurNames)})"));
                if (cameraMatrix is { } m)
                {
                    var through = PlanetaryColourBalance.GainsThrough(mean, targetMean, m);
                    var matrixed = bands.Select(b => PlanetaryColourBalance.Through(new LinearRgb(b.R * through.R, b.G, b.B * through.B), m)).ToArray();
                    var matrixSpread = PlanetaryColour.ChromaSpread(matrixed, counts);
                    var meanAfter = PlanetaryColourBalance.Through(new LinearRgb(mean.R * through.R, mean.G, mean.B * through.B), m);
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"  the gains and the camera's matrix at saturation 1: gains R {through.R:0.000}, G 1, B {through.B:0.000}; the disk mean after it {Describe(meanAfter)}, {meanAfter.ChromaDistance(targetMean):0.0000} from the target; chroma spread {matrixSpread:0.0000}, {matrixSpread / spread:0.00} of the gains' alone"));
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"    ratios {string.Join(", ", targetSpreads.Select(t => (matrixSpread / t).ToString("0.00", inv)))} ({string.Join(", ", blurNames)})"));
                }
            }
        });
        return command;
    }

    private static double[]? Saturations(string? list)
    {
        var values = new List<double>();
        foreach (var part in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                return null;
            }
            values.Add(value);
        }
        return values.Count > 0 ? [.. values] : null;
    }

    private static string Describe(in LinearRgb colour)
        => string.Create(CultureInfo.InvariantCulture, $"R/G {colour.R / colour.G:0.000}, B/G {colour.B / colour.G:0.000}, (r, g) {colour.ChromaR:0.0000}, {colour.ChromaG:0.0000}");

    // The composite decoded to linear, binned, its limb fitted at its instant, and its disk mean.
    private static Composite? ReadComposite(string path, DateTimeOffset utc, int bin)
    {
        if (!Image.TryReadImageFile(path, out var image) || image.ChannelCount < 3)
        {
            return null;
        }
        var (red, green, blue, width, height) = PlanetaryColour.LinearFromSrgb(image, bin);
        var luminance = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                luminance[y, x] = (red[i] + green[i] + blue[i]) / 3;
            }
        }
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, utc);
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        if (PlanetaryLimbFit.Fit(Image.FromChannel(luminance), options) is not { } fit)
        {
            return null;
        }
        var disk = MetricDisk.From(fit, options);
        var sky = PlanetaryColour.Sky(red, green, blue, width, height, disk);
        return new Composite(red, green, blue, width, height, aspect, new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg),
            options.AxisRatio, fit.AxisAngleDeg, sky, PlanetaryColour.DiskMean(red, green, blue, width, height, disk, sky));
    }

    private sealed record Composite(float[] Red, float[] Green, float[] Blue, int Width, int Height, PlanetAspect Aspect, DiskPlacement Placement,
        double AxisRatio, double AxisAngleDeg, LinearRgb Sky, LinearRgb DiskMean)
    {
        // The composite's chroma spread once shrunk to a master's disk radius and put through its blur.
        public double SpreadAt(double radiusPx, PlanetaryBlur blur)
        {
            var factor = Math.Min(1, radiusPx / Placement.EquatorialRadius);
            var planes = new[] { Red, Green, Blue }.Select(p =>
            {
                var (shrunk, w, h) = PlanetaryColour.Shrink(p, Width, Height, factor);
                return (Plane: blur.Apply(shrunk, w, h), W: w, H: h);
            }).ToArray();
            var placement = PlanetaryColour.PlacedAt(Placement, factor);
            var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, AxisRatio, AxisAngleDeg);
            var sky = new LinearRgb(Sky.R, Sky.G, Sky.B);
            var (bands, counts) = PlanetaryColour.RgbBands(planes[0].Plane, planes[1].Plane, planes[2].Plane, sky, planes[0].W, planes[0].H, disk,
                new PlanetaryProjection(Aspect, placement));
            return PlanetaryColour.ChromaSpread(bands, counts);
        }
    }

}
