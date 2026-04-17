#:sdk Microsoft.NET.Sdk
#:package FITS.Lib@4.5.0

// Reproject a Planck HEALPix dust opacity map to equirectangular float32.
//
// Output: raw float32 little-endian, width*height values, no header. Fed into
// `generate_milkyway.cs --dust-opacity <path>` so the Milky Way bake multiplies
// by exp(-k * tau) per pixel.
//
// Input assumptions (standard Planck HEALPix FITS layout):
//   - Single BINTABLE HDU with NSIDE + ORDERING (NESTED or RING) + COORDSYS=G
//   - Column of interest (default: first Float32/Float64 column; override with --column)
//   - Rows each hold 1024 pixels (typical Planck chunking); handled generically
//
// Default source: Planck GNILC dust opacity map (IRSA mirror)
//   Nside=2048, ~384 MB (float64 samples), public domain. Auto-downloaded to
//   tools/data/ if --fits is omitted. Smaller variants work too via --fits.
//
// Usage:
//   dotnet run tools/reproject_planck_dust.cs -- --output out.f32 [--width 2048] [--column TAU353] [--fits PATH]
//
// If --fits is omitted, the Planck GNILC opacity FITS is downloaded to
// tools/data/COM_CompMap_Dust-GNILC-Model-Opacity_2048_R2.01.fits and cached
// there for reuse.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using nom.tam.fits;

const string PlanckGnilcUrl =
    "https://irsa.ipac.caltech.edu/data/Planck/release_2/all-sky-maps/maps/component-maps/foregrounds/" +
    "COM_CompMap_Dust-GNILC-Model-Opacity_2048_R2.01.fits";
const long MinExpectedDownloadBytes = 100L * 1024 * 1024;

