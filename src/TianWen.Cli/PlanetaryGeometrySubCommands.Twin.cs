using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

internal sealed partial class PlanetaryGeometrySubCommands
{
    /// <summary>
    /// <c>planetary twin</c>: a capture's synthetic twin with its air fitted to the capture's statistics (docs/plans/planetary-stacking.md, A1,
    /// #817), the search R2 and #1281 ran by hand. Everything <c>planetary degrade</c> reads off a capture is read here the same way; the five
    /// knobs (<see cref="TwinKnobs"/>), and a colour capture's defocus a colour, are fitted on short twins, and the best is made once at the
    /// length it is confirmed at. A colour capture is fitted over its three photosite colours together.
    /// </summary>
    public Command BuildTwin()
    {
        var inputArg = new Argument<string>("capture") { Description = "The real SER capture the twin stands in for, mono or colour (Bayer)." };
        var opalOpt = new Option<string>("--opal") { Description = "A folder of OPAL's global maps and their readmes; each plane's map is the apparition nearest the capture's year, its filter nearest the plane's wavelength.", Required = true };
        var outputOpt = new Option<string>("--output", "-o") { Description = "A folder for the search's twins, the fitted knobs (twin.json) and the confirmed twin (twin.ser, its truth and record beside it).", Required = true };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var pupilOpts = PlanetaryMasterScore.PupilOptions();
        pupilOpts.ApertureMm.Description = "The aperture, mm, of the telescope the capture was taken through (with --obstruction); the 254 mm Newtonian when neither this nor --telescope is given.";
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "A mono capture's filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var bayerWavelengthsOpt = new Option<string>("--bayer-wavelengths") { Description = "A colour capture's red, green and blue effective wavelengths, nm.", DefaultValueFactory = _ => "610,535,460" };
        var exposureOpt = new Option<double>("--exposure-ms") { Description = "Each frame's exposure, ms (a SER does not record it).", Required = true };
        var outerScaleOpt = new Option<double>("--outer-scale") { Description = "The free air's outer scale, m (not fitted; R2's calibrated twin had 4).", DefaultValueFactory = _ => 4 };
        var localOuterScaleOpt = new Option<double>("--local-outer-scale") { Description = "The still layer's outer scale, m: about the tube's (not fitted).", DefaultValueFactory = _ => 0.25 };
        var localWindOpt = new Option<double>("--local-wind") { Description = "The still layer's drift across the pupil, m/s (not fitted; R2's calibrated twin had none).", DefaultValueFactory = _ => 0 };
        var trialFramesOpt = new Option<int>("--frames") { Description = "The frames each trial twin is made and measured over, the capture's first as many.", DefaultValueFactory = _ => 300 };
        var confirmFramesOpt = new Option<int>("--confirm-frames") { Description = "The frames the best knobs are confirmed over (#1281: a twin met at 300 frames read 0.87 to 0.93 of the real edge width at 2,600); a colour twin's at most what it holds at once.", DefaultValueFactory = _ => 3000 };
        var trialsOpt = new Option<int>("--trials") { Description = "The most twins the search makes; 0 confirms --start as given, with no search.", DefaultValueFactory = _ => 40 };
        var startOpt = new Option<string?>("--start") { Description = "The search's start: r0 (cm), wind (m/s), the still layer's r0 (cm), the scatter's share and its core (arcsec), a comma list; R2's hand calibration of 2022-09-03 Red by default." };
        var startDefocusOpt = new Option<string>("--start-defocus") { Description = "A colour capture's start for each colour's static defocus, red, green and blue, nm RMS.", DefaultValueFactory = _ => "50,50,50" };
        var seedOpt = new Option<int>("--seed") { Description = "The draws' seed, the same for every trial so two differ by their knobs alone.", DefaultValueFactory = _ => 1 };
        var whitenedOpt = new Option<bool>("--whitened-correlation") { Description = "Register the statistics' frames by phase correlation, as R2's hand calibration did, not by a plain cross-correlation (on the EdgeHD Jupiter the whitened aligner jumped by whole pixels, up to 15, and read an aligner's error of 2.38 px where plain read 0.15)." };

        var command = new Command("twin",
            "A synthetic twin of a capture with its air fitted to the capture's statistics (A1 of #817): the free air's r0 and wind, the still layer's r0, the telescope's scatter and a colour capture's defocus a colour, searched on short twins and confirmed at length.")
        {
            Arguments = { inputArg },
            Options = { opalOpt, outputOpt, planetOpt, pupilOpts.ApertureMm, pupilOpts.Obstruction, pupilOpts.Telescope, wavelengthOpt, bayerWavelengthsOpt, exposureOpt, outerScaleOpt, localOuterScaleOpt, localWindOpt, trialFramesOpt, confirmFramesOpt, trialsOpt, startOpt, startDefocusOpt, seedOpt, whitenedOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var output = parseResult.GetValue(outputOpt) ?? "";
            Directory.CreateDirectory(output);
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var progress = new Progress<string>(line => consoleHost.WriteScrollable("    " + line));
            if (parseResult.GetValue(startOpt) is { } startText && ParseKnobs(startText) is null)
            {
                consoleHost.WriteError("--start takes five numbers: r0 (cm), wind (m/s), the still layer's r0 (cm), the scatter's share, its core (arcsec)");
                return 1;
            }
            var maxTrials = parseResult.GetValue(trialsOpt);

            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            if (reader.Timestamps is not { IsDefaultOrEmpty: false } allTimes)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var colour = whole.Layout == PlanetaryFrameLayout.SplitCfa;
            if (!colour && whole.Layout != PlanetaryFrameLayout.Mono)
            {
                consoleHost.WriteError($"{input}: a {whole.Layout} capture; the twin takes a mono or a Bayer one");
                return 1;
            }
            // A plane a colour of the capture: one for a mono capture, its red, green and blue photosites for a colour one.
            string[] names = colour ? ["r", "g", "b"] : [""];
            int[] channels = colour ? [CfaPlaneStream.Red, CfaPlaneStream.Green1, CfaPlaneStream.Blue] : [-1];
            var planes = names.Length;
            var wavelengths = colour ? CommaNumbers(parseResult.GetValue(bayerWavelengthsOpt)) : [parseResult.GetValue(wavelengthOpt)];
            var startDefocus = CommaNumbers(parseResult.GetValue(startDefocusOpt));
            if (wavelengths.Length != planes || (colour && startDefocus.Length != 3))
            {
                consoleHost.WriteError("--bayer-wavelengths and --start-defocus take three numbers each, red, green and blue");
                return 1;
            }
            var start = ParseKnobs(parseResult.GetValue(startOpt)) ?? TwinKnobs.HandCalibratedRed;
            if (colour)
            {
                start = start with { Defocus = new ColourDefocus(startDefocus[0], startDefocus[1], startDefocus[2]) };
            }
            if (maxTrials != 0 && maxTrials <= start.KnobCount)
            {
                consoleHost.WriteError($"--trials takes 0 (confirm --start) or more than the {start.KnobCount} knobs searched");
                return 1;
            }

            // Each plane's map: the OPAL apparition nearest the capture's year, its filter nearest the plane's wavelength (Saturn's filled zonally).
            var opalFolder = parseResult.GetValue(opalOpt) ?? "";
            var apparitions = OpalMaps.ReadApparitions(opalFolder, planet);
            var maps = new (PlanetMap Map, int Year, OpalFilter Filter)[planes];
            var mapNames = new string[planes];
            for (var c = 0; c < planes; c++)
            {
                if (OpalMaps.ForCapture(apparitions, allTimes[0].UtcDateTime.Year, wavelengths[c]) is not { } chosen)
                {
                    consoleHost.WriteError($"{opalFolder}: no OPAL maps of {planet} with their readme");
                    return 1;
                }
                maps[c] = chosen;
                mapNames[c] = string.Create(inv, $"OPAL {chosen.Year} {chosen.Filter.Name}");
                consoleHost.WriteScrollable(string.Create(inv,
                    $"the twin's {(colour ? $"{names[c]} " : "")}map: {mapNames[c]} ({chosen.Filter.PivotNm:0} nm, Minnaert k {chosen.Filter.MinnaertK:0.000}), for a capture of {allTimes[0].UtcDateTime.Year} at {wavelengths[c]:0} nm"));
            }

            var trialFrames = Math.Min(parseResult.GetValue(trialFramesOpt), whole.FrameCount);
            var confirmFrames = Math.Min(parseResult.GetValue(confirmFramesOpt), whole.FrameCount);
            // A colour twin's frames sit in one array until the last colour is made (PlanetaryDegrade.MakeBayerAsync).
            var holds = (int)Math.Min(int.MaxValue, (2L << 30) / ((long)reader.Width * reader.Height * 2));
            if (colour && confirmFrames > holds)
            {
                consoleHost.WriteScrollable($"a colour twin of {reader.Width} x {reader.Height} holds {holds} frames at once: confirming over {holds}, not {confirmFrames}");
                confirmFrames = holds;
            }
            var (_, ox, oy) = reader.ColorId.ToSensorType();
            using var trialReal = new PlanetaryFrameWindow(whole, 0, trialFrames);
            if (trialReal.MidCapture is not { } mid)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var aspect = PhysicalEphemeris.Compute(planet, mid);
            // The statistics register plainly by default: the whitened aligner places an 8-bit frame by its noise (R5) and on the EdgeHD
            // Jupiter jumped between wrong peaks, which spoils both the aligner's error and the mount's drift the twin replays.
            var measure = new CaptureStatisticsOptions(PlanetaryLimbFit.OptionsFor(aspect))
            {
                FullScaleAdu = reader.MaxSampleValue,
                WhitenedCorrelation = parseResult.GetValue(whitenedOpt),
            };
            var pupil = PlanetaryMasterScore.PupilFrom(parseResult, pupilOpts) ?? NewtonianPupil;
            var main = SaturnRings.Main.Rings;
            var rings = planet == CatalogIndex.Saturn ? SaturnRings.Structured(main[0].Level, main[1].Level, main[2].Level, main[3].Level) : null;

            // One plane of a stream measured: the stream itself, or a colour's photosites.
            async Task<CaptureStatistics?> MeasurePlane(IPlanetaryFrameStream stream, int c, IProgress<string>? report, CancellationToken token)
            {
                if (!colour)
                {
                    return await PlanetaryCaptureStatistics.MeasureAsync(stream, measure, report, token);
                }
                using var plane = new CfaPlaneStream(stream, channels[c]);
                return await PlanetaryCaptureStatistics.MeasureAsync(plane, measure, report, token);
            }

            // A window of the real capture, each plane measured once and kept in the output folder, keyed by the capture, the plane, its
            // frames and the options; then the disks placed on the window's own reference (a statistic's zero shift is its window's
            // reference frame, and the mount drifts a pixel a second), and the camera's terms read a plane at a time.
            async Task<TwinWindow?> Prepare(IPlanetaryFrameStream stream)
            {
                var truths = new CaptureStatistics[planes];
                for (var c = 0; c < planes; c++)
                {
                    var path = Path.Combine(output, string.Create(inv, $"real-{stream.FrameCount}{(colour ? $".{names[c]}" : "")}.statistics.json"));
                    var key = $"{Path.GetFullPath(input)} | {names[c]} | {stream.FrameCount} frames | {measure}";
                    var label = string.Create(inv, $"{Path.GetFileName(input)}{(colour ? $" ({names[c]})" : "")}, its first {stream.FrameCount} frames");
                    if (await PlanetaryCaptureStatistics.TryLoadAsync(path, key, ct) is { } cached)
                    {
                        consoleHost.WriteScrollable($"{label}: statistics read from {path}");
                        truths[c] = cached;
                        continue;
                    }
                    consoleHost.WriteScrollable($"measuring {label}");
                    if (await MeasurePlane(stream, c, progress, ct) is not { } measured)
                    {
                        consoleHost.WriteError($"{input}: no disk found{(colour ? $" in its {names[c]} photosites" : "")}");
                        return null;
                    }
                    await PlanetaryCaptureStatistics.SaveAsync(measured, key, path, ct);
                    truths[c] = measured;
                }

                DiskPlacement[] at;
                double scale;
                if (colour)
                {
                    using var greenPlane = new CfaPlaneStream(stream, CfaPlaneStream.Green1);
                    if (await PlaceColoursAsync(stream, greenPlane, truths[1], aspect, ox, oy, ct) is not { } placedColours)
                    {
                        consoleHost.WriteError($"{input}: the green stack's limb, or a colour plane's, could not be fitted");
                        return null;
                    }
                    at = [placedColours.Red, placedColours.Green, placedColours.Blue];
                    scale = placedColours.SensorScale;
                }
                else
                {
                    if (await PlaceDiskAsync(stream, truths[0], aspect, ct) is not { } placed)
                    {
                        consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                        return null;
                    }
                    at = [placed.Reference];
                    scale = placed.Scale;
                }

                // Everything but the knobs, as planetary degrade sets it: each colour's own gain, read on its own noise.
                var baseOptions = new DegradeOptions[planes];
                for (var c = 0; c < planes; c++)
                {
                    if (PlanetaryDegrade.GainFor(truths[c].Camera.DiskLevel, truths[c].Noise[0].Disk, truths[c].Camera.FarSkyNoise) is not { } electronsPerAdu)
                    {
                        consoleHost.WriteError($"the {(colour ? $"{names[c]} " : "")}disk's finest band leaves no room for shot noise: the camera's gain cannot be read off this capture");
                        return null;
                    }
                    baseOptions[c] = WithCamera(new DegradeOptions(pupil, wavelengths[c] * 1e-9)
                    {
                        OuterScaleM = parseResult.GetValue(outerScaleOpt),
                        ExposureSeconds = parseResult.GetValue(exposureOpt) / 1000,
                        LocalOuterScaleM = parseResult.GetValue(localOuterScaleOpt),
                        LocalWindMps = parseResult.GetValue(localWindOpt),
                        MinnaertK = maps[c].Filter.MinnaertK,
                        Seed = parseResult.GetValue(seedOpt),
                        Rings = rings,
                    }, truths[c].Camera, electronsPerAdu);
                }
                return new TwinWindow(stream.FrameCount, truths, at, scale, baseOptions);
            }

            // A twin of the window made with the knobs, each plane's planet at its own level through their blur (S3), and compared plane by
            // plane: a colour twin with green's motion and flux for all three (R5a). The rows of a colour twin are named by their colour.
            async Task<(ImmutableArray<TwinStatistic> Rows, CaptureStatistics[]? Twin)> MakeAndCompare(TwinKnobs knobs, string path, TwinWindow window, bool withTruth,
                CancellationToken token)
            {
                var times = allTimes[..window.Frames];
                var options = new DegradeOptions[planes];
                for (var c = 0; c < planes; c++)
                {
                    var o = knobs.ApplyTo(window.Base[c], colour ? c : -1);
                    options[c] = o with { DiskLevelAdu = o.DiskLevelAdu * PlanetaryDegrade.ShownLevelGain(maps[c].Map, planet, times, window.At[c], window.Scale, o) };
                }
                var motion = window.Truths[colour ? 1 : 0];
                var depth = motion.Camera.FullScaleAdu <= 255 ? 8 : 16;
                var buffer = new byte[reader.Width * reader.Height * (depth == 8 ? 1 : 2)];
                var partial = path + ".partial";
                ImmutableArray<SyntheticFrame> madeFrames;
                using (var writer = new SerWriter(partial, reader.Width, reader.Height, colour ? reader.ColorId : SerColorId.Mono, depth, instrument: "TianWen planetary twin"))
                {
                    void Write(int index, ushort[] samples) => writer.AppendFrame(Pack(samples, buffer, depth), times[index]);
                    if (colour)
                    {
                        ImmutableArray<double> moveX = [.. motion.MountX.Select(v => 2 * v)];
                        ImmutableArray<double> moveY = [.. motion.MountY.Select(v => 2 * v)];
                        var made = await PlanetaryDegrade.MakeBayerAsync(planet, times, new BayerColour(maps[0].Map, window.At[0], options[0]),
                            new BayerColour(maps[1].Map, window.At[1], options[1]), new BayerColour(maps[2].Map, window.At[2], options[2]), window.Scale,
                            moveX, moveY, motion.Flux, reader.Width, reader.Height, ox, oy, Write, cancellationToken: token);
                        madeFrames = made.Green;
                    }
                    else
                    {
                        madeFrames = await PlanetaryDegrade.MakeAsync(maps[0].Map, planet, times, window.At[0], window.Scale, motion.MountX, motion.MountY, motion.Flux,
                            reader.Width, reader.Height, options[0], Write, cancellationToken: token);
                    }
                }
                File.Move(partial, path, overwrite: true);
                if (withTruth)
                {
                    var referenceTime = times[motion.ReferenceIndex];
                    var referenceAspect = PhysicalEphemeris.Compute(planet, referenceTime);
                    for (var c = 0; c < planes; c++)
                    {
                        var truthImage = PlanetaryRender.RenderDiffracted(maps[c].Map, referenceAspect, window.At[c], reader.Width, reader.Height, options[c].MinnaertK, pupil,
                            options[c].WavelengthM, window.Scale, rings: options[c].Rings);
                        WriteTruth(Path.ChangeExtension(path, colour ? $".truth.{names[c]}.fits" : ".truth.fits"), truthImage, reader.Width, reader.Height, window.At[c],
                            options[c], referenceTime, mapNames[c]);
                    }
                    WriteRecord(Path.ChangeExtension(path, ".frames.csv"), madeFrames);
                }
                using var twin = SerFrameStream.Open(path);
                var rows = ImmutableArray.CreateBuilder<TwinStatistic>();
                var measured = new CaptureStatistics[planes];
                for (var c = 0; c < planes; c++)
                {
                    if (await MeasurePlane(twin, c, null, token) is not { } plane)
                    {
                        return ([], null);
                    }
                    measured[c] = plane;
                    rows.AddRange(TwinComparison.Compare(window.Truths[c], plane).Select(r => colour ? r with { Name = $"{names[c]}: {r.Name}" } : r));
                }
                return (rows.ToImmutable(), measured);
            }

            if (await Prepare(trialReal) is not { } trialWindow)
            {
                return 1;
            }
            TwinTrial best;
            ImmutableArray<TwinTrial> trials;
            if (maxTrials == 0)
            {
                consoleHost.WriteScrollable($"confirming the start as given, with no search: {Describe(start)}");
                best = new TwinTrial(start, double.NaN, []);
                trials = [];
            }
            else
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"searching the twin's air on the first {trialFrames} frames, at most {maxTrials} twins, from {Describe(start)}"));
                var trialPath = Path.Combine(output, "trial.ser");
                var searched = new Progress<TwinTrial>(trial => consoleHost.WriteScrollable($"    {Describe(trial.Knobs)}: {DescribeMismatch(trial.Mismatch)}"));
                var started = DateTime.UtcNow;
                (best, trials) = await PlanetaryTwinCalibration.FitAsync(start,
                    async (knobs, token) => (await MakeAndCompare(knobs, trialPath, trialWindow, withTruth: false, token)).Rows, maxTrials, searched, ct);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"the best of {trials.Length} twins, in {(DateTime.UtcNow - started).TotalMinutes:0.0} min: {Describe(best.Knobs)}, {DescribeMismatch(best.Mismatch)}"));
                WriteRows($"the best twin on the first {trialFrames} frames (synthetic over real; * fitted):", best.Rows);
            }

            // Confirmed at length: the knobs' twin over the capture's first confirm-frames, against the real capture's same frames, its disks
            // placed on that window's own reference.
            using var confirmReal = new PlanetaryFrameWindow(whole, 0, confirmFrames);
            if (await Prepare(confirmReal) is not { } confirmWindow)
            {
                return 1;
            }
            var twinPath = Path.Combine(output, "twin.ser");
            consoleHost.WriteScrollable(string.Create(inv, $"confirming on the first {confirmFrames} frames: {Path.GetFileName(twinPath)}"));
            var confirmStarted = DateTime.UtcNow;
            var (confirmRows, confirmed) = await MakeAndCompare(best.Knobs, twinPath, confirmWindow, withTruth: true, ct);
            if (confirmed is null)
            {
                consoleHost.WriteError($"{twinPath}: no disk found");
                return 1;
            }
            for (var c = 0; c < planes; c++)
            {
                var suffix = colour ? $" ({names[c]})" : "";
                WriteStatistics(Path.GetFileName(input) + suffix, confirmWindow.Truths[c]);
                WriteStatistics(Path.GetFileName(twinPath) + suffix, confirmed[c]);
                WriteComparison(confirmWindow.Truths[c], confirmed[c]);
            }
            var confirmedMismatch = PlanetaryTwinCalibration.MeanMismatch(confirmRows);
            consoleHost.WriteScrollable(string.Create(inv,
                $"confirmed in {(DateTime.UtcNow - confirmStarted).TotalMinutes:0.0} min: {Describe(best.Knobs)}, {DescribeMismatch(confirmedMismatch)}"));
            WriteTwinRecord(Path.Combine(output, "twin.json"), input, string.Join(", ", mapNames), best, confirmedMismatch, trialFrames, confirmFrames, trials);
            return 0;
        });
        return command;
    }

    // What a capture's window gives a twin: its frames, each plane's statistics and disk, the pixel scale, and each plane's options but the knobs.
    private sealed record TwinWindow(int Frames, CaptureStatistics[] Truths, DiskPlacement[] At, double Scale, DegradeOptions[] Base);

    private static string Describe(TwinKnobs k) => string.Create(CultureInfo.InvariantCulture,
        $"r0 {k.R0M * 100:0.00} cm, wind {k.WindMps:0.0} m/s, still layer r0 {k.LocalR0M * 100:0.00} cm, scatter {k.ScatterFraction * 100:0.00} % with a {k.ScatterCoreArcsec:0.0}\" core{(k.Defocus is { } d ? $", defocus {d.Red:0} / {d.Green:0} / {d.Blue:0} nm" : "")}");

    // The mean squared log ratio, and what it is as a typical statistic's ratio.
    private static string DescribeMismatch(double mismatch) => double.IsFinite(mismatch)
        ? string.Create(CultureInfo.InvariantCulture, $"mismatch {mismatch:0.00000} (a fitted statistic {100 * (Math.Exp(Math.Sqrt(mismatch)) - 1):0.0} % off, RMS)")
        : "no disk to measure";

    private static TwinKnobs? ParseKnobs(string? text)
    {
        var values = CommaNumbers(text);
        return values.Length == 5 ? new TwinKnobs(values[0] / 100, values[1], values[2] / 100, values[3], values[4]) : null;
    }

    // The search's record, written by hand (one small shape; no JsonSerializerContext for the AOT publish).
    private static void WriteTwinRecord(string path, string capture, string maps, TwinTrial best, double confirmedMismatch, int searchFrames, int confirmFrames,
        ImmutableArray<TwinTrial> trials)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        static void Knobs(Utf8JsonWriter json, TwinKnobs k)
        {
            json.WriteNumber("r0M", k.R0M);
            json.WriteNumber("windMps", k.WindMps);
            json.WriteNumber("localR0M", k.LocalR0M);
            json.WriteNumber("scatterFraction", k.ScatterFraction);
            json.WriteNumber("scatterCoreArcsec", k.ScatterCoreArcsec);
            if (k.Defocus is { } d)
            {
                json.WriteStartArray("defocusNm");
                json.WriteNumberValue(d.Red);
                json.WriteNumberValue(d.Green);
                json.WriteNumberValue(d.Blue);
                json.WriteEndArray();
            }
        }
        static void Mismatch(Utf8JsonWriter json, string name, double value)
        {
            if (double.IsFinite(value))
            {
                json.WriteNumber(name, value);
            }
            else
            {
                json.WriteNull(name);
            }
        }
        json.WriteStartObject();
        json.WriteString("capture", Path.GetFullPath(capture));
        json.WriteString("maps", maps);
        json.WriteNumber("searchFrames", searchFrames);
        json.WriteNumber("confirmFrames", confirmFrames);
        json.WriteStartObject("knobs");
        Knobs(json, best.Knobs);
        json.WriteEndObject();
        Mismatch(json, "searchMismatch", best.Mismatch);
        Mismatch(json, "confirmedMismatch", confirmedMismatch);
        json.WriteStartArray("trials");
        foreach (var trial in trials)
        {
            json.WriteStartObject();
            Knobs(json, trial.Knobs);
            Mismatch(json, "mismatch", trial.Mismatch);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
