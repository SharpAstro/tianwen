using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Devices;

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
/// One speed Play offers: what a second of the wall clock is on the atlas's clock, named, named compactly (for a short strip) and in
/// days.
/// </summary>
public readonly record struct OrreryPlaySpeed(string Name, string ShortName, double DaysPerSecond);

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

    /// <summary>
    /// How fast a play runs, in days of the atlas's clock per second of the wall clock: one of <see cref="PlaySpeeds"/>, a week by
    /// default (<see cref="DefaultPlaySpeed"/>).
    /// </summary>
    public double DaysPerSecond { get; set; } = DefaultPlaySpeed.DaysPerSecond;

    /// <summary>
    /// Which of the brightest comets the view draws (the strip's filter), Small telescope by default: kept for the session, as the
    /// view's other settings are, never saved.
    /// </summary>
    public OrreryCometFilter CometFilter { get; set; } = OrreryCometFilter.Default;

    // A month is a twelfth of a Julian year, so twelve of them are the year the list's next entries are.
    private const double JulianYearDays = 365.25;

    /// <summary>
    /// The speeds Play offers, "1 second = <see cref="OrreryPlaySpeed.Name"/>" (#1573, TheSkyLive's orrery's fifteen), slowest
    /// first: about a doubling a step, so the drop-up reads as a ladder from the Moon's motion (six hours a second) to Neptune's (ten
    /// years).
    /// </summary>
    public static readonly ImmutableArray<OrreryPlaySpeed> PlaySpeeds =
    [
        new("6 hours", "6h", 0.25),
        new("12 hours", "12h", 0.5),
        new("1 day", "1d", 1),
        new("2 days", "2d", 2),
        new("4 days", "4d", 4),
        new("1 week", "1w", 7),
        new("2 weeks", "2w", 14),
        new("1 month", "1mo", JulianYearDays / 12),
        new("2 months", "2mo", JulianYearDays / 6),
        new("4 months", "4mo", JulianYearDays / 3),
        new("6 months", "6mo", JulianYearDays / 2),
        new("1 year", "1y", JulianYearDays),
        new("2 years", "2y", 2 * JulianYearDays),
        new("5 years", "5y", 5 * JulianYearDays),
        new("10 years", "10y", 10 * JulianYearDays),
    ];

    /// <summary>
    /// The speed a new view plays at: a week a second, the entry nearest the ten days a second Play ran at before it offered a
    /// choice (3 days off against 2 weeks' 4; on the ladder's own ratio scale the two are all but tied, and the slower keeps the
    /// fastest thing on screen, the Moon, readable: a lunar month in four seconds rather than two).
    /// </summary>
    public static OrreryPlaySpeed DefaultPlaySpeed => PlaySpeeds[5];

    /// <summary>
    /// The latest instant Play runs to, where it stops: J2000 plus two thousand years, the span over which VSOP87, the planets'
    /// ephemeris, keeps its stated precision for every planet (Bretagnon and Francou 1988: 4000 years either side of J2000 for the
    /// inner planets, 6000 for Uranus and Neptune, 2000 for Jupiter and Saturn, the narrowest). Past it the orbits drawn are no longer
    /// the planets'; and at ten years a second the clock would leave <see cref="DateTimeOffset"/>'s range (year 9999) in about
    /// twenty minutes, which threw in the render path. From today Play reaches it in about three minutes at ten years a second.
    /// </summary>
    public static readonly DateTimeOffset PlayEnd = new(4000, 1, 1, 12, 0, 0, TimeSpan.Zero);

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
    /// The camera's frame where screen point (<paramref name="sx"/>, <paramref name="sy"/>) looks, in ecliptic coordinates: the unit
    /// vector back along the ray through it toward the camera, and the screen's right made square to that. A body drawn there is traced
    /// as seen along that ray (<see cref="TianWen.Lib.Imaging.Planetary.PlanetView"/>), whatever its own place in the scene: the Moon,
    /// which is placed on screen beside the Earth, is lit as it is drawn.
    /// </summary>
    public void RayFrame(float sx, float sy, Span<double> right, Span<double> toward)
    {
        var a = (sx - _cx) / _focal;
        var b = -(sy - _cy) / _focal;
        var (dx, dy, dz) = (_fx + (a * _rx) + (b * _ux), _fy + (a * _ry) + (b * _uy), _fz + (a * _rz) + (b * _uz));
        var n = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        (toward[0], toward[1], toward[2]) = (-dx / n, -dy / n, -dz / n);
        var along = (_rx * toward[0]) + (_ry * toward[1]) + (_rz * toward[2]);
        var (qx, qy, qz) = (_rx - (along * toward[0]), _ry - (along * toward[1]), _rz - (along * toward[2]));
        var m = Math.Sqrt((qx * qx) + (qy * qy) + (qz * qz));
        (right[0], right[1], right[2]) = (qx / m, qy / m, qz / m);
    }

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

