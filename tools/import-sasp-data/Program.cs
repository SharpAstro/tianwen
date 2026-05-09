using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using nom.tam.fits;

// Downloads SASP_data.fits from the setiastro GitHub repo and converts its
// SENSOR (QE), FILTER (transmission), and SED (Pickles stellar spectra) HDUs
// into .gs.gz ASCII-separated files for TianWen consumption.
//
// Record format (one per HDU, fields separated by 0x1E RS):
//   Name | OriginFilename | NumPoints | Wavelengths | Values
//
// Wavelengths and Values are sub-arrays joined by 0x1F (US). All doubles use
// G17 for exact round-trip. Records are separated by 0x1D (GS).
//
// Usage:
//   dotnet run --project tools/import-sasp-data -- [--sasp-fits <path>] [--output-dir <path>]

const string DefaultSaspUrl =
    "https://raw.githubusercontent.com/setiastro/setiastrosuitepro/main/src/setiastro/data/SASP_data.fits";

const byte GS = 0x1D;
const byte RS = 0x1E;
const byte US = 0x1F;

var saspFits = (string?)null;
var outputDir = (string?)null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sasp-fits": saspFits = args[++i]; break;
        case "--output-dir": outputDir = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Resolve paths relative to repo root (two levels up from tools/import-sasp-data).
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
outputDir ??= Path.Combine(repoRoot, "src", "TianWen.Lib", "Astrometry", "Catalogs");
Directory.CreateDirectory(outputDir);

