using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// What grading one frame costs the rolling stack (<see cref="RollingWindowStacker"/>), step by step, against what folding it costs
/// (#1174): a stack that folds only its best frames must still grade every frame a capture sends, so its grade has to fit the
/// capture's frame interval (4.6 ms at 216 frames a second). <see cref="FrameGrader.GradeAndShape"/> is the disk's bounding box and cut
/// test over the whole frame, the corrupt-readout scan, then the gradient over the box.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RollingGradeBenchmarks
{
    private const int Frames = 16;

    [Params(512, 768)]
    public int Size;

    /// <summary>synthetic: <see cref="PlanetaryBenchData.MonoDiskFrames"/> at <see cref="Size"/>; real: the first frames of the SER named by
    /// <c>TIANWEN_BENCH_SER</c> (2022-09-03 Red, 800 by 600, its disk 49 px across), <see cref="Size"/> then ignored.</summary>
    [Params("synthetic", "real")]
    public string Source = "synthetic";

    private Image[] _frames = null!;
    private PixelRect[] _regions = null!;
    private GlobalAligner _aligner = null!;
    private readonly IFrameQualityEstimator _estimator = new GradientEnergyEstimator();
    private float[][,] _sum = null!;
    private float[,] _weight = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _frames = new Image[Frames];
        _regions = new PixelRect[Frames];
        if (Source == "real")
        {
            var path = Environment.GetEnvironmentVariable("TIANWEN_BENCH_SER") ?? throw new InvalidOperationException("TIANWEN_BENCH_SER names no SER");
            using var stream = SerFrameStream.Open(path);
            for (var i = 0; i < Frames; i++)
            {
                _frames[i] = await stream.LoadAsync(i);
            }
        }
        else
        {
            var data = PlanetaryBenchData.MonoDiskFrames(Frames, Size);
            for (var i = 0; i < Frames; i++)
            {
                _frames[i] = Image.FromChannel(data[i], 1f, 0f);
            }
        }
        for (var i = 0; i < Frames; i++)
        {
            _regions[i] = PlanetaryDisk.BoundingBox(_frames[i]);
        }
        _sum = Image.CreateChannelData(_frames[0].ChannelCount, _frames[0].Height, _frames[0].Width);
        _weight = new float[_frames[0].Height, _frames[0].Width];
        var tile = Math.Clamp(NextPow2(Math.Max(_regions[0].Width, _regions[0].Height)), 64, 512);
        _aligner = GlobalAligner.FromReference(_frames[0], _regions[0], tile, whiten: false);
    }

    [Benchmark(Description = "a frame graded as the rolling stack grades it", OperationsPerInvoke = Frames)]
    public float Grade()
    {
        var total = 0f;
        for (var i = 0; i < Frames; i++)
        {
            total += FrameGrader.GradeAndShape(_estimator, _frames[i]).Score;
        }
        return total;
    }

    [Benchmark(Description = "the disk's bounding box alone", OperationsPerInvoke = Frames)]
    public int Box()
    {
        var total = 0;
        for (var i = 0; i < Frames; i++)
        {
            total += PlanetaryDisk.BoundingBox(_frames[i]).Width;
        }
        return total;
    }

    [Benchmark(Description = "the corrupt-readout scan alone", OperationsPerInvoke = Frames)]
    public int Corrupt()
    {
        var total = 0;
        for (var i = 0; i < Frames; i++)
        {
            total += FrameGrader.IsCorruptReadout(_frames[i]) ? 1 : 0;
        }
        return total;
    }

    [Benchmark(Description = "the gradient over the box alone", OperationsPerInvoke = Frames)]
    public float Gradient()
    {
        var total = 0f;
        for (var i = 0; i < Frames; i++)
        {
            total += _estimator.Score(_frames[i], _regions[i]);
        }
        return total;
    }

    [Benchmark(Description = "a frame registered and folded, its box known", OperationsPerInvoke = Frames)]
    public float Fold()
    {
        for (var i = 0; i < Frames; i++)
        {
            var shift = _aligner.Estimate(_frames[i], _regions[i]);
            _frames[i].AccumulateTranslatedInto(_sum, _weight, (float)shift.Dx, (float)shift.Dy, 1f, WarpInterpolation.Bilinear);
        }
        return _weight[_weight.GetLength(0) / 2, _weight.GetLength(1) / 2];
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