/// <summary>
/// Which comets the orrery draws besides the brightest (<see cref="OrreryScene.CometSetSize"/>): the brightest only while
/// <paramref name="ShowSet"/> (the atlas's comet layer), and always the comet the view was opened on, the focused one and the pinned
/// ones, as a pin shows a comet on the sky with its layer off.
/// </summary>
/// <param name="MaxMagnitude">The faintest month-best magnitude of the brightest drawn (<see cref="OrreryCometFilter"/>), or null for
/// every one ranked; the opened, focused and pinned comets are drawn whatever it is.</param>
public readonly record struct OrreryCometChoice(bool ShowSet, CatalogIndex? Opened, CatalogIndex? Focus, IReadOnlySet<CatalogIndex>? Pinned,
    double? MaxMagnitude = null);

/// <summary>
/// One step of the orrery's comet filter (amendment A2 of #1611: thirty comets were "super crowded"): of the comets ranked brightest
/// at the viewing instant, the ones whose best magnitude over the month (the ranking's own score, so Play inside a month never
/// flickers one at the limit) is <paramref name="MaxMagnitude"/> or brighter; null keeps them all, up to
/// <see cref="OrreryScene.CometSetSize"/>. Named in the drop-up, and compactly in a short strip.
/// </summary>
public readonly record struct OrreryCometFilter(string Name, string ShortName, double? MaxMagnitude)
{
    /// <summary>The four steps, the fewest comets first: what the eye, binoculars and a small telescope reach, and every one ranked.</summary>
    public static readonly ImmutableArray<OrreryCometFilter> Steps =
    [
        new("Naked eye", "≤6", 6),
        new("Binoculars", "≤9", 9),
        new("Small telescope", "≤12", 12),
        new("All", "All", null),
    ];

    /// <summary>A new view's step: Small telescope, magnitude 12.</summary>
    public static OrreryCometFilter Default => Steps[2];

    /// <summary>Every comet ranked.</summary>
    public static OrreryCometFilter All => Steps[3];
}

/// <summary>
/// A body where the orrery draws it at one instant: true heliocentric ecliptic AU (the Moon's own place, not the drawn one). For a
/// comet, <paramref name="ElementsStale"/> says its element set is a revolution or more old at that instant
/// (<see cref="CometElements.IsElementSetStale"/>), so the view asks for the current apparition while it draws it.
/// </summary>
public readonly record struct OrreryBody(CatalogIndex Index, string Name, double X, double Y, double Z, bool ElementsStale = false);

/// <summary>
/// What the orrery draws, cached so a drag or a scrub costs a projection and no ephemeris: the bodies' positions, the comets' among
/// them, per MINUTE of the atlas's clock and per version of the comet set; the planets' orbits per CENTURY (built off the render
/// thread, the Task the hand-off); each comet's orbit per element set, in a dictionary by its index. The comet set (the brightest
/// at the viewing instant, <see cref="CometSetSize"/>, ranked once per MONTH of the clock on the pool by <see cref="RankAt"/>, plus
/// the opened, focused and pinned ones) is read again only when its KEY changes: the repository, its
/// <see cref="ICometRepository.Version"/>, the ranking adopted, or the choice; never by comparing every element set on every frame. A drag changes none of the keys, so it builds nothing (<see cref="OrbitBuilds"/>,
/// <see cref="CometOrbitBuilds"/>, <see cref="PositionBuilds"/>), and Play across years computes the positions once a minute of the
/// clock and builds no orbit.
/// </summary>
public sealed class OrreryScene(Action? requestRedraw = null, ITimeProvider? clock = null, ILogger? logger = null)
{
    /// <summary>How a month is ranked: <see cref="RankAt"/>, unless a test gives one that fails.</summary>
    internal delegate ImmutableArray<(CatalogIndex Index, double Best)> Ranker(ImmutableArray<CometElements> candidates, long bucket, IReadOnlySet<CatalogIndex>? members);

