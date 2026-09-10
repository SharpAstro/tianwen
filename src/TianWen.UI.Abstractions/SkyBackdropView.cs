using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Points a <see cref="SkyMapState"/> at whatever a solved photograph is currently showing, so the
/// sky map can be drawn BEHIND the frame and line up with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The photograph is the master and the sky follows it.</b> The viewer's zoom, pan, fit and crop
/// already decide where every image pixel lands on screen; this reads that placement back through the
/// frame's own WCS and states the same view in the map's terms -- centre, roll, field of view and
/// handedness. Nothing about the image pipeline changes, and every existing gesture keeps working:
/// zoom out and the frame becomes a rectangle on a star field, zoom in and the sky recedes behind it.
/// </para>
/// <para>
/// <b>It is solved by PROBING rather than derived from the CD matrix, and that is the point.</b> Three
/// calls to <see cref="WCS.PixelToSky"/> -- the pane's centre, one pixel to its right, one pixel above
/// it -- answer all four unknowns without this file needing an opinion about FITS conventions, matrix
/// handedness or which way the map's east points. Each answer is then a fact about the two coordinate
/// systems in front of it, so a convention that changes on either side moves both probes together.
/// </para>
/// <para>
/// <b>What it cannot express is a frame that is not CONFORMAL.</b> The map's view is rigid -- a
/// rotation, a scale and at most a reflection -- so a CD matrix carrying a shear, or one scale for
/// columns and another for rows, is matched along the vertical (the axis the field of view is stated
/// over) and off by the difference along the horizontal. Real frames are square-pixel and shear-free
/// to well under a pixel across a sensor, and a frame that is not is already drawn with the wrong
/// aspect ratio by the image quad, which stretches one texture uniformly.
/// </para>
/// <para>
/// <b>The two projections are not the same and do not need to be.</b> A frame's WCS is gnomonic and
/// the map is stereographic, so they agree exactly at the view centre and separate by about theta
/// squared over four of the distance out to a point theta away -- a hundredth of a pixel at the
/// corner of a one-degree frame, and 1.85 px on a ten-degree one (measured, SkyBackdropViewTests).
/// It is also the wrong thing to worry about: the photograph is drawn OPAQUE on top, so the only
/// place the two are ever seen touching is its border.
/// </para>
/// </remarks>
public static class SkyBackdropView
{
    /// <summary>
    /// The narrowest field the driven view may state. Far below the map's own interactive floor
    /// (<see cref="SkyMapViewActions.MinFieldOfViewDeg"/>, half a degree), because a viewer at 1:1 on
    /// a long focal length is legitimately down here and clamping would put the star field at a
    /// visibly different scale from the picture.
    /// </summary>
    public const double MinFieldOfViewDeg = 0.005;

    /// <summary>The whole sky, the widest the projection is asked for.</summary>
    public const double MaxFieldOfViewDeg = 180.0;

    /// <summary>
    /// A view of the sky that matches a photograph's placement on screen: where it looks, how it is
    /// turned, how much sky the pane covers, and whether the frame's optics reversed it.
    /// </summary>
    public readonly record struct Solution(
        double CenterRaHours,
        double CenterDecDeg,
        double CenterRollRad,
        double FieldOfViewDeg,
        bool Mirror);

