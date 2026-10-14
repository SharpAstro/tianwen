using System;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A planet's disc as the sky atlas draws it once it is big enough on screen (docs/plans/atlas-planet-detail.md, "A4: the textured
/// disc", #1037): a square straight-alpha RGBA sprite rendered on the CPU by <see cref="PlanetaryRender.RenderRgba"/> from the body's
/// colour map, turned to its central meridian and pole, phased toward the Sun, Saturn's rings at their tilt, and turned on screen as
/// the view turns celestial north. Pure: the cache (<see cref="PlanetSpriteCache"/>) decides when one is built, off the render thread.
/// </summary>
public static class PlanetDiscSprite
{
    /// <summary>
    /// The smallest disc, in screen pixels of diameter, drawn as a sprite. Below it the dot path runs and no sprite work happens at all:
    /// at a 60 degree field Jupiter is about 0.2 px across.
    /// </summary>
    public const double MinDiscPx = 6;

    /// <summary>The largest sprite side, in pixels; a disc wider than this on screen is the sprite scaled up.</summary>
    public const int MaxSpritePx = 256;

    /// <summary>Rays per pixel along each axis: 3 x 3, against the stacker's 8 x 8, which a display sprite has no use for.</summary>
    public const int Supersample = 3;

    /// <summary>
    /// The Minnaert exponent each body is lit with: a display choice, near each map's own correction where it has one (OPAL's are 0.85
    /// to 1.0 for the filters behind its composites), and 0.5 for the airless bodies, whose full disc is evenly bright. The Sun is lit
    /// as a body seen at full phase, so its limb darkens as the square root of mu.
    /// </summary>
    public static double MinnaertK(CatalogIndex body) => body switch
    {
        CatalogIndex.Moon or CatalogIndex.Mercury => 0.5,
        CatalogIndex.Sol or CatalogIndex.Venus or CatalogIndex.Mars => 0.75,
        CatalogIndex.Uranus or CatalogIndex.Neptune => 0.85,
        _ => 0.95,
    };

    /// <summary>
    /// How far the sprite reaches past the globe, in equatorial radii: Saturn's ring system, every other body its globe.
    /// </summary>
    public static double ExtentRadii(CatalogIndex body) => body == CatalogIndex.Saturn ? SaturnRings.Main.OuterRadii : 1;

    /// <summary>
    /// The screen direction, in degrees from +x toward +y (y down), of the celestial north at (<paramref name="raHours"/>,
    /// <paramref name="decDeg"/>) through <paramref name="view"/>, and whether the view shows the sky mirrored (east to the right of
    /// north). Found by projecting the point and its neighbours a hundredth of a degree north and east: the view can roll, be
    /// horizon-up or be mirrored, and only the projection knows which. False when the point does not project.
    /// </summary>
    public static bool TryScreenNorth(double raHours, double decDeg, Matrix4x4 view, double ppr, float cx, float cy,
        out double northDeg, out bool mirrored)
    {
        const double Epsilon = 0.01;
        northDeg = 0;
        mirrored = false;
        // Near the pole a step north would cross it; step south and turn the answer round.
        var sign = decDeg > 89 ? -1 : 1;
        if (!SkyMapProjection.ProjectWithMatrix(raHours, decDeg, view, ppr, cx, cy, out var x0, out var y0)
            || !SkyMapProjection.ProjectWithMatrix(raHours, decDeg + (sign * Epsilon), view, ppr, cx, cy, out var xn, out var yn)
            || !SkyMapProjection.ProjectWithMatrix(raHours + (Epsilon / 15 / Math.Cos(decDeg * Math.PI / 180)), decDeg, view, ppr, cx, cy, out var xe, out var ye))
        {
            return false;
        }
        var (nx, ny) = (sign * (xn - x0), sign * (yn - y0));
        var (ex, ey) = (xe - x0, ye - y0);
        if ((nx * nx) + (ny * ny) == 0)
        {
            return false;
        }
        northDeg = Math.Atan2(ny, nx) * 180 / Math.PI;
        // Unmirrored, east is north turned a quarter AWAY from +y (north up, east left): the cross product north x east is negative.
        mirrored = ((nx * ey) - (ny * ex)) > 0;
        return true;
    }

