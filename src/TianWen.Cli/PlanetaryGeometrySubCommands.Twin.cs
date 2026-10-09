using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

internal sealed partial class PlanetaryGeometrySubCommands
{
    /// <summary>
    /// <c>planetary twin</c>: a capture's synthetic twin with its air fitted to the capture's statistics (docs/plans/planetary-stacking.md, A1,
    /// #817), the search R2 and #1281 ran by hand. Everything <c>planetary degrade</c> reads off a capture is read here the same way; the five
    /// knobs (<see cref="TwinKnobs"/>) are fitted on short twins, and the best is made once at the length it is confirmed at.
    /// </summary>
    public Command BuildTwin()
    {
        var inputArg = new Argument<string>("capture") { Description = "The real mono SER capture the twin stands in for." };
        var opalOpt = new Option<string>("--opal") { Description = "A folder of OPAL's global maps and their readmes; the twin's map is the apparition nearest the capture's year, its filter nearest --wavelength.", Required = true };
        var outputOpt = new Option<string>("--output", "-o") { Description = "A folder for the search's twins, the fitted knobs (twin.json) and the confirmed twin (twin.ser, its truth and record beside it).", Required = true };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var pupilOpts = PlanetaryMasterScore.PupilOptions();
        pupilOpts.ApertureMm.Description = "The aperture, mm, of the telescope the capture was taken through (with --obstruction); the 254 mm Newtonian when neither this nor --telescope is given.";
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var exposureOpt = new Option<double>("--exposure-ms") { Description = "Each frame's exposure, ms (a SER does not record it).", Required = true };
        var outerScaleOpt = new Option<double>("--outer-scale") { Description = "The free air's outer scale, m (not fitted; R2's calibrated twin had 4).", DefaultValueFactory = _ => 4 };
        var localOuterScaleOpt = new Option<double>("--local-outer-scale") { Description = "The still layer's outer scale, m: about the tube's (not fitted).", DefaultValueFactory = _ => 0.25 };
        var localWindOpt = new Option<double>("--local-wind") { Description = "The still layer's drift across the pupil, m/s (not fitted).", DefaultValueFactory = _ => 1 };
        var trialFramesOpt = new Option<int>("--frames") { Description = "The frames each trial twin is made and measured over, the capture's first as many.", DefaultValueFactory = _ => 300 };
        var confirmFramesOpt = new Option<int>("--confirm-frames") { Description = "The frames the best knobs are confirmed over (#1281: a twin met at 300 frames read 0.87 to 0.93 of the real edge width at 2,600).", DefaultValueFactory = _ => 3000 };
        var trialsOpt = new Option<int>("--trials") { Description = "The most twins the search makes.", DefaultValueFactory = _ => 40 };
        var startOpt = new Option<string?>("--start") { Description = "The search's start: r0 (cm), wind (m/s), the still layer's r0 (cm), the scatter's share and its core (arcsec), a comma list; R2's hand calibration of 2022-09-03 Red by default." };
        var seedOpt = new Option<int>("--seed") { Description = "The draws' seed, the same for every trial so two differ by their knobs alone.", DefaultValueFactory = _ => 1 };

        var command = new Command("twin",
            "A synthetic twin of a capture with its air fitted to the capture's statistics (A1 of #817): the free air's r0 and wind, the still layer's r0 and the telescope's scatter, searched on short twins and confirmed at length.")
        {
            Arguments = { inputArg },
            Options = { opalOpt, outputOpt, planetOpt, pupilOpts.ApertureMm, pupilOpts.Obstruction, pupilOpts.Telescope, wavelengthOpt, exposureOpt, outerScaleOpt, localOuterScaleOpt, localWindOpt, trialFramesOpt, confirmFramesOpt, trialsOpt, startOpt, seedOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var output = parseResult.GetValue(outputOpt) ?? "";
            Directory.CreateDirectory(output);
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var wavelengthNm = parseResult.GetValue(wavelengthOpt);
            var progress = new Progress<string>(line => consoleHost.WriteScrollable("    " + line));
            if (parseResult.GetValue(startOpt) is { } startText && ParseKnobs(startText) is null)
            {
                consoleHost.WriteError("--start takes five numbers: r0 (cm), wind (m/s), the still layer's r0 (cm), the scatter's share, its core (arcsec)");
                return 1;
            }
            var start = ParseKnobs(parseResult.GetValue(startOpt)) ?? TwinKnobs.HandCalibratedRed;

            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            if (reader.Timestamps is not { IsDefaultOrEmpty: false } allTimes)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            if (whole.Layout != PlanetaryFrameLayout.Mono)
            {
                consoleHost.WriteError($"{input}: a colour capture's twin is fitted per photosite colour, which this verb does not do yet; it takes a mono capture");
                return 1;
            }

            // The map: the OPAL apparition nearest the capture's year, its filter nearest the capture's wavelength (Saturn's filled zonally).
            var opalFolder = parseResult.GetValue(opalOpt) ?? "";
            if (OpalMaps.ForCapture(OpalMaps.ReadApparitions(opalFolder, planet), allTimes[0].UtcDateTime.Year, wavelengthNm) is not { } chosen)
            {
                consoleHost.WriteError($"{opalFolder}: no OPAL maps of {planet} with their readme");
                return 1;
            }
            var map = chosen.Map;
            var mapName = string.Create(inv, $"OPAL {chosen.Year} {chosen.Filter.Name}");
            consoleHost.WriteScrollable(string.Create(inv,
                $"the twin's map: {mapName} ({chosen.Filter.PivotNm:0} nm, Minnaert k {chosen.Filter.MinnaertK:0.000}), for a capture of {allTimes[0].UtcDateTime.Year} at {wavelengthNm:0} nm"));

            var trialFrames = Math.Min(parseResult.GetValue(trialFramesOpt), whole.FrameCount);
            var confirmFrames = Math.Min(parseResult.GetValue(confirmFramesOpt), whole.FrameCount);
            using var trialReal = new PlanetaryFrameWindow(whole, 0, trialFrames);
            if (trialReal.MidCapture is not { } mid)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var aspect = PhysicalEphemeris.Compute(planet, mid);
            var measure = new CaptureStatisticsOptions(PlanetaryLimbFit.OptionsFor(aspect)) { FullScaleAdu = reader.MaxSampleValue };

            // A window of the real capture measured once and kept in the output folder, keyed by the capture, its frames and the options.
            async Task<CaptureStatistics?> MeasureReal(IPlanetaryFrameStream stream)
            {
                var path = Path.Combine(output, string.Create(inv, $"real-{stream.FrameCount}.statistics.json"));
                var key = $"{Path.GetFullPath(input)} | {stream.FrameCount} frames | {measure}";
                if (await PlanetaryCaptureStatistics.TryLoadAsync(path, key, ct) is { } cached)
                {
                    consoleHost.WriteScrollable($"{Path.GetFileName(input)}, its first {stream.FrameCount} frames: statistics read from {path}");
                    return cached;
                }
                consoleHost.WriteScrollable($"measuring {Path.GetFileName(input)}, its first {stream.FrameCount} frames");
                if (await PlanetaryCaptureStatistics.MeasureAsync(stream, measure, progress, ct) is not { } measured)
                {
                    consoleHost.WriteError($"{input}: no disk found");
                    return null;
                }
                await PlanetaryCaptureStatistics.SaveAsync(measured, key, path, ct);
                return measured;
            }
            if (await MeasureReal(trialReal) is not { } trialTruth)
            {
                return 1;
            }
            if (await PlaceDiskAsync(trialReal, trialTruth, aspect, ct) is not { } placed)
            {
                consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                return 1;
            }
            var (reference, scale) = placed;
            if (PlanetaryDegrade.GainFor(trialTruth.Camera.DiskLevel, trialTruth.Noise[0].Disk, trialTruth.Camera.FarSkyNoise) is not { } electronsPerAdu)
            {
                consoleHost.WriteError("the disk's finest band leaves no room for shot noise: the camera's gain cannot be read off this capture");
                return 1;
            }

            // Everything but the five knobs, as planetary degrade sets it, the camera read off the trial window.
            var pupil = PlanetaryMasterScore.PupilFrom(parseResult, pupilOpts) ?? NewtonianPupil;
            var main = SaturnRings.Main.Rings;
            var baseOptions = WithCamera(new DegradeOptions(pupil, wavelengthNm * 1e-9)
            {
                OuterScaleM = parseResult.GetValue(outerScaleOpt),
                ExposureSeconds = parseResult.GetValue(exposureOpt) / 1000,
                LocalOuterScaleM = parseResult.GetValue(localOuterScaleOpt),
                LocalWindMps = parseResult.GetValue(localWindOpt),
                MinnaertK = chosen.Filter.MinnaertK,
                Seed = parseResult.GetValue(seedOpt),
                Rings = planet == CatalogIndex.Saturn ? SaturnRings.Structured(main[0].Level, main[1].Level, main[2].Level, main[3].Level) : null,
            }, trialTruth.Camera, electronsPerAdu);
            var depth = trialTruth.Camera.FullScaleAdu <= 255 ? 8 : 16;
            var buffer = new byte[reader.Width * reader.Height * (depth == 8 ? 1 : 2)];

            // A twin of the capture's first frames made with the knobs, the planet at its own level through their blur (S3), and measured.
            async Task<CaptureStatistics?> MakeAndMeasure(TwinKnobs knobs, string path, CaptureStatistics truth, bool withTruth, CancellationToken token)
            {
                var times = allTimes[..truth.Frames];
                var options = knobs.ApplyTo(baseOptions);
                options = options with { DiskLevelAdu = options.DiskLevelAdu * PlanetaryDegrade.ShownLevelGain(map, planet, times, reference, scale, options) };
                var partial = path + ".partial";
                ImmutableArray<SyntheticFrame> made;
                using (var writer = new SerWriter(partial, reader.Width, reader.Height, SerColorId.Mono, depth, instrument: "TianWen planetary twin"))
                {
                    made = await PlanetaryDegrade.MakeAsync(map, planet, times, reference, scale, truth.MountX, truth.MountY, truth.Flux, reader.Width, reader.Height, options,
                        (index, samples) => writer.AppendFrame(Pack(samples, buffer, depth), times[index]), cancellationToken: token);
                }
                File.Move(partial, path, overwrite: true);
                if (withTruth)
                {
                    var referenceTime = times[truth.ReferenceIndex];
                    var truthImage = PlanetaryRender.RenderDiffracted(map, PhysicalEphemeris.Compute(planet, referenceTime), reference, reader.Width, reader.Height,
                        options.MinnaertK, pupil, options.WavelengthM, scale, rings: options.Rings);
                    WriteTruth(Path.ChangeExtension(path, ".truth.fits"), truthImage, reader.Width, reader.Height, reference, options, referenceTime, mapName);
                    WriteRecord(Path.ChangeExtension(path, ".frames.csv"), made);
                }
                using var twin = SerFrameStream.Open(path);
                return await PlanetaryCaptureStatistics.MeasureAsync(twin, measure, null, token);
            }

            consoleHost.WriteScrollable(string.Create(inv,
                $"searching the twin's air on the first {trialFrames} frames, at most {parseResult.GetValue(trialsOpt)} twins, from {Describe(start)}"));
            var trialPath = Path.Combine(output, "trial.ser");
            var searched = new Progress<TwinTrial>(trial => consoleHost.WriteScrollable(string.Create(inv,
                $"    {Describe(trial.Knobs)}: {DescribeMismatch(trial.Mismatch)}")));
            var started = DateTime.UtcNow;
            var (best, trials) = await PlanetaryTwinCalibration.FitAsync(trialTruth, start,
                (knobs, token) => MakeAndMeasure(knobs, trialPath, trialTruth, withTruth: false, token), parseResult.GetValue(trialsOpt), searched, ct);
            consoleHost.WriteScrollable(string.Create(inv,
                $"the best of {trials.Length} twins, in {(DateTime.UtcNow - started).TotalMinutes:0.0} min: {Describe(best.Knobs)}, {DescribeMismatch(best.Mismatch)}"));
            WriteRows($"the best twin on the first {trialFrames} frames (synthetic over real; * fitted):", best.Rows);

            // Confirmed at length: the best knobs' twin over the capture's first confirm-frames, against the real capture's same frames.
            using var confirmReal = new PlanetaryFrameWindow(whole, 0, confirmFrames);
            if (await MeasureReal(confirmReal) is not { } confirmTruth)
            {
                return 1;
            }
            var twinPath = Path.Combine(output, "twin.ser");
            consoleHost.WriteScrollable(string.Create(inv, $"confirming on the first {confirmFrames} frames: {Path.GetFileName(twinPath)}"));
            if (await MakeAndMeasure(best.Knobs, twinPath, confirmTruth, withTruth: true, ct) is not { } confirmed)
            {
                consoleHost.WriteError($"{twinPath}: no disk found");
                return 1;
            }
            WriteStatistics(Path.GetFileName(input), confirmTruth);
            WriteStatistics(Path.GetFileName(twinPath), confirmed);
            WriteComparison(confirmTruth, confirmed);
            var confirmedMismatch = PlanetaryTwinCalibration.MeanMismatch(TwinComparison.Compare(confirmTruth, confirmed));
            consoleHost.WriteScrollable($"confirmed: {DescribeMismatch(confirmedMismatch)}");
            WriteTwinRecord(Path.Combine(output, "twin.json"), input, mapName, best, confirmedMismatch, trialFrames, confirmFrames, trials);
            return 0;
        });
        return command;
    }

    private static string Describe(TwinKnobs k) => string.Create(CultureInfo.InvariantCulture,
        $"r0 {k.R0M * 100:0.00} cm, wind {k.WindMps:0.0} m/s, still layer r0 {k.LocalR0M * 100:0.00} cm, scatter {k.ScatterFraction * 100:0.00} % with a {k.ScatterCoreArcsec:0.0}\" core");

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
    private static void WriteTwinRecord(string path, string capture, string map, TwinTrial best, double confirmedMismatch, int searchFrames, int confirmFrames,
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
        json.WriteString("map", map);
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
