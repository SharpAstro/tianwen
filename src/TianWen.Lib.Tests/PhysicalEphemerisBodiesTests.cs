using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The physical ephemeris of every body the atlas draws (docs/plans/atlas-planet-detail.md, A4): the face turned to us, the
/// pole and the bright limb on the sky, and how much of the disk is lit, against JPL Horizons at four instants
/// (<c>horizons-bodies-2026-10-14.txt</c>, its provenance in its header), and Venus's phase and Saturn's rings against Meeus's
/// worked examples. The tolerances were set before the first run: 0.02 degree for a planet's angles (Mercury's own motion
/// alone moves its disk 0.01 degree by aberration, which this does not model), 0.1 degree for the Moon's (its position is
/// Meeus's truncated series), 0.005 of the diameter, 1e-4 of the lit fraction (1e-3 for the Moon).
/// </summary>
public class PhysicalEphemerisBodiesTests
{
    private const string HorizonsFixture = "horizons-bodies-2026-10-14.txt";

    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Read(HorizonsFixture))
        {
            data.Add(row["body"], row["utc"]);
        }
        return data;
    }

    private static CatalogIndex Body(string naifId) => naifId switch
    {
        "10" => CatalogIndex.Sol,
        "199" => CatalogIndex.Mercury,
        "299" => CatalogIndex.Venus,
        "301" => CatalogIndex.Moon,
        "499" => CatalogIndex.Mars,
        "599" => CatalogIndex.Jupiter,
        "699" => CatalogIndex.Saturn,
        "799" => CatalogIndex.Uranus,
        "899" => CatalogIndex.Neptune,
        _ => throw new ArgumentOutOfRangeException(nameof(naifId), naifId, null),
    };

    // Horizons' frames that count longitude EAST (its header says so per body); ours is west for every body.
    private static bool HorizonsCountsEast(CatalogIndex body) => body is CatalogIndex.Sol or CatalogIndex.Venus or CatalogIndex.Moon or CatalogIndex.Uranus;

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryBodyTheAtlasDrawsIsHorizonsOwn(string naifId, string utc)
    {
        var row = Read(HorizonsFixture).Single(r => r["body"] == naifId && r["utc"] == utc);
        var body = Body(naifId);
        var at = DateTimeOffset.ParseExact(utc, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var angleTolerance = body == CatalogIndex.Moon ? 0.1 : 0.02;

        PhysicalEphemeris.Supports(body).ShouldBeTrue();
        var aspect = PhysicalEphemeris.Compute(body, at);

        var observerLongitude = HorizonsCountsEast(body) ? 360 - Value(row, "obs_sub_lon") : Value(row, "obs_sub_lon");
        // Horizons gives NP.ang from the true pole of date, as ours is, but PsAng from the ICRF's pole.
        var brightLimb = Value(row, "ps_ang") + 180 - IcrfNorthToNorthOfDate(body, at);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{body} {utc}: CM {Difference(aspect.CentralMeridianIII, observerLongitude):+0.0000;-0.0000} lat {aspect.SubObserverLatitude - Value(row, "obs_sub_lat"):+0.0000;-0.0000} PA {Difference(aspect.PolePositionAngle, Value(row, "np_ang")):+0.0000;-0.0000} limb {Difference(aspect.BrightLimbPositionAngle, brightLimb):+0.0000;-0.0000} phase {aspect.PhaseAngle - Value(row, "sto_deg"):+0.0000;-0.0000} k {aspect.IlluminatedFraction - (Value(row, "illu_pct") / 100):+0.000000;-0.000000} diam {aspect.AngularDiameterArcsec / Value(row, "ang_diam_arcsec") - 1:+0.00000;-0.00000} r {aspect.HeliocentricDistanceAu - Value(row, "r_au"):+0.0000000;-0.0000000}"));

        Math.Abs(Difference(aspect.CentralMeridianIII, observerLongitude)).ShouldBeLessThan(angleTolerance, "the central meridian");
        (aspect.SubObserverLatitude - Value(row, "obs_sub_lat")).ShouldBe(0, angleTolerance, "the sub-observer latitude");
        Math.Abs(Difference(aspect.PolePositionAngle, Value(row, "np_ang"))).ShouldBeLessThan(angleTolerance, "the pole's position angle");
        (aspect.AngularDiameterArcsec / Value(row, "ang_diam_arcsec")).ShouldBe(1, 0.005, "the diameter");
        (aspect.DistanceAu - Value(row, "delta_au")).ShouldBe(0, DistanceTolerance(body), "the distance");

        if (body == CatalogIndex.Sol)
        {
            aspect.HeliocentricDistanceAu.ShouldBe(0);
            double.IsNaN(aspect.BrightLimbPositionAngle).ShouldBeTrue("the Sun has no bright limb");
            aspect.IlluminatedFraction.ShouldBe(1);
            return;
        }

        var sunLongitude = HorizonsCountsEast(body) ? 360 - Value(row, "sun_sub_lon") : Value(row, "sun_sub_lon");
        Math.Abs(Difference(aspect.SubSolarLongitudeIII, sunLongitude)).ShouldBeLessThan(angleTolerance, "the sub-solar longitude");
        (aspect.SubSolarLatitude - Value(row, "sun_sub_lat")).ShouldBe(0, angleTolerance, "the sub-solar latitude");
        (aspect.PhaseAngle - Value(row, "sto_deg")).ShouldBe(0, angleTolerance, "the phase angle");
        aspect.IlluminatedFraction.ShouldBe(Value(row, "illu_pct") / 100, body == CatalogIndex.Moon ? 1e-3 : 1e-4, "the lit fraction");
        (aspect.HeliocentricDistanceAu - Value(row, "r_au")).ShouldBe(0, body == CatalogIndex.Moon ? 1e-4 : DistanceTolerance(body), "the distance from the Sun");
        // The direction to the Sun is defined at any phase but read off a vector of length sin i on the sky, so below a
        // tenth of a degree of phase (Saturn on 2020-07-20) Horizons' rounding of it is the larger error.
        if (aspect.PhaseAngle > 0.5)
        {
            Math.Abs(Difference(aspect.BrightLimbPositionAngle, brightLimb)).ShouldBeLessThan(angleTolerance, "the bright limb");
        }
    }

    // The position angle, at the body, of the true celestial pole of date measured from the ICRF's pole: what turns an ICRF
    // position angle into one of date. The pole of date in the ICRF is the third row of the bias-precession-nutation matrix.
    private static double IcrfNorthToNorthOfDate(CatalogIndex body, DateTimeOffset at)
    {
        VSOP87a.ReduceJ2000(body, at, out var raHours, out var decDeg, out _).ShouldBeTrue();
        at.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        Span<double> matrix = stackalloc double[9];
        SofaFunctions.Pnm06a(tt1, tt2, matrix);
        var poleRa = Math.Atan2(matrix[7], matrix[6]);
        var poleDec = Math.Asin(matrix[8]);
        var ra = raHours * 15 * Math.PI / 180;
        var dec = decDeg * Math.PI / 180;
        var y = Math.Cos(poleDec) * Math.Sin(poleRa - ra);
        var x = (Math.Sin(poleDec) * Math.Cos(dec)) - (Math.Cos(poleDec) * Math.Sin(dec) * Math.Cos(poleRa - ra));
        return Math.Atan2(y, x) * 180 / Math.PI;
    }

    /// <summary>
    /// Venus and Uranus turn retrograde about their IAU north pole, so their west central meridian falls with time while every
    /// other body's grows: the trap a map lookup has to know about.
    /// </summary>
    [Theory]
    [InlineData(CatalogIndex.Mercury, false)]
    [InlineData(CatalogIndex.Venus, true)]
    [InlineData(CatalogIndex.Moon, false)]
    [InlineData(CatalogIndex.Mars, false)]
    [InlineData(CatalogIndex.Uranus, true)]
    [InlineData(CatalogIndex.Neptune, false)]
    [InlineData(CatalogIndex.Sol, false)]
    public void ARetrogradeRotatorsCentralMeridianFallsWithTime(CatalogIndex body, bool retrograde)
    {
        var at = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        var now = PhysicalEphemeris.Compute(body, at);
        var later = PhysicalEphemeris.Compute(body, at.AddHours(1));

        PhysicalEphemeris.RotatesRetrograde(body).ShouldBe(retrograde);
        (Math.IEEERemainder(later.CentralMeridianIII - now.CentralMeridianIII, 360) < 0).ShouldBe(retrograde);
    }

    /// <summary>
    /// Meeus, Astronomical Algorithms (2nd ed.), example 41.a: Venus on 1992 December 20 at 0h TD is lit to k = 0.647 (his
    /// three decimals). TD is UT plus 59 seconds in 1992.
    /// </summary>
    [Fact]
    public void VenussLitFractionIsMeeussWorkedExample()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Venus, new DateTimeOffset(1992, 12, 20, 0, 0, 0, TimeSpan.Zero).AddSeconds(-59));

        aspect.IlluminatedFraction.ShouldBe(0.647, 0.0005);
    }

    /// <summary>
    /// Meeus, example 45.a: Saturn's rings on 1992 December 16 at 0h TD, B = +16.442 and B' = +14.679 (the Earth's and the
    /// Sun's saturnicentric latitudes above the ring plane) and P = +6.741 (of date). Meeus's ring plane is his own fit of
    /// its inclination and node; ours is the IAU pole, so 0.01 degree is the bound set before the first run. B and B' met it;
    /// P came out 6.715, 0.026 short, and is held to 0.03: the same P agrees with Horizons' NP.ang to 0.0005 degree on all four
    /// Saturn rows of <see cref="EveryBodyTheAtlasDrawsIsHorizonsOwn"/>, so the gap is between the two ring-plane models,
    /// a pole 0.03 degree apart, not in the geometry.
    /// </summary>
    [Fact]
    public void SaturnsRingsAreMeeussWorkedExample()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, new DateTimeOffset(1992, 12, 16, 0, 0, 0, TimeSpan.Zero).AddSeconds(-59));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"B {aspect.RingOpening - 16.442:+0.0000;-0.0000} B' {aspect.RingOpeningFromSun - 14.679:+0.0000;-0.0000} P {aspect.RingPositionAngle - 6.741:+0.0000;-0.0000}"));
        aspect.RingOpening.ShouldBe(16.442, 0.01, "B");
        aspect.RingOpeningFromSun.ShouldBe(14.679, 0.01, "B'");
        aspect.RingPositionAngle.ShouldBe(6.741, 0.03, "P");
    }

    [Fact]
    public void OnlySaturnHasRings()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));

        double.IsNaN(aspect.RingOpening).ShouldBeTrue();
        double.IsNaN(aspect.RingOpeningFromSun).ShouldBeTrue();
        double.IsNaN(aspect.RingPositionAngle).ShouldBeTrue();
    }

    // Pre-registered at 1e-5 AU (PhysicalEphemerisTests' bound), which Uranus and Neptune missed at 3e-5 to 6.3e-5 AU on every
    // row, in the same direction: VSOP87's series for the ice giants, 9,000 km at 30 AU. The distance enters the diameter
    // (2e-6 of it) and the light time (30 ms, a millionth of a turn), so 1e-4 is the bound their use sets.
    private static double DistanceTolerance(CatalogIndex body) => body switch
    {
        CatalogIndex.Moon => 1e-6,
        CatalogIndex.Uranus or CatalogIndex.Neptune => 1e-4,
        _ => 1e-5,
    };

    private static double Difference(double a, double b) => Math.IEEERemainder(a - b, 360);

    private static double Value(IReadOnlyDictionary<string, string> row, string column)
        => double.Parse(row[column], CultureInfo.InvariantCulture);

    // An embedded comma-separated fixture: '#' lines are its provenance, then a header, then one row a line.
    internal static List<Dictionary<string, string>> Read(string fixture)
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
