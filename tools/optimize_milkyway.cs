#:sdk Microsoft.NET.Sdk
#:project ../src/TianWen.Lib/TianWen.Lib.csproj

// Genetic-algorithm optimiser for Milky Way texture parameters.
//
// Drives MilkyWayTextureBaker with varying MilkyWayBakerOptions and scores
// each candidate against Stellarium's photographic milkyway.png. The score
// is deliberately loose, per the user: we want the candidate to look
// Stellarium-ish in structure + colour, but happily allow extra diffuse
// signal off the galactic plane (= faint nebulosity Stellarium doesn't
// have).
//
// Usage (run from the tianwen repo root):
//   dotnet run tools/optimize_milkyway.cs -- \
//       --reference ../../other/stellarium/textures/milkyway.png \
//       --luminance tools/data/radiance_2048.f32 \
//       --dust-opacity tools/data/dust_2048.f32
//
// Key flags (all optional):
//   --reference PATH    Stellarium milkyway.png (defaults to the common repo neighbour path)
//   --luminance PATH    Planck radiance float32 (defaults to tools/data/radiance_2048.f32)
//   --dust-opacity PATH Planck dust opacity float32 (defaults to tools/data/dust_2048.f32)
//   --pop N             population size (default 12)
//   --gens N            generations (default 15)
//   --seed N            RNG seed (default 42)
//   --output-dir DIR    where to drop top candidates as PNG (default tools/data/ga_out)
//   --keep-top N        how many top candidates to write at the end (default 3)
//
// Output: prints the best candidate's CLI-ready parameter line after each
// generation, writes the top-N candidates as PNG to --output-dir, and
// prints a final summary with a ready-to-run `bake_milkyway.ps1` command.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Extensions;