    /// <summary>The ranking the pool runs for a month: <see cref="RankAt"/> in the app.</summary>
    internal Ranker Rank { get; init; } = RankAt;

    /// <summary>Points per planet orbit, the last repeating the first.</summary>
    public const int OrbitSamples = 241;

    /// <summary>Points per comet orbit.</summary>
    public const int CometOrbitSamples = 361;

    /// <summary>How far an open (parabolic or hyperbolic) comet orbit is drawn from the Sun, AU.</summary>
    public const double OpenOrbitReachAu = 40;

    /// <summary>How many of the brightest comets at the viewing instant the orrery draws (<see cref="RankAt"/>): about the thirty the
    /// controller asked for, which at 2026-10-16 reaches magnitude 17 or so, 10P/Tempel, 161P/Hartley-IRAS, 220P/McNaught and
    /// P/2026 R2 (Leonard) among them.</summary>
    public const int CometSetSize = 30;

    private long _positionsMinute = long.MinValue;
    private int _positionsSetVersion = -1;
    private ImmutableArray<OrreryBody> _bodies = [];

    // Every comet the ranking can score (a magnitude model and an index), gathered once per repository and count of comets. Not the
    // sky's candidates: its candidacy is a PEAK estimate, which left P/2026 R2 (Leonard, peak 18.1 against 18.0) out while it was
    // magnitude 16 and the seventeenth brightest at the instant.
    private ICometRepository? _candidatesRepo;
    private int _candidatesCount = -1;
    private ImmutableArray<CometElements> _candidates = [];

    // The brightest at the viewing instant, ranked per MONTH of the clock off the render thread (the Task the hand-off); the ranking
    // adopted, the month and repository it is of, and its version, which is in the set's key.
    private ImmutableArray<(CatalogIndex Index, double Best)> _brightest = [];
    private long _brightestBucket = long.MinValue;
    private ICometRepository? _brightestRepo;
    private int _brightestCount = -1;
    private int _brightestVersion;
    private Task<Ranking>? _rankPending;

    // The month, repository and count of comets the ranking in flight is for, and those of the last one that FAILED: a failure is not
    // tried again until one of them moves, or it would be started anew every cooldown (#1631 R2), its exception never seen.
    private (ICometRepository? Repo, int Count, long Bucket) _rankPendingKey;
    private (ICometRepository? Repo, int Count, long Bucket) _rankFailedKey = (null, -1, long.MinValue);
    private int _cometRankings;
    private double _lastRankMs;

    private sealed record Ranking(ICometRepository Repo, int Count, long Bucket, ImmutableArray<(CatalogIndex Index, double Best)> Brightest, TimeSpan Duration);

    /// <summary>The ranking adopted: the brightest at the viewing instant, brightest first, each with its best magnitude over the month.</summary>
    internal ImmutableArray<(CatalogIndex Index, double Best)> Brightest => _brightest;

    // The set, read again only when its key moves.
    private ICometRepository? _setRepo;
    private int _setRepoVersion;
    private int _setBrightestVersion;
    private OrreryCometChoice _setChoice;
    private int _setVersion;
    private ImmutableArray<CometElements> _cometSet = [];
    private readonly Dictionary<CatalogIndex, (CometElements Elements, double[] Points)> _cometOrbits = [];

    private long _orbitCentury = long.MinValue;
    private Task<ImmutableDictionary<CatalogIndex, double[]>>? _orbitsPending;
    private ImmutableDictionary<CatalogIndex, double[]>? _orbits;
    private int _orbitBuilds;

    private int _cometOrbitBuilds;
    private int _positionBuilds;

    /// <summary>How many times the planets' orbits have been built: once, unless the clock crosses a century.</summary>
    public int OrbitBuilds => Volatile.Read(ref _orbitBuilds);

