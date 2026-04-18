#:sdk Microsoft.NET.Sdk
#:project ../src/TianWen.Lib/TianWen.Lib.csproj

// Generate a Milky Way equirectangular texture for TianWen's sky map.
//
// Bins real Tycho-2 photometry (~2.5M stars, V mag + B-V) into a galactic
// luminance map, applies a separable Gaussian blur to smear unresolved
// starlight, and tints each pixel from the weighted mean B-V of the stars
// that fell in it. The galactic plane, bulge, and Magellanic clouds all
// appear naturally because they exist in the catalog.
//
// --luminance <path> blends in a continuous Planck GNILC radiance map for
// smoother brightness. --dust-opacity <path> multiplies exp(-k*tau)
// extinction on top. See tools/bake_milkyway.ps1 for how to bake the
// Planck float32 maps from the HEALPix FITS.
//
// Output: milkyway.bgra.lz (lzip-compressed raw BGRA with 8-byte int32 LE
// width+height header; identical format to the previous Python tool so the
// runtime loader in SkyMapTab.TryLoadMilkyWayTexture works unchanged).
//
// The pipeline itself lives in TianWen.Lib/Astrometry/Catalogs/
// MilkyWayTextureBaker.cs so tools/optimize_milkyway.cs can drive the
// same code path against a reference image.
//
// Usage:
//   dotnet run tools/generate_milkyway.cs -- [options]
//
// Options map 1:1 to MilkyWayBakerOptions fields (see its XML docs for defaults).
//
// Requires: `lzip` CLI on PATH.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Extensions;

var width = 2048;
var height = 1024;
string? output = null;
string? dustPath = null;
string? luminancePath = null;
var opts = new MilkyWayBakerOptions();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--width": width = int.Parse(args[++i]); break;
        case "--height": height = int.Parse(args[++i]); break;
        case "--output": output = args[++i]; break;
        case "--dust-opacity": dustPath = args[++i]; break;
        case "--luminance": luminancePath = args[++i]; break;
        case "--k": opts = opts with { ExtinctionK = float.Parse(args[++i]) }; break;
        case "--blur-sigma": opts = opts with { BlurSigma = float.Parse(args[++i]) }; break;
        case "--min-mag": opts = opts with { MinMagnitude = float.Parse(args[++i]) }; break;
        case "--saturation": opts = opts with { ColorSaturation = float.Parse(args[++i]) }; break;
        case "--brightness": opts = opts with { BrightnessScale = float.Parse(args[++i]) }; break;
        case "--color-blur": opts = opts with { ColorBlurSigma = float.Parse(args[++i]) }; break;
        case "--warmth": opts = opts with { ColorWarmth = float.Parse(args[++i]) }; break;
        case "--dust-reddening": opts = opts with { DustReddening = float.Parse(args[++i]) }; break;
        case "--luminance-mix": opts = opts with { LuminanceMix = float.Parse(args[++i]) }; break;
        case "--help" or "-h":
            Console.WriteLine("Usage: dotnet run tools/generate_milkyway.cs -- [--width N] [--height N] [--output PATH] [--dust-opacity PATH] [--luminance PATH] [--k F] [--blur-sigma F] [--min-mag F] [--saturation F] [--brightness F] [--color-blur F] [--warmth F] [--dust-reddening F] [--luminance-mix F]");
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
opts = opts with { Width = width, Height = height };

if (output is null)
{
    var repoRoot = FindRepoRoot(Environment.CurrentDirectory)
                   ?? FindRepoRoot(AppContext.BaseDirectory)
                   ?? throw new DirectoryNotFoundException(
                       "Could not find repo root. Run from within the tianwen repo, or pass --output PATH explicitly.");
    output = Path.Combine(repoRoot, "src", "TianWen.UI.Gui", "Resources", "milkyway.bgra.lz");
}

Console.WriteLine($"Generating {width}x{height} Milky Way texture at {output}...");
var sw = Stopwatch.StartNew();

using var sp = new ServiceCollection().AddAstrometry().BuildServiceProvider();
var db = sp.GetRequiredService<ICelestialObjectDB>();
var (processed, failed) = await db.InitDBAsync(CancellationToken.None);
Console.WriteLine($"Catalog loaded in {sw.Elapsed.TotalSeconds:F1}s ({processed} processed, {failed} failed)");

sw.Restart();
var inputs = await MilkyWayBakerInputs.LoadAsync(db, width, height, luminancePath, dustPath, CancellationToken.None);
Console.WriteLine($"Inputs loaded in {sw.Elapsed.TotalSeconds:F1}s " +
                  $"({inputs.Stars.Length} stars, radiance={(inputs.Radiance is null ? "no" : "yes")}, dust={(inputs.DustOpacity is null ? "no" : "yes")})");

sw.Restart();
var bgra = MilkyWayTextureBaker.Bake(inputs, opts);
Console.WriteLine($"Bake in {sw.Elapsed.TotalSeconds:F1}s");

var outputDir = Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
var rawPath = output.EndsWith(".lz") ? output[..^3] : output + ".raw";
MilkyWayTextureBaker.WriteRaw(rawPath, width, height, bgra);
Console.WriteLine($"Raw size: {new FileInfo(rawPath).Length:N0} bytes");

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
    if (File.Exists(output)) File.Delete(output);
    File.Move(finalPath, output);
}
Console.WriteLine($"Compressed: {new FileInfo(output).Length:N0} bytes -> {output}");
return 0;

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