var width = 2048;
var height = 1024;
string? referencePath = null;
string? luminancePath = null;
string? dustPath = null;
string? outputDir = null;
var populationSize = 12;
var generations = 15;
var seed = 42;
var keepTop = 3;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--width": width = int.Parse(args[++i]); break;
        case "--height": height = int.Parse(args[++i]); break;
        case "--reference": referencePath = args[++i]; break;
        case "--luminance": luminancePath = args[++i]; break;
        case "--dust-opacity": dustPath = args[++i]; break;
        case "--pop": populationSize = int.Parse(args[++i]); break;
        case "--gens": generations = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--output-dir": outputDir = args[++i]; break;
        case "--keep-top": keepTop = int.Parse(args[++i]); break;
        case "--help" or "-h":
            Console.WriteLine("See tool header for usage.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

var repoRoot = FindRepoRoot(Environment.CurrentDirectory)
               ?? FindRepoRoot(AppContext.BaseDirectory)
               ?? throw new DirectoryNotFoundException("Could not find repo root.");

referencePath ??= Path.Combine(repoRoot, "..", "..", "other", "stellarium", "textures", "milkyway.png");
luminancePath ??= Path.Combine(repoRoot, "tools", "data", $"radiance_{width}.f32");
dustPath ??= Path.Combine(repoRoot, "tools", "data", $"dust_{width}.f32");
outputDir ??= Path.Combine(repoRoot, "tools", "data", "ga_out");

if (!File.Exists(referencePath))
{
    Console.Error.WriteLine($"Reference image not found: {referencePath}");
    return 1;
}
if (!File.Exists(luminancePath))
{
    Console.Error.WriteLine($"Luminance map not found: {luminancePath}. Run bake_milkyway.ps1 first to cache it.");
    return 1;
}
if (!File.Exists(dustPath))
{
    Console.Error.WriteLine($"Dust opacity map not found: {dustPath}. Run bake_milkyway.ps1 first to cache it.");
    return 1;
}

Directory.CreateDirectory(outputDir);

// -------------------------------------------------------------------------
// Load the Stellarium reference and compute a per-pixel luma + colour-ratio
// descriptor we compare against. Downsample to 512x256 for faster scoring
// (the GA needs the coarse impression, not pixel-exact matching).
// -------------------------------------------------------------------------
var scoreW = 512;
var scoreH = 256;
Console.WriteLine($"Loading reference image {referencePath} (scored at {scoreW}x{scoreH})");
var refDescriptor = LoadReferenceDescriptor(referencePath, scoreW, scoreH);

// -------------------------------------------------------------------------
// Pre-load catalog + survey rasters once.
// -------------------------------------------------------------------------
Console.WriteLine("Loading Tycho-2 catalog + Planck radiance + Planck dust opacity ...");
var sw = Stopwatch.StartNew();
using var sp = new ServiceCollection().AddAstrometry().BuildServiceProvider();
var db = sp.GetRequiredService<ICelestialObjectDB>();
await db.InitDBAsync(CancellationToken.None);
var inputs = await MilkyWayBakerInputs.LoadAsync(db, width, height, luminancePath, dustPath, CancellationToken.None);
Console.WriteLine($"Loaded {inputs.Stars.Length} stars in {sw.Elapsed.TotalSeconds:F1}s");

// -------------------------------------------------------------------------
// GA state. Each individual is a float[9] in the same order as Genes.
// -------------------------------------------------------------------------
var rng = new Random(seed);
var population = new List<Genes>(populationSize);

// Seed generation 0 with the known-good defaults as individual 0 so we
// never regress below the current production bake.
population.Add(Genes.Defaults());
while (population.Count < populationSize)
{
    population.Add(Genes.Random(rng));
}

var bgraBuf = new byte[width * height * 4];
var candidateLuma = new float[scoreW * scoreH];
var candidateR = new float[scoreW * scoreH];
var candidateG = new float[scoreW * scoreH];
var candidateB = new float[scoreW * scoreH];

var scored = new (Genes Genes, double Fitness)[populationSize];

for (var gen = 0; gen < generations; gen++)
{
    sw.Restart();
    for (var i = 0; i < populationSize; i++)
    {
        var g = population[i];
        var opts = g.ToOptions(width, height);
        MilkyWayTextureBaker.Bake(inputs, opts, bgraBuf);
        DescribeCandidate(bgraBuf, width, height, scoreW, scoreH, candidateLuma, candidateR, candidateG, candidateB);
        var fitness = ComputeFitness(candidateLuma, candidateR, candidateG, candidateB, refDescriptor, scoreW, scoreH);
        scored[i] = (g, fitness);
    }

    Array.Sort(scored, (a, b) => b.Fitness.CompareTo(a.Fitness));

    Console.WriteLine($"Gen {gen,2} ({sw.Elapsed.TotalSeconds:F0}s)  " +
                      $"best={scored[0].Fitness:F3}  median={scored[populationSize / 2].Fitness:F3}  " +
                      $"worst={scored[^1].Fitness:F3}");
    Console.WriteLine($"          best: {scored[0].Genes.ToCliArgs()}");

    if (gen == generations - 1) break;

    // Elitism: keep the top 2 unchanged.
    var next = new List<Genes>(populationSize)
    {
        scored[0].Genes,
        scored[1].Genes,
    };
    while (next.Count < populationSize)
    {
        var p1 = TournamentSelect(scored, rng, k: 3);
        var p2 = TournamentSelect(scored, rng, k: 3);
        var child = Genes.Crossover(p1, p2, rng);
        child = child.Mutate(rng, mutationRate: 0.2, sigma: 0.15);
        next.Add(child);
    }
    population = next;
}

// -------------------------------------------------------------------------
// Write top-N PNGs.
// -------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"Writing top-{keepTop} candidates to {outputDir}");
for (var i = 0; i < Math.Min(keepTop, populationSize); i++)
{
    var g = scored[i].Genes;
    var opts = g.ToOptions(width, height);
    MilkyWayTextureBaker.Bake(inputs, opts, bgraBuf);
    var pngPath = Path.Combine(outputDir, $"candidate_{i:D2}_fit{scored[i].Fitness:F3}.png");
    WritePng(pngPath, bgraBuf, width, height);
    var argsPath = Path.Combine(outputDir, $"candidate_{i:D2}.args.txt");
    File.WriteAllText(argsPath, g.ToCliArgs() + Environment.NewLine);
    Console.WriteLine($"  #{i}: fitness={scored[i].Fitness:F3}  {pngPath}");
}
Console.WriteLine();
Console.WriteLine("To rebake the overall best with the production pipeline:");
Console.WriteLine($"  pwsh tools/bake_milkyway.ps1 {scored[0].Genes.ToPwshArgs()}");
return 0;

// =========================================================================
// Helpers
// =========================================================================

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

static ReferenceDescriptor LoadReferenceDescriptor(string path, int scoreW, int scoreH)
{
    using var image = new MagickImage(path);
    image.Resize(new MagickGeometry((uint)scoreW, (uint)scoreH) { IgnoreAspectRatio = true });
    var pixels = image.GetPixelsUnsafe();
    var luma = new float[scoreW * scoreH];
    var rCh = new float[scoreW * scoreH];
    var gCh = new float[scoreW * scoreH];
    var bCh = new float[scoreW * scoreH];
    for (var y = 0; y < scoreH; y++)
    {
        for (var x = 0; x < scoreW; x++)
        {
            var idx = y * scoreW + x;
            var px = pixels.GetPixel(x, y);
            var r = px[0] / 65535f;
            var g = px[1] / 65535f;
            var b = px[2] / 65535f;
            rCh[idx] = r;
            gCh[idx] = g;
            bCh[idx] = b;
            luma[idx] = 0.299f * r + 0.587f * g + 0.114f * b;
        }
    }
    return new ReferenceDescriptor(luma, rCh, gCh, bCh);
}

// Downsample + extract luma/RGB from the candidate BGRA buffer.
static void DescribeCandidate(
    byte[] bgra, int width, int height, int scoreW, int scoreH,
    float[] luma, float[] rCh, float[] gCh, float[] bCh)
{
    // Simple box downsample: average RGB over each (width/scoreW) x (height/scoreH) block.
    var sx = width / scoreW;
    var sy = height / scoreH;
    var blockSize = sx * sy;
    var inv = 1f / (blockSize * 255f);

    for (var y = 0; y < scoreH; y++)
    {
        for (var x = 0; x < scoreW; x++)
        {
            var rSum = 0;
            var gSum = 0;
            var bSum = 0;
            var y0 = y * sy;
            var x0 = x * sx;
            for (var dy = 0; dy < sy; dy++)
            {
                var row = (y0 + dy) * width;
                for (var dx = 0; dx < sx; dx++)
                {
                    var offset = (row + x0 + dx) * 4;
                    bSum += bgra[offset + 0];
                    gSum += bgra[offset + 1];
                    rSum += bgra[offset + 2];
                }
            }
            var idx = y * scoreW + x;
            var r = rSum * inv;
            var g = gSum * inv;
            var b = bSum * inv;
            rCh[idx] = r;
            gCh[idx] = g;
            bCh[idx] = b;
            luma[idx] = 0.299f * r + 0.587f * g + 0.114f * b;
        }
    }
}

// Loose fitness: reward Stellarium-like luma in bright regions, allow extra
// glow in dark regions (nebulosity preservation), reward some colour match.
// Higher = better. Returns roughly [-1, 1] depending on tuning.
static double ComputeFitness(
    float[] candLuma, float[] candR, float[] candG, float[] candB,
    ReferenceDescriptor refd, int w, int h)
{
    var bright = 0.0;
    var brightCount = 0;
    var darkPenalty = 0.0;
    var darkBonus = 0.0;
    var darkCount = 0;
    var colorDiff = 0.0;
    var colorCount = 0;

    // Stellarium brightness split: top 50 % of pixels are "structure"
    // (bright galactic plane + bulge), bottom 50 % are "dark" (off plane).
    var sortedRef = (float[])refd.Luma.Clone();
    Array.Sort(sortedRef);
    var brightThreshold = sortedRef[(int)(sortedRef.Length * 0.5)];

    for (var i = 0; i < candLuma.Length; i++)
    {
        var refL = refd.Luma[i];
        var candL = candLuma[i];

        if (refL >= brightThreshold)
        {
            // Bright zone: reward small abs diff in luma.
            bright += Math.Abs(refL - candL);
            brightCount++;

            // Colour: compare per-channel ratio to luma (hue proxy), skipping
            // extremely dark pixels to avoid divide-by-zero noise.
            if (refL > 0.02f && candL > 0.02f)
            {
                var refDr = refd.R[i] / refL - 1f;
                var refDg = refd.G[i] / refL - 1f;
                var refDb = refd.B[i] / refL - 1f;
                var cDr = candR[i] / candL - 1f;
                var cDg = candG[i] / candL - 1f;
                var cDb = candB[i] / candL - 1f;
                colorDiff += Math.Abs(refDr - cDr) + Math.Abs(refDg - cDg) + Math.Abs(refDb - cDb);
                colorCount++;
            }
        }
        else
        {
            // Dark zone: only penalise if candidate is MORE dark than reference
            // (= we've lost structure/nebulosity Stellarium has). Allow up to
            // refL + 0.08 extra signal as preserved nebulosity (rewarded lightly).
            var delta = candL - refL;
            if (delta < 0)
            {
                darkPenalty += -delta;
            }
            else
            {
                darkBonus += Math.Min(delta, 0.08);
            }
            darkCount++;
        }
    }

    var brightScore = brightCount > 0 ? 1.0 - bright / brightCount : 0.0;
    var darkScore = darkCount > 0 ? 1.0 - darkPenalty / darkCount : 0.0;
    var nebulosityBonus = darkCount > 0 ? darkBonus / darkCount : 0.0;
    var colorScore = colorCount > 0 ? 1.0 - Math.Min(1.0, colorDiff / (3.0 * colorCount)) : 0.0;

    // Weights — structure dominates, nebulosity is a gentle pull, colour is secondary.
    return 0.45 * brightScore + 0.30 * darkScore + 0.15 * colorScore + 0.10 * nebulosityBonus;
}

static Genes TournamentSelect((Genes Genes, double Fitness)[] scored, Random rng, int k)
{
    var best = scored[rng.Next(scored.Length)];
    for (var i = 1; i < k; i++)
    {
        var challenger = scored[rng.Next(scored.Length)];
        if (challenger.Fitness > best.Fitness) best = challenger;
    }
    return best.Genes;
}

static void WritePng(string path, byte[] bgra, int width, int height)
{
    // ReadPixels path: raw byte buffer in BGRA order, 8-bit per channel.
    var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA);
    using var image = new MagickImage(bgra, settings);
    image.Write(path, MagickFormat.Png);
}

