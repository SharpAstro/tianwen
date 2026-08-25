using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Comets;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The ephemeris-to-canvas-rate step, which is what makes comet-aligned registration unattended:
/// without it the rate is a constant somebody typed.
///
/// <para>Driven by the same frozen Horizons response the parser tests use, projected through a WCS
/// built at the plate scale the 10P field actually solved to (4.7172"/px). That makes the expected
/// answer the plan's own hand-derived 12.64 px/hr on PA 152 deg, arrived at through code rather than
/// through a shell session.</para>
/// </summary>
[Collection("Catalog")]
public class CometRateSolverTests(ITestOutputHelper output)
{
    private const double PlateScaleArcsecPerPx = 4.7172;

    private static EphemerisSample[] FrozenTrack()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("horizons-10p-observer-2026-08-16.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        HorizonsObserverSource.TryParse(reader.ReadToEnd(), out var samples).ShouldBeTrue();
        return [.. samples];
    }

    /// <summary>
    /// A north-up, east-left tangent plane at the given scale, centred on the field. Deliberately
    /// unrotated so the fitted components are readable as "east/west" and "north/south" rather than
    /// having to be un-rotated first; the magnitude and position angle are rotation-invariant anyway.
    /// </summary>
    private static WCS FieldWcs(double centreRaDeg, double centreDecDeg, double rollDeg = 0.0)
    {
        var cdelt = PlateScaleArcsecPerPx / 3600.0;
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(rollDeg));
        return new WCS(centreRaDeg / 15.0, centreDecDeg)
        {
            CRPix1 = 2057,
            CRPix2 = 1356,
            // RA increases to the LEFT on a sky image, hence the negative first term.
            CD1_1 = -cdelt * cos,
            CD1_2 = cdelt * sin,
            CD2_1 = cdelt * sin,
            CD2_2 = cdelt * cos,
        };
    }

    [Fact]
    public void TheFittedRateIsThePlansHandDerivedRate()
    {
        var track = FrozenTrack();
        var wcs = FieldWcs(331.74, -30.49);

        var rate = CometRateSolver.SolveCanvasRatePxPerHour(wcs, track);

        rate.ShouldNotBeNull();
        var magnitude = rate.Value.PxPerHour.Length();
        output.WriteLine($"rate = ({rate.Value.PxPerHour.X:F3}, {rate.Value.PxPerHour.Y:F3}) px/hr, "
            + $"|v| = {magnitude:F3}, residual {rate.Value.MaxResidualPx:F4} px over {rate.Value.SampleCount} samples");

        magnitude.ShouldBe(12.64f, tolerance: 0.15f);
        rate.Value.SampleCount.ShouldBe(8);
    }

    [Fact]
    public void TheTrackIsLinearEnoughForASingleRate()
    {
        // The plan justifies ONE linear rate by measuring the departure from linear at 0.185 px worst
        // case over the run -- under the registration's own residual, so a quadratic term would be
        // fitting noise. The solver reports that number instead of taking the claim on trust, which is
        // what lets a faster body or a longer night be caught rather than silently mis-modelled.
        var rate = CometRateSolver.SolveCanvasRatePxPerHour(FieldWcs(331.74, -30.49), FrozenTrack());

        rate.ShouldNotBeNull();
        output.WriteLine($"max residual {rate.Value.MaxResidualPx:F4} px");
        rate.Value.MaxResidualPx.ShouldBeLessThan(0.25);
    }

    [Fact]
    public void TheRateMagnitudeSurvivesAFieldRotation()
    {
        // Canvas orientation is a property of how the camera sat, not of the comet. A rolled field
        // must give the same SPEED with the components rotated, and this is the cheapest guard
        // against a transposed or mis-signed CD matrix in the projection path.
        var straight = CometRateSolver.SolveCanvasRatePxPerHour(FieldWcs(331.74, -30.49), FrozenTrack());
        var rolled = CometRateSolver.SolveCanvasRatePxPerHour(FieldWcs(331.74, -30.49, rollDeg: 37.0), FrozenTrack());

        straight.ShouldNotBeNull();
        rolled.ShouldNotBeNull();
        rolled.Value.PxPerHour.Length().ShouldBe(straight.Value.PxPerHour.Length(), tolerance: 0.02f);
        // ...and genuinely rotated, so the test cannot pass by the roll being ignored.
        Vector2.Distance(rolled.Value.PxPerHour, straight.Value.PxPerHour).ShouldBeGreaterThan(1.0f);
    }

    [Fact]
    public void AConstantPixelOffsetCannotChangeARate()
    {
        // SkyToPixel answers in the solver's own centroid coordinates, and whether to treat those as
        // 1-based is a live hazard elsewhere in this codebase. A rate is a DIFFERENCE, so shifting
        // the reference pixel -- which shifts every projected position by a constant -- must leave it
        // untouched. Pinning it means nobody has to re-reason about the convention here.
        var track = FrozenTrack();
        var baseWcs = FieldWcs(331.74, -30.49);
        var a = CometRateSolver.SolveCanvasRatePxPerHour(baseWcs, track);
        var shifted = baseWcs with { CRPix1 = baseWcs.CRPix1 + 1.0, CRPix2 = baseWcs.CRPix2 - 0.5 };
        var b = CometRateSolver.SolveCanvasRatePxPerHour(shifted, track);

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        b.Value.PxPerHour.X.ShouldBe(a.Value.PxPerHour.X, tolerance: 1e-4f);
        b.Value.PxPerHour.Y.ShouldBe(a.Value.PxPerHour.Y, tolerance: 1e-4f);
    }

    private static EphemerisSample[] FrozenGeocentricTrack()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("horizons-10p-geocentric-2026-08-16.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        HorizonsObserverSource.TryParse(reader.ReadToEnd(), out var samples).ShouldBeTrue();
        return [.. samples];
    }

    [Fact]
    public void AGeocentricEphemerisWouldGetTheRateWrong()
    {
        // The measured justification for asking Horizons from the SITE rather than reusing the
        // geocentric CometEphemeris already in the codebase. Both fixtures are the same body over the
        // same instants, differing only in where the observer stood.
        //
        // What corrupts a rate is not the parallax OFFSET but its CHANGE: a constant offset merely
        // shifts where the comet sits on the canvas, which registration absorbs. Across this run the
        // offset sweeps 3.27 px down to 0.68 px, so ~2.6 px of apparent motion is pure observer
        // geometry -- against a 0.11 px SIP residual, and worth several percent of the rate itself.
        var wcs = FieldWcs(331.74, -30.49);
        var topo = CometRateSolver.SolveCanvasRatePxPerHour(wcs, FrozenTrack());
        var geo = CometRateSolver.SolveCanvasRatePxPerHour(wcs, FrozenGeocentricTrack());

        topo.ShouldNotBeNull();
        geo.ShouldNotBeNull();

        var drift = Vector2.Distance(topo.Value.PxPerHour, geo.Value.PxPerHour);
        var span = (FrozenTrack()[^1].TimeUtc - FrozenTrack()[0].TimeUtc).TotalHours;
        output.WriteLine($"topocentric {topo.Value.PxPerHour.Length():F3} px/hr, "
            + $"geocentric {geo.Value.PxPerHour.Length():F3} px/hr, "
            + $"rate difference {drift:F3} px/hr = {drift * span:F2} px over {span:F2} h "
            + $"({100 * drift / topo.Value.PxPerHour.Length():F1}% of the rate)");

        // Accumulated over the run this is ~2.6 px, more than twenty times the registration residual
        // the WCS achieves (0.11 px), so it is nowhere near ignorable.
        (drift * span).ShouldBeGreaterThan(2.0);
        (drift * span).ShouldBeLessThan(3.5);

        // And the error is mostly in DIRECTION, not speed, which is worth pinning because it is the
        // opposite of what a casual reading suggests: the two SPEEDS are within 0.16 px/hr of each
        // other (12.65 vs 12.81), so a test comparing magnitudes would conclude the geocentric track
        // was nearly good enough. The rate VECTORS differ by 0.78 px/hr because the parallax term
        // sweeps across the field rather than along the comet's path, which is ~3.5 degrees of
        // heading -- and a heading error is exactly what smears a stack over 3.5 hours.
        var headingDeg = double.RadiansToDegrees(Math.Acos(Math.Clamp(
            Vector2.Dot(Vector2.Normalize(topo.Value.PxPerHour), Vector2.Normalize(geo.Value.PxPerHour)),
            -1f, 1f)));
        output.WriteLine($"heading difference {headingDeg:F2} deg");
        headingDeg.ShouldBeGreaterThan(1.0);

        // THE RESIDUAL DOES NOT MERELY MISS THIS -- IT PREFERS THE WRONG ANSWER. MaxResidualPx measures
        // how STRAIGHT a track was, never whether it pointed the right way, and the geocentric track is
        // straighter: parallax is precisely what bends the topocentric one, so removing the observer
        // removes the curvature along with the correctness. Measured here at 0.0142 px geocentric
        // against 0.1589 px topocentric, a factor of TEN in favour of the track that is wrong by
        // 3.4 degrees of heading.
        //
        // So a quality gate on this number alone does not just fail to reject a bad ephemeris, it
        // ranks it first. The defence is asking topocentrically by construction, and ultimately
        // checking the prediction against the nucleus's own centroids (item 2 in the plan) -- a
        // heading error shows there as a GROWING cross-track offset, ~2.7 px by the end of this run
        // against a 2.15 px FWHM, which is unmissable.
        output.WriteLine($"residuals: topocentric {topo.Value.MaxResidualPx:F4} px, "
            + $"geocentric {geo.Value.MaxResidualPx:F4} px -- the WRONG track fits BETTER");
        geo.Value.MaxResidualPx.ShouldBeLessThan(topo.Value.MaxResidualPx);
    }

    [Fact]
    public void AnUnusableTrackAnswersNullRatherThanAGuess()
    {
        var wcs = FieldWcs(331.74, -30.49);
        var one = FrozenTrack().Take(1).ToArray();
        CometRateSolver.SolveCanvasRatePxPerHour(wcs, one).ShouldBeNull();

        // Every sample at one instant: arithmetically there is no slope, and answering zero would be
        // a silent "this comet does not move".
        var sameInstant = FrozenTrack().Select(s => s with { TimeUtc = FrozenTrack()[0].TimeUtc }).ToArray();
        CometRateSolver.SolveCanvasRatePxPerHour(wcs, sameInstant).ShouldBeNull();

        // No CD matrix: nothing to project through.
        CometRateSolver.SolveCanvasRatePxPerHour(new WCS(22.1, -30.49), FrozenTrack()).ShouldBeNull();
    }
}
