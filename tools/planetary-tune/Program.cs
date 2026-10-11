using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PlanetaryTune;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;

// The AUTO mode's per-capture tune (docs/plans/planetary-stacking.md, A2 and A3 of #817): every candidate setting (PlanetaryAutoTune: the
// keep, the strength and the target) stacked and sharpened on a capture's calibrated twin (tianwen planetary twin) and scored against the
// twin's truth; the whole grid printed, so a second twin's run answers how far the first one's choice carries. Given --apply, the real
// capture is stacked with the tuned settings and with today's defaults, both through the one best-stack routine (PlanetaryBestStack), for
// tianwen planetary judge to set against the capture's post. It was the verb planetary tune on an unmerged branch; nothing it finds is
// adopted (A2, A3), so it is a tool, not a verb.

var inv = CultureInfo.InvariantCulture;
string? twinPath = null, truthPath = null, planetName = null, applyPath = null, output = null;
double? apertureMm = null, obstruction = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--truth": truthPath = args[++i]; break;
        case "--planet": planetName = args[++i]; break;
        case "--apply": applyPath = args[++i]; break;
        case "--output" or "-o": output = args[++i]; break;
        case "--aperture-mm": apertureMm = double.Parse(args[++i], inv); break;
        case "--obstruction": obstruction = double.Parse(args[++i], inv); break;
        case "--help" or "-h":
            Usage();
            return 0;
        default:
            if (twinPath is null && !args[i].StartsWith('-'))
            {
                twinPath = args[i];
                break;
            }
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            Usage();
            return 1;
    }
}
if (twinPath is null || truthPath is null || output is null)
{
    Usage();
    return 1;
}
Directory.CreateDirectory(output);
CatalogIndex? planet = planetName?.ToLowerInvariant() switch
{
    null => PlanetaryCaptureName.Planet(applyPath ?? twinPath),
    "mars" => CatalogIndex.Mars,
    "jupiter" => CatalogIndex.Jupiter,
    "saturn" => CatalogIndex.Saturn,
    _ => null,
};
if (planet is not { } body)
{
    Console.Error.WriteLine("--planet: mars, jupiter or saturn (the capture's name does not say)");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

using var twin = SerFrameStream.Open(twinPath);
var colour = twin.Layout == PlanetaryFrameLayout.SplitCfa;
string[] truthPaths = colour ? [.. new[] { "r", "g", "b" }.Select(c => Path.ChangeExtension(truthPath, $".{c}.fits"))] : [truthPath];
var truths = ImmutableArray.CreateBuilder<PlanetaryTruth>();
foreach (var path in truthPaths)
{
    if (PlanetaryTruth.Read(path) is not { } truth)
    {
        Console.Error.WriteLine($"{path}: not a twin's truth (a FITS with its disk and wavelength cards)");
        return 1;
    }
    truths.Add(truth);
}

// The sharpening is derived at each plane's own wavelength, the one its truth was rendered at, through the twin's telescope: the 254 mm
// Newtonian the twins were made through unless an aperture is given.
var pupil = apertureMm is { } mm
    ? new Pupil(mm / 1000, ObstructionRatio: obstruction ?? 0)
    : new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001);
var options = new PlanetaryBestStackOptions(body, pupil)
{
    Stack = new PlanetaryStackOptions { GradeCache = Path.Combine(output, "grades") },
    WavelengthsNm = [.. truths.Select(t => t.WavelengthNm)],
};
Console.WriteLine(string.Create(inv,
    $"tuning on {Path.GetFileName(twinPath)} ({twin.FrameCount} frames) against {string.Join(", ", truthPaths.Select(Path.GetFileName))}: keeps {string.Join(", ", PlanetaryAutoTune.Keeps)}, strengths {string.Join(", ", PlanetaryAutoTune.Strengths)}, targets {string.Join(", ", PlanetaryAutoTune.Targets)}"));
