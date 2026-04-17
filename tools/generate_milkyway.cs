#:sdk Microsoft.NET.Sdk
#:project ../src/TianWen.Lib/TianWen.Lib.csproj

// Generate a Milky Way equirectangular texture for TianWen's sky map.
//
// Unlike the earlier analytical model, this tool bins real Tycho-2 photometry
// (~2.5M stars, V mag + B-V) into a galactic luminance map, applies a separable
// Gaussian blur to smear unresolved starlight, and tints each pixel from the
// weighted mean B-V of the stars that fell in it. The galactic plane, bulge,
// and Magellanic clouds all appear naturally because they exist in the catalog.
//
// Optional --dust-opacity <path> multiplies extinction on top, where <path>
// is an equirectangular 32-bit float map of Planck tau_353 (or similar dust
// opacity proxy) at the same resolution. See PLAN-skymap-milkyway.md for how
// to bake one from the Planck Legacy Archive HEALPix FITS.
//
// Output: milkyway.bgra.lz (lzip-compressed raw BGRA with 8-byte int32 LE
// width+height header; identical format to the previous Python tool so the
// runtime loader in SkyMapTab.TryLoadMilkyWayTexture works unchanged).
//
// Usage:
//   dotnet run tools/generate_milkyway.cs -- [options]
//
//   --width <px>        output width  (default 2048)
//   --height <px>       output height (default 1024; must be width / 2)
//   --output <path>     output path   (default src/TianWen.UI.Gui/Resources/milkyway.bgra.lz)
//   --dust-opacity <f>  optional 32-bit float equirectangular tau_353 map
//   --k <float>         extinction scale factor (default 1.5)
//   --blur-sigma <px>   Gaussian blur sigma in pixels (default 1.2)
//
// Requires: `lzip` CLI on PATH (same dependency as Get-Tycho2Catalogs.ps1).

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Extensions;

// -----------------------------------------------------------------------------
// Argument parsing
// -----------------------------------------------------------------------------

