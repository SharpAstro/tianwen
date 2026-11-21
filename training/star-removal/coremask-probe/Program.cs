// Usage: CoreMaskProbe [--max-peak-ratio <ratio>] [--max-core-over-flat-top <times>] [--no-weights] [--core-stars] <outDir> <session-masters folder | master.fits>...
// The core mask of every master named (ExtendedCoreMask.Find at its defaults, as image remove-stars runs it by default),
// one JSON line each in <outDir>/survey.jsonl: the cores with the measures the rules read (EdgeDistancePx, PeakRatio, the
// flat top's area, roundness and fall), the area the starless plate takes back, and the catalogued stars inside it. A
// core's weight plane is written to <outDir>/<stem>.weight.u8.
// --max-peak-ratio moves the star rule's bar: at a very high one the sources the rule turns away are listed too, each with
// its ratio. --max-core-over-flat-top moves the clipped-star rule's (0 turns it off). With both rules off every source is
// listed with every measure, which is how the stars they read are surveyed (#2360). --no-weights writes no weight planes.
// --core-stars also runs the catalogue step on each plate-solved master with a core, as image protect-cores does (the
// installed ASTAP database), and writes <outDir>/<stem>.corestars.json: every compact peak the step judged, whether it
// took it for a star, and the catalogued stars on the cores (#2373; core_stars_where.py prints and draws it).
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;

var options = ExtendedCoreMask.Options.Default;
var writeWeights = true;
var coreStars = false;
var at = 0;
for (; at < args.Length && args[at].StartsWith("--", StringComparison.Ordinal); at++)
{
    if (args[at] == "--max-peak-ratio" && at + 1 < args.Length)
    {
        options = options with { MaxPeakRatio = double.Parse(args[++at], CultureInfo.InvariantCulture) };
    }
    else if (args[at] == "--max-core-over-flat-top" && at + 1 < args.Length)
    {
        options = options with { MaxCoreOverFlatTop = double.Parse(args[++at], CultureInfo.InvariantCulture) };
    }
    else if (args[at] == "--no-weights")
    {
        writeWeights = false;
    }
    else if (args[at] == "--core-stars")
    {
        coreStars = true;
    }
    else
    {
        Console.Error.WriteLine($"unknown switch: {args[at]}");
        return 2;
    }
}
if (args.Length - at < 2)
{
    Console.Error.WriteLine("usage: CoreMaskProbe [--max-peak-ratio <ratio>] [--max-core-over-flat-top <times>] [--no-weights] [--core-stars] <outDir> <session-masters folder | master.fits>...");
    return 2;
}
var outDir = args[at];
Directory.CreateDirectory(outDir);
var paths = args.Skip(at + 1).SelectMany(a => Directory.Exists(a)
    ? Directory.EnumerateFiles(a, "*.fits").Where(f => !Regex.IsMatch(f, @"\.(badpixels|coverage|rejection)\.fits$")).Order(StringComparer.Ordinal).ToArray()
    : new[] { a }).ToArray();
