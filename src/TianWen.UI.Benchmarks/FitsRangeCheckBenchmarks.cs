using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// What checking a file's stated DATAMIN / DATAMAX against its samples costs (#804), which is why
/// <see cref="Image.ResolveRange"/> makes it opt-in: every file TianWen writes states its range, and a
/// default read believes it. Measured as the whole read of such a file, believed and validated, and the
/// fold alone that validation adds.
/// </summary>
/// <remarks>
/// First measured 2026-09-26 (win-arm64, Release, ShortRun), against an early-exit compare-only scan
/// the reader shipped with for a day: the scan took 5.5 ms on the sub and 6.8 on the master, SLOWER than
/// the 3.8 and 4.8 ms fold that answers the whole question, and about a fifth of the 26 and 31 ms read.
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
public class FitsRangeCheckBenchmarks
{
    private string _path = null!;
    private float[][,] _planes = null!;

    /// <summary>
    /// "sub": one 6248x4176 plane (IMX571, 26 MP), a captured sub.
    /// "master": three 4114x2711 planes, the size of the #804 drizzle master.
    /// </summary>
    [Params("sub", "master")]
    public string Shape { get; set; } = "sub";

    [GlobalSetup]
    public void Setup()
    {
        var (planes, height, width) = Shape == "sub" ? (1, 4176, 6248) : (3, 2711, 4114);
        var rng = new Random(42);
        _planes = new float[planes][,];
        for (var c = 0; c < planes; c++)
        {
            var p = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    p[y, x] = 1000f + (float)rng.NextDouble() * 500f;
                }
            }
            _planes[c] = p;
        }

        var (min, max) = Image.ObservedRange(_planes);

        // WriteToFitsFile stamps DATAMIN / DATAMAX, as it does on every file TianWen writes.
        var image = new Image(_planes, BitDepth.Float32, max, min, 0f, default);
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks", nameof(FitsRangeCheckBenchmarks));
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{Shape}.fits");
        image.WriteToFitsFile(_path);
    }

    /// <summary>The fold validation adds, and what a file WITHOUT the cards pays anyway.</summary>
    [Benchmark]
    public float ObservedRange() => Image.ObservedRange(_planes).Max;

    /// <summary>The default read: the stated range is believed.</summary>
    [Benchmark(Baseline = true)]
    public float ReadBelieved() => Read(validateRange: false);

    /// <summary>The opt-in read: the stated range is checked against the samples.</summary>
    [Benchmark]
    public float ReadValidated() => Read(validateRange: true);

    private float Read(bool validateRange)
    {
        if (!Image.TryReadFitsFile(_path, out var image, out _, pooled: true, validateRange))
        {
            throw new InvalidOperationException("read failed");
        }
        var max = image.MaxValue;
        image.Release();
        return max;
    }
}
