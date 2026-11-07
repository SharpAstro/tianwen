using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TianWen.Lib.Imaging.Grading;
using SkyTrendProbe;

const int MaxParallel = 4;

if (args.Contains("--self-check"))
{
    return SelfCheck.Run();
}

List<string> folders = [];
string? outDir = null;
string? referencesPath = null;
var parallel = 3;
var syntheticMoon = false;
double moonSep = 60, darkNl = 100;
(double, double)? site = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--references": referencesPath = args[++i]; break;
        case "--parallel": parallel = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--synthetic-moon": syntheticMoon = true; break;
        case "--moon-sep": moonSep = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--dark-nl": darkNl = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--site":
            var parts = args[++i].Split(',');
            site = (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
            break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unknown option {args[i]}");
                return 2;
            }
            folders.Add(args[i]);
            break;
    }
}
if (outDir is null || folders.Count == 0)
{
    Console.Error.WriteLine("usage: SkyTrendProbe <light folder>... --out <folder> [--parallel N (max 4)] [--references clear-sky.json] [--site lat,lon]");
    Console.Error.WriteLine("                     [--synthetic-moon [--moon-sep deg] [--dark-nl nL]] | --self-check");
    return 2;
}
parallel = Math.Clamp(parallel, 1, MaxParallel);
Directory.CreateDirectory(outDir);
#if SKY_TREND
Console.WriteLine("built WITH #2030: SkyOverTrend and SkyRising are read.");
#else
Console.WriteLine("built WITHOUT #2030: the SkyOverTrend and SkyRising columns are blank (rebuild with -p:SkyTrend=true over its two Lib files).");
#endif

var references = default(ImmutableArray<LightGradeReference>);
if (referencesPath is not null)
{
    await using var stream = File.OpenRead(referencesPath);
    var file = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, LightGradeReferenceJsonContext.Default.LightGradeReferenceFile);
    references = file is null ? default : [.. file.References];
    Console.WriteLine($"references: {references.Length} clear-night references from {referencesPath}");
}
else
{
    Console.WriteLine("references: none (the run's own baselines decide; SkyRising counts only with no reference night)");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

List<(string Folder, IReadOnlyList<Graded> Frames)> graded = [];
foreach (var folder in folders)
{
    var files = Grader.Enumerate(folder);
    Console.WriteLine($"{folder}: {files.Count} files");
    var results = new Graded?[files.Count];
    var done = 0;
    await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cts.Token }, async (idx, ct) =>
    {
        try
        {
            results[idx] = await Grader.GradeFileAsync(folder, files[idx], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"skip {files[idx]}: {ex.Message}");
        }
        var d = Interlocked.Increment(ref done);
        if (d % 50 == 0)
        {
            Console.WriteLine($"  {d}/{files.Count}");
        }
    });
    graded.Add((folder, [.. results.OfType<Graded>()]));
}

var all = Grader.WithAstro([.. graded.SelectMany(g => g.Frames)], site);
var byFolder = all.GroupBy(g => g.Folder).ToDictionary(g => g.Key, g => (IReadOnlyList<Graded>)[.. g]);

var rows = byFolder.Values.SelectMany(f => Grader.Evaluate(f, references)).ToList();
Report.Write(outDir, "", rows);

if (syntheticMoon)
{
    foreach (var scenario in Moonlight.Scenarios)
    {
        List<FrameRow> synthetic = [];
        foreach (var (folder, frames) in byFolder)
        {
            // The Moon rises a fraction of the way through each series, so a series is one run.
            var span = frames.Where(f => f.Meta.ExposureStartTime != default).GroupBy(f => f.Series)
                .ToDictionary(g => g.Key, g => (Start: g.Min(f => f.Meta.ExposureStartTime), End: g.Max(f => f.Meta.ExposureStartTime)));
            var lit = frames.Select(f =>
            {
                if (f.Meta.ExposureStartTime == default || !span.TryGetValue(f.Series, out var s))
                {
                    return f;
                }
                var alt = scenario.AltitudeAt(f.Meta.ExposureStartTime, s.Start, s.End);
                var factor = (float)Moonlight.SkyFactor(alt, scenario.PhaseAngleDeg, moonSep, f.Astro.ObjAltDeg, darkNl);
                return f with
                {
                    Viewer = f.Viewer is { } v ? v with { SkyLevel = v.SkyLevel * factor } : null,
                    Live = f.Live is { } l ? l with { SkyLevel = l.SkyLevel * factor } : null,
                    Astro = f.Astro with { MoonAltDeg = alt, MoonIllum = scenario.Illumination, MoonSepDeg = moonSep },
                };
            }).ToList();
            synthetic.AddRange(Grader.Evaluate(lit, references));
        }
        Report.Write(outDir, $"synthetic-{scenario.Name}-", synthetic);
        Console.WriteLine($"synthetic {scenario.Name}: {synthetic.Count} frames");
    }
}
return 0;
