using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Targets <see cref="Image.FindStarsAsync"/> on plate-solve-shaped frames.
///
/// The general <c>ImageBenchmarks.FindStars_*</c> covers small (1280) and
/// medium (4096) mono / color frames at <c>snrMin = 10</c> with ~50 stars
/// sprinkled in. That matches the live-preview scoring path well, but tells
/// us nothing about the plate-solver path which:
/// <list type="bullet">
///   <item><description>Uses <c>snrMin = 5</c> (lower threshold to find faint
///     stars at the polar caps where Tycho-2 itself thins out).</description></item>
///   <item><description>Runs against full IMX455M-class sensors (9576 x 6388
///     = 61 megapixels) -- the user's polar-alignment fake camera.</description></item>
///   <item><description>Hits the <c>maxStars</c> retry loop hard on sparse
///     fields: when fewer than 500 detections are returned by the first pass
///     <see cref="Image.FindStarsAsync"/> re-scans every pixel up to two more
///     times at lower detection thresholds, tripling wall time on a frame
///     that already takes seconds.</description></item>
/// </list>
/// This benchmark exists to (a) catch a regression in any of those paths
/// and (b) give us a calibrated number we can quote when deciding whether to
/// downsample (target ~1.5 arcsec/px) or cache the StarList on the Image.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FindStarsBenchmarks
{
    private Image _smallSparseImage = null!;
    private Image _smallDenseImage = null!;
    private Image _largeSparseImage = null!;
    private Image _largeDenseImage = null!;
    // Real IMX533M frame from the SVBONY SV605CC test session -- 3008x3008
    // mono Bayer (RGGB), 60s exposure of the Statue of Liberty Nebula. Loaded
    // and debayered once in GlobalSetup so per-iteration cost is FindStars only.
    private Image _imx533RealDebayered = null!;

    /// <summary>
    /// Image.FindStarsAsync memoizes results keyed on (channel, snrMin, maxStars,
    /// minStars, maxRetries). Without invalidation BDN's per-iteration calls
    /// after the first one would just return the cached StarList, reporting
    /// sub-microsecond timings instead of real detection cost. Invalidating
    /// per iteration restores the measurement.
    /// </summary>
    [IterationCleanup]
    public void InvalidateCaches()
    {
        _smallSparseImage?.InvalidateStarListCache();
        _smallDenseImage?.InvalidateStarListCache();
        _largeSparseImage?.InvalidateStarListCache();
        _largeDenseImage?.InvalidateStarListCache();
        _imx533RealDebayered?.InvalidateStarListCache();
    }

    [GlobalSetup]
    public void Setup()
    {
        // 1280 x 960 -- small sensor, e.g. guide camera (IMX178M class).
        _smallSparseImage = BuildImage(width: 1280, height: 960, starCount: 40, seed: 1);
        _smallDenseImage = BuildImage(width: 1280, height: 960, starCount: 400, seed: 2);

        // 9576 x 6388 -- IMX455M, full main-camera frame. Plate solving on the
        // polar-alignment preview does this exact size at snrMin=5.
        _largeSparseImage = BuildImage(width: 9576, height: 6388, starCount: 40, seed: 3);
        _largeDenseImage = BuildImage(width: 9576, height: 6388, starCount: 400, seed: 4);

        // Real IMX533M frame (3008x3008 OSC). Decompress gz -> parse FITS ->
        // VNG debayer once, then keep the result for the benchmarks to slap
        // FindStars on. Loaded via the same path the stacking runner uses so
        // numbers are comparable.
        _imx533RealDebayered = LoadRealImx533Debayered();
    }

    private static Image LoadRealImx533Debayered()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FITS", "imx533_sv605cc_60s_real.fits.gz");
        // Decompress to a temp file because TryReadFitsFile(string) is the
        // simplest path -- avoids manual nom.tam.fits stream wiring here.
        var tempPath = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks_imx533_real.fits");
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            using var fileStream = File.OpenRead(path);
            using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
            using var outStream = File.Create(tempPath);
            gz.CopyTo(outStream);
        }
        if (!Image.TryReadFitsFile(tempPath, out var raw))
        {
            throw new InvalidDataException($"Failed to parse FITS at {tempPath}");
        }
        // VNG to match the stacking runner. Synchronous wait is fine in
        // GlobalSetup -- runs once before BDN starts iterating.
        return raw.DebayerAsync(DebayerAlgorithm.VNG).GetAwaiter().GetResult();
    }

    private static Image BuildImage(int width, int height, int starCount, int seed)
    {
        var rng = new Random(seed);
        var data = new float[1][,];
        data[0] = new float[height, width];
        FillRealisticChannel(data[0], rng, background: 1000f, noise: 30f, starCount: starCount);

        return new Image(
            data, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta(
                "", default, default, FrameType.Light, "",
                3.76f, 3.76f, 800, 0, default,
                1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, 0f, 0f));
    }

    private static void FillRealisticChannel(float[,] channel, Random rng, float background, float noise, int starCount)
    {
        var height = channel.GetLength(0);
        var width = channel.GetLength(1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var u1 = (float)rng.NextDouble();
                var u2 = (float)rng.NextDouble();
                var z = MathF.Sqrt(-2f * MathF.Log(u1 + 1e-10f)) * MathF.Cos(2f * MathF.PI * u2);
                channel[y, x] = Math.Clamp(background + noise * z, 0f, 65535f);
            }
        }

        for (var i = 0; i < starCount; i++)
        {
            var cx = rng.Next(20, width - 20);
            var cy = rng.Next(20, height - 20);
            var brightness = 5000f + (float)rng.NextDouble() * 55000f;
            var radius = 2 + rng.Next(5);

            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var dist2 = dx * dx + dy * dy;
                    if (dist2 <= radius * radius)
                    {
                        var falloff = 1f - (float)dist2 / (radius * radius);
                        var py = cy + dy;
                        var px = cx + dx;
                        channel[py, px] = Math.Clamp(channel[py, px] + brightness * falloff, 0f, 65535f);
                    }
                }
            }
        }
    }

    // Plate-solver path: snrMin=5, maxStars=500. The lower SNR threshold +
    // sparse fields trigger the full retry loop.
    [Benchmark]
    public Task<StarList> Small_Sparse_PlateSolveCfg() =>
        _smallSparseImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500);

    [Benchmark]
    public Task<StarList> Small_Dense_PlateSolveCfg() =>
        _smallDenseImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500);

    [Benchmark]
    public Task<StarList> Large_Sparse_PlateSolveCfg() =>
        _largeSparseImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500);

    [Benchmark]
    public Task<StarList> Large_Dense_PlateSolveCfg() =>
        _largeDenseImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500);

    // Live-preview path: snrMin=10 (existing default; included for comparison).
    [Benchmark]
    public Task<StarList> Large_Sparse_PreviewCfg() =>
        _largeSparseImage.FindStarsAsync(0, snrMin: 10f, maxStars: 500);

    // Real IMX533M frame, three configs:
    // 1) Default: snrMin=10, minStars=500. First-pass detection exits with
    //    ~1300 stars on these dense Milky Way fields.
    // 2) Runner: snrMin=5, minStars=2000. Forces a 2nd retry-pass at
    //    detection_level/4.3, pulling ~6500 stars. This is what the stacking
    //    pipeline uses.
    // 3) Preview: snrMin=10, default cfg, exits on pass 1. Equivalent to (1)
    //    but kept distinct for symmetry with the synthetic Large_* variants.
    [Benchmark]
    public Task<StarList> Real_IMX533_DefaultCfg() =>
        _imx533RealDebayered.FindStarsAsync(0, snrMin: 10f, maxStars: 500);

    [Benchmark]
    public Task<StarList> Real_IMX533_RunnerCfg() =>
        _imx533RealDebayered.FindStarsAsync(0, snrMin: 5f, minStars: 2000);

    [Benchmark]
    public Task<StarList> Real_IMX533_PlateSolveCfg() =>
        _imx533RealDebayered.FindStarsAsync(0, snrMin: 5f, maxStars: 500);
}
