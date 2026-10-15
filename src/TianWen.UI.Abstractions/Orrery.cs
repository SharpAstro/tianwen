using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.UI.Abstractions;

/// <summary>
/// How the orrery lays distances out from the Sun. Either way a direction from the Sun is kept, so the Sun's light falls on every
/// body as it truly does.
/// </summary>
public enum OrreryScale
{
    /// <summary>
    /// Distances compressed as far as the camera's framing needs (<see cref="OrreryScaling"/>): true within an AU, the inner planets
    /// pulled out of the Sun as the camera frames the outer ones.
    /// </summary>
    Auto,

    /// <summary>True distances, in AU.</summary>
    True,
}

/// <summary>
/// The distance scale of one frame: <c>r' = Z (r / Z)^p</c>, with <c>Z</c> the reach in true AU the camera frames
/// (<see cref="ReachAu"/>) and <c>p</c> running from 1 at <c>Z</c> of an AU or less to one half at 20 AU or more, by a smoothstep of
/// <c>ln Z</c> (<see cref="Exponent"/>). So the scale is the identity close in, continuous in <c>Z</c> (a zoom never jumps a body),
/// monotonic in <c>r</c> (orbits never cross) and keeps every direction from the Sun (the lighting's). <see cref="OrreryScale.True"/>
/// is the identity at every <c>Z</c>; a distance at the reach itself is the same in both, so the toggle never moves the framing.
/// </summary>
public readonly record struct OrreryScaling(OrreryScale Scale, double ReachAu)
{
    /// <summary>The reach <c>Z</c> at which the compression starts, and the one at which it is whole, AU.</summary>
    public const double FullScaleReachAu = 1, MostCompressedReachAu = 20;

    /// <summary>The camera's distance from the Sun (AU) to the reach it frames: the inverse of <see cref="OrreryCamera.DistanceToFrame"/>.</summary>
    public static double ReachFor(double cameraDistance) => cameraDistance * Math.Sin(0.85 * OrreryCamera.FieldOfViewDeg * Math.PI / 360);

    /// <summary>The power <c>p</c> the distance is raised to.</summary>
    public double Exponent
    {
        get
        {
            if (Scale == OrreryScale.True)
            {
                return 1;
            }
            var t = Math.Clamp(Math.Log(ReachAu / FullScaleReachAu) / Math.Log(MostCompressedReachAu / FullScaleReachAu), 0, 1);
            return 1 - (0.5 * t * t * (3 - (2 * t)));
        }
    }

    /// <summary>A true heliocentric point (AU) where the frame draws it, its direction from the Sun kept.</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var p = Exponent;
        if (p == 1)
        {
            return (x, y, z);
        }
        var r = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (r == 0)
        {
            return (0, 0, 0);
        }
        var k = ReachAu * Math.Pow(r / ReachAu, p) / r;
        return (x * k, y * k, z * k);
    }
}

/// <summary>
/// The atlas's solar system in 3D (docs/plans/atlas-planet-detail.md, A5): a MODE of the sky map tab, whose state lives on
/// <see cref="SkyMapState.Orrery"/>. The camera orbits the Sun; the instant is the atlas's own (<see cref="SkyMapState.TimeOffset"/>).
/// </summary>
public sealed class OrreryState
{
    /// <summary>Whether the tab shows the orrery instead of the sky.</summary>
    public bool IsOpen { get; internal set; }

    /// <summary>The body the view was opened on, highlighted and framed; null when it was opened with no solar-system body selected.</summary>
    public CatalogIndex? Focus { get; internal set; }

    /// <summary>
    /// The comet the view was opened on, drawn with its orbit until the view closes. Apart from <see cref="Focus"/>, which a click
    /// moves: a click on a planet moves the highlight and must not take the comet away.
    /// </summary>
    public CatalogIndex? Comet { get; internal set; }

    /// <summary>
    /// Whether the camera looks at <see cref="Focus"/> (the strip's Centre toggle) rather than the Sun, following it as time runs; a
    /// click on another body moves the camera with the highlight, a click on the Sun puts the camera back on it.
    /// </summary>
    public bool CentredOnFocus { get; internal set; }

