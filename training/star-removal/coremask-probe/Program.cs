// Usage: CoreMaskProbe <outDir> <session-masters folder | master.fits>...
// The core mask of every master named (ExtendedCoreMask.Find at its defaults, as image remove-stars --protect-cores runs it),
// one JSON line each in <outDir>/survey.jsonl: the cores with the two measures the rules read (EdgeDistancePx, PeakRatio), the area the
// starless plate takes back, and the catalogued stars inside it. A core's weight plane is written to <outDir>/<stem>.weight.u8.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;

var outDir = args[0];
Directory.CreateDirectory(outDir);
var paths = args.Skip(1).SelectMany(a => Directory.Exists(a)
    ? Directory.EnumerateFiles(a, "*.fits").Where(f => !Regex.IsMatch(f, @"\.(badpixels|coverage|rejection)\.fits$")).Order(StringComparer.Ordinal)
    : [a]).ToArray();
using var jsonl = new StreamWriter(Path.Combine(outDir, "survey.jsonl"), false, new System.Text.UTF8Encoding(false));
foreach (var path in paths)
{
    var stem = Path.GetFileNameWithoutExtension(path);
    if (!Image.TryReadFitsFile(path, out var raw)) { Console.Error.WriteLine($"unreadable: {path}"); continue; }
    var image = MasterAlignment.MaskAbsent(raw);
    var mask = ExtendedCoreMask.Find(image);
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
    if (mask.Cores.Length > 0)
    {
        var bytes = new byte[mask.Weight.Length];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)Math.Round(255 * mask.Weight[i]); }
        File.WriteAllBytes(Path.Combine(outDir, stem + ".weight.u8"), bytes);
    }
    jsonl.WriteLine(JsonSerializer.Serialize(new
    {
        stem, mask.Width, mask.Height, sigma = mask.SkySigma, hasCatalogue = File.Exists(csv),
        edgeReachPx = ExtendedCoreMask.Options.Default.EdgeReachPx, maxPeakRatio = ExtendedCoreMask.Options.Default.MaxPeakRatio,
        cores = mask.Cores.Select(c => new { c.X, c.Y, peak = Math.Round(c.PeakSigma, 1), c.SourceAreaPx, c.CoreAreaPx, c.EdgeDistancePx, peakRatio = Math.Round(c.PeakRatio, 2) }).ToArray(),
        fullPx = full, anyPx = any, inside, all,
    }));
    jsonl.Flush();
    image.Release();
    raw.Release();
}
