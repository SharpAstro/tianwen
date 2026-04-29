using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Targets <see cref="Image.FindStarsAsync"/> on real
/// <see cref="SyntheticStarFieldRenderer"/> output at IMX455 size, mirroring
/// the polar-alignment Phase A path exactly. The original
/// <see cref="FindStarsBenchmarks"/> uses a hand-rolled circular falloff
/// that doesn't match the renderer's Gaussian PSF + xorshift background, so
/// it can't tell us whether a flux/aperture-scaling change in the synth
/// blows the FindStars wall-clock past
/// <see cref="Sequencing.PolarAlignment.AdaptiveExposureRamp"/>'s per-rung
/// budget.
///
/// Each row is the polar-align rung at (aperture, exposure). Aperture-scale
/// is <c>(D_mm / 50)^2</c> (collecting-area ratio against the 50mm reference
/// the synth's <c>flux = 10000 * 10^-0.4(m-5) * t * scale</c> is calibrated
/// against). So:
/// <list type="bullet">
///   <item><description>scale = 1.0 -> 50mm mini-guider</description></item>
///   <item><description>scale = 5.76 -> 120mm (f/5 fallback when OTA aperture
///     is unset for a 600mm focal length)</description></item>
///   <item><description>scale = 16 -> 200mm (the user's f/3 600mm setup)</description></item>
/// </list>
///
/// We seed a 677-star projected list at roughly the SCP density that
/// CatalogPlateSolver actually projects on the IMX455 polar preview, then
/// time FindStarsAsync at <c>snrMin=5, maxStars=500, maxRetries=0</c>
/// (matches <see cref="Astrometry.PlateSolve.CatalogPlateSolver"/>'s call).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FindStarsSynthBenchmarks
{
    private const int Width = 9576;
    private const int Height = 6388;
    // Mirror FakeCameraDriver's MaxSynthStars cap so this bench measures the
    // production scenario, not the unbounded "dump everything in the FOV"
    // case that exposed the deblending pathology in the first place.
    private const int ProjectedStars = 150;

    // Image stores its FindStarsAsync result in a per-instance cache keyed on
    // the call signature, so calling it twice with the same args returns in
    // ~300 ns (cache hit). The benchmark therefore stores raw float buffers,
    // not Image instances, and wraps a fresh Image around the same buffer per
    // benchmark invocation -- new instance = empty cache = real work measured.
    // Buffers are immutable (renderer wrote them once at GlobalSetup) so it's
    // safe to share them across the throwaway wrappers.
    private float[,] _data50mm_150ms = null!;
    private float[,] _data200mm_100ms = null!;
    private float[,] _data200mm_200ms = null!;
    private float[,] _data200mm_500ms = null!;
    private float[,] _data200mm_1000ms = null!;
    private float[,] _data200mm_5000ms = null!;
    private float _max50mm_150ms, _max200mm_100ms, _max200mm_200ms,
                  _max200mm_500ms, _max200mm_1000ms, _max200mm_5000ms;
    private float _min50mm_150ms, _min200mm_100ms, _min200mm_200ms,
                  _min200mm_500ms, _min200mm_1000ms, _min200mm_5000ms;

    [GlobalSetup]
    public void Setup()
    {
        // Reuse one stable star list across all scenes so the only thing
        // varying between rows is exposure + aperture, matching what the
        // polar-align ramp does (same target, same FOV, same projected list,
        // different rung settings). Magnitudes are drawn from a roughly
        // Tycho-2 power-law distribution (more faint stars than bright) so
        // the SNR-derived cutoff actually moves the bright/faint balance
        // across rungs.
        var stars = BuildProjectedStars(ProjectedStars, seed: 42);

        (_data50mm_150ms, _min50mm_150ms, _max50mm_150ms) = RenderToBuffer(stars, 0.15, 1.0);
        (_data200mm_100ms, _min200mm_100ms, _max200mm_100ms) = RenderToBuffer(stars, 0.1, 16.0);
        (_data200mm_200ms, _min200mm_200ms, _max200mm_200ms) = RenderToBuffer(stars, 0.2, 16.0);
        (_data200mm_500ms, _min200mm_500ms, _max200mm_500ms) = RenderToBuffer(stars, 0.5, 16.0);
        (_data200mm_1000ms, _min200mm_1000ms, _max200mm_1000ms) = RenderToBuffer(stars, 1.0, 16.0);
        (_data200mm_5000ms, _min200mm_5000ms, _max200mm_5000ms) = RenderToBuffer(stars, 5.0, 16.0);
    }

    private static Image WrapImage(float[,] data, float min, float max)
    {
        // FakeCameraDriver produces BitDepth.Int16 in production, NOT Float32.
        // Float32 routes Background() / Histogram() through a different
        // normalisation path that miscalibrates the sky-mode bin for synth
        // data with peak ADU << 65535, returning background=4.39 instead of
        // ~10 -- which makes every noise pixel pass the detection threshold.
        // Int16 with maxValue=65535 matches the Image construction path
        // FakeCameraDriver uses internally and the FindStars hot path tested.
        // Suppress the unused parameters to keep the public bench API tidy.
        _ = min; _ = max;
        var channel = new float[1][,];
        channel[0] = data;
        return new Image(
            channel, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta(
                "", default, default, FrameType.Light, "",
                3.76f, 3.76f, 800, 0, default,
                1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, 0f, 0f));
    }

    private static ProjectedStar[] BuildProjectedStars(int count, int seed)
    {
        // Power-law magnitude distribution: dN/dm ~ 10^(0.4*m). Inverse-CDF
        // sampling on the log10 of a uniform draw gives a ~Tycho-2-shaped
        // distribution between mag 5 (bright outliers) and mag 13 (well past
        // the SNR=5 detection floor at 5s/200mm).
        var rng = new Random(seed);
        var arr = new ProjectedStar[count];
        for (var i = 0; i < count; i++)
        {
            var u = rng.NextDouble();
            var mag = 5.0 + 8.0 * Math.Pow(u, 0.4);
            var x = rng.NextDouble() * (Width - 40) + 20;
            var y = rng.NextDouble() * (Height - 40) + 20;
            arr[i] = new ProjectedStar(x, y, mag);
        }
        return arr;
    }

    private static (float[,] Data, float Min, float Max) RenderToBuffer(ProjectedStar[] stars, double exposureSec, double apertureScale)
    {
        var data = SyntheticStarFieldRenderer.Render(
            width: Width, height: Height, defocusSteps: 0,
            stars: stars,
            exposureSeconds: exposureSec,
            apertureScaleFactor: apertureScale,
            seed: 42, noiseSeed: 7);

        // Compute min/max so MTF / SNR scaling has the same MaxValue floor
        // FindStarsAsync sees in production. Using a fixed maxValue=65535 with
        // a synth peak of ~250 ADU would skew the noise floor estimate.
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        return (data, min, max);
    }

    // Polar-align config: snrMin=5, maxStars=500, maxRetries=0.

    [Benchmark(Baseline = true, Description = "50mm/150ms (test rig - rung 2 of integration test)")]
    public async Task<int> A_50mm_150ms()
    {
        var img = WrapImage(_data50mm_150ms, _min50mm_150ms, _max50mm_150ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }

    [Benchmark(Description = "200mm f/3, 100ms (rung 1, was CANCELLED)")]
    public async Task<int> B_200mm_100ms()
    {
        var img = WrapImage(_data200mm_100ms, _min200mm_100ms, _max200mm_100ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }

    [Benchmark(Description = "200mm f/3, 200ms (rung 3, was CANCELLED)")]
    public async Task<int> C_200mm_200ms()
    {
        var img = WrapImage(_data200mm_200ms, _min200mm_200ms, _max200mm_200ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }

    [Benchmark(Description = "200mm f/3, 500ms (rung 5, was CANCELLED)")]
    public async Task<int> D_200mm_500ms()
    {
        var img = WrapImage(_data200mm_500ms, _min200mm_500ms, _max200mm_500ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }

    [Benchmark(Description = "200mm f/3, 1000ms (rung 6, was CANCELLED)")]
    public async Task<int> E_200mm_1000ms()
    {
        var img = WrapImage(_data200mm_1000ms, _min200mm_1000ms, _max200mm_1000ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }

    [Benchmark(Description = "200mm f/3, 5000ms (rung 8, ran 22-27s in GUI)")]
    public async Task<int> F_200mm_5000ms()
    {
        var img = WrapImage(_data200mm_5000ms, _min200mm_5000ms, _max200mm_5000ms);
        var stars = await img.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0);
        return stars.Count;
    }
}