// Download SASP_data.fits if not provided locally.
if (saspFits is null)
{
    saspFits = Path.Combine(outputDir, "SASP_data.fits");
    if (!File.Exists(saspFits))
    {
        Console.WriteLine($"Downloading {DefaultSaspUrl} ...");
        using var client = new HttpClient();
        using var response = await client.GetAsync(DefaultSaspUrl, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(saspFits);
        await response.Content.CopyToAsync(fs, CancellationToken.None);
        Console.WriteLine($"  -> {saspFits} ({new FileInfo(saspFits).Length:N0} bytes)");
    }
    else
    {
        Console.WriteLine($"Using cached {saspFits}");
    }
}

// ---------------------------------------------------------------------------
// Read SASP_data.fits, bin HDUs by CTYPE.
// ---------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
var sedEntries = new List<(string Name, string Origin, float[] Wavelengths, float[] Values)>();
var sensorEntries = new List<(string Name, string Origin, float[] Wavelengths, float[] Values)>();
var filterEntries = new List<(string Name, string Origin, float[] Wavelengths, float[] Values)>();

var fits = new Fits(saspFits);
var hduCount = 0;
while (true)
{
    var hdu = fits.ReadHDU();
    if (hdu is null) break;
    hduCount++;

    if (hdu is not BinaryTableHDU table) continue;

    var header = table.Header;
    var ctype = (header.GetStringValue("CTYPE") ?? "").Trim().ToUpperInvariant();
    var extname = (header.GetStringValue("EXTNAME") ?? "").Trim();
    var origin = (header.GetStringValue("ORIGIN") ?? "").Trim();
    if (extname.Length == 0) extname = $"HDU{hduCount}";
    if (origin.Length == 0) origin = "unknown";

    // Find WAVELENGTH and value (FLUX or THROUGHPUT) columns by name.
    var nCols = table.NCols;
    var wlCol = -1;
    var valCol = -1;
    for (var c = 0; c < nCols; c++)
    {
        var colName = (header.GetStringValue($"TTYPE{c + 1}") ?? "").Trim().ToUpperInvariant();
        if (colName == "WAVELENGTH") wlCol = c;
        else if (colName is "FLUX" or "THROUGHPUT") valCol = c;
    }

    if (wlCol < 0 || valCol < 0)
    {
        Console.Error.WriteLine($"Warning: skipping HDU {hduCount} '{extname}' (CTYPE={ctype}) — missing WAVELENGTH or FLUX/THROUGHPUT column.");
        continue;
    }

    var wlData = table.GetColumn(wlCol);
    var valData = table.GetColumn(valCol);

    // GetColumn returns float[] for scalar float32 columns, object[] or float[][] for vector columns.
    // SASP_data columns are scalar float32 throughout.
    var wavelengths = wlData as float[] ?? throw new InvalidOperationException(
        $"HDU {hduCount} '{extname}': expected float[] for WAVELENGTH, got {wlData.GetType()}");
    var values = valData as float[] ?? throw new InvalidOperationException(
        $"HDU {hduCount} '{extname}': expected float[] for FLUX/THROUGHPUT, got {valData.GetType()}");

    var entry = (extname, origin, wavelengths, values);

    switch (ctype)
    {
        case "SED":    sedEntries.Add(entry);    break;
        case "SENSOR": sensorEntries.Add(entry); break;
        case "FILTER": filterEntries.Add(entry); break;
        default:
            Console.Error.WriteLine($"Warning: skipping HDU {hduCount} '{extname}' with unknown CTYPE={ctype}");
            break;
    }
}

Console.WriteLine($"Read {hduCount} HDUs in {sw.Elapsed.TotalSeconds:F1}s: " +
    $"{sedEntries.Count} SEDs, {sensorEntries.Count} sensors, {filterEntries.Count} filters.");

// ---------------------------------------------------------------------------
// Write .gs.gz files.
// ---------------------------------------------------------------------------
WriteGsGz(Path.Combine(outputDir, "pickles_sed.gs.gz"), sedEntries);
WriteGsGz(Path.Combine(outputDir, "sensor_qe.gs.gz"), sensorEntries);
WriteGsGz(Path.Combine(outputDir, "filter_curves.gs.gz"), filterEntries);

Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s.");
return 0;

// ===========================================================================
static void WriteGsGz(string outputPath, List<(string Name, string Origin, float[] Wavelengths, float[] Values)> entries)
{
    if (entries.Count == 0)
    {
        Console.WriteLine($"  {Path.GetFileName(outputPath)}: no entries, skipping.");
        return;
    }

    var invariant = CultureInfo.InvariantCulture;
    var sb = new StringBuilder(entries.Count * 4096); // rough estimate

    for (var i = 0; i < entries.Count; i++)
    {
        var (name, origin, wavelengths, values) = entries[i];

        AssertNoControlBytes(name, $"{name} name");
        AssertNoControlBytes(origin, $"{name} origin");

        if (i > 0) sb.Append((char)GS);

        // Name | Origin | NumPoints | Wavelengths | Values
        sb.Append(name);
        sb.Append((char)RS);
        sb.Append(origin);
        sb.Append((char)RS);
        sb.Append(wavelengths.Length.ToString(invariant));
        sb.Append((char)RS);
        AppendDoubleArray(sb, wavelengths, invariant);
        sb.Append((char)RS);
        AppendDoubleArray(sb, values, invariant);
    }

    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    var tmp = outputPath + ".tmp";
    try
    {
        using var fs = File.Create(tmp);
        using var gzip = new GZipStream(fs, CompressionLevel.Optimal);
        gzip.Write(bytes, 0, bytes.Length);
        gzip.Flush();
        fs.Flush(flushToDisk: true);
    }
    catch
    {
        try { File.Delete(tmp); } catch { /* best-effort */ }
        throw;
    }
    File.Move(tmp, outputPath, overwrite: true);

    Console.WriteLine($"  {Path.GetFileName(outputPath)}: {entries.Count} entries, " +
        $"{bytes.Length:N0} raw bytes -> {new FileInfo(outputPath).Length:N0} compressed.");
}

static void AppendDoubleArray(StringBuilder sb, ReadOnlySpan<float> values, CultureInfo invariant)
{
    for (var i = 0; i < values.Length; i++)
    {
        if (i > 0) sb.Append((char)US);
        sb.Append(((double)values[i]).ToString("G17", invariant));
    }
}

static void AssertNoControlBytes(string s, string context)
{
    if (s.Contains((char)GS) || s.Contains((char)RS) || s.Contains((char)US))
        throw new InvalidOperationException($"{context} contains an ASCII separator byte.");
}
