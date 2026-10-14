using System;
using System.Collections.Immutable;
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
    /// <summary>The square root of the distance: Neptune 8.8 times Mercury's distance out rather than 77, so the inner planets are not one dot.</summary>
    Compressed,

    /// <summary>True distances, in AU.</summary>
    True,
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

    /// <summary>The camera's longitude about the ecliptic pole, degrees (ecliptic of J2000).</summary>
    public double AzimuthDeg { get; set; } = -90;

    /// <summary>The camera's height above the ecliptic, degrees, kept inside +/- <see cref="OrreryCamera.MaxElevationDeg"/>.</summary>
    public double ElevationDeg { get; set; } = 30;

    /// <summary>The camera's distance from the Sun, in the scale's own units (AU, or the square root of AU).</summary>
    public double Distance { get; set; } = 6;

    /// <summary>True or compressed distances.</summary>
    public OrreryScale Scale { get; set; } = OrreryScale.Compressed;

    /// <summary>Whether time runs on its own, at <see cref="DaysPerSecond"/>.</summary>
    public bool Playing { get; set; }

    /// <summary>How fast a play runs, in days of the atlas's clock per second of the wall clock.</summary>
    public double DaysPerSecond { get; set; } = 10;

    internal bool Dragging;
    internal bool Pinching;
    internal (float X, float Y) DragStart;
    internal (double Az, double El) DragStartAngles;
    internal long PlayTimestamp;
}

/// <summary>
/// A perspective camera on a point orbiting the Sun, looking at it, the ecliptic's north up: projects a point of the scene (ecliptic
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

    public OrreryCamera(double azimuthDeg, double elevationDeg, double distance, RectF32 rect)
    {
        var (sinAz, cosAz) = Math.SinCos(azimuthDeg * Math.PI / 180);
        var (sinEl, cosEl) = Math.SinCos(Math.Clamp(elevationDeg, -MaxElevationDeg, MaxElevationDeg) * Math.PI / 180);
        (_ex, _ey, _ez) = (distance * cosEl * cosAz, distance * cosEl * sinAz, distance * sinEl);
        // Forward toward the Sun; right = forward x ecliptic north; up = right x forward.
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

    /// <summary>The Moon's drawn distance from the Earth as a fraction of the Earth's drawn distance from the Sun: an exaggeration, as its size is.</summary>
    public const double MoonOffsetFraction = 0.08;

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

    /// <summary>
    /// A true heliocentric point in the scale's units: unchanged in <see cref="OrreryScale.True"/>, its distance's square root in
    /// <see cref="OrreryScale.Compressed"/>, the direction kept.
    /// </summary>
    public static (double X, double Y, double Z) Scaled(OrreryScale scale, double x, double y, double z)
    {
        if (scale == OrreryScale.True)
        {
            return (x, y, z);
        }
        var r = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (r == 0)
        {
            return (0, 0, 0);
        }
        var k = Math.Sqrt(r) / r;
        return (x * k, y * k, z * k);
    }

    /// <summary>
    /// Where <paramref name="body"/> is DRAWN: its scaled position, but the Moon is put <see cref="MoonOffsetFraction"/> of the Earth's
    /// drawn distance from the Earth along its true direction, since at any scale that shows the planets it would sit on the Earth.
    /// </summary>
    public (double X, double Y, double Z) Drawn(OrreryScale scale, in OrreryBody body)
    {
        if (body.Index != CatalogIndex.Moon || !TryFind(CatalogIndex.Earth, out var earth))
        {
            return Scaled(scale, body.X, body.Y, body.Z);
        }
        var (ex, ey, ez) = Scaled(scale, earth.X, earth.Y, earth.Z);
        var (dx, dy, dz) = (body.X - earth.X, body.Y - earth.Y, body.Z - earth.Z);
        var d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        var reach = MoonOffsetFraction * Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez)) / d;
        return (ex + (dx * reach), ey + (dy * reach), ez + (dz * reach));
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