    /// <summary>The camera's longitude about the ecliptic pole, degrees (ecliptic of J2000).</summary>
    public double AzimuthDeg { get; set; } = -90;

    /// <summary>The camera's height above the ecliptic, degrees, kept inside +/- <see cref="OrreryCamera.MaxElevationDeg"/>.</summary>
    public double ElevationDeg { get; set; } = 30;

    /// <summary>The camera's distance from the Sun, in the scale's own units (AU, or the square root of AU).</summary>
    public double Distance { get; set; } = 6;

    /// <summary>True or compressed distances.</summary>
    public OrreryScale Scale { get; set; } = OrreryScale.Auto;

    /// <summary>Whether time runs on its own, at <see cref="DaysPerSecond"/>.</summary>
    public bool Playing { get; set; }

    /// <summary>Whether the sky map's stars and Milky Way are drawn behind the scene, at infinity (on by default).</summary>
    public bool ShowBackdrop { get; set; } = true;

    /// <summary>How fast a play runs, in days of the atlas's clock per second of the wall clock.</summary>
    public double DaysPerSecond { get; set; } = 10;

    internal bool Dragging;
    internal bool Pinching;
    internal (float X, float Y) DragStart;
    internal (double Az, double El) DragStartAngles;
    internal long PlayTimestamp;
}

