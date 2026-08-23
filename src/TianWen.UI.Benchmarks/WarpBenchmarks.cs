using System;
using System.Numerics;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// The bilinear resample loops, which are the only place in the library that touches a plane
/// PER DESTINATION PIXEL: a 2048-square 3-channel pass is 12.6M samples.
/// <para>ONE sampler, TWO stackers, and the distinction matters when reading these numbers.
/// <c>Accumulate*</c> is the PLANETARY translate-and-average kernel -- <c>LuckyImagingStacker</c>
/// for the batch path, and <c>RollingWindowStacker</c> twice for the live one (once to add a frame,
/// once to evict it by re-folding with a negated weight, which is why the kernel has to stay linear
/// in weight). The DEEP-SKY path reaches the same <c>SubpixelValue</c> through
/// <c>FrameRegistration</c> -> <c>WarpToReferenceGridAsync</c> per registered frame and
/// <c>TilePipelinedStrategy</c> -> <c>WarpRegionAsync</c> per strip. The measurements below are the
/// planetary kernel, because it is single-threaded and therefore measurable; deep-sky shares the
/// sampler and so is expected to benefit, but that is INFERRED and not measured here.</para>
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
/// <para><b>Measured, default job (NOT ShortRun), win-arm64, Accumulate ms.</b> FOUR variants,
/// because an earlier version of this comment conflated the middle two and so billed the whole
/// regression to D1':</para>
/// <code>
///                    pre-D1'         D1' shipped      thread-safe        hoisted
///                   JIT     AOT     JIT     AOT     JIT     AOT      JIT     AOT
///  Mono  1024     10.32   10.30   10.42   10.35   11.62   11.64    11.52   10.40
///  Mono  2048     42.09   41.66   41.53   41.55   47.42   46.51    45.25   41.16
///  Color 1024     28.62   27.78   28.54   28.06   32.85   32.05    29.09   28.39
///  Color 2048    119.18  121.26  126.81  121.70  137.80  146.37   127.52  129.05
/// </code>
/// <para><b>D1' as shipped cost nothing.</b> It put one bool check (<c>if (_planesReleased)</c>) ahead
/// of the same field read and the same single <c>Channel</c> copy that pre-D1' already did, and a
/// predicted not-taken branch is free: seven of the eight cases land within <b>1.3%</b> of pre-D1'.
/// The regression arrived with the fix that made residency observable from two threads, which DERIVES
/// residency from the plane array instead of keeping a flag beside it -- so <c>IsEvicted(planes[0])</c>
/// is a SECOND 72-byte <c>Channel</c> copy plus a dependent <c>.Data</c> load and a <c>Length</c>
/// check, 12.6M times. That step is <b>+8.7% to +20.3%</b> over D1' and <b>+11.6% to +20.7%</b> over
/// pre-D1': the whole of the band once billed to D1' belongs to it. Which does not make the fix
/// wrong -- a torn read of a half-restored plane array is not a cost worth saving -- it makes the
/// hoist below the thing that pays for it.</para>
/// <para><b>72 bytes, not the five slots the primary constructor suggests.</b> It reads as
/// <c>(float[,], Filter, float, float, byte)</c>, but <c>Filter</c> is ITSELF a 40-byte
/// <c>readonly record struct</c> of four strings and an enum, and <c>Channel</c> carries a sixth
/// field the parameter list does not show (the <c>ChannelBuffer?</c> init property). Flattened
/// that is six reference slots plus two floats, an enum and a byte -- measured with
/// <c>Unsafe.SizeOf</c>, because counting constructor parameters is how the earlier
/// "five-field" figure in this comment got there.</para>
/// <para>The hoist recovers most of that, and under AOT -- which is what ships -- returns to parity
/// with D1' as shipped (-1% to +6% across the four cases). It helps MORE under AOT because the JIT
/// has dynamic PGO and can hoist a repeated struct copy on its own. And AOT is not uniformly faster:
/// with the check in the loop it was 6% SLOWER than the JIT on Color/2048 (146.37 against 137.80), so
/// measuring only the JIT would have understated both the regression and the fix.</para>
/// <para><b>Two things about how this was measured, because the conclusion is an ATTRIBUTION and not
/// just a number.</b> The pre-D1' / D1' / thread-safe columns were taken back to back in one worktree;
/// the hoisted column is from the earlier session, and re-measuring thread-safe in the new one agreed
/// to within 2.6% (six of eight within 1.6%), which is what licenses reading them in one table. And
/// <b>pre-D1' is an ABLATION of D1'</b> rather than the pre-D1' tree: pre-D1' <c>Image.cs</c> has no
/// <c>Planes</c> accessor and the rest of the partial class has since migrated onto it, so a
/// file-level revert does not compile -- and an ablation is the better instrument anyway, differing by
/// exactly the one term under test.</para>
/// <para><b>Color/2048 under the JIT is the noisy cell</b> and no conclusion should rest on it alone:
/// nominally similar builds have read 119.18, 121.02 and 126.81 there, a ~6% spread. That is why the
/// one pre-D1'-to-D1' reading that looks like a real cost (+6.4%) is not treated as one when its AOT
/// twin moved +0.4%.</para>
/// <para><b>This table is where <c>docs/plans/frame-lifecycle.md</c> gets its budget from</b>, so a
/// re-measurement belongs in both places. That plan's rule -- ownership work is per-FRAME work and
/// must never appear per-pixel or per-sample -- is this measurement generalised, and its cure is the
/// one below: hoist the resolution to a scope (<c>Image.ResidentPlanes()</c>) rather than trying to
/// make the per-sample check cheaper. The plan also took the methodological half: a before/after pair
/// spanning two commits is a BAND, not an attribution, and it took four columns here to land the cost
/// on the change that actually caused it.</para>
/// <para>Run both: <c>--runtimes net10.0 nativeaot10.0</c>. Every shipped binary here is
/// <c>PublishAot</c>, so a JIT-only number is not the number that matters.</para>
/// </remarks>
// NOT ShortRunJob. Three iterations cannot resolve a single-digit-percent per-sample cost: the
// same build read 36.17 ms and 42.26 ms on Accumulate_Mono/2048 under ShortRun, while the default
// job reports 45.25 ms +/- 0.62. A short job is for keeping a suite quick, not for deciding whether
// an optimisation worked.
[MemoryDiagnoser]
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
