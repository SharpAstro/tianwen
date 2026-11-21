// Usage: CoreMaskProbe [--max-peak-ratio <ratio>] [--max-core-over-flat-top <times>] [--no-weights] <outDir> <session-masters folder | master.fits>...
// The core mask of every master named (ExtendedCoreMask.Find at its defaults, as image remove-stars runs it by default),
// one JSON line each in <outDir>/survey.jsonl: the cores with the measures the rules read (EdgeDistancePx, PeakRatio, the
// flat top's area, roundness and fall), the area the starless plate takes back, and the catalogued stars inside it. A
// core's weight plane is written to <outDir>/<stem>.weight.u8.
// --max-peak-ratio moves the star rule's bar: at a very high one the sources the rule turns away are listed too, each with
// its ratio. --max-core-over-flat-top moves the clipped-star rule's (0 turns it off). With both rules off every source is
// listed with every measure, which is how the stars they read are surveyed (#2360). --no-weights writes no weight planes.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;

var options = ExtendedCoreMask.Options.Default;
var writeWeights = true;
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
    else
    {
        Console.Error.WriteLine($"unknown switch: {args[at]}");
        return 2;
    }
}
if (args.Length - at < 2)
{
    Console.Error.WriteLine("usage: CoreMaskProbe [--max-peak-ratio <ratio>] [--max-core-over-flat-top <times>] [--no-weights] <outDir> <session-masters folder | master.fits>...");
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
    if (!Image.TryReadFitsFile(path, out var raw)) { Console.Error.WriteLine($"unreadable: {path}"); continue; }
    var image = MasterAlignment.MaskAbsent(raw);
    var mask = ExtendedCoreMask.Find(image, options);
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
        }).ToArray(),
        fullPx = full, anyPx = any, inside, all,
    }));
    jsonl.Flush();
    image.Release();
    raw.Release();
}
return 0;