/// <summary>
/// A perspective camera on a point orbiting the Sun (or the body the view is centred on), looking at it, the ecliptic's north up: projects a point of the scene (ecliptic
/// coordinates in the scale's units) to the screen. A proper view, never mirrored: screen right, screen up and the direction back
/// toward the camera make a right-handed frame, so a body's sprite is drawn unmirrored.
/// </summary>
public readonly struct OrreryCamera
{
    /// <summary>The vertical field of view, degrees.</summary>
    public const double FieldOfViewDeg = 45;

    /// <summary>The steepest the camera looks down on (or up at) the ecliptic: short of the pole, where "up" would be undefined.</summary>
    public const double MaxElevationDeg = 89;

    private readonly double _ex, _ey, _ez;
    private readonly double _fx, _fy, _fz;
    private readonly double _rx, _ry, _rz;
    private readonly double _ux, _uy, _uz;
    private readonly double _focal;
    private readonly float _cx, _cy;

    /// <param name="target">The point the camera looks at, in the scene's units: the Sun (the origin) unless the view is centred on a
    /// body. Only the camera's place moves with it; its rotation, and so the backdrop's stars, are the same.</param>
    public OrreryCamera(double azimuthDeg, double elevationDeg, double distance, RectF32 rect, (double X, double Y, double Z) target = default)
    {
        var (sinAz, cosAz) = Math.SinCos(azimuthDeg * Math.PI / 180);
        var (sinEl, cosEl) = Math.SinCos(Math.Clamp(elevationDeg, -MaxElevationDeg, MaxElevationDeg) * Math.PI / 180);
        (_ex, _ey, _ez) = (target.X + (distance * cosEl * cosAz), target.Y + (distance * cosEl * sinAz), target.Z + (distance * sinEl));
        // Forward toward the target; right = forward x ecliptic north; up = right x forward.
        (_fx, _fy, _fz) = (-cosEl * cosAz, -cosEl * sinAz, -sinEl);
        var (rx, ry) = (_fy, -_fx);
        var rn = Math.Sqrt((rx * rx) + (ry * ry));
        (_rx, _ry, _rz) = (rx / rn, ry / rn, 0);
        (_ux, _uy, _uz) = ((_ry * _fz) - (_rz * _fy), (_rz * _fx) - (_rx * _fz), (_rx * _fy) - (_ry * _fx));
        _focal = rect.Height / 2 / Math.Tan(FieldOfViewDeg * Math.PI / 360);
        _cx = rect.X + (rect.Width / 2);
        _cy = rect.Y + (rect.Height / 2);
    }

    /// <summary>Where the camera stands.</summary>
    public (double X, double Y, double Z) Eye => (_ex, _ey, _ez);

    /// <summary>The distance in front of the camera, along its axis: what the painter sorts by and what decides a point is behind it.</summary>
    public double Depth(double x, double y, double z) => ((x - _ex) * _fx) + ((y - _ey) * _fy) + ((z - _ez) * _fz);

    /// <summary>The screen point of (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>); false when it is not in front of the camera.</summary>
    public bool TryProject(double x, double y, double z, out float sx, out float sy)
    {
        var (dx, dy, dz) = (x - _ex, y - _ey, z - _ez);
        var depth = (dx * _fx) + (dy * _fy) + (dz * _fz);
        if (depth <= 1e-6)
        {
            sx = sy = float.NaN;
            return false;
        }
        var k = _focal / depth;
        sx = (float)(_cx + (k * ((dx * _rx) + (dy * _ry) + (dz * _rz))));
        sy = (float)(_cy - (k * ((dx * _ux) + (dy * _uy) + (dz * _uz))));
        return true;
    }

    /// <summary>The gnomonic focal length in pixels: a direction <c>theta</c> off the axis lands this times <c>tan theta</c> out.</summary>
    public double FocalPx => _focal;

    /// <summary>
    /// The camera's view of the stars at infinity as a sky-map view matrix (J2000 equatorial to camera space, the camera looking along
    /// -Z, x right, y up): the camera's ROTATION alone, no translation, since a star is at infinity, composed with the fixed tilt
    /// from the ecliptic of J2000, the orrery's frame, to the equator the catalogue is in (<see cref="SolarSystemOrbits.EclipticToEquatorial"/>,
    /// VSOP87's own, the obliquity 23.44 degrees). <paramref name="axis"/> is the look direction in J2000, the star cull's axis.
    /// Projected gnomonically with <see cref="FocalPx"/>, a star lands where <see cref="TryProject"/> puts a point infinitely far along it.
    /// </summary>
    public Matrix4x4 EquatorialView(out (float X, float Y, float Z) axis)
    {
        Span<double> right = [_rx, _ry, _rz];
        Span<double> up = [_ux, _uy, _uz];
        Span<double> forward = [_fx, _fy, _fz];
        SolarSystemOrbits.EclipticToEquatorial(right);
        SolarSystemOrbits.EclipticToEquatorial(up);
        SolarSystemOrbits.EclipticToEquatorial(forward);
        axis = ((float)forward[0], (float)forward[1], (float)forward[2]);
        return new Matrix4x4(
            (float)right[0], (float)right[1], (float)right[2], 0,
            (float)up[0], (float)up[1], (float)up[2], 0,
            (float)-forward[0], (float)-forward[1], (float)-forward[2], 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// The camera's distance that shows a sphere of <paramref name="radius"/> about the Sun filling <paramref name="fill"/> of the view's
    /// height: how a view opened on a body frames that body's orbit.
    /// </summary>
    public static double DistanceToFrame(double radius, double fill = 0.85)
        => radius / Math.Sin(fill * FieldOfViewDeg * Math.PI / 360);
}

/// <summary>A body where the orrery draws it at one instant: true heliocentric ecliptic AU (the Moon's own place, not the drawn one).</summary>
public readonly record struct OrreryBody(CatalogIndex Index, string Name, double X, double Y, double Z);

/// <summary>
/// What the orrery draws, cached so a drag or a scrub costs a projection and no ephemeris: the bodies' positions per MINUTE of the
/// atlas's clock, the planets' orbits per CENTURY (built off the render thread, the Task the hand-off), and a comet's orbit per
/// element set. A drag changes none of the keys, so it builds nothing (<see cref="OrbitBuilds"/>, <see cref="CometOrbitBuilds"/>).
/// </summary>
public sealed class OrreryScene(Action? requestRedraw = null)
{
    /// <summary>Points per planet orbit, the last repeating the first.</summary>
    public const int OrbitSamples = 241;

    /// <summary>Points per comet orbit.</summary>
    public const int CometOrbitSamples = 361;

    /// <summary>How far an open (parabolic or hyperbolic) comet orbit is drawn from the Sun, AU.</summary>
    public const double OpenOrbitReachAu = 40;

    private long _positionsMinute = long.MinValue;
    private CometElements? _positionsComet;
    private ImmutableArray<OrreryBody> _bodies = [];

    private long _orbitCentury = long.MinValue;
    private Task<ImmutableDictionary<CatalogIndex, double[]>>? _orbitsPending;
    private ImmutableDictionary<CatalogIndex, double[]>? _orbits;
    private int _orbitBuilds;

    private CometElements? _cometOrbitElements;
    private double[] _cometOrbit = [];
    private int _cometOrbitBuilds;
    private int _positionBuilds;

    /// <summary>How many times the planets' orbits have been built: once, unless the clock crosses a century.</summary>
    public int OrbitBuilds => Volatile.Read(ref _orbitBuilds);

    /// <summary>How many times a comet orbit has been built: once per element set.</summary>
    public int CometOrbitBuilds => _cometOrbitBuilds;

    /// <summary>How many times the positions have been computed: once per minute of the clock that a frame shows.</summary>
    public int PositionBuilds => _positionBuilds;

    /// <summary>The Sun, the planets, the Moon and the comet (when one is given) at the last instant <see cref="Update"/> saw.</summary>
    public ImmutableArray<OrreryBody> Bodies => _bodies;

    /// <summary>
    /// Brings the positions to <paramref name="utc"/> (to its minute) and the comet's to <paramref name="comet"/>'s elements, and
    /// starts the orbits' build if the century changed. Cheap when nothing did.
    /// </summary>
    public void Update(DateTimeOffset utc, in CometElements? comet)
    {
        var minute = utc.UtcTicks / TimeSpan.TicksPerMinute;
        // Keyed on the comet's ELEMENTS, as its orbit is: an apparition upgrade keeps the index and changes them, and a position
        // keyed on the index would stay on the old orbit until the minute changed.
        if (minute != _positionsMinute || comet != _positionsComet)
        {
            _positionsMinute = minute;
            _positionsComet = comet;
            _bodies = ComputeBodies(new DateTimeOffset(minute * TimeSpan.TicksPerMinute, TimeSpan.Zero), comet);
            _positionBuilds++;
        }

        var century = (long)Math.Floor((utc.UtcDateTime.Year - 2000) / 100.0);
        if (century != _orbitCentury)
        {
            _orbitCentury = century;
            var start = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero).AddYears((int)(century * 100));
            Interlocked.Increment(ref _orbitBuilds);
            _orbitsPending = Task.Run(() => BuildOrbits(start));
            if (requestRedraw is { } redraw)
            {
                _orbitsPending.ContinueWith(_ => redraw(), TaskScheduler.Default);
            }
        }
    }

    /// <summary>The planets' orbits (true AU, <see cref="OrbitSamples"/> points each), or null until the first build lands.</summary>
    public ImmutableDictionary<CatalogIndex, double[]>? Orbits
    {
        get
        {
            if (_orbitsPending is { IsCompleted: true } done)
            {
                _orbitsPending = null;
                if (done.IsCompletedSuccessfully)
                {
                    _orbits = done.Result;
                }
            }
            return _orbits;
        }
    }

    /// <summary>The comet's orbit (true AU), built once per element set: <see cref="CometOrbitSamples"/> points.</summary>
    public double[] CometOrbit(in CometElements elements)
    {
        if (_cometOrbitElements != elements)
        {
            _cometOrbitElements = elements;
            var points = new double[3 * CometOrbitSamples];
            CometEphemeris.SampleOrbit(elements, points, OpenOrbitReachAu);
            _cometOrbit = points;
            _cometOrbitBuilds++;
        }
        return _cometOrbit;
    }

    /// <summary>For a test that wants the orbits the next frame draws.</summary>
    internal async Task WhenOrbitsBuiltAsync()
    {
        if (_orbitsPending is { } pending)
        {
            await pending;
        }
    }

    /// <summary>A true heliocentric point where <paramref name="scaling"/> draws it (<see cref="OrreryScaling.Apply"/>).</summary>
    public static (double X, double Y, double Z) Scaled(in OrreryScaling scaling, double x, double y, double z) => scaling.Apply(x, y, z);

    /// <summary>
    /// Where <paramref name="body"/> is DRAWN in the scene: its scaled position, and for the Moon the Earth's, since at any scale that
    /// shows the planets the Moon would sit on the Earth. The view places the Moon on SCREEN, a steady reach from the Earth along its
    /// true geocentric direction (<c>SkyMapTab.OrreryMoonScreenDirection</c>); this is what its depth and its lighting read.
    /// </summary>
    public (double X, double Y, double Z) Drawn(in OrreryScaling scale, in OrreryBody body)
    {
        if (body.Index == CatalogIndex.Moon && TryFind(CatalogIndex.Earth, out var earth))
        {
            return Scaled(scale, earth.X, earth.Y, earth.Z);
        }
        return Scaled(scale, body.X, body.Y, body.Z);
    }

    /// <summary>The body at the last instant, if the scene holds it.</summary>
    public bool TryFind(CatalogIndex index, out OrreryBody body)
    {
        foreach (var b in _bodies)
        {
            if (b.Index == index)
            {
                body = b;
                return true;
            }
        }
        body = default;
        return false;
    }

    private static ImmutableArray<OrreryBody> ComputeBodies(DateTimeOffset utc, in CometElements? comet)
    {
        var builder = ImmutableArray.CreateBuilder<OrreryBody>(11);
        builder.Add(new OrreryBody(CatalogIndex.Sol, "Sun", 0, 0, 0));
        Span<double> xyz = stackalloc double[3];
        foreach (var planet in SolarSystemOrbits.Planets)
        {
            SolarSystemOrbits.TryGetPosition(planet, utc, xyz);
            builder.Add(new OrreryBody(planet, NameOf(planet), xyz[0], xyz[1], xyz[2]));
        }
        SolarSystemOrbits.TryGetPosition(CatalogIndex.Moon, utc, xyz);
        builder.Add(new OrreryBody(CatalogIndex.Moon, "Moon", xyz[0], xyz[1], xyz[2]));

        if (comet is { } el && el.CatalogIndex is { } index)
        {
            utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
            if (CometEphemeris.TryGetHeliocentricEcliptic(el, tt1 + tt2, xyz, out _))
            {
                builder.Add(new OrreryBody(index, el.DisplayName, xyz[0], xyz[1], xyz[2]));
            }
        }
        return builder.ToImmutable();
    }

    private static ImmutableDictionary<CatalogIndex, double[]> BuildOrbits(DateTimeOffset start)
    {
        var orbits = ImmutableDictionary.CreateBuilder<CatalogIndex, double[]>();
        foreach (var planet in SolarSystemOrbits.Planets)
        {
            var points = new double[3 * OrbitSamples];
            SolarSystemOrbits.SampleOrbit(planet, start, points);
            orbits[planet] = points;
        }
        return orbits.ToImmutable();
    }

    /// <summary>The body's name as the orrery labels it.</summary>
    public static string NameOf(CatalogIndex body) => body switch
    {
        CatalogIndex.Sol => "Sun",
        CatalogIndex.Mercury => "Mercury",
        CatalogIndex.Venus => "Venus",
        CatalogIndex.Earth => "Earth",
        CatalogIndex.Mars => "Mars",
        CatalogIndex.Jupiter => "Jupiter",
        CatalogIndex.Saturn => "Saturn",
        CatalogIndex.Uranus => "Uranus",
        CatalogIndex.Neptune => "Neptune",
        CatalogIndex.Moon => "Moon",
        _ => body.ToString(),
    };
}