    /// <summary>How many times a comet orbit has been built: once per comet and element set.</summary>
    public int CometOrbitBuilds => _cometOrbitBuilds;

    /// <summary>How many times the positions have been computed: once per minute of the clock that a frame shows, and per comet set.</summary>
    public int PositionBuilds => _positionBuilds;

    /// <summary>How many times the brightest comets have been ranked: once per month of the clock that a frame shows (and per repository).</summary>
    public int CometRankings => Volatile.Read(ref _cometRankings);

    /// <summary>
    /// How many of the last ranking's durations pass, after it lands, before the next may start (R1 Q2 of PR #1631): ranking takes at
    /// most one part in five of the time on any host (a fifth of 1 + 4), wherever the ranking runs. On a desktop a ranking is a pool
    /// thread's 13 to 16 ms; in the browser's single-threaded WebAssembly build it runs on the page's one thread between frames, 55 to
    /// 86 ms, and fast Play, which crosses a month nearly every frame, started one nearly every frame (51 in 57 frames) at 3.8 frames a
    /// second. The months between are skipped: the set is the month's the clock is in when the next ranking starts.
    /// </summary>
    public const double RankCooldownFactor = 4;

    /// <summary>
    /// The least time between a ranking landing and the next starting, whatever the ranking cost: a played frame steps at most 100 ms
    /// of the wall clock (<c>SkyMapTab.MaxPlayStep</c>), so this is one played step, at most ten rankings a second however fast they are.
    /// </summary>
    public static readonly TimeSpan RankCooldownFloor = TimeSpan.FromMilliseconds(100);

    /// <summary>The time after a ranking of <paramref name="rankDuration"/> lands before the next may start.</summary>
    public static TimeSpan CooldownAfter(TimeSpan rankDuration) => (RankCooldownFactor * rankDuration) + RankCooldownFloor;

    // The clock the cooldown is kept on (the system's unless a test gives its own), when the last ranking landed and how long it took,
    // and whether a frame is already asked for when the cooldown ends.
    private readonly ITimeProvider _clock = clock ?? SystemTimeProvider.Instance;
    private bool _everRanked;
    private long _rankLandedAt;
    private TimeSpan _lastRankDuration;
    private int _wakePending;

    /// <summary>How long the last ranking took off the render thread, milliseconds.</summary>
    public double LastRankMs => _lastRankMs;

    /// <summary>
    /// How many magnitudes brighter a comet already in the set is scored than it is (#1611): the edge of the set moves only for a
    /// comet that is clearly brighter, so a month's re-ranking does not flicker members in and out at the cutoff.
    /// </summary>
    public const double MembershipHysteresisMag = 1.0;

    /// <summary>The instants of a month a comet is scored at: its best magnitude over them, so a comet bright for part of the month counts.</summary>
    public const int RankSamplesPerMonth = 4;

    /// <summary>
    /// The most revolutions an element set may be past its epoch, at the month's middle, for its comet to be ranked. Two-body
    /// propagation from an old osculating set drifts in PHASE (CometRepository), and the magnitude law then predicts a perihelion
    /// passage that is not happening. In the catalogue of 2026-10-16 (1,984 comets ranked) the brightest stale set was C/2020 P4-B, 5.97
    /// revolutions old, modelled at magnitude 5.8 at r = 0.095 AU near a modelled perihelion on about 2026-10-27, the first of the
    /// month's best by six magnitudes; every other set among the 45 brightest was at most 4.1 old, the known periodics 0.4 to 2.9. Five
    /// keeps those and drops the fragments decades out (79P-A and B, 8.6 and 8.7). A comet with no period (an open orbit) has no
    /// revolutions to count and is kept.
    /// </summary>
    public const double MaxRevolutionsSinceEpoch = 5;

    /// <summary>The month of <paramref name="utc"/> the set is ranked for: a ranking holds while the view's clock stays in it.</summary>
    public static long MonthBucket(DateTimeOffset utc) => (utc.UtcDateTime.Year * 12L) + utc.UtcDateTime.Month - 1;