// =========================================================================
// Types
// =========================================================================

record ReferenceDescriptor(float[] Luma, float[] R, float[] G, float[] B);

record Genes(
    float MinMag, float ExtinctionK, float BlurSigma, float ColorBlur,
    float Saturation, float Warmth, float DustReddening, float LuminanceMix,
    float Brightness)
{
    // Parameter bounds for random init + mutation clamp. Kept loose so the
    // GA can explore, but not so wide that obviously-broken values dominate
    // early generations.
    public static readonly (float Min, float Max)[] Bounds =
    [
        (7.0f, 10.0f),   // MinMag
        (2.0f, 20.0f),   // ExtinctionK
        (0.3f, 4.0f),    // BlurSigma
        (2.0f, 15.0f),   // ColorBlur
        (0.5f, 4.0f),    // Saturation
        (-0.2f, 0.6f),   // Warmth
        (0.0f, 0.8f),    // DustReddening
        (0.5f, 1.0f),    // LuminanceMix
        (0.2f, 1.5f),    // Brightness
    ];

    public static Genes Defaults() => new(8.5f, 10.0f, 1.0f, 6.0f, 2.0f, 0.25f, 0.3f, 0.85f, 0.5f);

    public static Genes Random(Random rng)
    {
        var v = new float[9];
        for (var i = 0; i < 9; i++)
        {
            var (lo, hi) = Bounds[i];
            v[i] = lo + (float)rng.NextDouble() * (hi - lo);
        }
        return FromArray(v);
    }

    public float[] ToArray() => [MinMag, ExtinctionK, BlurSigma, ColorBlur, Saturation, Warmth, DustReddening, LuminanceMix, Brightness];

    public static Genes FromArray(float[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);

    public static Genes Crossover(Genes a, Genes b, Random rng)
    {
        var av = a.ToArray();
        var bv = b.ToArray();
        var c = new float[9];
        for (var i = 0; i < 9; i++)
        {
            c[i] = rng.NextDouble() < 0.5 ? av[i] : bv[i];
        }
        return FromArray(c);
    }

    public Genes Mutate(Random rng, double mutationRate, double sigma)
    {
        var v = ToArray();
        for (var i = 0; i < 9; i++)
        {
            if (rng.NextDouble() >= mutationRate) continue;
            var (lo, hi) = Bounds[i];
            var range = hi - lo;
            // Box-Muller gaussian.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            v[i] = (float)Math.Max(lo, Math.Min(hi, v[i] + n * sigma * range));
        }
        return FromArray(v);
    }

    public MilkyWayBakerOptions ToOptions(int width, int height) => new(
        Width: width,
        Height: height,
        MinMagnitude: MinMag,
        ExtinctionK: ExtinctionK,
        BlurSigma: BlurSigma,
        ColorBlurSigma: ColorBlur,
        ColorSaturation: Saturation,
        ColorWarmth: Warmth,
        DustReddening: DustReddening,
        LuminanceMix: LuminanceMix,
        BrightnessScale: Brightness);

    public string ToCliArgs() =>
        $"--min-mag {MinMag:F2} --k {ExtinctionK:F2} --blur-sigma {BlurSigma:F2} " +
        $"--color-blur {ColorBlur:F2} --saturation {Saturation:F2} --warmth {Warmth:F3} " +
        $"--dust-reddening {DustReddening:F3} --luminance-mix {LuminanceMix:F3} " +
        $"--brightness {Brightness:F3}";

    public string ToPwshArgs() =>
        $"-MinMag {MinMag:F2} -K {ExtinctionK:F2} -BlurSigma {BlurSigma:F2} " +
        $"-ColorBlur {ColorBlur:F2} -Saturation {Saturation:F2} -Warmth {Warmth:F3} " +
        $"-DustReddening {DustReddening:F3} -LuminanceMix {LuminanceMix:F3} " +
        $"-Brightness {Brightness:F3}";
}
