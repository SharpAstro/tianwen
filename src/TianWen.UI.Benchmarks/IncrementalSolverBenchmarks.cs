using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Compares <see cref="IncrementalSolver.RefineAsync"/> against the existing
/// full-solve path on a polar-align-shaped frame. The handoff target was
/// "<![CDATA[<20 ms per refine vs ~700 ms for a full hinted solve]]>"; this
/// bench gives us a calibrated number to ship.
///
/// <para><b>What we don't bench here:</b></para>
/// <list type="bullet">
///   <item><description>The full <see cref="CatalogPlateSolver"/> path
///     end-to-end. That requires a Tycho-2 catalog DB which is too heavy for
///     a CI bench (~50 MB load, multi-second init). Use the
///     <c>PlateSolverTests</c> manual diagnostic for that comparison
///     instead.</description></item>
///   <item><description>The seed step, which only fires once per
///     <c>RefineFullSolveInterval</c> frames. ROI-Refine is the hot path.</description></item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class IncrementalSolverBenchmarks
{
    private const int LargeWidth = 9576;
    private const int LargeHeight = 6388;
    private const int MediumWidth = 4096;
    private const int MediumHeight = 4096;
    private const int SmallWidth = 1280;
    private const int SmallHeight = 960;

    // Anchor list at native resolution drives Refine cost; sizes match
    // typical sensors -- guide cam, full-frame astrograph, IMX455 polar setup.
    private IncrementalSolver _smallSolver = null!;
    private IncrementalSolver _mediumSolver = null!;
    private IncrementalSolver _largeSolver = null!;
    private Image _smallRefineFrame = null!;
    private Image _mediumRefineFrame = null!;
    private Image _largeRefineFrame = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        // Use 60 stars at perfect focus -- representative of a polar-align
        // FOV with the plate-solve SNR threshold (snrMin = 5).
        (_smallSolver, _smallRefineFrame) = await BuildSeededAsync(SmallWidth, SmallHeight, starCount: 60, seed: 7);
        (_mediumSolver, _mediumRefineFrame) = await BuildSeededAsync(MediumWidth, MediumHeight, starCount: 60, seed: 11);
        (_largeSolver, _largeRefineFrame) = await BuildSeededAsync(LargeWidth, LargeHeight, starCount: 60, seed: 13);
    }

    private static async Task<(IncrementalSolver, Image)> BuildSeededAsync(int width, int height, int starCount, int seed)
    {
        var seedFrame = RenderSyntheticFrame(width, height, offsetX: 0, offsetY: 0, starCount: starCount, seed: seed);

        // Pixel-scale chosen so PixelToSky / SkyToPixel produce sane numbers.
        // 1.5 arcsec/px is typical of a polar-align main camera at 200 mm
        // focal length.
        const double pixelScaleArcsec = 1.5;
        var pixelScaleDeg = pixelScaleArcsec / 3600.0;
        var seedWcs = new WCS(0.0, 0.0)
        {
            CRPix1 = (width + 1) / 2.0,
            CRPix2 = (height + 1) / 2.0,
            CD1_1 = pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = pixelScaleDeg,
        };

        var solver = new IncrementalSolver();
        await solver.SeedAsync(seedFrame, seedWcs);

        // Refine frame: same field shifted by 2 px diagonally -- typical sub-pixel
        // knob-nudge magnitude. Bigger shifts are bench-irrelevant: the
        // residual gate kicks in and Refine returns null.
        var refineFrame = RenderSyntheticFrame(width, height, offsetX: 2.0f, offsetY: 1.0f, starCount: starCount, seed: seed);
        return (solver, refineFrame);
    }

    private static Image RenderSyntheticFrame(int width, int height, float offsetX, float offsetY, int starCount, int seed)
    {
        var data = SyntheticStarFieldRenderer.Render(
            width: width, height: height,
            defocusSteps: 0,
            offsetX: offsetX, offsetY: offsetY,
            hyperbolaA: 2.0, hyperbolaB: 50.0,
            exposureSeconds: 1.0,
            skyBackground: 50.0, readNoise: 3.0,
            starCount: starCount, seed: seed,
            pixelScaleArcsec: 1.5);

        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        return new Image(
            [data], BitDepth.Float32, max, min, 0,
            new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, default,
                1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));
    }

    [Benchmark]
    public async Task<PlateSolveResult?> Refine_Small_1280x960() =>
        await _smallSolver.RefineAsync(_smallRefineFrame);

    [Benchmark]
    public async Task<PlateSolveResult?> Refine_Medium_4096x4096() =>
        await _mediumSolver.RefineAsync(_mediumRefineFrame);

    [Benchmark]
    public async Task<PlateSolveResult?> Refine_Large_9576x6388() =>
        await _largeSolver.RefineAsync(_largeRefineFrame);
}