var width = 2048;
var height = 1024;
string? output = null;
string? dustPath = null;
string? luminancePath = null;
var extinctionK = 10.0f;
var blurSigma = 1.0f;
// Skip stars brighter than V=6.5 — those are drawn crisply by the instanced
// star pipeline (EffectiveMagnitudeLimit defaults to 6.5 at FOV=60..180 deg).
// The diffuse texture only represents UNRESOLVED starlight: the faint stars
// that pile up into a milky glow and can't be individually drawn.
var minMagnitude = 8.5f;
// Saturation boost applied to each pixel's B-V deviation from the all-sky
// mean — without it the warm-cool contrast between the bulge and the arms
// is invisible after averaging across ~1000 stars per pixel near the plane.
var colorSaturation = 2.0f;
// Global warmth bias added to the per-pixel B-V before Planckian conversion.
// Positive = warmer (yellow/orange), negative = cooler. Compensates for the
// fact that most Tycho-2 stars are solar-type so the natural mean B-V is
// already yellowish, but averaging washes it out to near-grey.
var colorWarmth = 0.25f;
// Dust reddening: adds tau_353 * dustReddening to each pixel's effective B-V
// before Planckian conversion. Physically motivated (blue light scatters
// more than red through dust) and gives dust lanes a subtle brown tint
// instead of pure black.
// Max B-V shift added to pixels at the 99th percentile of dust opacity.
// 0.3 means the densest dust regions look ~0.3 B-V warmer (distinctly brown).
var dustReddening = 0.3f;
// When both --luminance and Tycho-2 are available, blend them. 1.0 = pure
// radiance (smoothest, best dust-lane detail via extinction), 0.0 = pure
// Tycho-2 (preserves stellar density cues but grainier). 0.7 mixes mostly
// radiance with a Tycho-2 nudge for density weighting.
var luminanceMix = 0.85f;
// Final brightness multiplier applied after normalisation + gamma. Lower to
// tame the "light saber" look when the luminance channel is a high-dynamic-
// range radiance map; 1.0 is full-strength.
var brightnessScale = 0.5f;
// Spatial blur sigma (pixels) for the colour channel. The B-V ratio per
// pixel is computed from 1-2 Tycho-2 stars which produces a fine-grained
// orange/blue speckle pattern when the texture is upsampled in the GPU.
// Blurring the B-V accumulators before dividing averages colour across a
// neighbourhood.
var colorBlurSigma = 6.0f;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--width": width = int.Parse(args[++i]); break;
        case "--height": height = int.Parse(args[++i]); break;
        case "--output": output = args[++i]; break;
        case "--dust-opacity": dustPath = args[++i]; break;
        case "--luminance": luminancePath = args[++i]; break;
        case "--k": extinctionK = float.Parse(args[++i]); break;
        case "--blur-sigma": blurSigma = float.Parse(args[++i]); break;
        case "--min-mag": minMagnitude = float.Parse(args[++i]); break;
        case "--saturation": colorSaturation = float.Parse(args[++i]); break;
        case "--brightness": brightnessScale = float.Parse(args[++i]); break;
        case "--color-blur": colorBlurSigma = float.Parse(args[++i]); break;
        case "--warmth": colorWarmth = float.Parse(args[++i]); break;
        case "--dust-reddening": dustReddening = float.Parse(args[++i]); break;
        case "--luminance-mix": luminanceMix = float.Parse(args[++i]); break;
        case "--help" or "-h":
            Console.WriteLine("Usage: dotnet run tools/generate_milkyway.cs -- [--width N] [--height N] [--output PATH] [--dust-opacity PATH] [--k FLOAT] [--blur-sigma FLOAT] [--min-mag FLOAT] [--saturation FLOAT]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (height * 2 != width)
{
    Console.Error.WriteLine($"Height ({height}) must equal width ({width}) / 2 for an equirectangular map.");
    return 1;
}

if (output is null)
{
    // `dotnet run` of a file-based app puts AppContext.BaseDirectory into an
    // obj/ cache outside the repo, so try the current working directory first
    // (users invoke from the tianwen repo root or src/).
    var repoRoot = FindRepoRoot(Environment.CurrentDirectory)
                   ?? FindRepoRoot(AppContext.BaseDirectory)
                   ?? throw new DirectoryNotFoundException(
                       "Could not find repo root. Run from within the tianwen repo, or pass --output PATH explicitly.");
    output = Path.Combine(repoRoot, "src", "TianWen.UI.Gui", "Resources", "milkyway.bgra.lz");
}

Console.WriteLine($"Generating {width}x{height} Milky Way texture at {output}...");

// -----------------------------------------------------------------------------
// Load Tycho-2 catalog via the same code path the runtime uses.
// -----------------------------------------------------------------------------

var sw = Stopwatch.StartNew();
using var sp = new ServiceCollection().AddAstrometry().BuildServiceProvider();
var db = sp.GetRequiredService<ICelestialObjectDB>();
var (processed, failed) = await db.InitDBAsync(CancellationToken.None);
Console.WriteLine($"Catalog loaded in {sw.Elapsed.TotalSeconds:F1}s ({processed} processed, {failed} failed)");

// -----------------------------------------------------------------------------
// Bin stars into equirectangular flux + B-V accumulators.
// -----------------------------------------------------------------------------

// Per-pixel flux accumulators.
//   tychoLuminanceFlux -- only stars fainter than minMagnitude (fallback
//                         luminance source when --luminance is not provided;
//                         bright stars excluded to avoid halos around the
//                         point-sprite renderer's draws).
//   bvFluxForColor     -- ALL stars (including bright ones), used purely for
//                         colour. Including bright O/B stars preserves the
//                         blue tint of spiral-arm young stellar populations
//                         that would otherwise be washed out by the more
//                         numerous red dwarfs.
//   bvWeightedSum      -- sum of flux * B-V, same filter as bvFluxForColor.
float[] flux;
var bvWeightedSum = new float[width * height];
var bvFluxForColor = new float[width * height];
var tychoLuminanceFlux = new float[width * height];

sw.Restart();
var batch = new Tycho2StarLite[65536];
var totalStreamed = 0;
var totalBinnedColor = 0;
var totalBinnedLum = 0;
var startIdx = 0;
while (true)
{
    var n = db.CopyTycho2Stars(batch, startIdx);
    if (n == 0) break;
    startIdx += n;
    totalStreamed += n;

    for (var i = 0; i < n; i++)
    {
        var star = batch[i];
        if (float.IsNaN(star.VMag)) continue;

        var raRad = star.RaHours * (MathF.PI / 12f);
        var decRad = star.DecDeg * (MathF.PI / 180f);
        var raSigned = raRad;
        if (raSigned > MathF.PI) raSigned -= 2f * MathF.PI;
        var u = raSigned / (2f * MathF.PI) + 0.5f;
        var v = 0.5f - decRad / MathF.PI;

        var px = (int)(u * width);
        var py = (int)(v * height);
        if (px < 0) px = 0; else if (px >= width) px = width - 1;
        if (py < 0) py = 0; else if (py >= height) py = height - 1;
        var idx = py * width + px;

        // Only faint unresolved stars contribute to both luminance and colour.
        // Bright stars (V < minMagnitude) are drawn as crisp point sprites by
        // the instanced renderer with their own per-star B-V colour, so
        // including them here only smears halos of their colour through the
        // colour blur (visible as large warm/cool blobs around Antares etc.).
        if (star.VMag >= minMagnitude)
        {
            var f = MathF.Exp(-0.921034f * star.VMag);
            tychoLuminanceFlux[idx] += f;
            bvFluxForColor[idx] += f;
            bvWeightedSum[idx] += f * star.BMinusV;
            totalBinnedColor++;
            totalBinnedLum++;
        }
    }
}
Console.WriteLine($"Tycho-2: {totalBinnedColor} stars binned for colour, {totalBinnedLum} for luminance (V>={minMagnitude}) in {sw.Elapsed.TotalSeconds:F1}s");

// -----------------------------------------------------------------------------
// Luminance channel: either a continuous surface-brightness map (e.g. Planck
// GNILC Radiance) or Tycho-2 flux binning. The continuous map is strongly
// preferred — Tycho-2 is a point catalogue and produces pointillistic
// "salt-and-pepper" noise even after blur, because most pixels contain 0-2
// stars off the galactic plane.
// -----------------------------------------------------------------------------

if (luminancePath is not null)
{
    Console.WriteLine($"Loading luminance from {luminancePath}");
    var radiance = LoadFloatEquirectangular(luminancePath, width, height);
    // Dust radiance spans ~6 orders of magnitude. Log-compress into [0, ~13].
    var radianceSorted = (float[])radiance.Clone();
    Array.Sort(radianceSorted);
    var rFloor = MathF.Max(radianceSorted[(int)(radianceSorted.Length * 0.01f)], 1e-12f);
    var radianceLog = new float[radiance.Length];
    for (var i = 0; i < radiance.Length; i++)
    {
        radianceLog[i] = MathF.Log(MathF.Max(radiance[i], rFloor) / rFloor);
    }

    if (luminanceMix >= 1.0f)
    {
        flux = radianceLog;
        Console.WriteLine("Luminance: Planck radiance only");
    }
    else
    {
        // Blend radiance (smooth, continuous) with Tycho-2 stellar density.
        // Tycho-2 contributes "where the stars pile up" signal that radiance
        // lacks because radiance is dust emission, not direct starlight.
        var tychoBlurred = GaussianBlur(tychoLuminanceFlux, width, height, blurSigma);
        var tychoLog = new float[tychoBlurred.Length];
        for (var i = 0; i < tychoBlurred.Length; i++)
        {
            tychoLog[i] = MathF.Log(tychoBlurred[i] + 1e-12f) - MathF.Log(1e-12f);
        }
        // Normalise both into roughly the same range before mixing.
        NormaliseInPlace(radianceLog);
        NormaliseInPlace(tychoLog);
        flux = new float[radianceLog.Length];
        for (var i = 0; i < flux.Length; i++)
        {
            flux[i] = luminanceMix * radianceLog[i] + (1f - luminanceMix) * tychoLog[i];
        }
        Console.WriteLine($"Luminance: {luminanceMix:F2}*radiance + {1f - luminanceMix:F2}*Tycho-2");
    }
}
else
{
    // Tycho-2 only — requires heavy blur to avoid salt-and-pepper output.
    flux = tychoLuminanceFlux;
    Console.WriteLine("Luminance: Tycho-2 flux only (use large --blur-sigma to smooth)");
}

// -----------------------------------------------------------------------------
// Compute flux-weighted mean B-V per pixel, before blurring (blur would mix
// bright-star colour into empty pixels otherwise).
// -----------------------------------------------------------------------------

// Blur the numerator and denominator SEPARATELY at the same sigma, then
// divide per pixel. This yields a spatially smooth B-V ratio even where
// individual pixels had only one star (or none), avoiding the pointillistic
// orange/blue speckle pattern that appears when dividing first and blurring
// the ratio. The blur sigma here is large (~6 px) because colour resolution
// doesn't need to match luminance resolution.
var bvFluxSmoothed = GaussianBlur(bvFluxForColor, width, height, colorBlurSigma);
var bvSumSmoothed = GaussianBlur(bvWeightedSum, width, height, colorBlurSigma);
var bv = new float[width * height];
for (var i = 0; i < bvFluxSmoothed.Length; i++)
{
    bv[i] = bvFluxSmoothed[i] > 1e-12f ? bvSumSmoothed[i] / bvFluxSmoothed[i] : 0.65f;
}
Console.WriteLine($"Colour channel smoothed with sigma={colorBlurSigma}px");

// -----------------------------------------------------------------------------
// Separable Gaussian blur of the flux channel (1D horizontal, then vertical).
// The B-V field is left unblurred — colour is flux-weighted so empty pixels
// stay at the default value.
// -----------------------------------------------------------------------------

sw.Restart();
if (blurSigma > 0)
{
    flux = GaussianBlur(flux, width, height, blurSigma);
}
Console.WriteLine($"Gaussian blur (sigma={blurSigma}px) in {sw.Elapsed.TotalSeconds:F1}s");

// -----------------------------------------------------------------------------
// Optional: multiply by exp(-k * tau) from a dust opacity map.
// -----------------------------------------------------------------------------

float[]? dust = null;
float[]? dustNormalised = null;
if (dustPath is not null)
{
    Console.WriteLine($"Loading dust opacity from {dustPath}");
    dust = LoadFloatEquirectangular(dustPath, width, height);
    for (var i = 0; i < flux.Length; i++)
    {
        flux[i] *= MathF.Exp(-extinctionK * dust[i]);
    }

    // Normalised [0,~1] copy for colour reddening. tau_353 spans ~4 orders of
    // magnitude; using raw values would make `dustReddening` a weird tiny scale.
    var dustSorted = (float[])dust.Clone();
    Array.Sort(dustSorted);
    var dust99 = dustSorted[(int)(dustSorted.Length * 0.99f)];
    dustNormalised = new float[dust.Length];
    if (dust99 > 0)
    {
        var inv = 1f / dust99;
        for (var i = 0; i < dust.Length; i++)
        {
            dustNormalised[i] = MathF.Min(dust[i] * inv, 1.5f);
        }
    }
}

// -----------------------------------------------------------------------------
// Normalise flux to a display range. Clip at p25 (not p50) so three-quarters
// of the sky goes pitch black — like Stellarium, the Milky Way is a narrow
// band, not an all-sky smear. Cap at p99.5 so the bulge doesn't blow out.
// -----------------------------------------------------------------------------

var sorted = (float[])flux.Clone();
Array.Sort(sorted);
var lowIdx = (int)(sorted.Length * 0.25f);
var highIdx = (int)(sorted.Length * 0.995f);
var lowVal = sorted[lowIdx];
var highVal = sorted[highIdx];
var range = highVal - lowVal;
if (range <= 0) range = 1;
Console.WriteLine($"Flux normalisation: p25={lowVal:E2}, p99.5={highVal:E2}");

// Mean B-V across non-empty pixels, so the saturation boost acts around a
// physically meaningful reference rather than the arbitrary 0.65 default.
var bvMean = 0f;
var bvCount = 0;
for (var i = 0; i < flux.Length; i++)
{
    if (flux[i] > lowVal) { bvMean += bv[i]; bvCount++; }
}
bvMean = bvCount > 0 ? bvMean / bvCount : 0.65f;
Console.WriteLine($"Mean B-V across lit pixels: {bvMean:F2}");

// -----------------------------------------------------------------------------
// Compose BGRA output.
// -----------------------------------------------------------------------------

var bgra = new byte[width * height * 4];
for (var py = 0; py < height; py++)
{
    for (var px = 0; px < width; px++)
    {
        var idx = py * width + px;
        var normalised = (flux[idx] - lowVal) / range;
        normalised = MathF.Max(0, MathF.Min(1, normalised));

        // Soft gamma so the wide diffuse glow is visible without the core
        // saturating. sqrt gives roughly the right perceptual curve.
        // brightnessScale dims the whole texture so the Milky Way doesn't
        // look like a lightsaber against the star layer.
        var brightness = MathF.Sqrt(normalised) * brightnessScale;

        // Amplify the per-pixel B-V deviation from the all-sky mean by
        // `colorSaturation` so the bulge (redder stars, B-V > mean) looks
        // warm and the arms (bluer young stars) look cool. Without this
        // boost the flux-weighted averaging across ~hundreds of stars per
        // pixel flattens everything to a near-neutral tone.
        // Dust reddening: physical reddening from dust adds proportional to
        // tau_353. Gives dark lanes a subtle brown cast instead of neutral
        // grey. tau_353 values are typically 0..0.01; reddeningStrength ~1
        // yields a gentle B-V bump in dusty regions.
        var dustRedden = dustNormalised is not null ? dustNormalised[idx] * dustReddening : 0f;
        var amplifiedBv = bvMean + (bv[idx] - bvMean) * colorSaturation + colorWarmth + dustRedden;
        var (r, g, b) = BVToRgb(amplifiedBv);
        var outIdx = idx * 4;
        bgra[outIdx + 0] = ToByte(b * brightness);
        bgra[outIdx + 1] = ToByte(g * brightness);
        bgra[outIdx + 2] = ToByte(r * brightness);
        bgra[outIdx + 3] = ToByte(brightness);
    }
}

// -----------------------------------------------------------------------------
// Write raw file with 8-byte int32 LE header, then shell out to lzip.
// -----------------------------------------------------------------------------

var outputDir = Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

var rawPath = output.EndsWith(".lz") ? output[..^3] : output + ".raw";
using (var fs = File.Create(rawPath))
{
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(header, width);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], height);
    fs.Write(header);
    fs.Write(bgra);
}
Console.WriteLine($"Raw size: {new FileInfo(rawPath).Length:N0} bytes");