var started = DateTime.UtcNow;
var progress = new Progress<PlanetaryTuneTrial>(trial => Console.WriteLine($"    {Describe(trial)}"));
var (best, trials) = await PlanetaryAutoTune.TuneAsync(twin, truths.ToImmutable(), options, progress, ct);
var defaults = trials.FirstOrDefault(t => t.Settings == PlanetaryAutoSettings.Defaults);
Console.WriteLine(string.Create(inv, $"{trials.Length} candidates in {(DateTime.UtcNow - started).TotalMinutes:0.0} min"));
Console.WriteLine($"the defaults: {(defaults is null ? "not in the grid" : Describe(defaults))}");
Console.WriteLine($"the tuned:    {Describe(best)}");
WriteGrid(trials);

if (applyPath is null)
{
    return 0;
}
foreach (var (name, settings) in new[] { ("auto", best.Settings), ("default", PlanetaryAutoSettings.Defaults) })
{
    Console.WriteLine($"stacking {Path.GetFileName(applyPath)} with the {name} settings: {DescribeSettings(settings)}");
    using var capture = SerFrameStream.Open(applyPath);
    // The real capture keeps its de-rotation and its coverage crop: only the tuned settings differ from the default stack's.
    var result = await PlanetaryBestStack.RunAsync(capture, settings.ApplyTo(options with
    {
        Stack = new PlanetaryStackOptions { GradeCache = Path.Combine(output, "grades") },
        WavelengthsNm = [],
    }), null, ct);
    try
    {
        var (masterFits, sharpenedFits) = PlanetaryBestStack.OutputPaths(output, Path.GetFileNameWithoutExtension(applyPath), $"{name}_");
        result.Stack.Master.WriteToFitsFile(masterFits, null, result.Balance?.HeaderCards());
        result.Sharpened.WriteToFitsFile(sharpenedFits, null, result.Balance?.HeaderCards());
        Console.WriteLine($"    wrote {Path.GetFileName(masterFits)} and {Path.GetFileName(sharpenedFits)}: {result.HowSharpened}");
    }
    finally
    {
        result.Stack.Master.Release();
        result.Sharpened.Release();
    }
}
return 0;

static void Usage() => Console.Error.WriteLine(
    "PlanetaryTune <twin.ser> --truth <twin.truth.fits> --output <dir> [--planet jupiter|saturn|mars] [--aperture-mm N --obstruction R] [--apply <capture.ser>]");

static string DescribeSettings(PlanetaryAutoSettings s) => string.Create(CultureInfo.InvariantCulture,
    $"keep {s.Keep:0.##}, strength {s.Strength:0.##}, target {s.Target}");

static string Describe(PlanetaryTuneTrial t) => string.Create(CultureInfo.InvariantCulture,
    $"{DescribeSettings(t.Settings)}: band error {(t.BandError is { } e ? e.ToString("0.0000", CultureInfo.InvariantCulture) : "not scored")}");

// The grid as a table: a row a keep and target, a column a strength, each cell the band error.
static void WriteGrid(ImmutableArray<PlanetaryTuneTrial> trials)
{
    var inv = CultureInfo.InvariantCulture;
    Console.WriteLine($"{"keep",-6}{"target",-10}" + string.Concat(PlanetaryAutoTune.Strengths.Select(s => string.Create(inv, $"{"strength " + s.ToString("0.##", inv),14}"))));
    foreach (var keep in PlanetaryAutoTune.Keeps)
    {
        foreach (var target in PlanetaryAutoTune.Targets)
        {
            IEnumerable<double?> cells = PlanetaryAutoTune.Strengths.Select(s => trials.FirstOrDefault(t => t.Settings == new PlanetaryAutoSettings(keep, s, target))?.BandError);
            Console.WriteLine(string.Create(inv, $"{keep,-6:0.##}{target,-10}") + string.Concat(cells.Select(e => e is { } v ? v.ToString("0.0000", inv).PadLeft(14) : "-".PadLeft(14))));
        }
    }
}
