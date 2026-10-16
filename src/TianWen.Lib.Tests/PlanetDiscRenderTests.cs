using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky atlas's textured planet discs at the renderer (docs/plans/atlas-planet-detail.md, "A4: the textured disc", #1037): the
/// colour render shares the mono render's geometry and leaves the mono render as it was, the lit limb faces the Sun, Saturn's near
/// ring crosses the globe, a turn of the central meridian moves a feature the planet's way, and the pole is turned on screen by BOTH
/// its position angle and the view's own north.
/// </summary>
public sealed class PlanetDiscRenderTests
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    // A map with latitude structure, so a wrong row or a wrong pole shows: bright in the north, dark in the south, mid at the equator.
    private static PlanetMap Zonal()
    {
        const int W = 72, H = 36;
        var values = new float[W * H];
        for (var r = 0; r < H; r++)
        {
            for (var c = 0; c < W; c++)
            {
                values[(r * W) + c] = 0.3f + (0.5f * (H - r) / H) + (0.05f * MathF.Sin(c * 0.4f));
            }
        }
        return new PlanetMap(values, W, H);
    }

    /// <summary>
    /// The mono render, the stacker's truth, is unchanged to the bit by the colour path that now shares its scene: a Saturn with rings
    /// (every branch of the ray trace: globe, ring in front, ring behind, both shadows) hashes as it did before the refactor.
    /// </summary>
    [Fact]
    public void TheMonoRenderIsUnchangedByTheColourPath()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Instant);
        var placement = new DiskPlacement(47.3, 51.1, 18.5, -70, Mirrored: false);

        var image = PlanetaryRender.Render(Zonal(), aspect, placement, 100, 96, 0.9, supersample: 4, rings: SaturnRings.Main);

        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(image.AsSpan())))[..16].ShouldBe("F5BA70A4D04E70C8", "the hash of this render on main before the colour path, which shares its scene");
    }

    /// <summary>
    /// The colour render IS the mono render's geometry: each plane of a grey colour map equals the mono render to float rounding, the
    /// terminator and the rings included, and the coverage is one over the globe and zero well off it.
    /// </summary>
    [Fact]
    public void TheColourRenderAgreesWithTheMonoRender()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Instant);
        var placement = new DiskPlacement(47.3, 51.1, 18.5, -70);
        var map = Zonal();

        var mono = PlanetaryRender.Render(map, aspect, placement, 100, 96, 0.9, supersample: 3, rings: SaturnRings.Main);
        var rgba = PlanetaryRender.RenderRgba(new PlanetColourMap(map, map, map), aspect, placement, 100, 96, 0.9, supersample: 3, rings: SaturnRings.Main);

        var lit = 0;
        for (var i = 0; i < mono.Length; i++)
        {
            rgba[i * 4].ShouldBe(mono[i], 1e-6f);
            rgba[(i * 4) + 1].ShouldBe(mono[i], 1e-6f);
            rgba[(i * 4) + 2].ShouldBe(mono[i], 1e-6f);
            lit += mono[i] > 0 ? 1 : 0;
        }
        lit.ShouldBeGreaterThan(1000, "the scene is not empty");
        rgba[((51 * 100) + 47) * 4 + 3].ShouldBe(1f, "the globe's centre is covered");
        rgba[3].ShouldBe(0f, "the corner is sky");
    }

    /// <summary>
    /// The lit side of a crescent faces the Sun: on a north-up, east-left sprite the light's centroid lies toward the bright limb's
    /// position angle (Meeus's chi, from the ephemeris), measured from celestial north through east. The Moon a 6 % crescent and Venus
    /// a 5 % one on 2026-10-13, Mars gibbous.
    /// </summary>
    [Theory]
    [InlineData(CatalogIndex.Moon)]
    [InlineData(CatalogIndex.Venus)]
    [InlineData(CatalogIndex.Mars)]
    public void TheLitLimbFacesTheSunsPositionAngle(CatalogIndex body)
    {
        var aspect = PhysicalEphemeris.Compute(body, Instant);
        const double Radius = 40;
        var pole = PlanetDiscSprite.PoleScreenAngle(aspect.PolePositionAngle, screenNorthDeg: -90, mirrored: false);
        var rgba = PlanetDiscSprite.Build(body, null, new RGBAColor32(200, 200, 200, 255), aspect, Radius, pole, false, null, out var side);

        double sx = 0, sy = 0, sum = 0;
        var c = (side - 1) / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var i = ((y * side) + x) * 4;
                var light = rgba[i + 3] / 255.0 * rgba[i];
                sx += light * (x - c);
                sy += light * (y - c);
                sum += light;
            }
        }
        sum.ShouldBeGreaterThan(0);
        // North is up (-y) and east left (-x): a position angle chi points along (-sin chi, -cos chi).
        var chi = aspect.BrightLimbPositionAngle * Math.PI / 180;
        var cos = ((sx * -Math.Sin(chi)) + (sy * -Math.Cos(chi))) / Math.Sqrt((sx * sx) + (sy * sy));
        Math.Acos(Math.Clamp(cos, -1, 1)).ShouldBeLessThan(10 * Math.PI / 180, $"{body}: light toward PA {aspect.BrightLimbPositionAngle:F1}");
    }

    /// <summary>
    /// Saturn's rings in front of the globe cross it on the side away from the face we see: with the rings open to their south face
    /// (B = -7 degrees on 2026-10-13) the near half lies over the NORTHERN globe. The minor axis of the B ring meets the globe twice; the
    /// near crossing is the ring's (the sprite's colour there is the ring's, apart from the globe's), the far one is hidden by the globe.
    /// </summary>
    [Fact]
    public void SaturnsNearRingCrossesTheGlobe()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Instant);
        aspect.RingOpening.ShouldBeLessThan(0);
        var placement = new DiskPlacement(100, 100, 40, -90);
        // The globe dark in the north and bright in the south, so a ring (at its plane's MEAN albedo) stands out over the north and the
        // southern globe, where the far ring passes behind, keeps its own brightness.
        var values = new float[8 * 36];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < values.Length / 2 ? 0.1f : 1f;
        }
        var split = new PlanetMap(values, 8, 36);
        var map = new PlanetColourMap(split, split, split);
        var withRings = PlanetaryRender.RenderRgba(map, aspect, placement, 200, 200, 0.95, rings: SaturnRings.Main);
        var bare = PlanetaryRender.RenderRgba(map, aspect, placement, 200, 200, 0.95);

        // The B ring's middle (1.75 radii) on the minor axis, sin|B| of that from the centre, north (-y) and south (+y).
        var offset = 1.75 * Math.Sin(Math.Abs(aspect.RingOpening) * Math.PI / 180) * 40;
        // The near crossing shows the B ring itself, nearly opaque at this tilt and lit on the face we see: its level over the
        // plane's mean albedo. The far one shows the globe, never that (the rings' shadow may darken it, which is not the ring).
        var ringLevel = 0.85 * split.MeanAlbedo;
        double At(float[] image, int y) => image[((y * 200) + 100) * 4];
        var near = (int)Math.Round(100 - offset);
        var far = (int)Math.Round(100 + offset);
        At(withRings, near).ShouldBe(ringLevel, ringLevel * 0.15, "the near (northern) crossing is the B ring");
        At(bare, near).ShouldBeLessThan(ringLevel * 0.5, "where the globe alone is dark");
        Math.Abs(At(withRings, far) - ringLevel).ShouldBeGreaterThan(ringLevel * 0.5, "the far crossing is behind the globe");
    }

    /// <summary>
    /// A turn of the central meridian moves a feature the way the planet turns: a spot on the equator at west longitude 120 lies EAST
    /// of the central meridian (left, north up) while the meridian is short of it, and WEST (right) once past it.
    /// </summary>
    [Fact]
    public void TurningTheCentralMeridianMovesAFeatureWest()
    {
        const int W = 360, H = 180;
        var values = new float[W * H];
        Array.Fill(values, 0.2f);
        // Column c begins at west 360 - c, so west 120 is column 240; latitude 0 is row 90.
        for (var r = 85; r < 95; r++)
        {
            for (var c = 235; c < 245; c++)
            {
                values[(r * W) + c] = 1f;
            }
        }
        var map = new PlanetMap(values, W, H);
        var colour = new PlanetColourMap(map, map, map);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Instant);
        var placement = new DiskPlacement(50, 50, 40, -90);

        double SpotX(double centralMeridian)
        {
            var rgba = PlanetaryRender.RenderRgba(colour, aspect.TurnedTo(centralMeridian), placement, 101, 101, 0.95);
            double sx = 0, sum = 0;
            for (var i = 0; i < 101 * 101; i++)
            {
                var bright = rgba[i * 4] > 0.6f ? rgba[i * 4] : 0;
                sx += bright * (i % 101);
                sum += bright;
            }
            sum.ShouldBeGreaterThan(0, $"the spot is on the disc at CM {centralMeridian}");
            return sx / sum;
        }

        SpotX(90).ShouldBeLessThan(45, "before the meridian reaches it, the spot is east (left)");
        SpotX(150).ShouldBeGreaterThan(55, "after, west (right)");
    }

    /// <summary>
    /// The pole on screen is the position angle laid off from the VIEW's north at the planet, which a rolled (or horizon-up, or
    /// mirrored) view turns: the screen angle the sprite is built with equals the direction to a sky point a little way from the planet
    /// at its pole's position angle, projected through the same view. With the view's north term dropped (a fixed "north up") the
    /// rolled case is 35 degrees out.
    /// </summary>
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(35.0, false)]
    [InlineData(-120.0, false)]
    [InlineData(35.0, true)]
    public void ThePoleIsTurnedByTheViewsOwnNorth(double rollDeg, bool mirror)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Instant);
        const double Ra = 7.5, Dec = 22.0;
        var view = SkyMapState.ComputeViewMatrix(Ra + 0.3, Dec - 2, rollDeg * Math.PI / 180, mirror);
        const double Ppr = 5000;
        const float Cx = 500, Cy = 400;

        PlanetDiscSprite.TryScreenNorth(Ra, Dec, view, Ppr, Cx, Cy, out var north, out var mirrored).ShouldBeTrue();
        mirrored.ShouldBe(mirror);
        var pole = PlanetDiscSprite.PoleScreenAngle(aspect.PolePositionAngle, north, mirrored);

        // A point 0.05 degrees from the planet toward position angle P (east of north), projected.
        var p = aspect.PolePositionAngle * Math.PI / 180;
        const double Step = 0.05;
        var dec2 = Dec + (Step * Math.Cos(p));
        var ra2 = Ra + (Step * Math.Sin(p) / Math.Cos(Dec * Math.PI / 180) / 15);
        SkyMapProjection.ProjectWithMatrix(Ra, Dec, view, Ppr, Cx, Cy, out var x0, out var y0).ShouldBeTrue();
        SkyMapProjection.ProjectWithMatrix(ra2, dec2, view, Ppr, Cx, Cy, out var x1, out var y1).ShouldBeTrue();
        var expected = Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI;

        var difference = ((pole - expected) % 360 + 540) % 360 - 180;
        Math.Abs(difference).ShouldBeLessThan(0.5, $"pole {pole:F2} against the sky's {expected:F2}");
    }

    /// <summary>
    /// The Sun lights itself (#1608): no phase and no Minnaert's law, but the limb darkening of the linear law I(mu) = 1 - u (1 - mu),
    /// u = <see cref="PlanetaryRender.SolarLimbDarkening"/>, read off a flat map's render at mu 0.9, 0.6, 0.3 and 0.15 against the
    /// centre. Minnaert's law at full phase, mu^(2k - 1), which the Sun was drawn with, is 0.548 at mu 0.3 where the law is 0.58.
    /// </summary>
    [Fact]
    public void TheSunIsLimbDarkenedByTheLinearLaw()
    {
        const int Side = 241;
        const double Radius = 110;
        var flat = new PlanetMap([0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f], 4, 2);
        var map = new PlanetColourMap(flat, flat, flat);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Sol, Instant);
        var placement = new DiskPlacement(120, 120, Radius, -90);
        var image = PlanetaryRender.RenderRgba(map, aspect, placement, Side, Side, PlanetDiscSprite.MinnaertK(CatalogIndex.Sol), 3);

        double Level(int x, int y)
        {
            var i = ((y * Side) + x) * 4;
            return image[i + 3] > 0 ? image[i] / image[i + 3] : 0;
        }
        var centre = Level(120, 120);
        centre.ShouldBe(0.8, 0.002, "the centre of the disc is the map's own level");
        foreach (var mu in new[] { 0.9, 0.6, 0.3, 0.15 })
        {
            // The pixel whose centre is nearest that mu along the screen's x, and the law at the mu of its centre exactly.
            var x = 120 + (int)Math.Round(Radius * Math.Sqrt(1 - (mu * mu)));
            var r = (x - 120) / Radius;
            var expected = 1 - (PlanetaryRender.SolarLimbDarkening * (1 - Math.Sqrt(1 - (r * r))));
            (Level(x, 120) / centre).ShouldBe(expected, 0.01, $"the limb darkening at mu {mu}");
        }
    }

    /// <summary>
    /// The Sun has no terminator from any direction: lit or not is never asked of it. Rendered straight from the ephemeris (whose Sun has
    /// no sub-solar point, NaN) and from aspects whose sub-solar point stands anywhere, the far side included, every pixel inside 0.95 of the
    /// radius is at least nine tenths of the law's level there.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(180.0, 0.0)]
    [InlineData(90.0, 40.0)]
    [InlineData(0.0, -80.0)]
    public void TheSunHasNoTerminatorFromAnyDirection(double subSolarTurn, double subSolarLatitude)
    {
        const int Side = 101;
        const double Radius = 45;
        var flat = new PlanetMap([0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f], 4, 2);
        var map = new PlanetColourMap(flat, flat, flat);
        var ephemeris = PhysicalEphemeris.Compute(CatalogIndex.Sol, Instant);
        var aspect = ephemeris with
        {
            SubSolarLongitudeIII = ephemeris.CentralMeridianIII + subSolarTurn,
            SubSolarLatitude = subSolarLatitude,
            SubSolarLatitudeCentric = subSolarLatitude,
        };
        var image = PlanetaryRender.RenderRgba(map, aspect, new DiskPlacement(50, 50, Radius, -90), Side, Side,
            PlanetDiscSprite.MinnaertK(CatalogIndex.Sol), 3);

        var inside = 0;
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var r = Math.Sqrt(((x - 50) * (x - 50)) + ((y - 50) * (y - 50))) / Radius;
                if (r > 0.95)
                {
                    continue;
                }
                inside++;
                var i = ((y * Side) + x) * 4;
                var law = 0.8 * (1 - (PlanetaryRender.SolarLimbDarkening * (1 - Math.Sqrt(1 - (r * r)))));
                image[i].ShouldBeGreaterThan((float)(0.9 * law), $"a dark pixel at ({x}, {y}): a terminator");
            }
        }
        inside.ShouldBeGreaterThan(5000);
    }

    /// <summary>Night mode's sprite is the luminance in the Night accent: blue is zero everywhere, and the disc is still drawn.</summary>
    [Fact]
    public void TheNightTintHasNoBlue()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Neptune, Instant);
        var blue = new PlanetMap([0.2f, 0.2f, 0.2f, 0.2f], 2, 2);
        var map = new PlanetColourMap(blue, blue, new PlanetMap([0.9f, 0.9f, 0.9f, 0.9f], 2, 2));

        var rgba = PlanetDiscSprite.Build(CatalogIndex.Neptune, map, default, aspect, 20, -90, false, GuiTheme.NightPalette.Accent, out var side);

        var drawn = 0;
        for (var i = 0; i < side * side; i++)
        {
            rgba[(i * 4) + 2].ShouldBe((byte)0);
            drawn += rgba[i * 4] > 0 ? 1 : 0;
        }
        drawn.ShouldBeGreaterThan(500);
    }

    /// <summary>
    /// The Sun under Night (#1608) is its atlas map's luminance in the Night accent, as every body is: no blue anywhere, and still
    /// limb-darkened by the linear law, read along the sprite's middle row at 0.98 of the radius against the centre, where Minnaert's law at full phase is 0.07 darker.
    /// </summary>
    [Fact]
    public void TheNightSunHasNoBlueAndADarkenedLimb()
    {
        var sun = PlanetTextureAtlas.Decode(System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName)))[CatalogIndex.Sol];
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Sol, Instant);
        const double Radius = 100;
        var rgba = PlanetDiscSprite.Build(CatalogIndex.Sol, sun, default, aspect, Radius, -90, false, GuiTheme.NightPalette.Accent, out var side);

        var drawn = 0;
        for (var i = 0; i < side * side; i++)
        {
            rgba[(i * 4) + 2].ShouldBe((byte)0);
            drawn += rgba[i * 4] > 0 ? 1 : 0;
        }
        drawn.ShouldBeGreaterThan(30000);

        var middle = (side - 1) / 2;
        double Red(int dx) => (rgba[((middle * side) + middle + dx) * 4] + rgba[((middle * side) + middle - dx) * 4]) / 2.0;
        var dx = (int)Math.Round(0.98 * Radius);
        var r = dx / Radius;
        var law = 1 - (PlanetaryRender.SolarLimbDarkening * (1 - Math.Sqrt(1 - (r * r))));
        (Red(dx) / Red(0)).ShouldBe(law, 0.03, "the Night Sun's limb is not the law's");
    }
}