string? fitsPath = null;
string? output = null;
var width = 2048;
var height = 0;
string? columnName = null;
var url = PlanckGnilcUrl;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--fits": fitsPath = args[++i]; break;
        case "--output": output = args[++i]; break;
        case "--width": width = int.Parse(args[++i]); break;
        case "--height": height = int.Parse(args[++i]); break;
        case "--column": columnName = args[++i]; break;
        case "--url": url = args[++i]; break;
        case "--help" or "-h":
            Console.WriteLine("Usage: dotnet run tools/reproject_planck_dust.cs -- --output PATH [--fits PATH] [--url URL] [--width N] [--height N] [--column NAME]");
            Console.WriteLine("If --fits is omitted, --url (default: Planck GNILC opacity) is auto-downloaded to tools/data/ and cached by filename.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (output is null)
{
    Console.Error.WriteLine("--output is required.");
    return 1;
}
if (height == 0) height = width / 2;
if (height * 2 != width)
{
    Console.Error.WriteLine($"Height ({height}) must equal width ({width}) / 2 for an equirectangular map.");
    return 1;
}

// -----------------------------------------------------------------------------
// Download the Planck GNILC opacity FITS if --fits was not provided. Cached to
// tools/data/ (gitignored) so subsequent runs skip the 200 MB transfer.
// -----------------------------------------------------------------------------

if (fitsPath is null)
{
    var repoRoot = FindRepoRoot(Environment.CurrentDirectory)
                   ?? FindRepoRoot(AppContext.BaseDirectory)
                   ?? throw new DirectoryNotFoundException(
                       "Could not find repo root. Run from within the tianwen repo, or pass --fits PATH explicitly.");
    var cacheDir = Path.Combine(repoRoot, "tools", "data");
    Directory.CreateDirectory(cacheDir);
    // Cache by the URL's filename so opacity + radiance etc. don't collide.
    var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
    fitsPath = Path.Combine(cacheDir, fileName);

    if (!File.Exists(fitsPath) || new FileInfo(fitsPath).Length < MinExpectedDownloadBytes)
    {
        if (File.Exists(fitsPath))
        {
            Console.WriteLine($"Existing cache at {fitsPath} is smaller than expected; re-downloading.");
            File.Delete(fitsPath);
        }
        await DownloadWithProgressAsync(url, fitsPath);
    }
    else
    {
        Console.WriteLine($"Using cached {fitsPath} ({new FileInfo(fitsPath).Length / (1024 * 1024)} MB)");
    }
}
else if (!File.Exists(fitsPath))
{
    Console.Error.WriteLine($"--fits path does not exist: {fitsPath}");
    return 1;
}

// -----------------------------------------------------------------------------
// Load the HEALPix FITS file.
// -----------------------------------------------------------------------------

Console.WriteLine($"Opening {fitsPath} ({new FileInfo(fitsPath).Length / (1024*1024)} MB)...");
var sw = Stopwatch.StartNew();
var fits = new Fits(fitsPath);

// Skip the primary HDU (typically empty) and find the BINTABLE.
BinaryTableHDU? table = null;
for (var hduIdx = 0; ; hduIdx++)
{
    var hdu = fits.ReadHDU();
    if (hdu is null) break;
    if (hdu is BinaryTableHDU bt) { table = bt; break; }
}
if (table is null)
{
    Console.Error.WriteLine("No BINTABLE HDU found in FITS file.");
    return 1;
}

var header = table.Header;
var nside = header.GetIntValue("NSIDE", -1);
var ordering = (header.GetStringValue("ORDERING") ?? "").Trim();
var coordsys = (header.GetStringValue("COORDSYS") ?? "").Trim();
if (nside <= 0)
{
    Console.Error.WriteLine("Header missing NSIDE.");
    return 1;
}
if (ordering != "NESTED" && ordering != "RING")
{
    Console.Error.WriteLine($"Unknown ORDERING '{ordering}' (expected NESTED or RING).");
    return 1;
}
if (!string.IsNullOrEmpty(coordsys) && coordsys != "G" && coordsys != "GALACTIC")
{
    Console.Error.WriteLine($"Warning: COORDSYS='{coordsys}' (expected 'G'). Reprojection assumes galactic input.");
}
var nPix = 12L * nside * nside;
Console.WriteLine($"HEALPix: Nside={nside} ({nPix:N0} pixels), Ordering={ordering}, Coordsys={coordsys}");

// Pick the column. If --column not specified, use the first float column.
var nCols = table.NCols;
var colIdx = -1;
if (columnName is not null)
{
    for (var c = 0; c < nCols; c++)
    {
        if (string.Equals(header.GetStringValue($"TTYPE{c + 1}")?.Trim(), columnName, StringComparison.OrdinalIgnoreCase))
        {
            colIdx = c;
            break;
        }
    }
    if (colIdx < 0) { Console.Error.WriteLine($"Column '{columnName}' not found."); return 1; }
}
else
{
    for (var c = 0; c < nCols; c++)
    {
        var form = header.GetStringValue($"TFORM{c + 1}")?.Trim() ?? "";
        if (form.Contains('E') || form.Contains('D')) { colIdx = c; break; }
    }
    if (colIdx < 0) { Console.Error.WriteLine("No Float32/Float64 column found."); return 1; }
}
var colName = header.GetStringValue($"TTYPE{colIdx + 1}")?.Trim() ?? $"col{colIdx}";
Console.WriteLine($"Extracting column {colIdx}: {colName}");

// Flatten the column into a single pixel-indexed array.
var columnData = table.GetColumn(colIdx);
var pixels = FlattenHealpixColumn(columnData, nPix);
Console.WriteLine($"Loaded {pixels.Length:N0} HEALPix samples in {sw.Elapsed.TotalSeconds:F1}s");

// -----------------------------------------------------------------------------
// Reproject to equirectangular (galactic input -> J2000 equirectangular output).
//
// For each output pixel:
//   u, v in [0,1) -> RA, Dec (J2000)
//   J2000 -> galactic (l, b)
//   (l, b) -> (theta, phi) where theta = PI/2 - b, phi = l
//   (theta, phi) -> HEALPix pixel index
// -----------------------------------------------------------------------------

sw.Restart();
var outputMap = new float[width * height];
for (var py = 0; py < height; py++)
{
    // Sample at pixel centre: v = (py + 0.5) / height
    var v = (py + 0.5) / height;
    var decRad = (0.5 - v) * Math.PI;
    for (var px = 0; px < width; px++)
    {
        var u = (px + 0.5) / width;
        var raSigned = (u - 0.5) * 2.0 * Math.PI; // [-PI, PI]

        // J2000 RA/Dec -> galactic (l, b). Same constants as Tycho-2 tool.
        var (l, b) = EquatorialToGalactic(raSigned, decRad);

        // Galactic (l, b) -> HEALPix spherical (theta, phi)
        var theta = Math.PI / 2 - b;
        var phi = l;
        if (phi < 0) phi += 2 * Math.PI;

        long pix = ordering == "NESTED"
            ? AngToPixNest(nside, theta, phi)
            : AngToPixRing(nside, theta, phi);

        outputMap[py * width + px] = pixels[pix];
    }
}
Console.WriteLine($"Reprojected {width}x{height} pixels in {sw.Elapsed.TotalSeconds:F1}s");

// Report value range for sanity.
var min = float.MaxValue; var max = float.MinValue;
for (var i = 0; i < outputMap.Length; i++)
{
    if (float.IsFinite(outputMap[i]))
    {
        if (outputMap[i] < min) min = outputMap[i];
        if (outputMap[i] > max) max = outputMap[i];
    }
}
Console.WriteLine($"Value range: [{min:E2}, {max:E2}]");

// Write raw float32 LE.
var outDir = Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
using (var fs = File.Create(output))
{
    Span<byte> buf = stackalloc byte[4];
    foreach (var f in outputMap)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buf, f);
        fs.Write(buf);
    }
}
Console.WriteLine($"Wrote {new FileInfo(output).Length:N0} bytes to {output}");
return 0;

