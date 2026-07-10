using System;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Steady-state cost of the ephemeris methods the sky map runs on the render thread when a solar-system
/// object is selected: a single planet position (<see cref="VSOP87a.ReduceJ2000"/>), a single comet
/// position (<see cref="CometEphemeris.TryGetEquatorialJ2000"/>), the 49-sample sky-path builds, and the
/// 32-sample vmag sparkline. VSOP87 is pre-warmed in setup so the one-time ~330 ms JIT + static-table init
/// is out of the measurement. These numbers decide whether the per-selection / per-day-scrub sampling
/// needs to move off the render thread (task #26) and how coarse the path cache can be for slow movers.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EphemerisBenchmarks
{
    private static readonly DateTimeOffset When = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    // Matches SkyMapState.SkyPathSampleCount / CometCurveSampleCount and the per-body path windows.
    private const int PathSamples = 49;
    private const int CurveSamples = 32;
    private const double PlanetWindowDays = 120.0;
    private const double CometWindowDays = 45.0;
    private const double CurveWindowDays = 90.0;

    private CometElements _comet;

    [GlobalSetup]
    public void Setup()
    {
        CometDesignation.TryParse("12P", out var designation);
        // 12P/Pons-Brooks osculating elements (same frozen set the ephemeris tests pin).
        _comet = new CometElements(designation, "Pons-Brooks",
            PerihelionDistanceAu: 0.7808611331423883,
            Eccentricity: 0.9545612442767357,
            InclinationDeg: 74.19091017013747,
            AscendingNodeDeg: 255.8553510995133,
            ArgumentOfPerihelionDeg: 198.9879994677832,
            PerihelionJdTt: 2460421.631159499004,
            EpochJdTt: 2460211.5,
            AbsoluteMagnitudeM1: 5.0,
            SlopeK1: 15.0);

        // Pre-warm: the FIRST VSOP87 call pays a one-time ~330 ms JIT + static-table init that must not
        // land in the measured steady state (it's pre-warmed at app startup too).
        VSOP87a.ReduceJ2000(CatalogIndex.Mars, When, out _, out _, out _);
        CometEphemeris.TryGetEquatorialJ2000(_comet, When, out _, out _, out _, out _);
    }

    [Benchmark]
    public double Planet_Position_Single()
    {
        VSOP87a.ReduceJ2000(CatalogIndex.Mars, When, out var ra, out _, out _);
        return ra;
    }

    [Benchmark]
    public double Comet_Position_Single()
    {
        CometEphemeris.TryGetEquatorialJ2000(_comet, When, out var ra, out _, out _, out _);
        return ra;
    }

    [Benchmark]
    public double Planet_Path_49Samples()
    {
        var start = When - TimeSpan.FromDays(PlanetWindowDays / 2.0);
        var step = TimeSpan.FromDays(PlanetWindowDays / (PathSamples - 1));
        var sum = 0.0;
        for (var i = 0; i < PathSamples; i++)
        {
            VSOP87a.ReduceJ2000(CatalogIndex.Mars, start + step * i, out var ra, out _, out _);
            sum += ra;
        }
        return sum;
    }

    [Benchmark]
    public double Comet_Path_49Samples()
    {
        var start = When - TimeSpan.FromDays(CometWindowDays / 2.0);
        var step = TimeSpan.FromDays(CometWindowDays / (PathSamples - 1));
        var sum = 0.0;
        for (var i = 0; i < PathSamples; i++)
        {
            CometEphemeris.TryGetEquatorialJ2000(_comet, start + step * i, out var ra, out _, out _, out _);
            sum += ra;
        }
        return sum;
    }

    [Benchmark]
    public double Comet_MagnitudeCurve_32Samples()
    {
        Span<double> mags = stackalloc double[CurveSamples];
        CometEphemeris.SampleMagnitudeCurve(
            _comet, When - TimeSpan.FromDays(CurveWindowDays / 2.0),
            TimeSpan.FromDays(CurveWindowDays / (CurveSamples - 1)), mags);
        return mags[0];
    }
}
