using System;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// What the viewer's veil term costs the render thread (#1974, a review note: the cost for a folder of 300 to 2000 frames was never
/// measured). The file list reads <see cref="LightGrading.Verdict"/> per painted row, and a verdict's baselines are built once per
/// grade-table SNAPSHOT: every <c>ViewerState.RecordFrameGrade</c> swaps in a new one, so the first row painted after a grade lands pays
/// for the baselines and, with no stored model for the rig, for the night's self-fit (<see cref="ExpectedSkyModel.Fit"/>), which is lazy
/// per night and object. The four cases are the fit alone, that first verdict with the self-fit, the same with a stored model (the baselines
/// alone) and a verdict on a snapshot already built (what every other painted row costs).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SkySelfFitBenchmarks
{
    [Params(300, 1000, 2000)]
    public int Frames;

    private SkySample[] _samples = null!;
    private ImmutableDictionary<string, LightGrade> _table = null!;
    private LightGrade _newest;
    private LightGrade _probe;
    private ImmutableArray<ExpectedSkyEntry> _stored;

    [GlobalSetup]
    public void Setup()
    {
        // One night of one rig and object crossing the meridian, the last eighth of it under a thin veil (+7 % level, +86 % gradient):
        // the shape of LightGradingTests.SyntheticSkyNight, which the grade's own tests read the veil from.
        var grades = Night(Frames, veilFrom: Frames - Frames / 8, seed: 7);
        _samples = Array.ConvertAll(grades, static g => new SkySample(g.Covariates, g.Surface.Level, g.Surface.GradientStrength));
        var table = ImmutableDictionary.CreateBuilder<string, LightGrade>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < grades.Length; i++)
        {
            table[$"f{i:0000}"] = grades[i];
        }
        _table = table.ToImmutable();
        _newest = Night(Frames + 1, veilFrom: int.MaxValue, seed: 9)[Frames];
        _probe = grades[Frames / 2];
        var clearSamples = Array.ConvertAll(Night(Frames, veilFrom: int.MaxValue, seed: 3),
            static g => new SkySample(g.Covariates, g.Surface.Level, g.Surface.GradientStrength));
        var model = ExpectedSkyModel.Fit(clearSamples) ?? throw new InvalidOperationException("the clear night fits");
        _stored = [new ExpectedSkyEntry(new ExpectedSkyKey(SiteLat, SiteLon, _probe.Rig, _probe.Key.SensorType, _probe.Key.FilterKey, _probe.Key.ExposureMs, _probe.Key.Bin), model, 1)];
        // The first verdict of a snapshot is the cold one: build it once so the warm case reads a table already built.
        _ = LightGrading.Verdict(_table, _probe);
    }

    [Benchmark(Description = "ExpectedSkyModel.Fit over the night")]
    public ExpectedSkyModel? FitAlone() => ExpectedSkyModel.Fit(_samples);

    [Benchmark(Description = "first verdict after a new grade, no stored model (baselines + self-fit)")]
    public LightGradeVerdict FirstVerdictSelfFit() => LightGrading.Verdict(_table.SetItem("newest", _newest), _probe);

    [Benchmark(Description = "first verdict after a new grade, stored model (baselines only)")]
    public LightGradeVerdict FirstVerdictStored() => LightGrading.Verdict(_table.SetItem("newest", _newest), _probe, expectedSky: _stored);

    [Benchmark(Description = "a verdict on a snapshot already built (every other painted row)")]
    public LightGradeVerdict WarmVerdict() => LightGrading.Verdict(_table, _probe);

    private const double SiteLat = -37.9;
    private const double SiteLon = 145.2;

    private static LightGrade[] Night(int count, int veilFrom, int seed)
    {
        var random = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var start = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var frames = new LightGrade[count];
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0 : -1 + 2.0 * i / (count - 1);
            var x = 1 + t * t;
            var west = t > 0;
            var logLevel = Math.Log(1000) + 0.30 * (x - 1) + 0.10 * (x - 1) * (x - 1) + (west ? 0.05 * (x - 1) : 0) + 0.02 * 3 * t + 0.012 * Gauss();
            var logGradient = Math.Log(50) + 0.5 * (x - 1) + 0.12 * Gauss();
            var level = Math.Exp(logLevel) * (i >= veilFrom ? 1.07 : 1);
            var gradient = Math.Exp(logGradient) * (i >= veilFrom ? 1.86 : 1);
            var covariates = new SkyCovariates(0, -30, 3 * t, 90 - 60 * t * t, 180, x, west, 0, -30, 0, 90, 0, 0, 3 * t);
            frames[i] = new LightGrade(new LightGradeKey(4, 4, SensorType.Monochrome, "", "T", 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
                Patchiness: 1f, Night: new DateOnly(2026, 7, 6), Taken: start.AddMinutes(i), SiteLatitude: SiteLat, SiteLongitude: SiteLon,
                Surface: new SkySurface((float)level, (float)gradient, 0f, 1f), Covariates: covariates);
        }
        return frames;
    }
}