    /// <summary>
    /// Solves the sky view that puts <paramref name="wcs"/>'s frame exactly where the viewer has
    /// drawn it, or null when the frame carries no solution or the placement is degenerate.
    /// </summary>
    /// <param name="wcs">The frame's astrometric solution.</param>
    /// <param name="pane">The image pane in screen pixels: the map projects about its centre.</param>
    /// <param name="imageOriginX">Screen x of image pixel column 0's left edge.</param>
    /// <param name="imageOriginY">Screen y of image pixel row 0's top edge.</param>
    /// <param name="scale">Screen pixels per image pixel.</param>
    public static Solution? Solve(in WCS wcs, RectF32 pane, float imageOriginX, float imageOriginY, float scale)
    {
        if (!wcs.HasCDMatrix || scale <= 0f || pane.Height <= 0f || pane.Width <= 0f)
        {
            return null;
        }

        // The image pixel under the middle of the pane, which is where the map projects from. The half
        // pixel is the centre convention the overlay engine draws by: image pixel (x, y) has its
        // CENTRE at origin + (x + 0.5) * scale, and a WCS is in centroid coordinates.
        var centreX = (pane.X + pane.Width * 0.5 - imageOriginX) / scale - 0.5;
        var centreY = (pane.Y + pane.Height * 0.5 - imageOriginY) / scale - 0.5;

        // One pixel right and one pixel UP the screen. Row indices grow downward in a drawn frame, so
        // screen-up is the MINUS y probe; getting that backwards turns the sky upside down rather
        // than failing, which is what the corner test in SkyBackdropViewTests is for.
        if (wcs.PixelToSky(centreX, centreY) is not { } centre
            || wcs.PixelToSky(centreX + 1.0, centreY) is not { } rightward
            || wcs.PixelToSky(centreX, centreY - 1.0) is not { } upward)
        {
            return null;
        }

        var c = UnitVector(centre.RA, centre.Dec);
        var r = UnitVector(rightward.RA, rightward.Dec);
        var u = UnitVector(upward.RA, upward.Dec);
        var (right0, up0) = ReferenceAxes(centre.RA, centre.Dec);

        // The roll that carries the frame's up onto the screen's up. In the map's camera space a sky
        // direction lands at x = cos(roll)*a + sin(roll)*b and y = -sin(roll)*a + cos(roll)*b, where a
        // and b are its components along the reference frame's right and up axes; screen-up is x = 0
        // with y positive, which is this one root of the first equation and the sign that satisfies
        // the second.
        var a = Dot(right0, u);
        var b = Dot(up0, u);
        if (a == 0.0 && b == 0.0)
        {
            return null;
        }

        var roll = Math.Atan2(-a, b);

        // The vertical pixel scale, because the map states its field over the pane's HEIGHT. Taken as
        // atan2 of the cross product against the dot rather than as an arccosine: the angle across one
        // pixel is of order 1e-5 rad, where acos(1 - 5e-11) has lost most of its significant digits.
        var radiansPerPixel = Math.Atan2(CrossLength(c, u), Dot(c, u));
        if (!(radiansPerPixel > 0.0))
        {
            return null;
        }

        // Invert the map's own pixels-per-radian: it maps half the field onto 2*tan(fov/4) on the
        // projection plane, so ppr = height / (4 tan(fov/4)).
        var pixelsPerRadian = scale / radiansPerPixel;
        var fieldOfViewDeg = double.RadiansToDegrees(4.0 * Math.Atan(pane.Height / (4.0 * pixelsPerRadian)));

        // Handedness, asked of the frame rather than of the CD matrix's determinant: with the roll
        // solved, the screen-right probe must land on the RIGHT. When it lands left, the light path
        // reversed the field and no rotation can undo that -- see SkyMapState.MirrorView.
        var (sinRoll, cosRoll) = Math.SinCos(roll);
        var mirror = cosRoll * Dot(right0, r) + sinRoll * Dot(up0, r) < 0.0;

        return new Solution(
            CenterRaHours: ((centre.RA % 24.0) + 24.0) % 24.0,
            CenterDecDeg: centre.Dec,
            CenterRollRad: roll,
            FieldOfViewDeg: Math.Clamp(fieldOfViewDeg, MinFieldOfViewDeg, MaxFieldOfViewDeg),
            Mirror: mirror);
    }

    /// <summary>
    /// Writes a solution onto the map's view. Deliberately does NOT declare the view driven
    /// (<see cref="SkyMapState.ViewDrivenExternally"/>): that is the host saying it has taken the view
    /// over, which has to be true before the first frame and stay true on a frame this cannot solve.
    /// </summary>
    public static void ApplyTo(SkyMapState state, in Solution solution)
    {
        ArgumentNullException.ThrowIfNull(state);

        // No pole clamp, unlike the interactive path's NormalizeView: that clamp keeps a PAN out of
        // the projection's singularity, and here the centre is wherever the photograph actually
        // points. A frame taken at the pole is drawn at the pole.
        state.CenterRA = solution.CenterRaHours;
        state.CenterDec = solution.CenterDecDeg;
        state.CenterRoll = solution.CenterRollRad;
        state.FieldOfViewDeg = solution.FieldOfViewDeg;
        state.MirrorView = solution.Mirror;
    }

    /// <summary>
    /// The J2000 unit vector for an RA/Dec, in DOUBLE precision. <see cref="SkyMapState.RaDecToUnitVec"/>
    /// is the same formula in float, which is right for a star buffer and not for this: the probes
    /// here differ from the centre in the fifth decimal place, where float has three digits left.
    /// </summary>
    private static (double X, double Y, double Z) UnitVector(double raHours, double decDeg)
    {
        var (sinRA, cosRA) = Math.SinCos(raHours * (Math.PI / 12.0));
        var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDeg));
        return (cosDec * cosRA, cosDec * sinRA, sinDec);
    }

    /// <summary>
    /// The view frame's right and up axes at a centre, in double precision and in exactly the
    /// convention <see cref="SkyMapState.ReferenceFrame"/> states: right toward DECREASING RA (the map
    /// is east-left), up toward celestial north, both well conditioned at the poles.
    /// </summary>
    private static ((double X, double Y, double Z) Right, (double X, double Y, double Z) Up)
        ReferenceAxes(double raHours, double decDeg)
    {
        var (sinRA, cosRA) = Math.SinCos(raHours * (Math.PI / 12.0));
        var forward = UnitVector(raHours, decDeg);
        var right = (X: sinRA, Y: -cosRA, Z: 0.0);
        // up = right x forward, as ComputeViewMatrix takes it.
        var up = (
            X: right.Y * forward.Z - right.Z * forward.Y,
            Y: right.Z * forward.X - right.X * forward.Z,
            Z: right.X * forward.Y - right.Y * forward.X);
        return (right, up);
    }

    private static double Dot(in (double X, double Y, double Z) a, in (double X, double Y, double Z) b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static double CrossLength(in (double X, double Y, double Z) a, in (double X, double Y, double Z) b)
    {
        var x = a.Y * b.Z - a.Z * b.Y;
        var y = a.Z * b.X - a.X * b.Z;
        var z = a.X * b.Y - a.Y * b.X;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}
