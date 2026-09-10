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
    /// <summary>
    /// How far, in degrees, the FURTHEST corner of <paramref name="pane"/> lies from the frame's own
    /// tangent point -- i.e. how far past its reference pixel the frame's gnomonic deprojection is
    /// being asked to extrapolate. NaN when the frame carries no usable scale or reference.
    /// </summary>
    /// <remarks>
    /// <para><b>This, and not a field of view, is what decides whether the frame's own grid is still
    /// the right one.</b> That grid is drawn on the frame's TANGENT PLANE, so what breaks it is
    /// DISTANCE FROM THE TANGENT POINT -- and a nominal field of view does not state that, because it
    /// describes the map's projection parameter rather than how much sky the pane reaches. Gating on
    /// it let the frame's grid run at a view with the south celestial pole on screen: reported as "at
    /// 10 percent the grid looks okay, at 12 percent not", where the good one was the map's spherical
    /// grid taking over and the bad one was the tangent plane sweeping PAST the pole in broad arcs
    /// instead of converging on it.</para>
    /// <para>Computed IN the tangent plane rather than by deprojecting the corners, deliberately:
    /// <c>theta = atan(r * scale)</c> is exact for a gnomonic projection, monotonic in r, and cannot
    /// wrap -- whereas asking the WCS about a pane corner far outside the sensor is the very
    /// extrapolation that produced the zoom-out flip, so a guard built on it would share the failure
    /// it exists to catch.</para>
    /// </remarks>
    public static double MaxTangentAngleDeg(in WCS wcs, RectF32 pane,
        float imageOriginX, float imageOriginY, float scale)
    {
        if (!wcs.HasCDMatrix || scale <= 0f || pane.Width <= 0f || pane.Height <= 0f
            || !double.IsFinite(wcs.CRPix1) || !double.IsFinite(wcs.CRPix2)
            || !double.IsFinite(wcs.PixelScaleArcsec) || wcs.PixelScaleArcsec <= 0.0)
        {
            return double.NaN;
        }

        var radPerPixel = wcs.PixelScaleArcsec / 3600.0 * Math.PI / 180.0;
        var worst = 0.0;

        // The pane's four corners, back through the placement into the frame's own pixel grid. The
        // +1 matches the convention every other screen<->image conversion here uses.
        for (var i = 0; i < 4; i++)
        {
            var cornerX = (i & 1) == 0 ? pane.X : pane.X + pane.Width;
            var cornerY = (i & 2) == 0 ? pane.Y : pane.Y + pane.Height;

            var imageX = ((cornerX - imageOriginX) / scale) + 1.0;
            var imageY = ((cornerY - imageOriginY) / scale) + 1.0;

            var dx = imageX - wcs.CRPix1;
            var dy = imageY - wcs.CRPix2;
            var theta = Math.Atan(Math.Sqrt((dx * dx) + (dy * dy)) * radPerPixel);
            if (theta > worst)
            {
                worst = theta;
            }
        }

        return worst * 180.0 / Math.PI;
    }

    public static Solution? Solve(in WCS wcs, RectF32 pane, float imageOriginX, float imageOriginY, float scale)
    {
        if (!wcs.HasCDMatrix || scale <= 0f || pane.Height <= 0f || pane.Width <= 0f)
        {
            return null;
        }

        // Probe the FRAME's own reference pixel and one pixel either side of it -- never the pane's
        // centre. A WCS deprojects gnomonically about that reference, so asking it about a point far
        // outside the sensor is not merely inaccurate: past 90 degrees away the tangent plane wraps
        // and the answer jumps to the OPPOSITE side of the sky. Zoomed out, the pane's centre IS that
        // far off the sensor -- tens of thousands of virtual pixels, some 65 degrees on a 4.7 arcsec
        // frame at 2% zoom -- so probing there flipped the whole sky as the view widened.
        var anchorX = double.IsFinite(wcs.CRPix1) ? wcs.CRPix1 : 0.0;
        var anchorY = double.IsFinite(wcs.CRPix2) ? wcs.CRPix2 : 0.0;

        // One pixel right and one pixel UP the screen. Row indices grow downward in a drawn frame, so
        // screen-up is the MINUS y probe; getting that backwards turns the sky upside down rather
        // than failing, which is what the corner test in SkyBackdropViewTests is for.
        if (wcs.PixelToSky(anchorX, anchorY) is not { } anchor
            || wcs.PixelToSky(anchorX + 1.0, anchorY) is not { } rightward
            || wcs.PixelToSky(anchorX, anchorY - 1.0) is not { } upward)
        {
            return null;
        }

        var skyAnchor = UnitVector(anchor.RA, anchor.Dec);
        var skyRight = UnitVector(rightward.RA, rightward.Dec);
        var skyUp = UnitVector(upward.RA, upward.Dec);

        // The vertical pixel scale, because the map states its field over the pane's HEIGHT. Taken as
        // atan2 of the cross product against the dot rather than as an arccosine: the angle across one
        // pixel is of order 1e-5 rad, where acos(1 - 5e-11) has lost most of its significant digits.
        var radiansPerPixel = Math.Atan2(CrossLength(skyAnchor, skyUp), Dot(skyAnchor, skyUp));
        if (!(radiansPerPixel > 0.0))
        {
            return null;
        }

        // Invert the map's own pixels-per-radian: it maps half the field onto 2*tan(fov/4) on the
        // projection plane, so ppr = height / (4 tan(fov/4)).
        var pixelsPerRadian = scale / radiansPerPixel;
        var fieldOfViewDeg = double.RadiansToDegrees(4.0 * Math.Atan(pane.Height / (4.0 * pixelsPerRadian)));

        // Where those three probes are DRAWN, and therefore which camera direction each has to end up
        // pointing along. The map projects about the pane's centre, so a screen offset from there
        // inverts through the stereographic into a camera direction -- the same inverse
        // SkyMapProjection.UnprojectWithMatrix uses, minus the rotation, which is what is being solved
        // for here. Nothing is extrapolated: all three points sit inside the sensor.
        var paneCentreX = pane.X + pane.Width * 0.5;
        var paneCentreY = pane.Y + pane.Height * 0.5;
        var camAnchor = CameraDirection(imageOriginX, imageOriginY, scale, anchorX, anchorY,
            paneCentreX, paneCentreY, pixelsPerRadian);
        var camRight = CameraDirection(imageOriginX, imageOriginY, scale, anchorX + 1.0, anchorY,
            paneCentreX, paneCentreY, pixelsPerRadian);
        var camUp = CameraDirection(imageOriginX, imageOriginY, scale, anchorX, anchorY - 1.0,
            paneCentreX, paneCentreY, pixelsPerRadian);

        // The view is a rigid rotation of the sphere, and a reflection too where the light path
        // mirrored the field. Build an orthonormal frame on each side of the correspondence and read
        // the transform off as the one carrying the sky frame onto the camera frame.
        if (Frame(skyAnchor, skyUp) is not { } skyFrame || Frame(camAnchor, camUp) is not { } camFrame)
        {
            return null;
        }

        // Handedness comes from the THIRD probe, which the two frames above do not constrain: with
        // the up axis matched, screen-right decides which side east ends up on, and no rotation can
        // move it to the other one. Compared in the frame's own coordinates so the test is a sign
        // rather than a distance.
        var skyRightComp = Dot(skyRight, skyFrame.E3) - Dot(skyAnchor, skyFrame.E3);
        var camRightComp = Dot(camRight, camFrame.E3) - Dot(camAnchor, camFrame.E3);
        var mirror = skyRightComp * camRightComp < 0.0;

        // R maps sky to camera. Its rows are the camera axes expressed in sky coordinates, which is
        // exactly what SkyMapState.ComputeViewMatrix builds from a centre and a roll -- so the rows
        // are what gets decomposed back into those below.
        var e3 = mirror ? Negate(camFrame.E3) : camFrame.E3;
        var rowRight = RowOf(camFrame.E1, camFrame.E2, e3, skyFrame.E1.X, skyFrame.E2.X, skyFrame.E3.X);
        var rowUp = RowOf(camFrame.E1, camFrame.E2, e3, skyFrame.E1.Y, skyFrame.E2.Y, skyFrame.E3.Y);
        var rowBack = RowOf(camFrame.E1, camFrame.E2, e3, skyFrame.E1.Z, skyFrame.E2.Z, skyFrame.E3.Z);

        // The view axis is the sky direction that maps onto camera -Z; the roll is where the camera's
        // right axis sits relative to the reference frame there. A mirrored view stores the roll of
        // the UNMIRRORED right axis, because ComputeViewMatrix applies the mirror after the roll.
        var forward = Negate((rowRight.Z, rowUp.Z, rowBack.Z));
        var centreDec = double.RadiansToDegrees(Math.Asin(Math.Clamp(forward.Z, -1.0, 1.0)));
        var centreRa = Math.Atan2(forward.Y, forward.X) / (Math.PI / 12.0);

        var (right0, up0) = ReferenceAxes(centreRa, centreDec);
        var rightAxis = (X: rowRight.X, Y: rowUp.X, Z: rowBack.X);
        if (mirror)
        {
            rightAxis = Negate(rightAxis);
        }

        var roll = Math.Atan2(Dot(rightAxis, up0), Dot(rightAxis, right0));

        return new Solution(
            CenterRaHours: ((centreRa % 24.0) + 24.0) % 24.0,
            CenterDecDeg: centreDec,
            CenterRollRad: roll,
            FieldOfViewDeg: Math.Clamp(fieldOfViewDeg, MinFieldOfViewDeg, MaxFieldOfViewDeg),
            Mirror: mirror);
    }

    /// <summary>
    /// The camera-space direction one IMAGE pixel has to end up pointing along, from where the viewer
    /// draws it: its screen position, then the inverse of the map's stereographic about the pane's
    /// centre. Forward is -Z and screen y grows downward, exactly as
    /// <see cref="SkyMapProjection.UnprojectWithMatrix"/> has it.
    /// </summary>
    private static (double X, double Y, double Z) CameraDirection(
        float imageOriginX, float imageOriginY, float scale,
        double pixelX, double pixelY,
        double paneCentreX, double paneCentreY, double pixelsPerRadian)
    {
        var screenX = imageOriginX + (pixelX + 0.5) * scale;
        var screenY = imageOriginY + (pixelY + 0.5) * scale;

        var px = (screenX - paneCentreX) / pixelsPerRadian;
        var py = -(screenY - paneCentreY) / pixelsPerRadian;
        var rho = Math.Sqrt(px * px + py * py);
        if (rho < 1e-15)
        {
            return (0.0, 0.0, -1.0);
        }

        var c = 2.0 * Math.Atan(rho * 0.5);
        var (sinC, cosC) = Math.SinCos(c);
        return (sinC * px / rho, sinC * py / rho, -cosC);
    }

    /// <summary>
    /// An orthonormal frame from two directions: the first as its primary axis, the second
    /// orthogonalised against it. Null when the two are parallel, which for a one-pixel probe means
    /// the placement has no scale at all.
    /// </summary>
    private static ((double X, double Y, double Z) E1, (double X, double Y, double Z) E2,
        (double X, double Y, double Z) E3)? Frame(
        (double X, double Y, double Z) primary, (double X, double Y, double Z) secondary)
    {
        var e1 = Normalize(primary);
        var d = Dot(secondary, e1);
        var perp = (X: secondary.X - d * e1.X, Y: secondary.Y - d * e1.Y, Z: secondary.Z - d * e1.Z);
        var len = Math.Sqrt(Dot(perp, perp));
        if (!(len > 1e-12))
        {
            return null;
        }

        var e2 = (X: perp.X / len, Y: perp.Y / len, Z: perp.Z / len);
        return (e1, e2, Cross(e1, e2));
    }

    /// <summary>One row of the sky-to-camera transform: the camera basis weighted by a column of the
    /// sky frame, which is the product of the two frames without materialising either matrix.</summary>
    private static (double X, double Y, double Z) RowOf(
        (double X, double Y, double Z) f1, (double X, double Y, double Z) f2, (double X, double Y, double Z) f3,
        double a, double b, double c)
        => (f1.X * a + f2.X * b + f3.X * c,
            f1.Y * a + f2.Y * b + f3.Y * c,
            f1.Z * a + f2.Z * b + f3.Z * c);

    private static (double X, double Y, double Z) Negate((double X, double Y, double Z) v)
        => (-v.X, -v.Y, -v.Z);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var len = Math.Sqrt(Dot(v, v));
        return len > 0.0 ? (v.X / len, v.Y / len, v.Z / len) : v;
    }

    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

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
