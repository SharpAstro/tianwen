using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// The per-pixel loops in <c>Image.*.cs</c> that still read a plane through <c>float[,]</c>
/// indexing, priced single-threaded so the number is the loop and not the scheduler.
/// </summary>
/// <remarks>
/// <para><b>Why the spelling of a plane read is worth a benchmark.</b> A 3024 x 3025 microbenchmark
/// (2026-09-15, win-arm64) put a 3x3 stencil at 28.7 ms through <c>[y, x]</c> on a <c>float[,]</c>,
/// 19.4 ms through <c>y * w + x</c> on a flat <c>float[]</c>, and 11.8 ms through row slices of a
/// span over the SAME <c>float[,]</c>, under the AOT that ships; a bilinear gather moved 17.5 to
/// 14.7 and a plain stream not at all. So the storage type is not the lever and never was -- the
/// row-sliced span beats a native flat array, because a bounded slice is what lets the compiler drop
/// the checks -- and the cost is entirely in loops still spelled <c>[y, x]</c>. These three are the
/// ones on a hot path.</para>
/// <para><c>Lanczos3</c> is the DEFAULT stack warp kernel since 7.1, 36 taps per destination pixel;
/// <c>LumaStats</c> is the colour document's stretch statistic and read every plane through the
/// residency accessor per sample; <c>SplitMergeCfa</c> is the per-photosite split every mosaic
/// pass (background extraction, planetary) starts and ends with.</para>
/// </remarks>
[MemoryDiagnoser]
public class PlaneAccessBenchmarks
{
    private float[,] _plane = null!;
    private Image _color = null!;
    private Image _mosaic = null!;

    [Params(1024, 2048)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _plane = MakePlane(Size, rng);
        _color = Make(3, Size, rng, SensorType.Color);
        _mosaic = Make(1, Size, rng, SensorType.RGGB);
    }

    /// <summary>Every destination pixel of a <see cref="Size"/>-square grid sampled at a fractional
    /// offset, so all 36 taps are live and none takes the integer shortcut.</summary>
    [Benchmark]
    public float Lanczos3()
    {
        var s = 0f;
        var n = Size - 6;
        for (var y = 3; y < n; y++)
        {
            for (var x = 3; x < n; x++)
            {
                s += Image.Lanczos3Value(_plane, x + 0.37f, y + 0.61f, Image.LanczosClampingThreshold);
            }
        }

        return s;
    }

    [Benchmark]
    public async Task<(float, float, float)> LumaStats() => await _color.GetLumaStretchStatsAsync();

    [Benchmark]
    public Image SplitMergeCfa() => _mosaic.SplitBayerChannels().MergeBayerChannels();

    private static float[,] MakePlane(int size, Random rng)
    {
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y, x] = 500f + (float)rng.NextDouble() * 30f;
            }
        }

        return plane;
    }

    private static Image Make(int channelCount, int size, Random rng, SensorType sensorType)
    {
        var planes = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            planes[c] = MakePlane(size, rng);
        }

        return new Image(planes, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("", default, default, FrameType.Light, "", 0, 0, 0, 0, default, 1, 1, float.NaN,
                sensorType, 0, 0, RowOrder.TopDown, 0f, 0f));
    }
}
