using System;
using System.Drawing;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Per-stage cost of the live planetary rolling-window stack (<see cref="RollingWindowStacker"/>) so we can
/// see which stage dominates a window integration -- the question behind the "30s to adjust" report.
/// <see cref="FullStack"/> is the end-to-end first build of an <see cref="Frames"/>-frame window;
/// <see cref="Grade"/> / <see cref="Align"/> / <see cref="Fold"/> isolate the per-frame stages over the same
/// frames (their sum ~ FullStack minus the one-off normalise + demosaic). The per-stage benches operate on
/// PRE-LOADED frames so they measure pure stage cost (no decode); <see cref="Load"/> measures the decode and
/// <see cref="FullStack"/> is the realistic loads-once-internally path.
/// <para>Synthetic mono frames (textured disk + small per-frame shift) at a sub-plane-sized ROI -- no fixtures.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PlanetaryStackBenchmarks
{
    /// <summary>Sub-plane edge in px (a split-CFA half-plane on a planetary ROI is ~this).</summary>
    public const int Size = 256;

    [Params(100, 300, 500)]
    public int Frames;

    private float[][,] _data = null!;
    private BenchFrameStream _stream = null!;
    private Image[] _frames = null!;      // pre-loaded (no per-iteration decode in the stage benches)
    private GlobalAligner _aligner = null!;
    private LaplacianEnergyEstimator _estimator = null!;
    private float[][,] _sum = null!;
    private float[,] _weight = null!;
    private Rectangle[] _regions = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = PlanetaryBenchData.MonoDiskFrames(Frames, Size);
        _stream = new BenchFrameStream(_data);
        _estimator = new LaplacianEnergyEstimator();

        _frames = new Image[Frames];
        _regions = new Rectangle[Frames];
        for (var i = 0; i < Frames; i++)
        {
            _frames[i] = Image.FromChannel(_data[i], 1f, 0f); // share the buffer (read-only in the stage benches)
            _regions[i] = PlanetaryDisk.BoundingBox(_frames[i]);
        }

        var tile = Math.Clamp(NextPow2(Math.Max(_regions[0].Width, _regions[0].Height)), 64, 512);
        _aligner = GlobalAligner.FromReference(_frames[0], _regions[0], tile);
        _sum = Image.CreateChannelData(1, Size, Size);
        _weight = new float[Size, Size];
    }

    [Benchmark(Description = "Full first-build stack (load+grade+align+fold+normalize)")]
    public async Task<int> FullStack()
    {
        // Force the window to span all Frames (no timestamps -> frame-count window; cap == count).
        var stacker = new RollingWindowStacker(_stream,
            new RollingWindowOptions { FallbackWindowFrames = Frames, MaxWindowFrames = Frames });
        var master = await stacker.StackToAsync(Frames - 1);
        return master.Width;
    }

    [Benchmark(Description = "Decode only (per-frame clone+wrap)")]
    public int Load()
    {
        var acc = 0;
        for (var i = 0; i < Frames; i++)
        {
            var f = _stream.LoadAsync(i).GetAwaiter().GetResult();
            acc += f.Width;
        }
        return acc;
    }

    [Benchmark(Description = "Grade only (per-frame Laplacian variance)")]
    public float Grade()
    {
        float acc = 0;
        for (var i = 0; i < Frames; i++)
        {
            acc += _estimator.Score(_frames[i], _regions[i]);
        }
        return acc;
    }

    [Benchmark(Description = "Align only (per-frame disk-COM + phase correlation)")]
    public double Align()
    {
        double acc = 0;
        for (var i = 0; i < Frames; i++)
        {
            acc += _aligner.Estimate(_frames[i], _regions[i]).Dx;
        }
        return acc;
    }

    [Benchmark(Description = "Fold only (per-frame translate-accumulate)")]
    public float Fold()
    {
        Array.Clear(_weight);
        Array.Clear(_sum[0]);
        for (var i = 0; i < Frames; i++)
        {
            _frames[i].AccumulateTranslatedInto(_sum, _weight, 0.3f, -0.2f, 1f);
        }
        return _weight[Size / 2, Size / 2];
    }

    private static int NextPow2(int value)
    {
        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }
}