// 2 MiB members so LzipDecoder gets 4-way parallel decompression on the
// 8 MiB raw texture at load time. Slight size hit (~2%) vs 4 MiB blocks.
Console.WriteLine("Compressing with lzip -9 -b 2MiB ...");
var lzip = Process.Start(new ProcessStartInfo("lzip", $"-9 -b 2MiB -f \"{rawPath}\"")
{
    UseShellExecute = false,
    RedirectStandardError = true
}) ?? throw new InvalidOperationException("failed to start lzip");
await lzip.WaitForExitAsync();
if (lzip.ExitCode != 0)
{
    Console.Error.WriteLine(await lzip.StandardError.ReadToEndAsync());
    return lzip.ExitCode;
}

var finalPath = rawPath + ".lz";
if (finalPath != output)
{
    // lzip always appends `.lz` to the source file; rename if the user asked
    // for a different suffix.
    if (File.Exists(output)) File.Delete(output);
    File.Move(finalPath, output);
}
Console.WriteLine($"Compressed: {new FileInfo(output).Length:N0} bytes -> {output}");

return 0;

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

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

static float[] GaussianBlur(float[] src, int w, int h, float sigma)
{
    // Vertical pass is constant-sigma (declination pixels are uniform on the sphere).
    var vRadius = (int)MathF.Ceiling(sigma * 3f);
    var vKernel = BuildGaussian(sigma, vRadius);

    var tmp = new float[src.Length];
    for (var y = 0; y < h; y++)
    {
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -vRadius; k <= vRadius; k++)
            {
                var sy = y + k;
                if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1; // pole clamp
                sum += src[sy * w + x] * vKernel[k + vRadius];
            }
            tmp[y * w + x] = sum;
        }
    }

    // Horizontal pass: sigma scales with 1/cos(dec) so the angular footprint on
    // the sphere is isotropic. At the equator row the kernel is `sigma` pixels
    // wide; near the poles it expands by ~1/cos(dec) which at dec=89 deg is ~57x.
    // Clamp cos(dec) >= 0.01 so the polar-cap rows don't degenerate to a
    // full-row convolution.
    var dst = new float[src.Length];
    for (var y = 0; y < h; y++)
    {
        var dec = (Math.PI / 2.0) - ((y + 0.5) / h) * Math.PI;
        var cosDec = Math.Max(0.01, Math.Cos(dec));
        var rowSigma = (float)(sigma / cosDec);
        // Cap radius so we don't convolve more than half the row at the pole.
        var rowRadius = Math.Min((int)MathF.Ceiling(rowSigma * 3f), w / 2);
        var rowKernel = BuildGaussian(rowSigma, rowRadius);

        var rowBase = y * w;
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -rowRadius; k <= rowRadius; k++)
            {
                var sx = (x + k + w) % w; // RA wrap
                sum += tmp[rowBase + sx] * rowKernel[k + rowRadius];
            }
            dst[rowBase + x] = sum;
        }
    }
    return dst;
}