// -----------------------------------------------------------------------------
// HEALPix and coordinate helpers
// -----------------------------------------------------------------------------

// Flatten a BINTABLE column (rows of 1024-pixel chunks, typically) into a
// single pixel-indexed float array of length nPix.
static float[] FlattenHealpixColumn(object columnData, long nPix)
{
    var result = new float[nPix];
    long written = 0;
    if (columnData is float[][] jaggedF)
    {
        foreach (var row in jaggedF)
        {
            foreach (var val in row)
            {
                if (written >= nPix) break;
                result[written++] = val;
            }
        }
    }
    else if (columnData is double[][] jaggedD)
    {
        foreach (var row in jaggedD)
        {
            foreach (var val in row)
            {
                if (written >= nPix) break;
                result[written++] = (float)val;
            }
        }
    }
    else if (columnData is float[] flatF)
    {
        var n = Math.Min(flatF.Length, nPix);
        for (long i = 0; i < n; i++) result[i] = flatF[i];
        written = n;
    }
    else if (columnData is double[] flatD)
    {
        var n = Math.Min(flatD.Length, nPix);
        for (long i = 0; i < n; i++) result[i] = (float)flatD[i];
        written = n;
    }
    else
    {
        throw new InvalidDataException($"Unexpected HEALPix column type: {columnData.GetType().Name}");
    }
    if (written != nPix)
    {
        Console.Error.WriteLine($"Warning: read {written} pixels, expected {nPix}. Missing pixels set to 0.");
    }
    return result;
}

