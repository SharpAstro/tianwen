using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Per-output-pixel microbenchmarks across all <see cref="IPixelRejector"/>
/// implementations. The streaming integrator walks output pixels row-major
/// and calls <see cref="IPixelRejector.Reject"/> once per pixel per channel,
/// passing a "stack column" of length N (= frame count) -- one value per
/// frame at that output position. On a 3008x3008 RGB master that's ~27M
/// invocations per integration, so per-call cost dominates wall time at
/// large stack counts -- a 2x speedup here turns the 7-minute SoL_60s
/// integration into ~3.5 min.
///
/// <para>
/// <c>FrameCount</c> is the stack column length, not the image side. Three
/// fixture sizes cover the operating range: 10 (small stacks, percentile
/// clip's sweet spot), 50 (winsorized / LFC), 244 (the Liberty 60s group
/// where the 7-min cost actually lives). Distribution: clean Gaussian vs
/// one cosmic-ray-style outlier -- the clean case isolates the kept-set
/// work, the contaminated one exercises the reject-then-iterate path.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PixelRejectorBenchmarks
{
    [Params(10, 50, 244)]
    public int FrameCount;

    private float[] _cleanColumn = null!;
    private float[] _contaminatedColumn = null!;
    private float[] _mask = null!;

    private SigmaClipRejector _sigma = null!;
    private WinsorizedSigmaClipRejector _winsorized = null!;
    private LinearFitClipRejector _lfc = null!;
    private PercentileClipRejector _percentile = null!;
    private MinMaxClipRejector _minMax = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Same Box-Muller setup used in SigmaClipRejectorTests: deterministic
        // N(0.5, 0.01) column, with the contaminated variant carrying one
        // cosmic-ray-style spike at 0.95.
        var rng = new Random(42);
        _cleanColumn = new float[FrameCount];
        _contaminatedColumn = new float[FrameCount];
        for (var i = 0; i < FrameCount; i += 2)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var r = Math.Sqrt(-2.0 * Math.Log(u1));
            var z1 = (float)(0.5 + 0.01 * r * Math.Cos(2.0 * Math.PI * u2));
            var z2 = (float)(0.5 + 0.01 * r * Math.Sin(2.0 * Math.PI * u2));
            _cleanColumn[i] = z1;
            _contaminatedColumn[i] = z1;
            if (i + 1 < FrameCount)
            {
                _cleanColumn[i + 1] = z2;
                _contaminatedColumn[i + 1] = z2;
            }
        }
        _contaminatedColumn[FrameCount - 1] = 0.95f;

        _mask = new float[FrameCount];

        _sigma = new SigmaClipRejector(3f, 3f, 5);
        _winsorized = new WinsorizedSigmaClipRejector(3f, 3f, 5);
        _lfc = new LinearFitClipRejector(3f, 3f, 5);
        _percentile = new PercentileClipRejector(0.1f, 0.1f);
        _minMax = new MinMaxClipRejector(1, 1);
    }

    // ---------------- Clean column ----------------

    [Benchmark]
    public int Sigma_Clean() => _sigma.Reject(_cleanColumn, _mask);

    [Benchmark]
    public int Winsorized_Clean() => _winsorized.Reject(_cleanColumn, _mask);

    [Benchmark]
    public int LinearFit_Clean() => _lfc.Reject(_cleanColumn, _mask);

    [Benchmark]
    public int Percentile_Clean() => _percentile.Reject(_cleanColumn, _mask);

    [Benchmark]
    public int MinMax_Clean() => _minMax.Reject(_cleanColumn, _mask);

    // ---------------- One-outlier column ----------------

    [Benchmark]
    public int Sigma_Contaminated() => _sigma.Reject(_contaminatedColumn, _mask);

    [Benchmark]
    public int Winsorized_Contaminated() => _winsorized.Reject(_contaminatedColumn, _mask);

    [Benchmark]
    public int LinearFit_Contaminated() => _lfc.Reject(_contaminatedColumn, _mask);

    [Benchmark]
    public int Percentile_Contaminated() => _percentile.Reject(_contaminatedColumn, _mask);

    [Benchmark]
    public int MinMax_Contaminated() => _minMax.Reject(_contaminatedColumn, _mask);
}
