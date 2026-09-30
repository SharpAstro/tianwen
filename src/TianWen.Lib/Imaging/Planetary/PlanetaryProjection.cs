using System;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's oblate spheroid as it lies in an image at an instant: which planetographic latitude and west longitude a pixel
/// sees, and where on the image a latitude and longitude lie (docs/plans/planetary-restoration.md, R2 and R6). The orientation
/// is the ephemeris' (the sub-observer latitude, the central meridian, the flattening); the image supplies only the disk's
/// centre, scale and north (<see cref="DiskPlacement"/>). <see cref="PlanetaryRender"/> casts its rays through it, and a
/// de-rotation (<see cref="PlanetaryDerotation"/>) reads a pixel's latitude and longitude at one instant and finds them at
/// another.
/// <para>
/// The disk frame is the sky plane through the planet's centre: u toward the sky's WEST, v toward the projected north pole, w
/// toward the observer, in equatorial radii. The body frame shares u (it lies in the equator, since the pole lies in the v-w
/// plane) and adds e2, the equator's direction nearest the observer, and the pole.
/// </para>
/// </summary>
public readonly struct PlanetaryProjection
{
    private readonly double _centerX, _centerY, _radius;
    private readonly double _northX, _northY, _westX, _westY;
    private readonly double _sinD, _cosD, _q2, _q2Inverse, _a;
    private readonly double _centralMeridian;
    private readonly double _sun1, _sun2, _sun3;

    /// <summary>The planet at <paramref name="aspect"/>, where <paramref name="placement"/> puts its disk.</summary>
    public PlanetaryProjection(in PlanetAspect aspect, in DiskPlacement placement)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placement.EquatorialRadius);
        _centerX = placement.CenterX;
        _centerY = placement.CenterY;
        _radius = placement.EquatorialRadius;
        var (sinN, cosN) = Math.SinCos(placement.NorthAngleDeg * Math.PI / 180);
        _northX = cosN;
        _northY = sinN;
        // West is north turned a quarter toward +y: with north up on a y-down screen, west is to the right, as the sky looks to
        // the eye. A mirrored image has it the other way.
        var mirror = placement.Mirrored ? -1 : 1;
        _westX = -sinN * mirror;
        _westY = cosN * mirror;

        var q = 1 - aspect.Flattening;
        var d = aspect.SubObserverLatitudeCentric * Math.PI / 180;
        (_sinD, _cosD) = Math.SinCos(d);
        _q2 = q * q;
        _q2Inverse = 1 / (q * q);
        _a = (_cosD * _cosD) + (_sinD * _sinD * _q2Inverse);
        _centralMeridian = aspect.CentralMeridianIII;
        SinD = _sinD;
        CosD = _cosD;

        // The direction to the Sun, from the disk frame into the body frame: e1 is west, e2 = toward cos D - north sin D,
        // the pole = toward sin D + north cos D.
        var (west, north, toward) = PhysicalEphemeris.SunOnTheDisk(aspect);
        _sun1 = west;
        _sun2 = (toward * _cosD) - (north * _sinD);
        _sun3 = (toward * _sinD) + (north * _cosD);
    }

    /// <summary>The sine of the sub-observer planetocentric latitude, the body frame's tilt toward the observer.</summary>
    internal double SinD { get; }

    /// <summary>The cosine of the sub-observer planetocentric latitude.</summary>
    internal double CosD { get; }

    /// <summary>
    /// The surface normal, in the body frame, of the point pixel (<paramref name="x"/>, <paramref name="y"/>) sees, and the
    /// ray's meeting with the spheroid (its west-ward and toward-the-observer body coordinates, for the longitude); false off
    /// the disk. The render's ray cast, exactly.
    /// </summary>
    internal bool TryNormal(double x, double y, out double n1, out double n2, out double n3, out double x1, out double x2)
    {
        var dx = (x - _centerX) / _radius;
        var dy = (y - _centerY) / _radius;
        var u = (dx * _westX) + (dy * _westY);
        var v = (dx * _northX) + (dy * _northY);

        // The ray (u, v, t) meets the spheroid X1^2 + X2^2 + X3^2 / q^2 = 1, with X1 = u, X2 = t cos D - v sin D and
        // X3 = t sin D + v cos D; the nearer of the two meetings is the larger t.
        var b = 2 * v * _sinD * _cosD * (_q2Inverse - 1);
        var c = (u * u) + (v * v * _sinD * _sinD) + (v * v * _cosD * _cosD * _q2Inverse) - 1;
        var discriminant = (b * b) - (4 * _a * c);
        if (discriminant < 0)
        {
            (n1, n2, n3, x1, x2) = (0, 0, 0, 0, 0);
            return false;
        }
        var t = (-b + Math.Sqrt(discriminant)) / (2 * _a);
        x1 = u;
        x2 = (t * _cosD) - (v * _sinD);
        var x3 = (t * _sinD) + (v * _cosD);

        // The normal, in the body frame; its latitude is the planetographic latitude.
        n1 = x1;
        n2 = x2;
        n3 = x3 * _q2Inverse;
        var norm = Math.Sqrt((n1 * n1) + (n2 * n2) + (n3 * n3));
        n1 /= norm;
        n2 /= norm;
        n3 /= norm;
        return true;
    }

    /// <summary>
    /// What pixel (<paramref name="x"/>, <paramref name="y"/>) sees: its planetographic latitude and west longitude (degrees,
    /// System III), and the cosines of its emission (<paramref name="mu"/>, toward the observer) and incidence
    /// (<paramref name="mu0"/>, toward the Sun) angles, negative on the night side; false off the disk.
    /// </summary>
    public bool TrySurface(double x, double y, out double latitude, out double westLongitude, out double mu, out double mu0)
    {
        if (!TryNormal(x, y, out var n1, out var n2, out var n3, out var x1, out var x2))
        {
            (latitude, westLongitude, mu, mu0) = (double.NaN, double.NaN, double.NaN, double.NaN);
            return false;
        }
        mu = (n2 * _cosD) + (n3 * _sinD);
        mu0 = (n1 * _sun1) + (n2 * _sun2) + (n3 * _sun3);
        (latitude, westLongitude) = (LatitudeOf(n3), WestLongitudeOf(x1, x2));
        return true;
    }

    /// <summary>
    /// The lighting Minnaert's law gives pixel (<paramref name="x"/>, <paramref name="y"/>), <c>mu0^k mu^(k-1)</c> (what a
    /// uniform albedo of one would read there), and zero off the disk or on its night side.
    /// </summary>
    public double Minnaert(double x, double y, double k)
        => TrySurface(x, y, out _, out _, out var mu, out var mu0) && mu > 0 && mu0 > 0 ? Math.Pow(mu0, k) * Math.Pow(mu, k - 1) : 0;

    /// <summary>
    /// The planetographic latitude and west longitude (degrees, System III) pixel (<paramref name="x"/>, <paramref name="y"/>)
    /// sees; false off the disk.
    /// </summary>
    public bool TryUnproject(double x, double y, out double latitude, out double westLongitude)
    {
        if (!TryNormal(x, y, out _, out _, out var n3, out var x1, out var x2))
        {
            (latitude, westLongitude) = (double.NaN, double.NaN);
            return false;
        }
        (latitude, westLongitude) = (LatitudeOf(n3), WestLongitudeOf(x1, x2));
        return true;
    }

    /// <summary>
    /// Where on the image the point at <paramref name="latitude"/> (planetographic) and <paramref name="westLongitude"/>
    /// (System III) lies; false where it is on the far side of the planet. The inverse of <see cref="TryUnproject"/> over the
    /// visible hemisphere.
    /// </summary>
    public bool TryProject(double latitude, double westLongitude, out double x, out double y)
    {
        // The unit normal at the point, in the body frame: its longitude from the central meridian is the angle atan2(n1, n2).
        var (sinPhi, cosPhi) = Math.SinCos(latitude * Math.PI / 180);
        var (sinAlpha, cosAlpha) = Math.SinCos((_centralMeridian - westLongitude) * Math.PI / 180);
        var n1 = cosPhi * sinAlpha;
        var n2 = cosPhi * cosAlpha;
        var n3 = sinPhi;
        if ((n2 * _cosD) + (n3 * _sinD) <= 0)
        {
            (x, y) = (double.NaN, double.NaN);
            return false;
        }

        // The surface point is along (n1, n2, q^2 n3), scaled onto X1^2 + X2^2 + X3^2 / q^2 = 1; the sky plane's (u, v) from it.
        var s = 1 / Math.Sqrt((n1 * n1) + (n2 * n2) + (_q2 * n3 * n3));
        var (x1, x2, x3) = (s * n1, s * n2, s * _q2 * n3);
        var u = x1;
        var v = (x3 * _cosD) - (x2 * _sinD);
        x = _centerX + (((u * _westX) + (v * _northX)) * _radius);
        y = _centerY + (((u * _westY) + (v * _northY)) * _radius);
        return true;
    }

    /// <summary>The planetographic latitude of a unit normal whose polar component is <paramref name="n3"/>.</summary>
    internal static double LatitudeOf(double n3) => Math.Asin(Math.Clamp(n3, -1, 1)) * 180 / Math.PI;

    /// <summary>
    /// The west longitude of the point whose body coordinates are (<paramref name="x1"/>, <paramref name="x2"/>, ...): a point
    /// toward the sky's west has crossed the central meridian already, so its west longitude is the smaller.
    /// </summary>
    internal double WestLongitudeOf(double x1, double x2) => _centralMeridian - (Math.Atan2(x1, x2) * 180 / Math.PI);
}