using var jsonl = new StreamWriter(Path.Combine(outDir, "survey.jsonl"), false, new System.Text.UTF8Encoding(false));
foreach (var path in paths)
{
    var stem = Path.GetFileNameWithoutExtension(path);
    if (!Image.TryReadFitsFile(path, out var raw, out var wcs)) { Console.Error.WriteLine($"unreadable: {path}"); continue; }
    var image = MasterAlignment.MaskAbsent(raw);
    var mask = ExtendedCoreMask.Find(image, options);
    if (coreStars && mask.Cores.Length > 0)
    {
        // The catalogue step as image protect-cores runs it (the installed ASTAP database), with every compact peak it
        // judged and the catalogued stars on the cores, for #2373.
        if (wcs is not { } solution || !CatalogFrameStars.IsSolved(solution))
        {
            Console.Error.WriteLine($"no plate solution: {stem}");
        }
        else
        {
            var (catalogued, why) = await CatalogFrameStars.ReadAsync(solution, image.Width, image.Height, TianWen.Lib.Astrometry.Catalogs.AstapStarDatabase.FindInstalled(), null);
            if (catalogued is null)
            {
                Console.Error.WriteLine($"no catalogued stars ({why}): {stem}");
            }
            else
            {
                var peaks = new List<CoreStars.CorePeak>();
                var (footprint, report) = CoreStars.Find(image, mask, catalogued.Stars, null, peaks);
                var near = catalogued.Stars.Where(s =>
                {
                    var (sx, sy) = ((int)MathF.Round(s.X), (int)MathF.Round(s.Y));
                    return sx >= 0 && sy >= 0 && sx < mask.Width && sy < mask.Height && mask.Weight[(sy * mask.Width) + sx] > 0f;
                }).ToArray();
                File.WriteAllText(Path.Combine(outDir, stem + ".corestars.json"), JsonSerializer.Serialize(new
                {
                    stem, source = catalogued.Source, sigma = mask.SkySigma,
                    report = new { report.Peaks, report.Stars, report.CalibrationStars, report.ZeroPoint, report.ZeroPointScatter, report.ReachPerMagnitude, report.ReachAtMagnitude10 },
                    footprintPx = footprint?.Count(f => f >= 0.5f) ?? 0,
                    cores = mask.Cores.Select(c => new
                    {
                        c.X, c.Y, c.CoreAreaPx, c.FlatTopPx, c.HasStarTop, flatTopX = Math.Round(c.FlatTopX, 2), flatTopY = Math.Round(c.FlatTopY, 2),
                    }).ToArray(),
                    peaks = peaks.Select(p => new { p.X, p.Y, heightSigma = Math.Round(p.Height / mask.SkySigma, 1), p.IsStar, p.Radius }).ToArray(),
                    starsOnCores = near.Select(s => new { x = Math.Round(s.X, 2), y = Math.Round(s.Y, 2), magnitude = Math.Round(s.Magnitude, 2) }).ToArray(),
                }));
                Console.Error.WriteLine($"{stem}: {report.Stars} of {report.Peaks} peaks are catalogued stars; {near.Length} catalogued stars lie on the cores");
            }
        }
    }
    int full = 0, any = 0;
    foreach (var w in mask.Weight) { if (w >= 0.5f) full++; if (w > 0f) any++; }
    var inside = new SortedDictionary<string, int>();
    var all = new SortedDictionary<string, int>();
    var csv = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "starless", "plates", stem + "_plate.stars.csv");
    if (File.Exists(csv) && mask.Cores.Length > 0)
    {
        var lines = File.ReadAllLines(csv);
        var head = lines[0].Split(',');
        int ix = Array.IndexOf(head, "x"), iy = Array.IndexOf(head, "y"), isg = Array.IndexOf(head, "significance"), isat = Array.IndexOf(head, "saturated");
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            var x = (int)Math.Round(double.Parse(f[ix], CultureInfo.InvariantCulture));
            var y = (int)Math.Round(double.Parse(f[iy], CultureInfo.InvariantCulture));
            var sg = double.Parse(f[isg], CultureInfo.InvariantCulture);
            var band = f[isat] == "1" ? "saturated" : sg >= 1000 ? "1000+" : sg >= 100 ? "100-1000" : sg >= 20 ? "020-100" : "000-20";
            all[band] = all.GetValueOrDefault(band) + 1;
            if (x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && mask.Weight[(y * mask.Width) + x] >= 0.5f)
            {
                inside[band] = inside.GetValueOrDefault(band) + 1;
            }
        }
    }
    if (writeWeights && mask.Cores.Length > 0)
    {
        var bytes = new byte[mask.Weight.Length];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)Math.Round(255 * mask.Weight[i]); }
        File.WriteAllBytes(Path.Combine(outDir, stem + ".weight.u8"), bytes);
    }
    jsonl.WriteLine(JsonSerializer.Serialize(new
    {
        stem, mask.Width, mask.Height, sigma = mask.SkySigma, hasCatalogue = File.Exists(csv),
        edgeReachPx = options.EdgeReachPx, maxPeakRatio = options.MaxPeakRatio, maxCoreOverFlatTop = options.MaxCoreOverFlatTop,
        cores = mask.Cores.Select(c => new
        {
            c.X, c.Y, peak = Math.Round(c.PeakSigma, 1), c.SourceAreaPx, c.CoreAreaPx, c.EdgeDistancePx, peakRatio = Math.Round(c.PeakRatio, 2),
            c.FlatTopPx, flatTopRoundness = Math.Round(c.FlatTopRoundness, 3), flatTopFall = Math.Round(c.FlatTopFall, 4),
            flatTopX = Math.Round(c.FlatTopX, 2), flatTopY = Math.Round(c.FlatTopY, 2), c.HasStarTop,
        }).ToArray(),
        fullPx = full, anyPx = any, inside, all,
    }));
    jsonl.Flush();
    image.Release();
    raw.Release();
}
return 0;
