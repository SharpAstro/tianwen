using System;
using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary training-pairs</c> (R9, #1056): synthetic (stack, sharpened, truth) triples for a learned refiner on the shipped
/// sharpening, each written as three FITS beside a CSV of what was drawn (<see cref="PlanetaryTrainingPairs"/>).
/// </summary>
internal sealed partial class PlanetaryTrainingPairsSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var mapsOpt = new Option<string[]>("--map") { Description = "OPAL global maps (FITS); each pair draws one. The filter's wavelength is read off its name (f631n) for its Minnaert k.", Required = true, AllowMultipleArgumentsPerToken = true };
        var psfOpt = new Option<string[]>("--psf") { Description = "Twins' PSF files (<capture>.psf, from planetary degrade --psf-truth); each pair draws one.", Required = true, AllowMultipleArgumentsPerToken = true };
        var countOpt = new Option<int>("--count") { Description = "How many pairs.", DefaultValueFactory = _ => 100 };
        var seedOpt = new Option<int>("--seed") { Description = "The draw's seed; pair i draws from seed and i alone, so a set can be made again or extended.", DefaultValueFactory = _ => 1 };
        var firstOpt = new Option<int>("--first") { Description = "The first pair's index (to extend a set).", DefaultValueFactory = _ => 0 };
        var radiusOpt = new Option<string>("--radius") { Description = "The disk's radius range, px.", DefaultValueFactory = _ => "35,110" };
        var keepOpt = new Option<string>("--keep") { Description = "The kept share of the twin's frames, log-uniform.", DefaultValueFactory = _ => "0.05,0.5" };
        var broadenOpt = new Option<double>("--broaden") { Description = "The most the stack's PSF is broadened by a Gaussian, sigma px (uniform from 0).", DefaultValueFactory = _ => 1.5 };
        var snrOpt = new Option<string>("--snr") { Description = "The finest band's signal to noise inside 0.9 radii, log-uniform.", DefaultValueFactory = _ => "3,40" };
        var parallelOpt = new Option<int>("--parallel") { Description = "Pairs made at once.", DefaultValueFactory = _ => 4 };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The folder to write into.", Required = true };
        var command = new Command("training-pairs", "Synthetic (stack, sharpened, truth) triples for R9's learned refiner (#1056): OPAL maps through twins' PSFs, sharpened as shipped.")
        {
            Options = { mapsOpt, psfOpt, countOpt, seedOpt, firstOpt, radiusOpt, keepOpt, broadenOpt, snrOpt, parallelOpt, outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            if (Range(parseResult.GetValue(radiusOpt)) is not { } radius || Range(parseResult.GetValue(keepOpt)) is not { } keep || Range(parseResult.GetValue(snrOpt)) is not { } snr)
            {
                consoleHost.WriteError("--radius, --keep and --snr take LEAST,MOST");
                return 1;
            }
            var maps = new (string Path, PlanetMap Map, double K)[(parseResult.GetValue(mapsOpt) ?? []).Length];
            for (var m = 0; m < maps.Length; m++)
            {
                var path = parseResult.GetValue(mapsOpt)![m];
                if (PlanetMap.ReadFits(path) is not { } map)
                {
                    consoleHost.WriteError($"{path}: not a global map");
                    return 1;
                }
                var nm = Regex.Match(Path.GetFileName(path), @"_f(\d{3})[nmw]_", RegexOptions.IgnoreCase) is { Success: true } match ? double.Parse(match.Groups[1].Value, inv) : 550;
                maps[m] = (path, map, PlanetaryLimbFit.OpalMinnaertK(CatalogIndex.Jupiter, nm) ?? 0.95);
            }
            var psfs = (parseResult.GetValue(psfOpt) ?? []).Select(PlanetaryTrainingPairs.Load).ToArray();
            if (psfs.Any(p => p is null) || maps.Length == 0 || psfs.Length == 0)
            {
                consoleHost.WriteError("every --psf must be a twin's PSF file, and at least one --map and one --psf are needed");
                return 1;
            }
            var output = parseResult.GetValue(outputOpt)!;
            Directory.CreateDirectory(output);
            var (count, seed, first, broaden) = (parseResult.GetValue(countOpt), parseResult.GetValue(seedOpt), parseResult.GetValue(firstOpt), parseResult.GetValue(broadenOpt));
            var rows = new ConcurrentBag<(int Index, string Row)>();
            var made = 0;
            await Parallel.ForAsync(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parseResult.GetValue(parallelOpt)), CancellationToken = ct }, (index, _) =>
            {
                var random = new Random(HashCode.Combine(seed, index));
                var (mapPath, map, k) = maps[random.Next(maps.Length)];
                var psf = psfs[random.Next(psfs.Length)]!;
                if (PlanetaryTrainingPairs.Make(map, k, psf, random, radius, keep, broaden, snr) is not { } pair)
                {
                    rows.Add((index, string.Create(inv, $"{index},{Path.GetFileName(mapPath)},{psf.Name},,,,,,,,sharpening not derived")));
                    return ValueTask.CompletedTask;
                }
                var stem = Path.Combine(output, index.ToString("D5", inv));
                pair.Stack.WriteToFitsFile(stem + "_stack.fits");
                pair.Sharpened.WriteToFitsFile(stem + "_sharp.fits");
                // The truth as planetary degrade writes one, so every verb that scores against a truth reads it.
                var headers = new System.Collections.Generic.Dictionary<string, (object Value, string Comment)>
                {
                    ["DATE-OBS"] = (pair.Draw.When.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv), "the instant rendered"),
                    ["DISKX"] = (pair.Placement.CenterX, "disk centre x, px (0-based)"),
                    ["DISKY"] = (pair.Placement.CenterY, "disk centre y, px (0-based)"),
                    ["DISKR"] = (pair.Placement.EquatorialRadius, "equatorial radius, px"),
                    ["NORTHANG"] = (pair.Placement.NorthAngleDeg, "direction to the north pole, deg from +x toward +y"),
                    ["WAVELEN"] = (pair.WavelengthNm, "the wavelength imaged, nm"),
                    ["SRCMAP"] = (Path.GetFileName(mapPath), "the global map rendered"),
                };
                pair.Truth.WriteToFitsFile(stem + "_truth.fits", null, headers);
                var d = pair.Draw;
                rows.Add((index, string.Create(inv,
                    $"{index},{Path.GetFileName(mapPath)},{psf.Name},{d.When:O},{d.RadiusPx:0.00},{d.NorthDeg:0.0},{d.KeepFraction:0.0000},{d.BroadenPx:0.000},{d.FinestBandSnr:0.00},{d.NoiseSigma:0.000000},{d.Size}")));
                var done = Interlocked.Increment(ref made);
                if (done % 25 == 0)
                {
                    consoleHost.WriteScrollable(string.Create(inv, $"{done} of {count} pairs"));
                }
                return ValueTask.CompletedTask;
            });
            var csv = Path.Combine(output, "pairs.csv");
            var header = !File.Exists(csv);
            await using (var writer = new StreamWriter(csv, append: true))
            {
                if (header)
                {
                    await writer.WriteLineAsync("index,map,psf,when,radius_px,north_deg,keep,broaden_px,finest_snr,noise_sigma,size");
                }
                foreach (var (_, row) in rows.OrderBy(r => r.Index))
                {
                    await writer.WriteLineAsync(row);
                }
            }
            consoleHost.WriteScrollable(string.Create(inv, $"{made} of {count} pairs written to {output}"));
            return 0;
        });
        return command;
    }

    /// <summary>
    /// <c>planetary fidelity</c> (R9, #1056): any image scored against a truth by R8's measure (<see cref="PlanetaryMasterScore.AgainstTruth"/>,
    /// as <c>planetary sharpen --truth</c> scores the master as stacked), with nothing sharpened: how a refiner's output is read.
    /// </summary>
    public Command BuildFidelity()
    {
        var inputsArg = new Argument<string[]>("images") { Description = "Images to score (FITS), each against the truth.", Arity = ArgumentArity.OneOrMore };
        var truthOpt = new Option<string>("--truth") { Description = "The truth (its DATE-OBS the instant, its disk fitted).", Required = true };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var command = new Command("fidelity", "Images scored against a truth by R8's measure: each band's transfer and error inside 0.9 radii, the limb's undershoot and profile (R9, #1056).")
        {
            Arguments = { inputsArg },
            Options = { truthOpt, planetOpt },
        };
        command.SetAction((parseResult, ct) =>
        {
            if (PlanetaryGeometrySubCommands.ParsePlanet(parseResult.GetValue(planetOpt), null) is not { } planet)
            {
                consoleHost.WriteError("--planet jupiter or saturn");
                return Task.FromResult(1);
            }
            var failed = 0;
            foreach (var path in parseResult.GetValue(inputsArg) ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (!TianWen.Lib.Imaging.Image.TryReadImageFile(path, out var image))
                {
                    consoleHost.WriteError($"{path}: no image to read");
                    failed++;
                    continue;
                }
                PlanetaryMasterScore.AgainstTruth(consoleHost, image, parseResult.GetValue(truthOpt)!, planet, Path.GetFileName(path));
            }
            return Task.FromResult(failed == 0 ? 0 : 1);
        });
        return command;
    }

    private static (double Least, double Most)? Range(string? text)
    {
        var parts = (text ?? "").Split(',');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var least)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var most) && most >= least && least > 0
            ? (least, most) : null;
    }
}
