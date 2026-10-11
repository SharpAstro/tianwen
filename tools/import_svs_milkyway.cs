#:sdk Microsoft.NET.Sdk
#:project ../src/TianWen.Lib/TianWen.Lib.csproj

// Import NASA's starless all-sky map as TianWen's Milky Way texture.
//
// The map is the Goddard Scientific Visualization Studio's "Deep Star Maps 2020" Milky Way background
// (https://svs.gsfc.nasa.gov/4851): rendered from about 1.7 billion Hipparcos-2, Tycho-2 and Gaia DR2 stars, with
// the Hipparcos and Tycho stars left out, since the sky map draws those itself. Credit: NASA/Goddard Space Flight
// Center Scientific Visualization Studio; Gaia DR2: ESA/Gaia/DPAC.
//
// Download the 4K map (about 35 MB) and run, from the repo root:
//   curl -LO https://svs.gsfc.nasa.gov/vis/a000000/a004800/a004851/milkyway_2020_4k.exr
//   dotnet run tools/import_svs_milkyway.cs -- milkyway_2020_4k.exr
//
// The conversion (the mirror into the sky map's orientation, linear averaging, the black point, the sRGB encoding,
// the dimming in alpha) is MilkyWaySvsImport in TianWen.Lib, with its reasons and its tests. This writes the
// same milkyway.bgra.lz the Tycho-2 bake (tools/generate_milkyway.cs) writes, in the same format, so the desktop
// loader and tools/bake-milkyway (the browser's PNG) read it unchanged.
//
// Options: --output PATH (default src/TianWen.UI.Gui/Resources/milkyway.bgra.lz), --width N (2048; the source
// width must be a whole multiple), --scale F (0.33), --black-point F (0.0025).

using System;
using System.Diagnostics;
using System.IO;
using SharpAstro.Exr;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;

string? input = null;
string? output = null;
var opts = new MilkyWaySvsImportOptions();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output": output = args[++i]; break;
        case "--width":
            var width = int.Parse(args[++i]);
            opts = opts with { Width = width, Height = width / 2 };
            break;
        case "--scale": opts = opts with { Scale = float.Parse(args[++i]) }; break;
        case "--black-point": opts = opts with { BlackPoint = float.Parse(args[++i]) }; break;
        case "--help" or "-h":
            Console.WriteLine("Usage: dotnet run tools/import_svs_milkyway.cs -- <milkyway_2020_4k.exr> [--output PATH] [--width N] [--scale F] [--black-point F]");
            return 0;
        default:
            if (input is null && !args[i].StartsWith('-'))
            {
                input = args[i];
                break;
            }

            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (input is null || !File.Exists(input))
{
    Console.Error.WriteLine($"The NASA map was not found: {input ?? "(none given)"}. See the header of this file for where to get it.");
    return 1;
}

if (output is null)
{
    var root = FindRepoRoot(Directory.GetCurrentDirectory());
    if (root is null)
    {
        Console.Error.WriteLine("Run from inside the repository, or pass --output.");
        return 1;
    }

    output = Path.Combine(root, "src", "TianWen.UI.Gui", "Resources", "milkyway.bgra.lz");
}

var sw = Stopwatch.StartNew();
var (sourceWidth, sourceHeight, rgb) = ExrImageCodec.DecodeRgbHalf(File.ReadAllBytes(input));
Console.WriteLine($"Read {input}: {sourceWidth}x{sourceHeight} in {sw.Elapsed.TotalSeconds:F1}s");

var bgra = MilkyWaySvsImport.ToBgra(rgb, sourceWidth, sourceHeight, opts);
Console.WriteLine($"Converted to {opts.Width}x{opts.Height} (scale {opts.Scale}, black point {opts.BlackPoint}) in {sw.Elapsed.TotalSeconds:F1}s");

var rawPath = output.EndsWith(".lz") ? output[..^3] : output + ".raw";
MilkyWayTextureBaker.WriteRaw(rawPath, opts.Width, opts.Height, bgra);

// Level 9 + 2 MiB members, as the Tycho-2 bake writes it: the multi-member layout lets SkyMapTab's loader decode
// the blob across cores.
var raw = File.ReadAllBytes(rawPath);
File.WriteAllBytes(output, LzipEncoder.Compress(raw, new LzipOptions { Level = 9, MemberSize = 2 << 20 }));
File.Delete(rawPath);
Console.WriteLine($"Wrote {output}: {new FileInfo(output).Length:N0} bytes in {sw.Elapsed.TotalSeconds:F1}s");
return 0;

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "TianWen.UI.Gui", "Resources")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}
