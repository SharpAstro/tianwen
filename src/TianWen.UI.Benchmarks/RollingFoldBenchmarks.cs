using System;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// One frame's whole fold, as the rolling stack (<see cref="RollingWindowStacker"/>) and the batch stack's global passes do it:
/// graded, registered against the reference and folded in, per <see cref="Recipe"/>. What each of the enhanced pipeline's choices
/// costs a frame (docs/plans/planetary-restoration.md): the gradient, the climbed plain correlation and clamped Lanczos-3, which
/// reads 36 samples a pixel where bilinear reads 4. <see cref="Size"/> 512 squared is 262,144 px, 85 % of a 640 by 480 frame.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RollingFoldBenchmarks
{
    private const int Frames = 16;

    [Params(256, 512)]
    public int Size;

    /// <summary>
    /// legacy: the Laplacian, phase correlation, bilinear; gradient-whitened and laplacian-plain: one of the two changed, bilinear;
    /// plain-bilinear: the gradient and plain correlation, bilinear; pipeline: the same with clamped Lanczos-3.
    /// </summary>
    [Params("legacy", "gradient-whitened", "laplacian-plain", "plain-bilinear", "pipeline")]
    public string Recipe = "pipeline";

    private Image[] _frames = null!;
    private PixelRect[] _regions = null!;
    private GlobalAligner _aligner = null!;
    private IFrameQualityEstimator _estimator = null!;
    private WarpInterpolation _interpolation;
    private float[][,] _sum = null!;
    private float[,] _weight = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = PlanetaryBenchData.MonoDiskFrames(Frames, Size);
        _frames = new Image[Frames];
        _regions = new PixelRect[Frames];
        for (var i = 0; i < Frames; i++)
        {
            _frames[i] = Image.FromChannel(data[i], 1f, 0f);
            _regions[i] = PlanetaryDisk.BoundingBox(_frames[i]);
        }
        var legacy = RollingWindowOptions.Legacy;
        var options = Recipe switch
        {
            "legacy" => legacy,
            "gradient-whitened" => legacy with { QualityEstimator = new GradientEnergyEstimator() },
            "laplacian-plain" => legacy with { WhitenedCorrelation = false },
            _ => legacy with { QualityEstimator = new GradientEnergyEstimator(), WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3Clamped },
        };
        _estimator = options.QualityEstimator;
        _interpolation = Recipe == "pipeline" ? options.Interpolation : WarpInterpolation.Bilinear;
        var tile = Math.Clamp(NextPow2(Math.Max(_regions[0].Width, _regions[0].Height)), 64, 512);
        _aligner = GlobalAligner.FromReference(_frames[0], _regions[0], tile, options.WhitenedCorrelation);
        _sum = Image.CreateChannelData(1, Size, Size);
        _weight = new float[Size, Size];
    }

    [Benchmark(Description = "16 frames graded, registered and folded", OperationsPerInvoke = Frames)]
    public float Fold()
    {
        for (var i = 0; i < Frames; i++)
        {
            var frame = _frames[i];
            var region = PlanetaryDisk.BoundingBox(frame);
            var score = MathF.Max(0f, _estimator.Score(frame, region));
            var shift = _aligner.Estimate(frame, region);
            frame.AccumulateTranslatedInto(_sum, _weight, (float)shift.Dx, (float)shift.Dy, score, _interpolation);
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
