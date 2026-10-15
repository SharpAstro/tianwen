using System;
using System.Numerics;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A body as a view other than the sky sees it (the orrery's, docs/plans/atlas-planet-detail.md, A5), stated in the body's OWN frame:
/// x the prime meridian, z the IAU north pole, y = z x x, so a point's west longitude is <c>-atan2(y, x)</c> and its planetocentric
/// latitude <c>asin(z)</c>. Three unit vectors in that frame are all a renderer needs: the screen's <see cref="Right"/> at the body,
/// the direction <see cref="Toward"/> the camera, and the direction to the Sun. The screen's up is <c>Toward x Right</c> (right, up
/// and toward the camera make a right-handed frame, so the view is never mirrored).
/// <para>
/// This is what the GPU discs are traced from (<c>planet_body.frag</c>), with no sub-observer latitude, no sub-solar point and no pole
/// angle on screen: the chain those make from the sky is not needed when the camera's own axes are in hand. <see cref="ToAspect"/> and
/// <see cref="PoleScreenAngleDeg"/> turn the same view back into that chain for the CPU render (<see cref="PlanetaryRender.RenderRgba"/>),
/// which stays the reference the GPU is held to.
/// </para>
/// </summary>
/// <param name="Body">The body.</param>
/// <param name="Right">The screen's +x at the body, in the body frame, square to <paramref name="Toward"/>.</param>
/// <param name="Toward">From the body toward the camera, in the body frame.</param>
/// <param name="Sun">From the body toward the Sun, in the body frame; for the Sun itself, <paramref name="Toward"/> (lit at full phase).</param>
/// <param name="Flattening">The body's flattening, 1 less its polar over its equatorial radius.</param>
public readonly record struct PlanetView(CatalogIndex Body, Vector3 Right, Vector3 Toward, Vector3 Sun, double Flattening)
{
    /// <summary>The screen's up at the body (y down on screen is its opposite), in the body frame.</summary>
    public Vector3 Up => Vector3.Cross(Toward, Right);

    /// <summary>
    /// The view of <paramref name="body"/> at <paramref name="utc"/> from vectors in J2000 equatorial coordinates: the screen's right and
    /// the direction toward the camera at the body, and the direction toward the Sun (ignored for the Sun, which is lit at full phase).
    /// <paramref name="right"/> is made square to <paramref name="toward"/> here. The rotation model is the only ephemeris work.
    /// </summary>
    public static PlanetView FromEquatorial(CatalogIndex body, DateTimeOffset utc, ReadOnlySpan<double> right, ReadOnlySpan<double> toward,
        ReadOnlySpan<double> toSun)
    {
        Span<double> pole = stackalloc double[3];
        Span<double> meridian = stackalloc double[3];
        PhysicalEphemeris.BodyAxes(body, utc, pole, meridian);
        Span<double> east = stackalloc double[3];
        east[0] = (pole[1] * meridian[2]) - (pole[2] * meridian[1]);
        east[1] = (pole[2] * meridian[0]) - (pole[0] * meridian[2]);
        east[2] = (pole[0] * meridian[1]) - (pole[1] * meridian[0]);

        var towardBody = InBody(toward, meridian, east, pole);
        var rightBody = InBody(right, meridian, east, pole);
        rightBody = Vector3.Normalize(rightBody - (Vector3.Dot(rightBody, towardBody) * towardBody));
        var sunBody = body == CatalogIndex.Sol ? towardBody : InBody(toSun, meridian, east, pole);
        var (equatorial, polar) = PhysicalEphemeris.Radii(body);
        return new PlanetView(body, rightBody, towardBody, sunBody, 1 - (polar / equatorial));
    }

    /// <summary>
    /// The screen direction of the body's north pole, degrees from +x toward +y with y DOWN, as <see cref="DiskPlacement.NorthAngleDeg"/>
    /// takes it: the pole projected onto the screen's right and up. Straight up when the pole points at (or away from) the camera.
    /// </summary>
    public double PoleScreenAngleDeg
    {
        get
        {
            var (x, y) = (Right.Z, -Up.Z);
            return (x * x) + (y * y) > 1e-12 ? Math.Atan2(y, x) * 180 / Math.PI : -90;
        }
    }

    /// <summary>
    /// The same view as <c>PhysicalEphemeris.ComputeSeenAlong</c>'s aspect would state it, for <see cref="PlanetaryRender"/>: the central meridian and
    /// the sub-observer latitude from <see cref="Toward"/>, the sub-solar point from <see cref="Sun"/>, the phase angle between them. A
    /// disc placed with <see cref="PoleScreenAngleDeg"/>, unmirrored, then traces the very scene the GPU traces.
    /// </summary>
    public PlanetAspect ToAspect(DateTimeOffset utc)
    {
        var (observerLongitude, observerCentric) = SubPoint(Toward);
        var (sunLongitude, sunCentric) = SubPoint(Sun);
        return new PlanetAspect(
            Body,
            utc,
            CentralMeridianIII: observerLongitude,
            CentralMeridianI: double.NaN,
            CentralMeridianII: double.NaN,
            SubObserverLatitude: Graphic(observerCentric),
            SubObserverLatitudeCentric: observerCentric,
            SubSolarLongitudeIII: sunLongitude,
            SubSolarLatitude: Graphic(sunCentric),
            PolePositionAngle: double.NaN,
            PhaseAngle: Math.Acos(Math.Clamp(Vector3.Dot(Toward, Sun), -1, 1)) * 180 / Math.PI,
            AngularDiameterArcsec: double.NaN,
            DistanceAu: double.NaN,
            LightTimeSeconds: 0,
            Flattening: Flattening,
            HeliocentricDistanceAu: double.NaN,
            BrightLimbPositionAngle: double.NaN,
            SubSolarLatitudeCentric: sunCentric,
            HeliocentricEclipticLongitude: double.NaN);
    }

    // The west longitude and planetocentric latitude, degrees, of the point under a body-frame direction.
    private static (double WestLongitude, double CentricLatitude) SubPoint(Vector3 direction)
    {
        var west = -Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        return (west < 0 ? west + 360 : west, Math.Asin(Math.Clamp(direction.Z, -1, 1)) * 180 / Math.PI);
    }

    private double Graphic(double centricDeg)
    {
        var q = 1 - Flattening;
        return Math.Atan(Math.Tan(centricDeg * Math.PI / 180) / (q * q)) * 180 / Math.PI;
    }

    // A direction in the body frame, a unit vector.
    private static Vector3 InBody(ReadOnlySpan<double> v, ReadOnlySpan<double> meridian, ReadOnlySpan<double> east, ReadOnlySpan<double> pole)
    {
        var (x, y, z) = (Dot(v, meridian), Dot(v, east), Dot(v, pole));
        var n = Math.Sqrt((x * x) + (y * y) + (z * z));
        return new Vector3((float)(x / n), (float)(y / n), (float)(z / n));
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
