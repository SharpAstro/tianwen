using System;
using System.Globalization;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The apparent V magnitude of every body the atlas draws (docs/plans/atlas-planet-detail.md, A1, finding 2), against JPL
/// Horizons' APmag at the four instants of <c>horizons-bodies-2026-10-14.txt</c>: Horizons evaluates Mallama and Hilton
/// (2018) for the planets, as <see cref="PlanetaryMagnitude"/> does. The bound was set before the first run: 0.02 mag for a
/// planet and the Sun (Horizons prints three decimals, and our geometry differs from its by the hundredths of a degree
/// <see cref="PhysicalEphemerisBodiesTests"/> allows), 0.05 for the Moon.
/// </summary>
public class PlanetaryMagnitudeTests
{
    public static TheoryData<string, string> Rows() => PhysicalEphemerisBodiesTests.Rows();

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheMagnitudeIsHorizonsOwn(string naifId, string utc)
    {
        var row = PhysicalEphemerisBodiesTests.Read("horizons-bodies-2026-10-14.txt").Single(r => r["body"] == naifId && r["utc"] == utc);
        var body = naifId switch
        {
            "10" => CatalogIndex.Sol,
            "199" => CatalogIndex.Mercury,
            "299" => CatalogIndex.Venus,
            "301" => CatalogIndex.Moon,
            "499" => CatalogIndex.Mars,
            "599" => CatalogIndex.Jupiter,
            "699" => CatalogIndex.Saturn,
            "799" => CatalogIndex.Uranus,
            _ => CatalogIndex.Neptune,
        };
        var at = DateTimeOffset.ParseExact(utc, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var expected = double.Parse(row["apmag"], CultureInfo.InvariantCulture);

        var magnitude = PlanetaryMagnitude.Apparent(PhysicalEphemeris.Compute(body, at));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{body} {utc}: {magnitude:0.000} against {expected:0.000} ({magnitude - expected:+0.000;-0.000})"));
        magnitude.ShouldBe(expected, body == CatalogIndex.Moon ? 0.05 : 0.02);
    }

    /// <summary>
    /// Saturn's rings are most of its light when open and none of it when the Earth and the Sun light opposite faces: at the
    /// same distances and phase, the 2020 opening (B about 22 degrees) is about 0.8 mag brighter than an edge-on ring (Mallama
    /// and Hilton's ring term, 1.825 sin B plus 0.378 sin B at zero phase, B the geometric mean of the two openings).
    /// </summary>
    [Fact]
    public void SaturnsRingsBrightenIt()
    {
        var open = PhysicalEphemeris.Compute(CatalogIndex.Saturn, new DateTimeOffset(2020, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var edgeOn = open with { SubObserverLatitude = 0, SubObserverLatitudeCentric = 0, SubSolarLatitude = 0, SubSolarLatitudeCentric = 0 };
        var darkSide = open with { SubSolarLatitudeCentric = -open.SubSolarLatitudeCentric };
        var sinB = Math.Sin(Math.Sqrt(open.RingOpening * open.RingOpeningFromSun) * Math.PI / 180);

        sinB.ShouldBeGreaterThan(0.3, "the 2020 rings were open");
        (PlanetaryMagnitude.Apparent(edgeOn) - PlanetaryMagnitude.Apparent(open)).ShouldBe((1.825 * sinB) + (0.378 * sinB * Math.Exp(-2.25 * open.PhaseAngle)), 1e-9);
        PlanetaryMagnitude.Apparent(darkSide).ShouldBe(PlanetaryMagnitude.Apparent(edgeOn), 1e-9, "the rings seen from their unlit side add nothing");
    }
}