    /// <summary>
    /// The screen direction of the planet's north pole: its position angle, east of celestial north, laid off from
    /// <paramref name="screenNorthDeg"/> toward the screen's east. Both terms are needed; the view's own turn is the second.
    /// </summary>
    public static double PoleScreenAngle(double polePositionAngleDeg, double screenNorthDeg, bool mirrored)
        => mirrored ? screenNorthDeg + polePositionAngleDeg : screenNorthDeg - polePositionAngleDeg;

    /// <summary>The disc's placement in a sprite of <paramref name="side"/> pixels, centred, its equator <paramref name="radiusPx"/>.</summary>
    public static DiskPlacement Placement(int side, double radiusPx, double poleScreenAngleDeg, bool mirrored)
        => new((side - 1) / 2.0, (side - 1) / 2.0, radiusPx, poleScreenAngleDeg, mirrored);

    /// <summary>The sprite's side for a disc of <paramref name="radiusPx"/>: the globe or Saturn's rings, and a pixel of margin each side.</summary>
    public static int SideFor(CatalogIndex body, double radiusPx) => (int)Math.Ceiling(2 * radiusPx * ExtentRadii(body)) + 2;

    /// <summary>The largest equatorial radius whose sprite fits <see cref="MaxSpritePx"/>.</summary>
    public static double MaxRadiusPx(CatalogIndex body) => (MaxSpritePx - 2) / (2 * ExtentRadii(body));

    /// <summary>
    /// The sprite: <see cref="SideFor"/> squared pixels of 8-bit RGBA, straight (not premultiplied) alpha, as every host's blend takes
    /// it, so a limb pixel half covered keeps its colour at half alpha. <paramref name="map"/> null draws the body in a flat
    /// <paramref name="flat"/> colour (the Sun, or a body whose texture has not loaded). With <paramref name="nightTint"/> the sprite is
    /// the luminance in that colour, so Night mode keeps blue at zero. <paramref name="opacity"/> scales the alpha: a body below the
    /// horizon is drawn faint, as its label is, and every host then blends the sprite the same way.
    /// </summary>
    public static byte[] Build(CatalogIndex body, PlanetColourMap? map, RGBAColor32 flat, in PlanetAspect aspect, double radiusPx,
        double poleScreenAngleDeg, bool mirrored, RGBAColor32? nightTint, out int side, float opacity = 1)
    {
        side = SideFor(body, radiusPx);
        var placement = Placement(side, radiusPx, poleScreenAngleDeg, mirrored);
        var colours = map ?? Flat(flat);
        // The Sun lights itself: drawn as a body at full phase, the sub-solar point under the observer.
        var lit = body == CatalogIndex.Sol
            ? aspect with { SubSolarLongitudeIII = aspect.CentralMeridianIII, SubSolarLatitude = aspect.SubObserverLatitude, SubSolarLatitudeCentric = aspect.SubObserverLatitudeCentric }
            : aspect;
        var rings = body == CatalogIndex.Saturn ? SaturnRings.Main : null;
        var linear = PlanetaryRender.RenderRgba(colours, lit, placement, side, side, MinnaertK(body), Supersample, rings);

        var rgba = new byte[side * side * 4];
        for (var i = 0; i < side * side; i++)
        {
            var a = linear[(i * 4) + 3];
            if (a <= 0)
            {
                continue;
            }
            // Un-premultiply: the colour of what the pixel's covering rays saw.
            var (r, g, b) = (linear[i * 4] / a, linear[(i * 4) + 1] / a, linear[(i * 4) + 2] / a);
            if (nightTint is { } tint)
            {
                var luminance = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                (r, g, b) = (luminance * tint.Red / 255f, luminance * tint.Green / 255f, luminance * tint.Blue / 255f);
            }
            rgba[i * 4] = ToByte(r);
            rgba[(i * 4) + 1] = ToByte(g);
            rgba[(i * 4) + 2] = ToByte(b);
            rgba[(i * 4) + 3] = ToByte(a * opacity);
        }
        return rgba;
    }

    private static PlanetColourMap Flat(RGBAColor32 colour)
    {
        static PlanetMap Plane(byte value)
        {
            var values = new float[4 * 2];
            Array.Fill(values, value / 255f);
            return new PlanetMap(values, 4, 2);
        }
        return new PlanetColourMap(Plane(colour.Red), Plane(colour.Green), Plane(colour.Blue));
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
}
