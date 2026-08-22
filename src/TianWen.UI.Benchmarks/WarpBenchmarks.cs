using System;
using System.Numerics;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// The bilinear resample loops, which are the only place in the library that touches a plane
/// PER DESTINATION PIXEL: a 2048-square 3-channel warp is 12.6M samples, and the stacker runs one per
/// registered frame.
/// </summary>
/// <remarks>
/// <para>This exists to price a residency check. D1 (see
/// <c>docs/plans/viewer-memory-footprint.md</c>) put one on every plane read, and
/// <c>SubpixelValue</c> read the plane by channel INDEX, so the check landed inside the innermost
/// loop. The question this measures is whether hoisting it out -- resolve residency once per channel,
/// then sample a <c>float[,]</c> -- is worth the churn, or whether the parallel warp is so
/// scheduling-bound that the check is invisible.</para>
/// <para>Mono and colour both, because the per-channel hoist amortises over a whole plane: with one
/// channel there is one hoist for the whole image, with three there are three, and the per-pixel cost
/// is what stays constant.</para>
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
public class WarpBenchmarks
{
    private Image _mono = null!;
    private Image _color = null!;
    private Matrix3x2 _transform;
    private float[][,] _monoAccum = null!;
    private float[][,] _colorAccum = null!;
    private float[,] _weight = null!;

    [Params(1024, 2048)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _mono = Make(1, Size, rng, SensorType.Monochrome);
        _color = Make(3, Size, rng, SensorType.Color);

        // A rotate + translate, so essentially every destination pixel takes the 4-tap bilinear path
        // rather than the integer-aligned shortcut -- an axis-aligned shift would measure the wrong
        // branch.
        _transform = Matrix3x2.CreateRotation(0.03f) * Matrix3x2.CreateTranslation(1.5f, -2.5f);

        _monoAccum = [new float[Size, Size]];
        _colorAccum = [new float[Size, Size], new float[Size, Size], new float[Size, Size]];
        _weight = new float[Size, Size];
    }

    // The parallel warps below are dominated by thread-pool scheduling and by 50 MB of allocation per
    // operation (hundreds of Gen2 collections per 1000 ops), and their run-to-run spread exceeds the
    // per-sample cost this file exists to price -- one measured pair came out 18.7 ms and 42.9 ms for
    // the SAME build. So the accumulators are the instrument: single-threaded, no per-op allocation of
    // the output, and the same bilinear sampler in the innermost loop.
    [Benchmark]
    public void Accumulate_Mono() => _mono.AccumulateTranslatedInto(_monoAccum, _weight, 1.5f, -2.5f, 1f);

    [Benchmark]
    public void Accumulate_Color() => _color.AccumulateTranslatedInto(_colorAccum, _weight, 1.5f, -2.5f, 1f);

    [Benchmark]
    public async Task<Image> Warp_Mono() => await _mono.WarpToReferenceGridAsync(_transform, Size, Size);

    [Benchmark]
    public async Task<Image> Warp_Color() => await _color.WarpToReferenceGridAsync(_transform, Size, Size);

    private static Image Make(int channelCount, int size, Random rng, SensorType sensorType)
    {
        var planes = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            planes[c] = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][y, x] = 500f + (float)rng.NextDouble() * 30f;
                }
            }
        }

        return new Image(planes, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("", default, default, FrameType.Light, "", 0, 0, 0, 0, default, 1, 1, float.NaN,
                sensorType, 0, 0, RowOrder.TopDown, 0f, 0f));
    }
}
