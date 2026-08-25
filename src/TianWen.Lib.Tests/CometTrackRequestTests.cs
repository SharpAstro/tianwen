using Shouldly;
using System;
using System.Numerics;
using TianWen.Lib.Astrometry.Comets;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The step between "a folder of comet lights" and "an ephemeris query": what gets asked for, from
/// where, and over what window. It is pure so that the only part of the unattended path needing a
/// network is the fetch itself.
/// </summary>
public class CometTrackRequestTests
{
    private static readonly DateTimeOffset s_first = new(2026, 8, 16, 10, 53, 18, TimeSpan.Zero);
    private static readonly DateTimeOffset s_last = new(2026, 8, 16, 14, 25, 42, TimeSpan.Zero);
    private const double Lat = -37.8763888888889;
    private const double Lon = 145.178055555556;
    private const double Elev = 120.0;

    [Fact]
    public void TheDesignationIsReadOffTheFramesWhenNoneIsAsked()
    {
        // The unattended case: `stack --comet` with no value on a folder whose OBJECT card says what
        // it is. ToCompact is what Horizons' DES= takes, so "10P/Tempel 2" has to arrive as "10P".
        var request = CometTrackRequest.TryBuild(null, "10P/Tempel 2", Lat, Lon, Elev, s_first, s_last);

        request.ShouldNotBeNull();
        request.Value.Designation.ShouldBe("10P");
    }

    [Fact]
    public void AnExplicitDesignationBeatsTheObjectCard()
    {
        // A mislabelled OBJECT is exactly why the flag takes a value at all.
        var request = CometTrackRequest.TryBuild("C/2023 A3", "10P/Tempel 2", Lat, Lon, Elev, s_first, s_last);

        request.ShouldNotBeNull();
        request.Value.Designation.ShouldBe("C2023A3");
    }

    [Theory]
    [InlineData("M42")]
    [InlineData("NGC 7000")]
    [InlineData("")]
    [InlineData(null)]
    public void AnObjectThatIsNotACometDeclines(string? objectName)
    {
        // --comet pointed at a deep-sky folder must produce NO rate rather than a wrong one: the
        // caller logs the refusal and stacks star-aligned, which is the right answer for M42.
        CometTrackRequest.TryBuild(null, objectName, Lat, Lon, Elev, s_first, s_last).ShouldBeNull();
    }

    [Fact]
    public void AnUnknownSiteDeclinesRatherThanFallingBackToTheGeocentre()
    {
        // THE load-bearing refusal. Horizons answers a geocentric query perfectly happily, and the
        // answer is wrong by ~3.4 degrees of heading while fitting a STRAIGHTER line than the correct
        // track -- so no residual check downstream can catch it (see CometRateSolverTests). Declining
        // is the only defence, because the caller still has --comet-rate.
        CometTrackRequest.TryBuild(null, "10P", double.NaN, Lon, Elev, s_first, s_last).ShouldBeNull();
        CometTrackRequest.TryBuild(null, "10P", Lat, double.NaN, Elev, s_first, s_last).ShouldBeNull();
    }

    [Fact]
    public void AnUnknownElevationIsSeaLevelRatherThanARefusal()
    {
        // Unlike latitude and longitude this one is worth defaulting: 120 m against Earth's radius
        // moves a ~3 px diurnal parallax by well under a thousandth of a pixel, so refusing over a
        // missing SITEELEV would cost a whole run to buy nothing.
        var request = CometTrackRequest.TryBuild(null, "10P", Lat, Lon, double.NaN, s_first, s_last);

        request.ShouldNotBeNull();
        request.Value.SiteElevMetres.ShouldBe(0.0);
    }

    [Fact]
    public void TheWindowBracketsEveryFrame()
    {
        // The fit is a straight line and would extrapolate without complaint, but an extrapolated
        // endpoint is where a track's curvature is least constrained -- and the first and last frames
        // are the two that pay most for a wrong slope. So both ends are padded outside the session.
        var request = CometTrackRequest.TryBuild(null, "10P", Lat, Lon, Elev, s_first, s_last);

        request.ShouldNotBeNull();
        request.Value.Start.ShouldBeLessThan(s_first);
        request.Value.Stop.ShouldBeGreaterThan(s_last);
    }

    [Fact]
    public void TheStepIsWholeMinutesAndAtLeastOne()
    {
        // Horizons expresses STEP_SIZE in whole minutes, so a cadence it cannot honour would give a
        // different window than the one computed here.
        var request = CometTrackRequest.TryBuild(null, "10P", Lat, Lon, Elev, s_first, s_last);
        request.ShouldNotBeNull();
        request.Value.Step.TotalMinutes.ShouldBe(Math.Round(request.Value.Step.TotalMinutes));
        request.Value.Step.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));

        // A very short session must not round the step to zero and ask for an infinite series.
        var brief = CometTrackRequest.TryBuild(
            null, "10P", Lat, Lon, Elev, s_first, s_first + TimeSpan.FromSeconds(90));
        brief.ShouldNotBeNull();
        brief.Value.Step.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ASessionWithNoDurationDeclines()
    {
        // One frame, or a corrupt set of timestamps: there is no rate to fit and answering zero would
        // be a silent "this comet does not move".
        CometTrackRequest.TryBuild(null, "10P", Lat, Lon, Elev, s_first, s_first).ShouldBeNull();
        CometTrackRequest.TryBuild(null, "10P", Lat, Lon, Elev, s_last, s_first).ShouldBeNull();
    }
}

/// <summary>The offline rate form, <c>--comet-rate dx,dy</c>.</summary>
public class CometRateParseTests
{
    [Theory]
    [InlineData("-11.2,5.9", -11.2f, 5.9f)]
    [InlineData("  -11.2 , 5.9  ", -11.2f, 5.9f)]
    [InlineData("0,0", 0f, 0f)]
    [InlineData("1e1,-2.5e-1", 10f, -0.25f)]
    public void AWellFormedPairParses(string text, float dx, float dy)
    {
        CometRateSolver.TryParsePxPerHour(text, out var rate).ShouldBeTrue();
        rate.X.ShouldBe(dx, tolerance: 1e-4f);
        rate.Y.ShouldBe(dy, tolerance: 1e-4f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11.2")]          // no comma: a single number is not a direction
    [InlineData(",5.9")]          // no x component
    [InlineData("abc,5.9")]
    [InlineData("11.2,")]
    [InlineData("1,2,3")]
    [InlineData("NaN,5.9")]       // a non-finite rate would translate every frame to nowhere
    [InlineData("Infinity,0")]
    public void AMalformedRateIsRefusedRatherThanGuessed(string text)
    {
        CometRateSolver.TryParsePxPerHour(text, out var rate).ShouldBeFalse();
        rate.ShouldBe(Vector2.Zero);
    }

    [Fact]
    public void TheSeparatorIsAlwaysTheFieldSeparatorNeverADecimalComma()
    {
        // Parsing is invariant-culture by construction, so "1,5" is the PAIR (1, 5) and never the
        // single value 1.5 -- which is the reading a German locale would otherwise give it. Worth
        // pinning because the failure is silent and the value stays plausible.
        CometRateSolver.TryParsePxPerHour("1,5", out var rate).ShouldBeTrue();
        rate.ShouldBe(new Vector2(1f, 5f));
    }
}
