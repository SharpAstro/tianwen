using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Devices.Fake;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Targets <see cref="SyntheticStarFieldRenderer.Render"/>, the renderer
/// that drives both the <c>FakeCameraDriver</c> simulated frames and the
/// in-process unit / functional tests. The polar-align refining loop calls
/// it on every frame at the user's main-camera resolution -- if it's
/// slow, fake-camera-backed test runtime balloons and so does the
/// FakeCameraDriver-driven polar-refine cadence in the GUI.
///
/// We bench three resolutions covering guide-cam, mid-frame astrograph,
/// and IMX455 polar-align main camera, with a typical 50-star scene.
/// Sub-pixel offset is included to exercise the per-star Gaussian
/// rasterise loop (zero offset hits a marginally faster integer-aligned
/// path).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SyntheticStarFieldBenchmarks
{
    private float[,] _smallDest = null!;
    private float[,] _mediumDest = null!;
    private float[,] _largeDest = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Pre-allocate destination buffers so the bench measures rendering
        // cost only, not allocation. Mirrors the FakeCameraDriver path which
        // re-uses its ChannelBuffer across exposures.
        _smallDest = new float[960, 1280];
        _mediumDest = new float[4096, 4096];
        _largeDest = new float[6388, 9576];
    }

    [Benchmark]
    public float[,] Render_Small_1280x960() =>
        SyntheticStarFieldRenderer.Render(
            width: 1280, height: 960,
            defocusSteps: 0,
            offsetX: 0.5, offsetY: 0.5,
            starCount: 50, seed: 42,
            dest: _smallDest);

    [Benchmark]
    public float[,] Render_Medium_4096x4096() =>
        SyntheticStarFieldRenderer.Render(
            width: 4096, height: 4096,
            defocusSteps: 0,
            offsetX: 0.5, offsetY: 0.5,
            starCount: 50, seed: 42,
            dest: _mediumDest);

    [Benchmark]
    public float[,] Render_Large_9576x6388() =>
        SyntheticStarFieldRenderer.Render(
            width: 9576, height: 6388,
            defocusSteps: 0,
            offsetX: 0.5, offsetY: 0.5,
            starCount: 50, seed: 42,
            dest: _largeDest);

    // Heavily defocused stars rasterise wider PSFs -- ~10x more pixels
    // touched per star. This is the worst-case for the rough-focus phase
    // of an actual session and a sanity check that the per-frame cost
    // doesn't regress on early focus exposures.
    [Benchmark]
    public float[,] Render_Large_DefocusedFar() =>
        SyntheticStarFieldRenderer.Render(
            width: 9576, height: 6388,
            defocusSteps: 100,
            offsetX: 0.5, offsetY: 0.5,
            starCount: 50, seed: 42,
            dest: _largeDest);
}
