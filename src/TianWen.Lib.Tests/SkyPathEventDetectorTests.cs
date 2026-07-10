using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public class SkyPathEventDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromDays(1);

    [Fact]
    public void GivenARetrogradeLoopWhenDetectingThenTwoStationsAreFoundAtTheTurningPoints()
    {
        // RA rises (direct) 0..4, falls (retrograde) 4..8, rises again 8..12 -- a classic retrograde loop.
        // The peak (i=4) is the direct->retrograde station, the trough (i=8) the retrograde->direct station.
        var path = new (double RA, double Dec)[13];
        for (var i = 0; i <= 4; i++) path[i] = (0.5 * i, 10.0);           // 0.0 -> 2.0
        for (var i = 5; i <= 8; i++) path[i] = (2.0 - 0.25 * (i - 4), 10.0); // 1.75 -> 1.0
        for (var i = 9; i <= 12; i++) path[i] = (1.0 + 0.5 * (i - 8), 10.0); // 1.5 -> 3.0

        var results = new List<SkyPathEvent>();
        SkyPathEventDetector.Detect(path, ReadOnlySpan<(double, double)>.Empty, Start, Step,
            SkyPathBody.OuterPlanet, perihelion: null, results);

        results.Count.ShouldBe(2);

        var retro = results.Single(e => e.Kind == SkyPathEventKind.StationRetrograde);
        retro.Label.ShouldBe("R");
        retro.TimeUtc.ShouldBe(Start + Step * 4);

        var direct = results.Single(e => e.Kind == SkyPathEventKind.StationDirect);
        direct.Label.ShouldBe("D");
        direct.TimeUtc.ShouldBe(Start + Step * 8);
    }

    [Fact]
    public void GivenAMonotonicPathWhenDetectingThenNoStations()
    {
        var path = new (double RA, double Dec)[10];
        for (var i = 0; i < path.Length; i++) path[i] = (0.1 * i, 5.0); // steadily direct

        var results = new List<SkyPathEvent>();
        SkyPathEventDetector.Detect(path, ReadOnlySpan<(double, double)>.Empty, Start, Step,
            SkyPathBody.OuterPlanet, perihelion: null, results);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void GivenAnInferiorPlanetPullingAwayFromTheSunWhenDetectingThenGreatestElongation()
    {
        // Sun fixed at RA 0; planet RA rises 0->2h then falls back -- max separation at the turn (i=4).
        var path = new (double RA, double Dec)[9];
        var sun = new (double RA, double Dec)[9];
        for (var i = 0; i < 9; i++)
        {
            sun[i] = (0.0, 0.0);
            var ra = i <= 4 ? 0.25 * i : 0.25 * (8 - i); // 0..1..0 (hours)
            path[i] = (ra, 0.0);
        }

        var results = new List<SkyPathEvent>();
        SkyPathEventDetector.Detect(path, sun, Start, Step, SkyPathBody.InferiorPlanet, perihelion: null, results);

        var ge = results.Where(e => e.Kind == SkyPathEventKind.GreatestElongation).ToList();
        ge.ShouldHaveSingleItem();
        ge[0].Label.ShouldBe("GE");
        ge[0].TimeUtc.ShouldBe(Start + Step * 4);
    }

    [Fact]
    public void GivenACometWithPerihelionInWindowWhenDetectingThenPerihelionAtNearestSample()
    {
        var path = new (double RA, double Dec)[13];
        for (var i = 0; i < path.Length; i++) path[i] = (0.1 * i, 0.0); // monotonic (no stations)

        var results = new List<SkyPathEvent>();
        SkyPathEventDetector.Detect(path, ReadOnlySpan<(double, double)>.Empty, Start, Step,
            SkyPathBody.Comet, perihelion: Start + TimeSpan.FromDays(5.4), results);

        var peri = results.Single(e => e.Kind == SkyPathEventKind.Perihelion);
        peri.Label.ShouldBe("q");
        peri.TimeUtc.ShouldBe(Start + Step * 5); // 5.4 rounds to sample 5
    }

    [Fact]
    public void GivenPerihelionOutsideWindowWhenDetectingThenNoPerihelionEvent()
    {
        var path = new (double RA, double Dec)[13];
        for (var i = 0; i < path.Length; i++) path[i] = (0.1 * i, 0.0);

        var results = new List<SkyPathEvent>();
        SkyPathEventDetector.Detect(path, ReadOnlySpan<(double, double)>.Empty, Start, Step,
            SkyPathBody.Comet, perihelion: Start + TimeSpan.FromDays(99), results);

        results.ShouldNotContain(e => e.Kind == SkyPathEventKind.Perihelion);
    }
}