// J2000 RA/Dec (radians) -> galactic (l, b) in radians.
// North galactic pole: RA=192.85948 deg, Dec=27.12825 deg.
// Matches tools/generate_milkyway.py so the composite lines up.
static (double L, double B) EquatorialToGalactic(double raRad, double decRad)
{
    const double NgpRa = 192.85948 * Math.PI / 180.0;
    const double NgpDec = 27.12825 * Math.PI / 180.0;
    const double GcL = 122.93192 * Math.PI / 180.0;
    var sinNgpDec = Math.Sin(NgpDec);
    var cosNgpDec = Math.Cos(NgpDec);

    var sinDec = Math.Sin(decRad);
    var cosDec = Math.Cos(decRad);
    var da = raRad - NgpRa;
    var sinDa = Math.Sin(da);
    var cosDa = Math.Cos(da);

    var sinB = sinNgpDec * sinDec + cosNgpDec * cosDec * cosDa;
    var b = Math.Asin(Math.Clamp(sinB, -1.0, 1.0));
    var y = cosDec * sinDa;
    var x = cosNgpDec * sinDec - sinNgpDec * cosDec * cosDa;
    var l = GcL - Math.Atan2(y, x);

    // Normalise l to [0, 2*PI)
    l = ((l % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    return (l, b);
}

// HEALPix (theta, phi) -> NESTED pixel index. Reference: Gorski et al. 2005,
// ApJ 622, 759; translated from the C++ healpix_cxx implementation.
static long AngToPixNest(int nside, double theta, double phi)
{
    var z = Math.Cos(theta);
    var za = Math.Abs(z);
    var tt = ((phi / Math.PI * 2.0) % 4.0 + 4.0) % 4.0; // [0,4)

    int face, ix, iy;
    if (za <= 2.0 / 3.0)
    {
        // Equatorial region
        var temp1 = nside * (0.5 + tt);
        var temp2 = nside * (z * 0.75);
        var jp = (long)(temp1 - temp2); // ascending
        var jm = (long)(temp1 + temp2); // descending
        var ifp = jp / nside;
        var ifm = jm / nside;
        face = ifp == ifm ? (int)(ifp % 4 + 4)
             : ifp < ifm ? (int)(ifp % 4)
             : (int)(ifm % 4 + 8);
        ix = (int)(jm % nside);
        iy = (int)(nside - jp % nside - 1);
    }
    else
    {
        // Polar region
        var ntt = Math.Min((int)tt, 3);
        var tp = tt - ntt;
        var tmp = nside * Math.Sqrt(3.0 * (1.0 - za));
        var jp = (long)(tp * tmp);
        var jm = (long)((1.0 - tp) * tmp);
        jp = Math.Min(jp, nside - 1);
        jm = Math.Min(jm, nside - 1);
        if (z >= 0)
        {
            face = ntt;
            ix = (int)(nside - jm - 1);
            iy = (int)(nside - jp - 1);
        }
        else
        {
            face = ntt + 8;
            ix = (int)jp;
            iy = (int)jm;
        }
    }

    return (long)face * nside * nside + Xy2PixNest(ix, iy);
}

// HEALPix (theta, phi) -> RING pixel index. Same projection math as NESTED
// but with the RING-ordering index formula.
static long AngToPixRing(int nside, double theta, double phi)
{
    var z = Math.Cos(theta);
    var za = Math.Abs(z);
    var tt = ((phi / Math.PI * 2.0) % 4.0 + 4.0) % 4.0;

    if (za <= 2.0 / 3.0)
    {
        // Equatorial region
        var temp1 = nside * (0.5 + tt);
        var temp2 = nside * (z * 0.75);
        var jp = (long)(temp1 - temp2);
        var jm = (long)(temp1 + temp2);
        var ir = nside + 1 + jp - jm; // 1..2*nside+1
        var kshift = 1 - (ir & 1);
        var ip = ((jp + jm - nside + kshift + 1) / 2) % (4 * nside);
        return 2L * nside * (nside - 1) + (ir - 1) * 4L * nside + ip;
    }
    else
    {
        // Polar regions
        var tp = tt - (int)tt;
        var tmp = nside * Math.Sqrt(3.0 * (1.0 - za));
        var jp = (long)(tp * tmp);
        var jm = (long)((1.0 - tp) * tmp);
        var ir = jp + jm + 1;
        var ip = (long)(tt * ir) % (4L * ir);
        if (ip < 0) ip += 4L * ir;
        return z > 0
            ? 2L * ir * (ir - 1) + ip
            : 12L * nside * nside - 2L * ir * (ir + 1) + ip;
    }
}

// Interleave bits of (ix, iy) for NESTED within-face index.
static long Xy2PixNest(int ix, int iy)
{
    long pix = 0;
    for (var i = 0; i < 16; i++) // up to nside = 32768
    {
        pix |= (long)((ix >> i) & 1) << (2 * i);
        pix |= (long)((iy >> i) & 1) << (2 * i + 1);
    }
    return pix;
}

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "TianWen.UI.Gui", "Resources")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// Stream a large file with progress output. The FITS is ~200 MB; write to a
// .partial path and rename on success so an interrupted download doesn't leave
// a half-file that the next run would happily feed into FITS.Lib.
static async Task DownloadWithProgressAsync(string url, string destination)
{
    var tmp = destination + ".partial";
    if (File.Exists(tmp)) File.Delete(tmp);

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TianWen-generate-milkyway/1.0");

    Console.WriteLine($"Downloading {url}");
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

    var total = response.Content.Headers.ContentLength ?? -1L;
    var totalMb = total > 0 ? total / (1024.0 * 1024.0) : -1;

    long read;
    // Scoped so `dst` closes before we re-open the file for validation.
    {
        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = File.Create(tmp);
        var buf = new byte[128 * 1024];
        read = 0;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            var n = await src.ReadAsync(buf);
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n));
            read += n;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds > 500)
            {
                var mb = read / (1024.0 * 1024.0);
                if (totalMb > 0)
                    Console.Write($"\r  {mb:F1} / {totalMb:F1} MB ({mb * 100 / totalMb:F0}%)   ");
                else
                    Console.Write($"\r  {mb:F1} MB   ");
                lastReport = now;
            }
        }
    }
    Console.WriteLine();

    if (read < MinExpectedDownloadBytes)
    {
        File.Delete(tmp);
        throw new InvalidDataException(
            $"Downloaded {read:N0} bytes, expected at least {MinExpectedDownloadBytes:N0}. " +
            "Server response may have been a redirect page rather than the FITS file.");
    }

    // Basic sanity: FITS files start with "SIMPLE  =" in the first 80 bytes.
    using (var fs = File.OpenRead(tmp))
    {
        Span<byte> head = stackalloc byte[80];
        fs.ReadExactly(head);
        var headStr = System.Text.Encoding.ASCII.GetString(head);
        if (!headStr.StartsWith("SIMPLE"))
        {
            File.Delete(tmp);
            throw new InvalidDataException("Downloaded file does not look like a FITS file (missing SIMPLE keyword).");
        }
    }

    File.Move(tmp, destination);
    Console.WriteLine($"Cached to {destination} ({new FileInfo(destination).Length / (1024 * 1024)} MB)");
}
