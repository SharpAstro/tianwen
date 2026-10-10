using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The physical ephemeris against JPL Horizons at the planetary corpus's own session times, and Jupiter's Systems I and II
/// against Meeus's worked example (docs/plans/planetary-restoration.md, R1, #1049). The tolerances were set before the first
/// run: 0.01 degree for every angle, 0.005 arcsec for the diameter.
/// </summary>
/// <remarks>
/// Measured (2026-09-29): the central meridian is 0.0024 degree below Horizons on every Jupiter night (Saturn 0.0019) and the
/// sub-solar longitude 0.0050 (0.0036), constant with date and in proportion to the planet's orbital speed: Horizons'
/// aberration by the planet's own motion (v/c, 9 arcsec for Jupiter), which this does not model. At a 48 arcsec disk's centre
/// that is a thousandth of an arcsecond. The pole angle was 0.15 degree off until it was referred to the true pole of date,
/// as Horizons' NP.ang is: against the ICRF's it drifts with precession.
/// </remarks>
public class PhysicalEphemerisTests
{
    private const double AngleTolerance = 0.01;

    private const string HorizonsFixture = "horizons-physical-ephemeris-2026-09-29.txt";
    private const string WinJuposFixture = "winjupos-central-meridians-2022.txt";

    public static TheoryData<string, string> Sessions()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Rows(HorizonsFixture))
        {
            data.Add(row["body"], row["utc"]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void TheFaceTurnedToUsAndHowItIsLitAreHorizonsOwn(string body, string utc)
    {
        var row = Rows(HorizonsFixture).Single(r => r["body"] == body && r["utc"] == utc);
        var planet = body switch { "499" => CatalogIndex.Mars, "599" => CatalogIndex.Jupiter, _ => CatalogIndex.Saturn };
        var at = DateTimeOffset.ParseExact(utc, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        var aspect = PhysicalEphemeris.Compute(planet, at);

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{planet} {utc}: CM {aspect.CentralMeridianIII - Value(row, "obs_sub_lon"):+0.0000;-0.0000} lat {aspect.SubObserverLatitude - Value(row, "obs_sub_lat"):+0.0000;-0.0000} sun lon {aspect.SubSolarLongitudeIII - Value(row, "sun_sub_lon"):+0.0000;-0.0000} sun lat {aspect.SubSolarLatitude - Value(row, "sun_sub_lat"):+0.0000;-0.0000} PA {aspect.PolePositionAngle - Value(row, "np_ang"):+0.0000;-0.0000} phase {aspect.PhaseAngle - Value(row, "sto_deg"):+0.0000;-0.0000} diam {aspect.AngularDiameterArcsec - Value(row, "ang_diam_arcsec"):+0.0000;-0.0000} arcsec"));

        AngleBetween(aspect.CentralMeridianIII, Value(row, "obs_sub_lon")).ShouldBeLessThan(AngleTolerance, "the central meridian (System III)");
        (aspect.SubObserverLatitude - Value(row, "obs_sub_lat")).ShouldBe(0, AngleTolerance, "the sub-observer latitude");
        AngleBetween(aspect.SubSolarLongitudeIII, Value(row, "sun_sub_lon")).ShouldBeLessThan(AngleTolerance, "the sub-solar longitude");
        (aspect.SubSolarLatitude - Value(row, "sun_sub_lat")).ShouldBe(0, AngleTolerance, "the sub-solar latitude");
        AngleBetween(aspect.PolePositionAngle, Value(row, "np_ang")).ShouldBeLessThan(AngleTolerance, "the pole's position angle");
        (aspect.PhaseAngle - Value(row, "sto_deg")).ShouldBe(0, AngleTolerance, "the phase angle");
        (aspect.AngularDiameterArcsec - Value(row, "ang_diam_arcsec")).ShouldBe(0, 0.005, "the equatorial diameter");
        // Pre-registered at 1e-6 AU, which Saturn missed on 2022-10-09 at 3.2e-6 (470 km, VSOP87's Saturn). The distance
        // only enters through the diameter and the light time, and 1e-5 AU is 5 ms of light (a twentieth of a thousandth of a
        // degree of Jupiter's rotation) and 1e-6 of the diameter, so that is the bound its use sets.
        (aspect.DistanceAu - Value(row, "delta_au")).ShouldBe(0, 1e-5, "the distance");
    }

    /// <summary>
    /// Meeus, Astronomical Algorithms (2nd ed.), example 43.a: Jupiter on 1992 December 16 at 0h UT, omega1 = 268.06 and
    /// omega2 = 72.74 (his method's two decimals), DE = -2.48 (planetocentric) and P = +24.80. Meeus's central meridians are
    /// corrected for PHASE (the middle of the lit disk, 57.3 sin^2(i/2) toward the Sun, 0.43 degree at this 9.9 degree phase);
    /// ours are the geometric meridian Horizons gives, so the test adds his correction before comparing. Without it, both
    /// systems came out 0.42 and 0.49 degree short: the correction, not an error in the rates.
    /// </summary>
    [Fact]
    public void JupitersSystemsIAndIIAreMeeussWorkedExample()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, new DateTimeOffset(1992, 12, 16, 0, 0, 0, TimeSpan.Zero));
        var towardTheSun = Math.Sign(Math.IEEERemainder(aspect.SubSolarLongitudeIII - aspect.CentralMeridianIII, 360));
        var phaseCorrection = towardTheSun * 180 / Math.PI * Math.Pow(Math.Sin(aspect.PhaseAngle / 2 * Math.PI / 180), 2);

        AngleBetween(aspect.CentralMeridianI + phaseCorrection, 268.06).ShouldBeLessThan(0.1, "System I");
        AngleBetween(aspect.CentralMeridianII + phaseCorrection, 72.74).ShouldBeLessThan(0.1, "System II");
        aspect.SubObserverLatitudeCentric.ShouldBe(-2.48, 0.01, "DE, planetocentric");
        aspect.PolePositionAngle.ShouldBe(24.80, 0.01, "P");
    }

    public static TheoryData<string> WinJuposTimes()
    {
        var data = new TheoryData<string>();
        foreach (var row in Rows(WinJuposFixture))
        {
            data.Add(row["jd_ut"]);
        }
        return data;
    }

    /// <summary>
    /// WinJUPOS 12.1.2's own central meridians for the user's 2022 Jupiter images (its <c>.ims.xml</c> beside each stack): a
    /// second, independent implementation, and the only outside check of Systems I and II at the corpus's own times. Set
    /// before the first run: 0.05 degree for each system (WinJUPOS prints two decimals), 0.02 for the Earth's planetocentric
    /// declination.
    /// </summary>
    [Theory]
    [MemberData(nameof(WinJuposTimes))]
    public void JupitersThreeSystemsAreWinJuposOwn(string jd)
    {
        var row = Rows(WinJuposFixture).Single(r => r["jd_ut"] == jd);
        var at = DateTimeOffset.UnixEpoch.AddDays(double.Parse(jd, CultureInfo.InvariantCulture) - 2440587.5);

        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, at);

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"JD {jd}: CM1 {aspect.CentralMeridianI - Value(row, "cm1"):+0.000;-0.000} CM2 {aspect.CentralMeridianII - Value(row, "cm2"):+0.000;-0.000} CM3 {aspect.CentralMeridianIII - Value(row, "cm3"):+0.000;-0.000} DE {aspect.SubObserverLatitudeCentric - Value(row, "de"):+0.000;-0.000}"));
        AngleBetween(aspect.CentralMeridianI, Value(row, "cm1")).ShouldBeLessThan(0.05, "System I");
        AngleBetween(aspect.CentralMeridianII, Value(row, "cm2")).ShouldBeLessThan(0.05, "System II");
        AngleBetween(aspect.CentralMeridianIII, Value(row, "cm3")).ShouldBeLessThan(0.05, "System III");
        aspect.SubObserverLatitudeCentric.ShouldBe(Value(row, "de"), 0.02, "the Earth's planetocentric declination");
    }

    [Theory]
    [InlineData(CatalogIndex.Mars)]
    [InlineData(CatalogIndex.Saturn)]
    public void OnlyJupiterHasSystemsIAndII(CatalogIndex planet)
    {
        var aspect = PhysicalEphemeris.Compute(planet, new DateTimeOffset(2022, 10, 9, 11, 18, 0, TimeSpan.Zero));

        double.IsNaN(aspect.CentralMeridianI).ShouldBeTrue();
        double.IsNaN(aspect.CentralMeridianII).ShouldBeTrue();
    }

    [Fact]
    public void APlanetWithNoRotationModelIsRefused()
    {
        PhysicalEphemeris.Supports(CatalogIndex.Venus).ShouldBeFalse("no rotation model for Venus");
        Should.Throw<ArgumentOutOfRangeException>(() => PhysicalEphemeris.Compute(CatalogIndex.Venus, DateTimeOffset.UnixEpoch));
    }

    private static double AngleBetween(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360;
        return difference > 180 ? 360 - difference : difference;
    }

    private static double Value(IReadOnlyDictionary<string, string> row, string column)
        => double.Parse(row[column], CultureInfo.InvariantCulture);

    // An embedded comma-separated fixture: '#' lines are its provenance, then a header, then one row a line.
    private static List<Dictionary<string, string>> Rows(string fixture)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fixture, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        var header = lines[0].Split(',');
        return [.. lines.Skip(1).Select(l => header.Zip(l.Split(',')).ToDictionary(static p => p.First, static p => p.Second))];
    }
}
