using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
//                                                 [--extra-filters <dir>] [--merge-only]
//
// LOCAL FILTER CURVES (--extra-filters, default tools/import-sasp-data/local-filters):
// Curves that upstream does not carry -- a filter digitised from a vendor chart, say -- live as
// committed CSVs and are merged in on EVERY run. That is the whole point: this tool rewrites
// filter_curves.gs.gz wholesale from upstream, so a curve appended to the .gs.gz by hand would be
// destroyed by the next import, silently and with no sign in the diff beyond a shrinking file.
//
// A local CSV is `wavelength_nm,transmission_pct` with `#` comments, i.e. CHART units, so a row can
// be checked against the plot it came from by eye. The conversion to the database convention
// (Angstrom, fraction 0-1) happens here, once. Getting that wrong is silent and catastrophic:
// percent where a fraction is expected makes a filter 100x over-transmissive and every calibration
// derived from it confidently wrong.
//
// --merge-only rebuilds filter_curves.gs.gz from the EXISTING file plus the local CSVs, with no
// upstream fetch. That is what a local-curve change needs, and it keeps the 30 MB upstream FITS off
// the critical path for it.

const string DefaultSaspUrl =
    "https://raw.githubusercontent.com/setiastro/setiastrosuitepro/main/src/setiastro/data/SASP_data.fits";

const byte GS = 0x1D;
const byte RS = 0x1E;
const byte US = 0x1F;

var saspFits = (string?)null;
var outputDir = (string?)null;
var extraFiltersDir = (string?)null;
var mergeOnly = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sasp-fits": saspFits = args[++i]; break;
        case "--output-dir": outputDir = args[++i]; break;
        case "--extra-filters": extraFiltersDir = args[++i]; break;
        case "--merge-only": mergeOnly = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Resolve the repo root by WALKING UP until the marker directory appears, rather than counting
// "..": the count depends on the build layout (bin/<cfg>/<tfm>) and was one short, so every
// defaulted path landed under tools/ instead of the repo root. A search cannot drift that way.
var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
    ?? throw new DirectoryNotFoundException(
        $"could not locate the repo root above {AppContext.BaseDirectory} " +
        "(looking for src/TianWen.slnx); pass --output-dir");
outputDir ??= Path.Combine(repoRoot, "src", "TianWen.Lib", "Astrometry", "Catalogs");
Directory.CreateDirectory(outputDir);
extraFiltersDir ??= Path.Combine(repoRoot, "tools", "import-sasp-data", "local-filters");

