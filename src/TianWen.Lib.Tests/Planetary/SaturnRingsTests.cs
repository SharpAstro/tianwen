using System;
using System.Collections.Immutable;
using System.Globalization;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Saturn's rings in the render (docs/plans/planetary-restoration.md, S1, #1231): where the ephemeris puts them, against Meeus's worked
/// example, and that the render draws them there, in depth order with the globe, and with each one's shadow on the other. The rule was
/// set before the first run: B and P to 0.01 degree, the ring's apparent axes to 0.05 arcsec with Meeus's own constant for the outer
/// edge, the rendered ellipse's axes to a tenth of a pixel.
/// </summary>
public class SaturnRingsTests
{
    // 1992 December 16, 0h TD (Meeus's example 45.a): TT - UTC was 32.184 s plus 27 leap seconds.
    private static readonly DateTimeOffset MeeusExample = new DateTimeOffset(1992, 12, 16, 0, 0, 0, TimeSpan.Zero).AddSeconds(-59.184);

    // The colour capture S3 calibrates its twin on.
    private static readonly DateTimeOffset Capture = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);

    private static PlanetMap Uniform(float value) => new PlanetMap(Fill(360, 180, value), 360, 180);

    private static float[] Fill(int width, int height, float value)
    {
        var values = new float[width * height];
        Array.Fill(values, value);
        return values;
    }

    // One ring, from 1.3 equatorial radii to the A ring's outer edge.
    private static SaturnRings Band(double level, double opticalDepth)
        => new SaturnRings([new SaturnRing("band", 1.3 * PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial, 136775, level, opticalDepth)]);

    private static PlanetAspect FullyLit(PlanetAspect aspect)
        => aspect with { SubSolarLongitudeIII = aspect.CentralMeridianIII, SubSolarLatitude = aspect.SubObserverLatitude, PhaseAngle = 0 };

    /// <summary>
    /// Meeus, Astronomical Algorithms (2nd ed.), example 45.a: Saturn on 1992 December 16 at 0h TD, B = 16.442, P = 6.741, and the outer
    /// ring's apparent axes a = 35.87 and b = 10.15 arcsec. Meeus's B is the Earth's Saturnicentric latitude referred to the ring plane,
    /// which is the equatorial plane, so the ephemeris' planetocentric sub-observer latitude; his P the position angle of the axis.
    /// </summary>
    /// <remarks>
    /// P FAILED the rule as set (2026-10-03): 6.715 against Meeus's 6.741, 0.026 degree where 0.01 was allowed. The cause is the pole,
    /// not the calculation. Meeus takes the ring plane from his own elements (i = 28.0752, node 169.5085 on the ecliptic of date), whose
    /// pole lies 0.029 degree from the IAU 2015 pole the ephemeris uses; astropy, given each pole, puts the axis at 6.743 with his (his
    /// 6.741) and 6.7125 with the IAU's. So P is held to the IAU pole's own position angle, an independent reckoning of the same
    /// quantity, and to Horizons by <c>PhysicalEphemerisTests</c>. At a ring 90 px long 0.026 degree is 0.04 px.
    /// </remarks>
    [Fact]
    public void TheRingsLieWhereMeeussWorkedExamplePutsThem()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, MeeusExample);
        var (major, minor) = SaturnRings.ApparentAxes(aspect, SaturnRings.MeeusOuterEdgeKm);
        var sunCentric = Math.Atan(Math.Pow(1 - aspect.Flattening, 2) * Math.Tan(aspect.SubSolarLatitude * Math.PI / 180)) * 180 / Math.PI;

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"B {aspect.SubObserverLatitudeCentric:0.0000} (16.442), B' {sunCentric:0.0000} (14.679), P {aspect.PolePositionAngle:0.0000} (6.741), a {major:0.000}\" (35.87), b {minor:0.000}\" (10.15), delta {aspect.DistanceAu:0.00000} AU"));

        aspect.SubObserverLatitudeCentric.ShouldBe(16.442, 0.01, "B");
        aspect.PolePositionAngle.ShouldBe(6.7125, 0.01, "P with the IAU pole, astropy's (Meeus's 6.741 has his own ring pole)");
        major.ShouldBe(35.87, 0.05, "the outer edge's major axis");
        minor.ShouldBe(10.15, 0.05, "the outer edge's minor axis");
    }

    public static TheoryData<string> Instants() => new TheoryData<string> { "1992-12-15T23:59:00.816Z", "2022-10-09T11:25:00Z" };

    /// <summary>
    /// The outer edge of an opaque ring, read off the render along the axes through the centre, has the ellipse its radius gives:
    /// a major axis of twice the edge's radius and a semi-minor axis of that times the sine of B, to a tenth of a pixel. The near vertex
    /// of the minor axis lies over the globe (the far one behind it), so the globe is drawn dark and the ring at one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Instants))]
    public void TheRenderedRingHasTheEllipseItsRadiusGives(string utc)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture));
        const int Width = 220, Height = 120, Radius = 40;
        const double Cx = 110, Cy = 60;
        // A globe a millionth as bright as the ring, which the ring's level is stated against.
        var image = PlanetaryRender.Render(Uniform(1e-6f), aspect, new DiskPlacement(Cx, Cy, Radius, NorthAngleDeg: -90), Width, Height, minnaertK: 0.9,
            supersample: 16, rings: Band(level: 1e6, opticalDepth: 50));

        var edge = 136775 / PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial * Radius;
        var sinB = Math.Sin(aspect.SubObserverLatitudeCentric * Math.PI / 180);
        var middle = (1.3 * Radius + edge) / 2;

        // West is +x with north up. Outward from a pixel inside the band, an edge lies at that pixel's near side plus the coverage summed.
        var row = (int)Cy;
        var west = (Cx + Math.Round(middle)) - 0.5 + Covered(i => image[(row * Width) + (int)(Cx + Math.Round(middle)) + i]);
        var east = (Cx - Math.Round(middle)) + 0.5 - Covered(i => image[(row * Width) + (int)(Cx - Math.Round(middle)) - i]);
        // The near side of the rings lies toward the pole turned away: south (+y) when the north face is seen.
        var toward = Math.Sign(sinB);
        var start = (int)(Cy + (toward * Math.Round(middle * Math.Abs(sinB))));
        var near = start + (toward * (Covered(i => image[((start + (toward * i)) * Width) + (int)Cx]) - 0.5));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{utc}: B {aspect.SubObserverLatitudeCentric:0.000}; major {west - east:0.000} px ({2 * edge:0.000}), semi-minor {Math.Abs(near - Cy):0.000} px ({edge * Math.Abs(sinB):0.000})"));
        (west - east).ShouldBe(2 * edge, 0.1, "the major axis");
        Math.Abs(near - Cy).ShouldBe(edge * Math.Abs(sinB), 0.1, "the semi-minor axis");
        ((west + east) / 2).ShouldBe(Cx, 0.05, "the ellipse is centred on the globe");

        // The coverage summed outward until the ring ends.
        static double Covered(Func<int, float> at)
        {
            var sum = 0.0;
            for (var i = 0; i < 60 && at(i) > 1e-3; i++)
            {
                sum += Math.Min(1, at(i));
            }
            return sum;
        }
    }

    /// <summary>
    /// Lit from the observer's own direction every shadow falls behind what casts it, so the depth order shows alone: an opaque ring
    /// before the globe is the ring's level, a half-clear one lets through its transmission of the globe, and the globe hides the far
    /// side of the rings, where the render is the globe's alone, bit for bit.
    /// </summary>
    [Fact]
    public void TheNearSideOfTheRingsHidesTheGlobeAndTheGlobeTheFarSide()
    {
        var aspect = FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture));
        var placement = new DiskPlacement(110, 60, 40, NorthAngleDeg: -90);
        var map = Uniform(0.5f);
        var globe = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 4);
        var opaque = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 4, rings: Band(level: 0.8, opticalDepth: 50));
        var clear = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 4, rings: Band(level: 0, opticalDepth: 0.3));

        var sinB = Math.Sin(aspect.SubObserverLatitudeCentric * Math.PI / 180);
        var edge = 136775 / PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial * 40;
        var middle = (1.3 * 40 + edge) / 2 * Math.Abs(sinB);
        var nearPixel = (((int)Math.Round(60 + (Math.Sign(sinB) * middle))) * 220) + 110;
        var farPixel = (((int)Math.Round(60 - (Math.Sign(sinB) * middle))) * 220) + 110;

        opaque[nearPixel].ShouldBe((float)(0.8 * 0.5), 1e-6f, "an opaque ring before the globe");
        // Lit from our direction, the Sun's ray to that point of the globe crosses the same ring: the light passes it twice.
        clear[nearPixel].ShouldBe((float)(globe[nearPixel] * Math.Exp(-2 * 0.3 / Math.Abs(sinB))), 1e-6f, "the globe through a half-clear ring");
        opaque[farPixel].ShouldBe(globe[farPixel], "the far side of the rings behind the globe");
    }

    /// <summary>
    /// With the Sun off the line of sight, as at the colour capture, each body's shadow shows on the other: the globe's on the rings
    /// beside the limb away from the Sun, and the rings' on the globe, at the ring's transmission along the Sun's ray. Lit from the
    /// observer's direction, neither shows.
    /// </summary>
    [Fact]
    public void TheGlobeAndTheRingsShadowEachOtherAwayFromTheSun()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
        var placement = new DiskPlacement(110, 60, 40, NorthAngleDeg: -90);
        var map = Uniform(0.5f);
        var (sunWest, _, _) = PhysicalEphemeris.SunOnTheDisk(aspect);
        var projection = new PlanetaryProjection(aspect, placement);
        var sinB = projection.SinD;
        var sinSun = projection.SunSinElevation;

        // The globe's shadow on an opaque ring: ring pixels off the globe's disk that are dark.
        var opaque = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 1, rings: Band(level: 0.8, opticalDepth: 50));
        var lit = PlanetaryRender.Render(map, FullyLit(aspect), placement, 220, 120, minnaertK: 0.9, supersample: 1, rings: Band(level: 0.8, opticalDepth: 50));
        int shadowed = 0, wrongSide = 0, shadowedWhenLitFromUs = 0;
        for (var y = 0; y < 120; y++)
        {
            for (var x = 0; x < 220; x++)
            {
                if (projection.TrySurface(x, y, out _, out _, out _, out _) || !projection.TryRingPlane(x, y, out var radius, out _, out _, out _)
                    || !Band(0.8, 50).TryRingAt(radius, out _))
                {
                    continue;
                }
                if (opaque[(y * 220) + x] == 0)
                {
                    shadowed++;
                    wrongSide += (x - 110) * sunWest > 0 ? 1 : 0;
                }
                shadowedWhenLitFromUs += lit[(y * 220) + x] == 0 ? 1 : 0;
            }
        }

        // The rings' shadow on the globe, through a half-clear ring of no brightness of its own: a globe pixel is dimmed by the ring
        // before it (along the line of sight), by the ring the Sun's ray crosses, or by both.
        var globe = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 1);
        var clear = PlanetaryRender.Render(map, aspect, placement, 220, 120, minnaertK: 0.9, supersample: 1, rings: Band(level: 0, opticalDepth: 0.3));
        var (throughView, throughSun) = (Math.Exp(-0.3 / Math.Abs(sinB)), Math.Exp(-0.3 / Math.Abs(sinSun)));
        int inRingShadow = 0, unexplained = 0;
        for (var i = 0; i < globe.Length; i++)
        {
            if (globe[i] <= 0)
            {
                continue;
            }
            var ratio = clear[i] / globe[i];
            if (Math.Abs(ratio - throughSun) < 1e-5)
            {
                inRingShadow++;
            }
            else if (Math.Abs(ratio - 1) > 1e-5 && Math.Abs(ratio - throughView) > 1e-5 && Math.Abs(ratio - (throughView * throughSun)) > 1e-5)
            {
                unexplained++;
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"B {aspect.SubObserverLatitudeCentric:0.000}, B' {Math.Asin(sinSun) * 180 / Math.PI:0.000}, phase {aspect.PhaseAngle:0.000}, sun west {sunWest:+0.0000;-0.0000}: " +
            $"{shadowed} ring pixels in the globe's shadow ({wrongSide} toward the Sun, {shadowedWhenLitFromUs} lit from us); {inRingShadow} globe pixels in the rings' shadow alone, {unexplained} unexplained"));
        shadowed.ShouldBeGreaterThan(0, "the globe's shadow on the rings");
        wrongSide.ShouldBe(0, "the globe's shadow falls away from the Sun");
        shadowedWhenLitFromUs.ShouldBe(0, "lit from the observer, the globe's shadow is behind it");
        inRingShadow.ShouldBeGreaterThan(0, "the rings' shadow on the globe");
        unexplained.ShouldBe(0, "every dimmed globe pixel is the ring before it, the ring before the Sun, or both");
    }

    /// <summary>
    /// OPAL's Saturn maps hold zeros where Hubble never saw the globe; a filled map holds none, keeps every valid sample, and fills a
    /// valid row's holes with that row's mean.
    /// </summary>
    [Fact]
    public void AFilledMapKeepsItsSamplesAndHasNoHoles()
    {
        // Rows 0 to 3 valid with a hole each, rows 4 and 5 empty, rows 6 and 7 valid.
        const int W = 8, H = 8;
        var values = new float[W * H];
        for (var r = 0; r < H; r++)
        {
            for (var c = 0; c < W; c++)
            {
                values[(r * W) + c] = r is 4 or 5 ? 0 : (r + 1) + (c * 0.1f);
            }
            if (r < 4)
            {
                values[(r * W) + 3] = 0;
            }
        }
        var filled = new PlanetMap(values, W, H).FilledZonally(edgeDegrees: 0);

        for (var r = 0; r < H; r++)
        {
            for (var c = 0; c < W; c++)
            {
                var latitude = 90 - ((r + 0.5) * 180.0 / H);
                var west = 360 - ((c + 0.5) * 360.0 / W);
                var value = filled.Sample(latitude, west);
                value.ShouldBeGreaterThan(0, $"row {r}, column {c}");
                if (values[(r * W) + c] > 0)
                {
                    value.ShouldBe(values[(r * W) + c], 1e-5, $"row {r}, column {c} kept");
                }
            }
        }
        var rowMean = (1 + (0.1 * (0 + 1 + 2 + 4 + 5 + 6 + 7) / 7.0));
        filled.Sample(90 - (0.5 * 180.0 / H), 360 - (3.5 * 360.0 / W)).ShouldBe(rowMean, 1e-5, "a valid row's hole at its mean");
        // Row 4 lies a third of the way from row 3 (its hole at the row's mean) to row 6.
        filled.Sample(90 - (4.5 * 180.0 / H), 360 - (0.5 * 360.0 / W)).ShouldBe(4 + ((7 - 4) / 3.0), 1e-5, "an empty row between valid ones");
        var whole = new PlanetMap(Fill(4, 4, 1f), 4, 4);
        whole.FilledZonally().ShouldBeSameAs(whole, "a map with no holes");
    }

    /// <summary>
    /// The samples beside a hole are seen at the edge of what Hubble saw and read low (a tenth of their row on the 2022 F631N map): within
    /// two degrees of a hole they go with it, and the rows either side fill it.
    /// </summary>
    [Fact]
    public void TheSamplesBesideAHoleGoWithIt()
    {
        // A uniform map, one sample a degree, a hole from row 100 to 109 whose edge rows read a tenth and a fifth.
        const int W = 360, H = 180;
        var values = Fill(W, H, 1f);
        for (var c = 0; c < W; c++)
        {
            for (var r = 100; r < 110; r++)
            {
                values[(r * W) + c] = 0;
            }
            values[(99 * W) + c] = 0.1f;
            values[(110 * W) + c] = 0.2f;
        }
        var filled = new PlanetMap(values, W, H).FilledZonally();

        for (var r = 95; r < 115; r++)
        {
            filled.Sample(90 - (r + 0.5), 180.5).ShouldBe(1, 1e-6, $"row {r}");
        }
    }
}