    /// <summary>
    /// The <see cref="CometSetSize"/> brightest of <paramref name="scored"/> (each comet's best magnitude over the month), a member of
    /// the current set (<paramref name="members"/>) scored <see cref="MembershipHysteresisMag"/> brighter than it is; ties go to the
    /// lower index, so a ranking is the same however the candidates were ordered. Each comet comes with its month's best as scored,
    /// the hysteresis aside: what the filter (<see cref="OrreryCometChoice.MaxMagnitude"/>) reads.
    /// </summary>
    internal static ImmutableArray<(CatalogIndex Index, double Best)> SelectBrightest(ReadOnlySpan<(CatalogIndex Index, double Best)> scored, IReadOnlySet<CatalogIndex>? members)
    {
        var ranked = new (double Score, CatalogIndex Index, double Best)[scored.Length];
        var count = 0;
        foreach (var (index, best) in scored)
        {
            if (double.IsFinite(best))
            {
                ranked[count++] = (members is not null && members.Contains(index) ? best - MembershipHysteresisMag : best, index, best);
            }
        }
        var span = ranked.AsSpan(0, count);
        span.Sort(static (a, b) => a.Score.CompareTo(b.Score) is var byScore and not 0 ? byScore : a.Index.CompareTo(b.Index));
        var brightest = ImmutableArray.CreateBuilder<(CatalogIndex, double)>(Math.Min(CometSetSize, count));
        for (var i = 0; i < brightest.Capacity; i++)
        {
            brightest.Add((span[i].Index, span[i].Best));
        }
        return brightest.MoveToImmutable();
    }