static void NormaliseInPlace(float[] a)
{
    var max = 0f;
    for (var i = 0; i < a.Length; i++) if (a[i] > max) max = a[i];
    if (max <= 0) return;
    var inv = 1f / max;
    for (var i = 0; i < a.Length; i++) a[i] *= inv;
}

static float[] BuildGaussian(float sigma, int radius)
{
    var kernel = new float[radius * 2 + 1];
    var twoSigmaSq = 2f * sigma * sigma;
    var norm = 0f;
    for (var i = -radius; i <= radius; i++)
    {
        kernel[i + radius] = MathF.Exp(-i * i / twoSigmaSq);
        norm += kernel[i + radius];
    }
    for (var i = 0; i < kernel.Length; i++) kernel[i] /= norm;
    return kernel;
}

static float[] LoadFloatEquirectangular(string path, int expectedW, int expectedH)
{
    var bytes = File.ReadAllBytes(path);
    var expected = expectedW * expectedH * 4;
    if (bytes.Length != expected)
    {
        throw new InvalidDataException(
            $"Dust opacity map size {bytes.Length} does not match expected {expected} " +
            $"({expectedW}x{expectedH} float32). Re-project your HEALPix source at the same resolution.");
    }
    var result = new float[expectedW * expectedH];
    Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
    return result;
}

// B-V colour index to linear RGB via Planckian locus (same math as the
// Python version, kept in float for consistency with the rest of the pipeline).
static (float R, float G, float B) BVToRgb(float bv)
{
    bv = MathF.Max(-0.4f, MathF.Min(2.0f, bv));
    var t = 4600f * (1f / (0.92f * bv + 1.7f) + 1f / (0.92f * bv + 0.62f));

    float r, g, b;
    if (t <= 6600)
    {
        r = 1f;
        g = Clamp01(0.39f * MathF.Log(t / 100f) - 0.634f);
    }
    else
    {
        r = Clamp01(1.293f * MathF.Pow(t / 100f - 60f, -0.1332f));
        g = Clamp01(1.129f * MathF.Pow(t / 100f - 60f, -0.0755f));
    }

    if (t >= 6600) b = 1f;
    else if (t <= 1900) b = 0f;
    else b = Clamp01(0.543f * MathF.Log(t / 100f - 10f) - 1.186f);

    return (r, g, b);
}

static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
static byte ToByte(float x) => (byte)MathF.Max(0, MathF.Min(255, x * 255f));
