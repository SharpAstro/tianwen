using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TianWen.Lib.Astrometry.Comets;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The topocentric track that drives comet-aligned registration, pinned against a FROZEN REAL
/// response (<c>Data/horizons-10p-observer-2026-08-16.txt</c>: 10P/Tempel 2 seen from the site in
/// the 2026-08-16 frames' own headers, over that session's span).
///
/// <para>Frozen rather than live for the usual reason -- a unit test must not depend on JPL being
/// reachable -- but also because this response is EVIDENCE. The rate it implies reproduces the
/// hand-derived number in docs/plans/comet-integration.md to a fraction of a percent, from a
/// completely different route, and that agreement is the argument that the plan's measurement was
/// right. A live fetch would quietly re-derive both sides of that comparison.</para>
/// </summary>
[Collection("Catalog")]
public class HorizonsObserverSourceTests(ITestOutputHelper output)
{
    private static string FrozenResponse()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("horizons-10p-observer-2026-08-16.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheFrozenTrackParsesToItsEightSamples()
    {
        HorizonsObserverSource.TryParse(FrozenResponse(), out var samples).ShouldBeTrue();

        samples.Length.ShouldBe(8);
        samples[0].TimeUtc.ShouldBe(new DateTimeOffset(2026, 8, 16, 10, 53, 0, TimeSpan.Zero));
        samples[0].RaDeg.ShouldBe(331.72659, tolerance: 1e-5);
        samples[0].DecDeg.ShouldBe(-30.46172, tolerance: 1e-5);
        samples[^1].TimeUtc.ShouldBe(new DateTimeOffset(2026, 8, 16, 14, 23, 0, TimeSpan.Zero));
        samples[^1].RaDeg.ShouldBe(331.75809, tolerance: 1e-5);
        samples[^1].DecDeg.ShouldBe(-30.51298, tolerance: 1e-5);
    }

    [Fact]
    public void AFlagColumnIsNeverReadAsACoordinate()
    {
        // Horizons puts two UNNAMED flag columns (solar / lunar presence) between the date and the
        // coordinates, and populates them only sometimes: in this very fixture the first two rows
        // carry "m" and the rest are blank. A positional reader validated against the blank rows
        // would read a flag as a coordinate on the populated ones, so the columns are found by
        // header name. The two rows that would break it are asserted explicitly.
        HorizonsObserverSource.TryParse(FrozenResponse(), out var samples).ShouldBeTrue();

        samples[0].RaDeg.ShouldBeInRange(331.0, 332.0);
        samples[1].RaDeg.ShouldBeInRange(331.0, 332.0);
        samples[1].DecDeg.ShouldBeInRange(-31.0, -30.0);
    }

    [Fact]
    public void TheTrackReproducesTheHandDerivedRateFromThePlan()
    {
        // docs/plans/comet-integration.md records 44.7 px of drift over 3.538 h at 12.64 px/hr on a
        // position angle of 152 deg, measured by hand on the other machine. Recomputing it from this
        // ephemeris is an INDEPENDENT route to the same numbers, which is what makes either of them
        // trustworthy. Plate scale 4.7172"/px, the solved value from the same plan.
        const double PlateScaleArcsecPerPx = 4.7172;
        HorizonsObserverSource.TryParse(FrozenResponse(), out var samples).ShouldBeTrue();

        var first = samples[0];
        var last = samples[^1];
        var hours = (last.TimeUtc - first.TimeUtc).TotalHours;

        // RA converges toward the pole, so the true angular separation in the RA direction carries
        // cos(dec). Omitting it would understate the motion by 14% at this declination.
        var meanDecRad = double.DegreesToRadians((first.DecDeg + last.DecDeg) / 2.0);
        var dRaArcsec = (last.RaDeg - first.RaDeg) * 3600.0 * Math.Cos(meanDecRad);
        var dDecArcsec = (last.DecDeg - first.DecDeg) * 3600.0;

        var driftPx = Math.Sqrt(dRaArcsec * dRaArcsec + dDecArcsec * dDecArcsec) / PlateScaleArcsecPerPx;
        var ratePxPerHour = driftPx / hours;
        // Position angle: north through east.
        var positionAngleDeg = (double.RadiansToDegrees(Math.Atan2(dRaArcsec, dDecArcsec)) + 360.0) % 360.0;

        output.WriteLine($"span {hours:F3} h, drift {driftPx:F2} px, rate {ratePxPerHour:F3} px/hr, PA {positionAngleDeg:F1} deg");

        ratePxPerHour.ShouldBe(12.64, tolerance: 0.15);
        positionAngleDeg.ShouldBe(152.0, tolerance: 1.0);
        // Scaled to the full 3.538 h run the plan measured over, rather than this fixture's 3.5 h.
        (ratePxPerHour * 3.538).ShouldBe(44.7, tolerance: 0.6);
    }

    [Fact]
    public void TheQueryAsksTheRightPlaceInTheRightUnits()
    {
        // SITE_COORD is E-longitude, latitude, altitude in KILOMETRES. The unit is the trap: every
        // other altitude in this codebase is metres, and a 1000x error is a parallax mistake too
        // small to spot by eye and too large to accept in a registration rate.
        var url = HorizonsObserverSource.BuildQuery(
            new Uri("https://ssd.jpl.nasa.gov/api/horizons.api"),
            "10P",
            siteLatDeg: -37.876389, siteLonDeg: 145.178056, siteElevMetres: 120.0,
            new DateTimeOffset(2026, 8, 16, 10, 53, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 16, 14, 26, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(30));

        output.WriteLine(url);
        Uri.UnescapeDataString(url).ShouldContain("SITE_COORD='145.178056,-37.876389,0.12'");
        Uri.UnescapeDataString(url).ShouldContain("EPHEM_TYPE=OBSERVER");
        Uri.UnescapeDataString(url).ShouldContain("QUANTITIES='1'");
        Uri.UnescapeDataString(url).ShouldContain("STEP_SIZE='30 m'");
    }
}