// --merge-only: rebuild filter_curves.gs.gz from the existing file plus the local CSVs and stop.
// Deliberately does NOT touch pickles_sed / sensor_qe: they have no local-addition path, so
// rewriting them from nothing would empty them.
if (mergeOnly)
{
    var existing = Path.Combine(outputDir, "filter_curves.gs.gz");
    if (!File.Exists(existing))
    {
        Console.Error.WriteLine($"--merge-only needs an existing {existing}");
        return 2;
    }

    var merged = ReadGsGz(existing);
    Console.WriteLine($"Read {merged.Count} existing filter curves from {Path.GetFileName(existing)}.");

    // Retract curves whose CSV has gone. The .gs.gz is this step's own INPUT, so without this a
    // merge can only ever add: deleting a local CSV left its curve in the blob forever, and backing
    // one out meant restoring the file from git and re-merging by hand.
    //
    // Which needs a manifest, because ORIGIN cannot tell us who put a curve there. The obvious
    // discriminator -- an origin ending in .csv -- is wrong: SETI Astro built the upstream FITS from
    // CSVs too, so IDAS_LPS_P3_LIGHT_POLLUTION carries "IDAS-LPS-P3-Light-Pollution.csv" and
    // pruning on that would delete upstream data. The manifest records exactly what WE injected, so
    // nothing else can be touched.
    var manifest = Path.Combine(extraFiltersDir, ".merged-names.txt");
    var previouslyInjected = File.Exists(manifest)
        ? new HashSet<string>(File.ReadAllLines(manifest)
            .Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'), StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    var nowPresent = new HashSet<string>(
        Directory.Exists(extraFiltersDir)
            ? Directory.GetFiles(extraFiltersDir, "*.csv").Select(LocalCurveName)
            : [],
        StringComparer.Ordinal);

    var retracted = 0;
    foreach (var gone in previouslyInjected.Where(n => !nowPresent.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
    {
        var idx = merged.FindIndex(e => e.Name == gone);
        if (idx >= 0)
        {
            merged.RemoveAt(idx);
            retracted++;
            Console.WriteLine($"  retracted: {gone} (its CSV is gone)");
        }
    }

    var added = MergeLocalFilters(merged, extraFiltersDir);

    // The manifest is written only after the merge succeeded, so a failed run cannot leave a
    // manifest claiming curves the blob does not hold.
    File.WriteAllLines(manifest, (string[])[
        "# Names injected into filter_curves.gs.gz from the CSVs beside this file, written by",
        "# `dotnet run --project tools/import-sasp-data -- --merge-only`. It exists so a DELETED",
        "# CSV can be retracted from the blob: the blob is the merge's own input, and ORIGIN cannot",
        "# say who put a curve there (upstream SASP curves also carry .csv origins). Checked in.",
        .. nowPresent.OrderBy(n => n, StringComparer.Ordinal)]);

    WriteGsGz(existing, merged);
    Console.WriteLine(retracted > 0
        ? $"Merged {added} local curve(s), retracted {retracted}; wrote {merged.Count} total."
        : $"Merged {added} local curve(s); wrote {merged.Count} total.");
    return 0;
}

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
        Console.Error.WriteLine($"Warning: skipping HDU {hduCount} '{extname}' (CTYPE={ctype}), missing WAVELENGTH or FLUX/THROUGHPUT column.");
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
// Local curves go in on the full path as well, which is what keeps them from being lost the next
// time someone re-imports from upstream.
var localAdded = MergeLocalFilters(filterEntries, extraFiltersDir);
if (localAdded > 0)
{
    Console.WriteLine($"Merged {localAdded} local filter curve(s) from {extraFiltersDir}.");
}

WriteGsGz(Path.Combine(outputDir, "pickles_sed.gs.gz"), sedEntries);
WriteGsGz(Path.Combine(outputDir, "sensor_qe.gs.gz"), sensorEntries);
WriteGsGz(Path.Combine(outputDir, "filter_curves.gs.gz"), filterEntries);

Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s.");
return 0;

// ===========================================================================
static string? FindRepoRoot(string start)
{
    for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
    {
        // The marker is the SOLUTION FILE, not the src/TianWen.Lib directory. A directory marker was
        // tried and defeated immediately, by a decoy this tool's own bug had created: the previous
        // "..".."..".."..".."" path resolved the root to tools/, and Directory.CreateDirectory then
        // made tools/src/TianWen.Lib/Astrometry/Catalogs -- empty, untracked, and therefore invisible
        // to git status. The search then found THAT first, being nearer, and resolved the same wrong
        // root the counting did. A file that only the real root has cannot be manufactured as a
        // side effect of creating an output directory.
        if (File.Exists(Path.Combine(d.FullName, "src", "TianWen.slnx")))
        {
            return d.FullName;
        }
    }
    return null;
}

static List<(string Name, string Origin, float[] Wavelengths, float[] Values)> ReadGsGz(string path)
{
    using var fs = File.OpenRead(path);
    using var gz = new GZipStream(fs, CompressionMode.Decompress);
    using var reader = new StreamReader(gz, Encoding.UTF8);
    var text = reader.ReadToEnd();

    var result = new List<(string, string, float[], float[])>();
    foreach (var record in text.Split((char)GS))
    {
        if (record.Length == 0) continue;
        var f = record.Split((char)RS);
        if (f.Length < 5) continue;
        result.Add((f[0], f[1], ParseArray(f[3]), ParseArray(f[4])));
    }
    return result;

    static float[] ParseArray(string s)
    {
        var parts = s.Split((char)US);
        var a = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            a[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
        }
        return a;
    }
}

/// Merges every *.csv under `dir` into `entries`, converting CHART units (nm, percent) to the
/// database convention (Angstrom, fraction). Returns how many were added or replaced.
///
/// Replace-by-name rather than append, so re-running is idempotent and a corrected CSV supersedes
/// its predecessor instead of leaving two curves with one name for the fuzzy matcher to choose
/// between.
// Name from the file stem, in the database's own SHOUTY_UNDERSCORE convention, so TryMatchFilter
// tokenises it the same way it tokenises every upstream name. Shared with the retraction pass above
// rather than spelled twice: the manifest is matched against the blob BY NAME, so the two deriving
// it differently would silently retract nothing.
static string LocalCurveName(string csvPath)
    => Path.GetFileNameWithoutExtension(csvPath).ToUpperInvariant().Replace('-', '_').Replace(' ', '_');

static int MergeLocalFilters(
    List<(string Name, string Origin, float[] Wavelengths, float[] Values)> entries, string dir)
{
    if (!Directory.Exists(dir)) return 0;

    var added = 0;
    foreach (var csv in Directory.GetFiles(dir, "*.csv"))
    {
        var wl = new List<float>();
        var val = new List<float>();
        foreach (var raw in File.ReadLines(csv))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var left = line.AsSpan(0, comma).Trim();
            var right = line.AsSpan(comma + 1).Trim();
            if (!float.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var nm)
                || !float.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                continue; // the header row lands here
            }
            wl.Add(nm * 10f);      // nm -> Angstrom
            val.Add(pct / 100f);   // percent -> fraction
        }

        if (wl.Count < 2)
        {
            Console.Error.WriteLine($"Warning: {Path.GetFileName(csv)} yielded {wl.Count} points, skipping.");
            continue;
        }

        // Guard the conversion that would otherwise fail silently. A CSV already in fractions would
        // come out 100x too dark here and still look like a plausible curve, so refuse rather than
        // guess: a filter that peaks below 1 % transmission is not a filter anyone ships.
        var peak = 0f;
        foreach (var v in val) peak = Math.Max(peak, v);
        if (peak <= 0.01f)
        {
            Console.Error.WriteLine(
                $"Error: {Path.GetFileName(csv)} peaks at {peak:P2} after nm/percent conversion. " +
                "Is it already in fractions rather than percent?");
            continue;
        }

        var name = LocalCurveName(csv);

        var idx = entries.FindIndex(e => e.Name == name);
        var entry = (name, Path.GetFileName(csv), wl.ToArray(), val.ToArray());
        if (idx >= 0) entries[idx] = entry; else entries.Add(entry);
        added++;
        Console.WriteLine($"  local: {name} ({wl.Count} points, {wl[0] / 10:F0}..{wl[^1] / 10:F0}nm, peak {peak:P1})");
    }
    return added;
}

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
