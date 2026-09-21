using System;
using BenchmarkDotNet.Attributes;
using CommunityToolkit.HighPerformance;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// What narrowing a plane into its FITS container costs, per SAMPLE against per ROW.
///
/// <para>The question is whether hoisting is worth anything here, and it has to be answered with a
/// multiplier rather than asserted: <see cref="FitsSampleStorage.ToRaw(float)"/> derives the
/// container bounds from a switch, converts the offset and tests two modes, all of which are the
/// same answer for every pixel of the plane. <see cref="FitsSampleStorage.Narrow{T}"/> lifts them
/// out. One 3072x3060x3 map is 28 million samples, so a few nanoseconds either way is the
/// difference between a write that disappears into the compression and one that does not.</para>
///
/// <para>Both storages are measured because they take different branches: the CONVENTIONAL one is
/// what every ordinary integer-depth write uses (unit steps, truncating, float arithmetic), and the
/// SPANNING one is what a quantised map uses (a scale, rounding, double arithmetic including a
/// division per sample).</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FitsNarrowBenchmarks
{
    private float[,] _plane = null!;
    private short[,] _shorts = null!;
    private byte[,] _bytes = null!;
    private FitsSampleStorage _conventional;
    private FitsSampleStorage _spanning;

    /// <summary>One plane of a realistic map. 3072 is the short side of the real coverage map the
    /// storage rule was measured on.</summary>
    [Params(3072)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // A coverage plane's shape: flat inside, a ramp at the edge, and fractional weights
                // so neither branch gets to skip work on whole numbers.
                var edge = Math.Min(Math.Min(x, Size - 1 - x), Math.Min(y, Size - 1 - y));
                _plane[y, x] = edge >= 64 ? 50.7f : 50.7f * edge / 64f + (float)rng.NextDouble() * 0.01f;
            }
        }

        _shorts = new short[Size, Size];
        _bytes = new byte[Size, Size];
        _conventional = FitsSampleStorage.Conventional(BitDepth.Int16);
        _spanning = FitsSampleStorage.Spanning(BitDepth.Int8, 50.7, valuesAreWholeNumbers: false);
    }

    [Benchmark(Baseline = true)]
    public long ConventionalPerSample()
    {
        var sum = 0L;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                sum += _conventional.ToRaw(_plane[y, x]);
            }
        }

        return sum;
    }

    [Benchmark]
    public void ConventionalPerRow()
    {
        var source = _plane.AsSpan2D();
        var destination = _shorts.AsSpan2D();
        for (var y = 0; y < Size; y++)
        {
            _conventional.Narrow(source.GetRowSpan(y), destination.GetRowSpan(y));
        }
    }

    [Benchmark]
    public long SpanningPerSample()
    {
        var sum = 0L;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                sum += _spanning.ToRaw(_plane[y, x]);
            }
        }

        return sum;
    }

    [Benchmark]
    public void SpanningPerRow()
    {
        var source = _plane.AsSpan2D();
        var destination = _bytes.AsSpan2D();
        for (var y = 0; y < Size; y++)
        {
            _spanning.Narrow(source.GetRowSpan(y), destination.GetRowSpan(y));
        }
    }
}