    /// <summary>
    /// Ranks <paramref name="candidates"/> for the month <paramref name="bucket"/>: each comet's best predicted magnitude over
    /// <see cref="RankSamplesPerMonth"/> instants spread over the month, through the sky's own calls (one
    /// <see cref="CometEphemeris.TryGetEarthState"/> per instant, <see cref="CometEphemeris.TryGetEquatorialJ2000WithMagnitude(in CometElements, in CometEphemeris.EarthState, out double, out double, out double)"/>
    /// per candidate), then <see cref="SelectBrightest"/>. A comet the model cannot be trusted for is not scored however bright it
    /// says it is: a D/ (defunct or lost) designation, and an element set more than <see cref="MaxRevolutionsSinceEpoch"/> past its
    /// epoch at the month's middle. Pure: run on the pool.
    /// </summary>
    internal static ImmutableArray<(CatalogIndex Index, double Best)> RankAt(ImmutableArray<CometElements> candidates, long bucket, IReadOnlySet<CatalogIndex>? members)
    {
        var start = new DateTimeOffset((int)(bucket / 12), (int)(bucket % 12) + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var month = start.AddMonths(1) - start;
        var middleJdTt = CometEphemeris.TryGetEarthState(start + (month / 2), out var middle) ? middle.JdTt : double.NaN;
        var scored = new (CatalogIndex Index, double Best)[candidates.Length];
        Span<bool> trusted = candidates.Length <= 4096 ? stackalloc bool[candidates.Length] : new bool[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
        {
            var el = candidates[i];
            scored[i] = (el.CatalogIndex ?? default, double.PositiveInfinity);
            trusted[i] = el.Designation.Kind != CometOrbitKind.Defunct && !(el.RevolutionsSinceEpoch(middleJdTt) > MaxRevolutionsSinceEpoch);
        }
        for (var sample = 0; sample < RankSamplesPerMonth; sample++)
        {
            if (!CometEphemeris.TryGetEarthState(start + (month * ((sample + 0.5) / RankSamplesPerMonth)), out var earth))
            {
                continue;
            }
            for (var i = 0; i < candidates.Length; i++)
            {
                if (trusted[i] && CometEphemeris.TryGetEquatorialJ2000WithMagnitude(candidates[i], earth, out _, out _, out var magnitude)
                    && magnitude < scored[i].Best)
                {
                    scored[i].Best = magnitude;
                }
            }
        }
        return SelectBrightest(scored, members);
    }

    /// <summary>For a test that wants the ranking the next frame adopts, or sees fail: it waits, and the frame reads the outcome.</summary>
    internal async Task WhenCometsRankedAsync()
    {
        if (_rankPending is { } pending)
        {
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>The comets drawn, each with the element set it is drawn from (the repository's, apparition upgrade included).</summary>
    public ImmutableArray<CometElements> CometSet => _cometSet;

    /// <summary>The Sun, the planets, the Moon and the comet set at the last instant <see cref="Update"/> saw.</summary>
    public ImmutableArray<OrreryBody> Bodies => _bodies;

    /// <summary>
    /// Brings the comet set to what <paramref name="choice"/> asks of <paramref name="comets"/> (when its key changed), the positions
    /// to <paramref name="utc"/> (to its minute), and starts the planets' orbits' build if the century changed. Cheap when nothing did.
    /// </summary>
    public void Update(DateTimeOffset utc, ICometRepository? comets = null, in OrreryCometChoice choice = default)
    {
        UpdateRanking(comets, choice.ShowSet, utc);
        UpdateCometSet(comets, choice);
        var minute = utc.UtcTicks / TimeSpan.TicksPerMinute;
        // Keyed on the set's version too: an apparition upgrade keeps a comet's index and changes its elements, and must move it onto
        // its new orbit at once, not when the minute changes.
        if (minute != _positionsMinute || _setVersion != _positionsSetVersion)
        {
            _positionsMinute = minute;
            _positionsSetVersion = _setVersion;
            _bodies = ComputeBodies(new DateTimeOffset(minute * TimeSpan.TicksPerMinute, TimeSpan.Zero), _cometSet.AsSpan());
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

    /// <summary>
    /// The comet's orbit (true AU, <see cref="CometOrbitSamples"/> points), built once per comet and element set and kept by its
    /// index: what the set's read asks for each member, so a frame never does.
    /// </summary>
    public double[] CometOrbit(in CometElements elements)
    {
        var index = elements.CatalogIndex ?? default;
        if (_cometOrbits.TryGetValue(index, out var built) && built.Elements == elements)
        {
            return built.Points;
        }
        var points = new double[3 * CometOrbitSamples];
        CometEphemeris.SampleOrbit(elements, points, OpenOrbitReachAu);
        _cometOrbits[index] = (elements, points);
        _cometOrbitBuilds++;
        return points;
    }

    /// <summary>The orbit drawn for the comet <paramref name="index"/>, when it is in the set.</summary>
    public bool TryGetCometOrbit(CatalogIndex index, out double[] orbit)
    {
        if (_cometOrbits.TryGetValue(index, out var built))
        {
            orbit = built.Points;
            return true;
        }
        orbit = [];
        return false;
    }

    // Adopts a ranking that has landed, and starts one when the month (or the repository) moved past the one adopted and none is in
    // flight. The ranking runs on the pool, the adopted set drawn until it lands: fast Play crosses a month nearly every frame, so at most
    // one is in flight, and the month it lands for may already be behind the clock, which the next frame then ranks again.
    private void UpdateRanking(ICometRepository? comets, bool showSet, DateTimeOffset utc)
    {
        if (_rankPending is { IsCompleted: true } done)
        {
            _rankPending = null;
            if (done.IsCompletedSuccessfully && done.Result is var ranked && ReferenceEquals(ranked.Repo, comets))
            {
                (_brightest, _brightestBucket, _brightestRepo, _brightestCount) = (ranked.Brightest, ranked.Bucket, ranked.Repo, ranked.Count);
                _lastRankMs = ranked.Duration.TotalMilliseconds;
                _brightestVersion++;
            }
            else if (!done.IsCompletedSuccessfully)
            {
                // Reading the exception observes it; the month is not ranked again (the set adopted stays) until the key moves.
                var error = done.Exception?.GetBaseException();
                _rankFailedKey = _rankPendingKey;
                logger?.LogWarning(error, "The orrery could not rank the brightest comets for {Year}-{Month:00}; the comets drawn stay as they were",
                    _rankPendingKey.Bucket / 12, (_rankPendingKey.Bucket % 12) + 1);
            }
            // Landed, adopted or not: the cooldown runs from here.
            (_everRanked, _rankLandedAt, _lastRankDuration) = (true, _clock.GetTimestamp(), done.IsCompletedSuccessfully ? done.Result.Duration : TimeSpan.Zero);
        }
        if (comets is null || !showSet || _rankPending is not null)
        {
            return;
        }

        var all = comets.All;
        if (!ReferenceEquals(comets, _candidatesRepo) || all.Length != _candidatesCount)
        {
            var candidates = ImmutableArray.CreateBuilder<CometElements>();
            foreach (var el in all)
            {
                if (el.HasMagnitudeModel && el.CatalogIndex is not null)
                {
                    candidates.Add(el);
                }
            }
            (_candidatesRepo, _candidatesCount, _candidates) = (comets, all.Length, candidates.ToImmutable());
        }

        var bucket = MonthBucket(utc);
        if (bucket == _brightestBucket && ReferenceEquals(comets, _brightestRepo) && all.Length == _brightestCount)
        {
            return;
        }
        if (bucket == _rankFailedKey.Bucket && ReferenceEquals(comets, _rankFailedKey.Repo) && all.Length == _rankFailedKey.Count)
        {
            return;
        }
        if (_everRanked && CooldownAfter(_lastRankDuration) - _clock.GetElapsedTime(_rankLandedAt) is var remaining && remaining > TimeSpan.Zero)
        {
            WakeAfter(remaining);
            return;
        }
        var (pool, count, clock, rank) = (_candidates, all.Length, _clock, Rank);
        IReadOnlySet<CatalogIndex>? members = ReferenceEquals(comets, _brightestRepo) ? Indices(_brightest) : null;
        Interlocked.Increment(ref _cometRankings);
        _rankPendingKey = (comets, all.Length, bucket);
        _rankPending = Task.Run(() =>
        {
            var started = clock.GetTimestamp();
            var brightest = rank(pool, bucket, members);
            return new Ranking(comets, count, bucket, brightest, clock.GetElapsedTime(started));
        });
        if (requestRedraw is { } redraw)
        {
            _rankPending.ContinueWith(_ => redraw(), TaskScheduler.Default);
        }
    }

    private static HashSet<CatalogIndex> Indices(ImmutableArray<(CatalogIndex Index, double Best)> ranked)
    {
        var set = new HashSet<CatalogIndex>(ranked.Length);
        foreach (var (index, _) in ranked)
        {
            set.Add(index);
        }
        return set;
    }

    // A view held still inside a cooldown draws no frame of its own, so the month it moved to would never be ranked: one frame is asked
    // for when the cooldown ends (on the scene's clock), at most one at a time, and only where the scene can ask for a frame.
    private void WakeAfter(TimeSpan remaining)
    {
        if (requestRedraw is not { } redraw || Interlocked.CompareExchange(ref _wakePending, 1, 0) != 0)
        {
            return;
        }
        _ = WakeAsync();

        async Task WakeAsync()
        {
            try
            {
                await _clock.SleepAsync(remaining);
            }
            finally
            {
                Volatile.Write(ref _wakePending, 0);
                redraw();
            }
        }
    }

    // The set's key: the repository by reference, its version, the ranking adopted (its version moves with the month's ranking), and
    // the choice (the pinned set by reference: the tab's pinned set is rebuilt only when the proposals change). Read again only when it
    // moves: only a newcomer's orbit is built.
    private void UpdateCometSet(ICometRepository? comets, in OrreryCometChoice choice)
    {
        var version = comets?.Version ?? 0;
        if (ReferenceEquals(comets, _setRepo) && version == _setRepoVersion && _brightestVersion == _setBrightestVersion
            && choice.ShowSet == _setChoice.ShowSet && choice.Opened == _setChoice.Opened && choice.Focus == _setChoice.Focus
            && ReferenceEquals(choice.Pinned, _setChoice.Pinned) && choice.MaxMagnitude == _setChoice.MaxMagnitude)
        {
            return;
        }
        (_setRepo, _setRepoVersion, _setChoice, _setBrightestVersion) = (comets, version, choice, _brightestVersion);
        _setVersion++;
        if (comets is null)
        {
            _cometSet = [];
            _cometOrbits.Clear();
            return;
        }

        var members = new List<CatalogIndex>(CometSetSize + 4);
        void Add(CatalogIndex? index)
        {
            if (index is { } i && i.ToCatalog() == Catalog.Comet && !members.Contains(i))
            {
                members.Add(i);
            }
        }
        Add(choice.Opened);
        Add(choice.Focus);
        if (choice.Pinned is { } pinned)
        {
            foreach (var index in pinned)
            {
                Add(index);
            }
        }
        if (choice.ShowSet)
        {
            // The filter reads each comet's month-best, the ranking's own score, so it holds for the month as the ranking does.
            foreach (var (index, best) in _brightest)
            {
                if (choice.MaxMagnitude is not { } faintest || best <= faintest)
                {
                    Add(index);
                }
            }
        }

        var set = ImmutableArray.CreateBuilder<CometElements>(members.Count);
        foreach (var index in members)
        {
            if (comets.TryGet(index, out var elements))
            {
                set.Add(elements);
                CometOrbit(elements);
            }
        }
        _cometSet = set.ToImmutable();
        // The orbits of comets that left the set go with them.
        if (_cometOrbits.Count > _cometSet.Length)
        {
            foreach (var index in new List<CatalogIndex>(_cometOrbits.Keys))
            {
                if (!members.Contains(index))
                {
                    _cometOrbits.Remove(index);
                }
            }
        }
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

    private OrreryScaling _scaledFor;
    private int _scaledGeneration;
    private readonly Dictionary<double[], (int Generation, double[] Points)> _scaled = new(ReferenceEqualityComparer.Instance);
    private int _scaledOrbitBuilds;

    /// <summary>How many times an orbit's points have been scaled: once per orbit and scaling, so never by a drag or by Play.</summary>
    public int ScaledOrbitBuilds => _scaledOrbitBuilds;

    /// <summary>
    /// An orbit's points (<see cref="Orbits"/>, <see cref="TryGetCometOrbit"/>) where <paramref name="scaling"/> draws them, kept per
    /// orbit until the scaling changes (a zoom of the auto scale, the scale's toggle), into the same array: the drawing then only
    /// projects, which is what a drag or a played frame costs.
    /// </summary>
    public double[] ScaledOrbit(double[] orbit, in OrreryScaling scaling)
    {
        if (scaling != _scaledFor)
        {
            _scaledFor = scaling;
            _scaledGeneration++;
        }
        if (_scaled.TryGetValue(orbit, out var kept) && kept.Generation == _scaledGeneration)
        {
            return kept.Points;
        }
        if (kept.Points is null && _scaled.Count >= 4 * (SolarSystemOrbits.Planets.Length + CometSetSize))
        {
            // Orbits replaced (a century's, a comet's new elements) leave their arrays behind as keys: let them go.
            _scaled.Clear();
        }
        var points = kept.Points ?? new double[orbit.Length];
        for (var i = 0; i + 2 < orbit.Length; i += 3)
        {
            (points[i], points[i + 1], points[i + 2]) = scaling.Apply(orbit[i], orbit[i + 1], orbit[i + 2]);
        }
        _scaled[orbit] = (_scaledGeneration, points);
        _scaledOrbitBuilds++;
        return points;
    }

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

    private static ImmutableArray<OrreryBody> ComputeBodies(DateTimeOffset utc, ReadOnlySpan<CometElements> comets)
    {
        var builder = ImmutableArray.CreateBuilder<OrreryBody>(11 + comets.Length);
        builder.Add(new OrreryBody(CatalogIndex.Sol, "Sun", 0, 0, 0));
        Span<double> xyz = stackalloc double[3];
        foreach (var planet in SolarSystemOrbits.Planets)
        {
            SolarSystemOrbits.TryGetPosition(planet, utc, xyz);
            builder.Add(new OrreryBody(planet, NameOf(planet), xyz[0], xyz[1], xyz[2]));
        }
        SolarSystemOrbits.TryGetPosition(CatalogIndex.Moon, utc, xyz);
        builder.Add(new OrreryBody(CatalogIndex.Moon, "Moon", xyz[0], xyz[1], xyz[2]));

        if (comets.Length > 0)
        {
            utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
            foreach (var el in comets)
            {
                if (el.CatalogIndex is { } index && CometEphemeris.TryGetHeliocentricEcliptic(el, tt1 + tt2, xyz, out _))
                {
                    builder.Add(new OrreryBody(index, el.DisplayName, xyz[0], xyz[1], xyz[2], el.IsElementSetStale(tt1 + tt2)));
                }
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
