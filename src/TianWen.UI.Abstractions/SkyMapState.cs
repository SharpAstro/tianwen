using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Mutable viewport state for the sky map tab. Tracks view center (RA/Dec),
    /// field of view, display toggles, and drag state.
    /// </summary>
    public class SkyMapState
    {
        private const double Hours2Rad = Math.PI / 12.0;
        private const float Hours2RadF = MathF.PI / 12f;

        /// <summary>Viewport center RA in hours (J2000), range [0, 24).</summary>
        public double CenterRA { get; set; } = 0.0;

        /// <summary>Viewport center Dec in degrees (J2000), range [-90, +90].</summary>
        public double CenterDec { get; set; } = 0.0;

        /// <summary>True once the view has been initialized from site coordinates.</summary>
        public bool Initialized { get; set; }

        /// <summary>
        /// Set by a caller that has ALREADY decided where the view looks -- today a share link's
        /// <c>ra</c>/<c>dec</c> (P20). Consumed by the first home-positioning pass, which then records
        /// the site as usual but leaves the pointing alone.
        /// </summary>
        /// <remarks>
        /// A one-shot rather than a permanent opt-out, because the pass it suppresses does two jobs:
        /// place the view on first sight of a site, and RE-place it when the site changes. Only the
        /// first is in conflict with a caller's pointing; a later profile switch should still re-home.
        /// <para>
        /// It cannot be done by setting <see cref="Initialized"/> from outside, which is the obvious
        /// thing to try: the pass also fires whenever the site differs from the one it last homed for,
        /// and that comparison starts against <see cref="double.NaN"/> -- so it is unconditionally true
        /// the first time, whatever Initialized says.
        /// </para>
        /// </remarks>
        public bool ExternalViewPending { get; set; }

        /// <summary>Full viewport vertical field of view in degrees, range [0.5, 180].</summary>
        public double FieldOfViewDeg { get; set; } = 60.0;

        /// <summary>Display mode: equatorial (RA/Dec grid) or horizon (Alt/Az grid).
        /// Defaults to Horizon: keeps the horizon line horizontal and zenith up, which
        /// matches how most users naturally navigate ("look north-east", "high in the
        /// south") rather than by abstract RA/Dec coordinates.</summary>
        public SkyMapMode Mode { get; set; } = SkyMapMode.Horizon;

        // Display toggles
        /// <summary>Show constellation boundary outlines (B key).</summary>
        public bool ShowConstellationBoundaries { get; set; } = true;

        /// <summary>Show horizon line and clip below-horizon stars (H key).</summary>
        public bool ShowHorizon { get; set; } = true;

        /// <summary>Show constellation stick figures (C key).</summary>
        public bool ShowConstellationFigures { get; set; } = true;
        public bool ShowGrid { get; set; } = true;
        public bool ShowPlanets { get; set; } = true;

        /// <summary>Show JPL comet markers (E key, "com[e]t"). Comets are ephemeris-computed from the
        /// cached <see cref="ICometRepository"/> element set, exactly as planets come from VSOP87a.</summary>
        public bool ShowComets { get; set; } = true;

        /// <summary>Show Alt/Az coordinate grid (A key toggles mode + grid).</summary>
        public bool ShowAltAzGrid { get; set; }

        /// <summary>Show the diffuse Milky Way background texture (W key). Only visible
        /// when <see cref="MilkyWayAvailable"/> is true (texture file loaded).</summary>
        public bool ShowMilkyWay { get; set; } = true;

        /// <summary>True when the Milky Way texture has been loaded from disk.</summary>
        public bool MilkyWayAvailable { get; set; }

        /// <summary>
        /// Show the catalog object overlay (Messier / NGC / IC / named stars); same
        /// overlay as the FITS viewer's <c>[O]</c> toggle. Off by default because the
        /// sky map is already dense with stars and constellation figures.
        /// </summary>
        public bool ShowObjectOverlay { get; set; }

        /// <summary>
        /// Show dark nebulae (Barnard / LDN / Dobashi dust lanes) as their own overlay
        /// layer, toggled with the <c>[D]</c> key. Kept separate from
        /// <see cref="ShowObjectOverlay"/> (<c>[O]</c>) so the dust-cloud markers don't
        /// clutter the default deep-sky overlay. Off by default.
        /// </summary>
        public bool ShowDarkNebulae { get; set; }

        /// <summary>
        /// Current mount pointing for the reticle overlay. Null when no mount is connected
        /// or its coordinates can't be read. Populated by the event loop from the single
        /// canonical <c>LiveSessionState.MountState</c> (fed by the preview poll while idle,
        /// the running session's poll otherwise). RA/Dec are J2000 when available; native
        /// coords are used as a fallback for mounts where the J2000 conversion hasn't been
        /// populated yet.
        /// </summary>
        public SkyMapMountOverlay? MountOverlay { get; set; }

        /// <summary>Toggle the mount reticle (<c>[M]</c> key).</summary>
        public bool ShowMountOverlay { get; set; } = true;

        /// <summary>
        /// True while a sky-map Solve &amp; Sync (capture + plate-solve + sync) is in
        /// flight. Set by the signal handler when the capture starts and cleared when it
        /// finishes; the mount info-panel button reads it to show "Solving ..." and to
        /// suppress re-triggering mid-solve.
        /// </summary>
        public bool SolveSyncInProgress { get; set; }

        /// <summary>
        /// The mount's current slew destination (J2000) + display name while a goto issued
        /// from the sky map is in flight; null when the mount is not slewing to a
        /// GUI-known target. The renderer draws a destination marker, but when the target
        /// coincides with an already-rendered scheduled / pinned marker it augments that
        /// (connecting line + ETA) instead of drawing a duplicate reticle.
        /// </summary>
        public SlewTargetInfo? ActiveSlewTarget { get; set; }

        /// <summary>
        /// Estimated seconds until the <see cref="ActiveSlewTarget"/> slew completes, or
        /// <see cref="double.NaN"/> when not yet estimable (too little motion observed).
        /// Computed in the render path from the polled reticle position + wall clock so it
        /// does not add a second concurrent mount reader alongside the telemetry poll.
        /// </summary>
        public double SlewEtaSeconds { get; set; } = double.NaN;

        /// <summary>
        /// Pre-computed mosaic panel centres for pinned targets whose catalog shape
        /// exceeds the sensor FOV. Populated by the event loop alongside the mount
        /// overlay. Each entry is the RA/Dec centre of one panel. The sensor FOV
        /// (from <see cref="SkyMapMountOverlay.SensorFovDeg"/>) defines the rectangle
        /// size for every panel. Empty when no mosaic-worthy targets are pinned or
        /// no camera is connected.
        /// </summary>
        public ImmutableArray<(double RA, double Dec, string Name, int Row, int Col)> MosaicPanels { get; set; } = [];

        /// <summary>
        /// Committed observing-plan target(s) for the sky-map overlay: RA/Dec centre (J2000),
        /// display name, and whether this is the currently-executing observation. Populated by
        /// the event loop from the built schedule (and the running session's active observation),
        /// so the user can see where tonight's targets sit. Empty when no plan is committed.
        /// </summary>
        public ImmutableArray<(double RA, double Dec, string Name, bool IsActive)> ScheduleTargets { get; set; } = [];

        /// <summary>Cached view matrix, updated each frame by the rendering layer.</summary>
        public Matrix4x4 CurrentViewMatrix { get; set; } = Matrix4x4.Identity;

        /// <summary>
        /// Content rectangle from the most recent <see cref="SkyMapTab{TSurface}.Render"/>
        /// call. Used by out-of-tab signal handlers (e.g. click-select) that need to
        /// unproject screen coordinates without holding a tab reference.
        /// </summary>
        public DIR.Lib.RectF32 LastContentRect { get; set; }

        // Drag state
        public bool IsDragging { get; set; }
        public (float X, float Y) DragStart { get; set; }
        public (double RA, double Dec) DragStartCenter { get; set; }
        /// <summary>View matrix at drag start: needed for correct unproject during drag.</summary>
        public Matrix4x4 DragStartViewMatrix { get; set; }

        /// <summary>FOV at the start of a pinch gesture, for absolute scale application.</summary>
        public double PinchStartFov { get; set; }

        /// <summary>True while a two-finger pinch is active; suppresses drag.</summary>
        public bool IsPinching { get; set; }

        /// <summary>
        /// User-controlled base magnitude limit floor. Brighter = lower number.
        /// Keyboard + / − adjusts this; the effective limit sent to the GPU also
        /// grows as the user zooms in: see <see cref="EffectiveMagnitudeLimit"/>.
        /// Must stay in sync with the Milky Way bake's <c>--min-mag</c>: stars
        /// fainter than this limit contribute to the diffuse texture, brighter
        /// ones are drawn as point sprites. A mismatch produces halos.
        /// </summary>
        public float MagnitudeLimit { get; set; } = 8.5f;

        /// <summary>
        /// Slope of the zoom-OUT magnitude falloff, in magnitudes per decade of FOV. Separate from
        /// the 2.5 zoom-in slope because the two directions answer different questions: zooming in
        /// reveals stars that were too faint to matter, while zooming out is about stopping the ones
        /// already on screen from piling up.
        /// </summary>
        private const double WideFieldMagFalloffPerDecade = 3.0;

        /// <summary>Most magnitudes the zoom-out falloff may subtract, however wide the field.</summary>
        private const double MaxWideFieldMagReduction = 2.0;

        /// <summary>
        /// FOV-aware magnitude limit (Stellarium-style <c>computeRCMag</c> analogue).
        /// As the user zooms in (FOV shrinks) the effective limit grows, revealing
        /// fainter stars that live in the Tycho-2 regime at high zoom; as the user zooms
        /// out it falls, so the Milky Way stops blowing out.
        /// <para>
        /// Formula: <c>base + log10(60 / fov) * slope</c>, with slope 2.5 zooming in and
        /// <see cref="WideFieldMagFalloffPerDecade"/> zooming out, the reduction capped at
        /// <see cref="MaxWideFieldMagReduction"/>.
        /// </para>
        /// <para>
        /// <b>Why the zoom-out branch is not simply pinned to the floor any more.</b> A star sprite
        /// cannot shrink below about a pixel, so widening the field packs the same stars into fewer
        /// pixels and their flux adds up: at 94 degrees the Milky Way drew 58k sprites into a band a
        /// few hundred pixels wide and washed out to a solid glow. Dropping the limit is how
        /// planetarium software answers that, and it is cheaper as well, since the culled stars are
        /// never submitted.
        /// </para>
        /// <para>
        /// <b>The cost, stated plainly.</b> <see cref="MagnitudeLimit"/> is also the split against
        /// the Milky Way bake: fainter stars live in the diffuse texture, brighter ones are sprites.
        /// So a star between the reduced limit and the floor is drawn by neither, and the band is
        /// slightly dimmer than physically correct rather than merely less blown out. That is the
        /// intended trade at wide field, where those stars are sub-pixel anyway; it is also why the
        /// reduction is capped instead of running away with FOV. It is NOT a licence to move
        /// <see cref="MagnitudeLimit"/> itself out of sync with the bake, which still produces halos.
        /// </para>
        /// </summary>
        /// <returns>Effective magnitude cutoff for the GPU vertex shader.</returns>
        public float EffectiveMagnitudeLimit
        {
            get
            {
                var fov = Math.Max(0.1, FieldOfViewDeg);
                var decades = Math.Log10(60.0 / fov);
                var zoomAdjust = decades >= 0.0
                    ? decades * 2.5
                    : Math.Max(-MaxWideFieldMagReduction, decades * WideFieldMagFalloffPerDecade);
                return MagnitudeLimit + (float)zoomAdjust;
            }
        }

        /// <summary>
        /// Scrub offset added to the live wall clock for sky rendering. Zero = live.
        /// Stored as an offset (Stellarium-style) so the scrubbed instant keeps
        /// advancing with real time rather than freezing on a captured absolute date.
        /// Drives sky colour, LST (star/horizon/crosshair rotation), planet + Moon
        /// positions, horizon fill, and below-horizon label dimming -- all of which
        /// flow from the single <c>viewingTime</c> derivation in
        /// <see cref="SkyMapTab{TSurface}.Render"/>. Deliberately sky-map-scoped (not on
        /// <see cref="PlannerState"/>) so scrubbing never triggers a planner recompute,
        /// and not persisted across sessions.
        /// </summary>
        public TimeSpan TimeOffset { get; set; }

        /// <summary>True when viewport changed and the cached texture must be re-rendered.</summary>
        public bool NeedsRedraw { get; set; } = true;

        /// <summary>
        /// F3 search modal + info panel state. Owned by the sky map (not cross-component)
        /// so it lives here rather than on <see cref="PlannerState"/>.
        /// </summary>
        public SkyMapSearchState Search { get; } = new();

        // Cached sun altitude + the time it was computed at. Sun moves ~0.25 deg/min,
        // which is orders of magnitude slower than our per-frame update rate, so a
        // 10-second refresh window is ample and keeps VSOP87a out of the hot path.
        private DateTimeOffset _sunAltComputedAt = DateTimeOffset.MinValue;
        private double _cachedSunAltitudeDeg = double.NaN;

        /// <summary>
        /// Sun altitude in degrees for the given site, cached with a 10 second refresh.
        /// Returns <see cref="double.NaN"/> if VSOP87a cannot reduce the Sun position
        /// (e.g. site outside the ephemeris validity range).
        /// </summary>
        /// <remarks>
        /// Used by <see cref="SkyBackgroundColorForSunAltitude"/> to tint the sky map
        /// background to match the planner's twilight zones (day / civil / nautical /
        /// astronomical / full night).
        /// </remarks>
        public double GetSunAltitudeDegCached(DateTimeOffset nowUtc, double siteLat, double siteLon)
        {
            if (double.IsNaN(siteLat) || double.IsNaN(siteLon)) return double.NaN;

            if ((nowUtc - _sunAltComputedAt).Duration() < TimeSpan.FromSeconds(10)
                && !double.IsNaN(_cachedSunAltitudeDeg))
            {
                return _cachedSunAltitudeDeg;
            }

            if (VSOP87a.Reduce(CatalogIndex.Sol, nowUtc, siteLat, siteLon,
                    out _, out _, out _, out var altDeg, out _))
            {
                _cachedSunAltitudeDeg = altDeg;
                _sunAltComputedAt = nowUtc;
            }
            return _cachedSunAltitudeDeg;
        }

        /// <summary>
        /// How stale a cached ephemeris may be before it is recomputed. Planets move at most ~0.5"/s
        /// (the Moon; the planets much slower) and comets are the same order even on a close approach,
        /// so at the tightest FOV this map offers a 1 s refresh is still sub-pixel -- already
        /// over-accurate for a marker.
        /// </summary>
        /// <remarks>
        /// This is a TOLERANCE, and deliberately not the exact-equality key it replaced. That key was
        /// correct only while the producer happened to quantize: <c>SkyMapTab</c> fed a viewingTime
        /// taken from a 1 s clock cache, so bit equality hit on 59 of every 60 frames. It then began
        /// INTERPOLATING between those syncs -- rightly, because a once-a-second step moved the alt-az
        /// roll reference and the whole horizon view matrix in visible 1 Hz jumps -- and every frame
        /// started carrying a distinct <see cref="DateTimeOffset"/>. Both caches below then missed
        /// 100% of the time, silently, with nothing at either site to say so: the comet sweep alone
        /// measured 91 ms per frame in the browser build (~1,600 candidates x a ~3,500-term VSOP87a
        /// Earth series each), landing on every single pointer move. A cache key must state the
        /// staleness the cache can tolerate; it must not encode an assumption about how finely its
        /// caller happens to round, which the caller can invalidate without ever touching this file.
        /// </remarks>
        private static readonly TimeSpan EphemerisCacheRefreshInterval = TimeSpan.FromSeconds(1);

        // Planet positions at the current viewingTime, refreshed on the tolerance above (the planner
        // date shifting, or a time scrub, jumps far past it and so recomputes at once).
        private DateTimeOffset _planetCacheTime = DateTimeOffset.MinValue;
        private readonly (CatalogIndex Index, double RA, double Dec)[] _planetCache
            = new (CatalogIndex, double, double)[SkyMapRenderer.PlanetIndices.Length];
        private int _planetCacheCount;

        /// <summary>
        /// How many times <see cref="GetPlanetPositionsCached"/> has actually evaluated VSOP87a. A test
        /// seam: a hit and a miss return identical positions, so only a counter can show the cache is
        /// working, which is exactly how it came to miss on every frame unnoticed.
        /// </summary>
        internal int PlanetCacheRebuilds { get; private set; }

        /// <summary>
        /// Planet J2000 RA/Dec positions at <paramref name="viewingTime"/>, cached for
        /// <see cref="EphemerisCacheRefreshInterval"/>. Entries for bodies whose VSOP87a reduction
        /// fails are omitted from the returned span.
        /// </summary>
        /// <remarks>
        /// Uses <see cref="VSOP87a.ReduceJ2000"/>, not <see cref="VSOP87a.Reduce"/>:
        /// the sky map projects everything in J2000, so the regular precessed +
        /// topocentric reduction would offset planets ~0.35 deg off the J2000
        /// ecliptic line.
        /// </remarks>
        public ReadOnlySpan<(CatalogIndex Index, double RA, double Dec)> GetPlanetPositionsCached(DateTimeOffset viewingTime)
        {
            if ((viewingTime - _planetCacheTime).Duration() < EphemerisCacheRefreshInterval)
            {
                return _planetCache.AsSpan(0, _planetCacheCount);
            }

            PlanetCacheRebuilds++;
            var count = 0;
            foreach (var idx in SkyMapRenderer.PlanetIndices)
            {
                if (VSOP87a.ReduceJ2000(idx, viewingTime, out var ra, out var dec, out _))
                {
                    _planetCache[count++] = (idx, ra, dec);
                }
            }
            _planetCacheCount = count;
            _planetCacheTime = viewingTime;
            return _planetCache.AsSpan(0, count);
        }

        /// <summary>
        /// A ephemeris-computed comet marker for the sky map: its <see cref="Catalog.Comet"/> index, live
        /// J2000 RA/Dec, predicted total magnitude, and the full display label
        /// (<see cref="CometElements.DisplayName"/>). The comet analogue of the planet cache tuple.
        /// </summary>
        /// <param name="PositionUncertain">The element set is at least one revolution old
        /// (<see cref="CometElements.IsElementSetStale"/>), so this POSITION carries an along-track
        /// error that grows with the number of revolutions propagated. Measured at 9.3 degrees for 10P
        /// in 2026 (<c>CometEphemerisTests</c>), which is far more than a marker's width.
        ///
        /// <para>The MAGNITUDE is not qualified by this: it was checked against JPL Horizons for the
        /// same object and instant and agrees to 0.03 mag, because Horizons predicts brightness from
        /// the same M1/K1 this does. It is the two-body propagation over many revolutions, without the
        /// non-gravitational terms JPL fits, that drifts.</para></param>
        public readonly record struct CometMarker(
            CatalogIndex Index, double RA, double Dec, float VMag, string Label, bool PositionUncertain = false);

        // Base naked-marker magnitude floor for comets. A comet fainter than this at the current view
        // never draws (unless zooming in raises the effective limit -- see the max() at the call sites),
        // mirroring how the star field's floor grows with zoom. Comets are sparse, so this is generous.
        internal const float CometBaseMagnitudeLimit = 12.0f;

        // Static candidacy filter: a comet only enters the per-frame solve if its photometric model could
        // plausibly reach naked-marker range. Peak-ish brightness ~ M1 + K1*log10(q) (i.e. at r = q with a
        // 1 AU geocentric distance); the +6 slack over CometBaseMagnitudeLimit covers close approaches
        // (delta < 1) that brighten a comet beyond this crude estimate. Rebuilt only when the repository
        // reference or its element count changes -- NOT per frame.
        private const double CometCandidacyMagnitudeLimit = CometBaseMagnitudeLimit + 6.0;
        private ICometRepository? _cometCandidatesRepo;
        private int _cometCandidatesAllLength = -1;
        private CometElements[] _cometCandidates = [];

        // Per-viewingTime marker cache (positions + magnitudes for the candidate set), keyed on
        // EphemerisCacheRefreshInterval + repository identity, mirroring the planet cache.
        // Zoom-independent: it holds every candidate with a finite magnitude, and each consumer
        // (draw / click / info panel) applies its own magnitude limit -- so zooming never invalidates it.
        private DateTimeOffset _cometCacheTime = DateTimeOffset.MinValue;
        private ICometRepository? _cometCacheRepo;
        private CometMarker[] _cometCache = [];
        private int _cometCacheCount;

        /// <summary>
        /// Live comet markers at <paramref name="viewingTime"/> for the candidate set, cached until the
        /// viewingTime (or repository) changes. Returns an empty span when no repository is wired or it has
        /// not finished loading. The returned markers are NOT magnitude-filtered -- callers compare
        /// <see cref="CometMarker.VMag"/> against their own limit (the draw path uses
        /// <c>max(<see cref="CometBaseMagnitudeLimit"/>, <see cref="EffectiveMagnitudeLimit"/>)</c>) so the
        /// cache stays zoom-independent. Uses <see cref="CometEphemeris.TryGetEquatorialJ2000WithMagnitude"/>,
        /// the same two-body path the ephemeris tests pin.
        /// </summary>
        /// <summary>
        /// Whether a comet marker is drawn: normally it needs the comet layer on AND a predicted
        /// magnitude at or brighter than the zoom-aware limit, but a PINNED comet ignores both.
        ///
        /// <para>Pinned bypasses the layer toggle for the same reason a pinned catalog object does (a
        /// planned target is a landmark and must stay on the map with its layer off), and it bypasses
        /// the magnitude limit for a reason specific to comets: the limit is compared against a
        /// PREDICTION, and a comet's predicted magnitude is the least reliable number on the map. SBDB's
        /// photometric fit can be apparitions out of date, which is how 10P reads near 12.8 while
        /// sitting two days from perihelion at 0.4 AU. Hiding the user's own pinned target behind that
        /// guess is the wrong way round.</para>
        ///
        /// <para>Pure and static so it can be pinned by a test without a renderer: the comet layer is
        /// the only path a pinned comet has onto the sky map, because comets are deliberately absent
        /// from <c>ICelestialObjectDB</c> and so never reach the object-overlay pass.</para>
        /// </summary>
        public static bool ShouldDrawCometMarker(bool cometLayerOn, bool isPinned, double vmag, double magnitudeLimit)
            => isPinned || (cometLayerOn && !(vmag > magnitudeLimit));

        /// <summary>
        /// How many times <see cref="GetCometPositionsCached"/> has actually swept the candidate set.
        /// The counterpart of <see cref="PlanetCacheRebuilds"/>, and the more load-bearing of the two:
        /// this sweep is ~1,600 comets, so a miss costs about two orders of magnitude more than the
        /// planet one.
        /// </summary>
        internal int CometCacheRebuilds { get; private set; }

        public ReadOnlySpan<CometMarker> GetCometPositionsCached(ICometRepository? comets, DateTimeOffset viewingTime)
        {
            if (comets is null)
            {
                return default;
            }

            RebuildCometCandidatesIfNeeded(comets);

            if ((viewingTime - _cometCacheTime).Duration() < EphemerisCacheRefreshInterval
                && ReferenceEquals(comets, _cometCacheRepo))
            {
                return _cometCache.AsSpan(0, _cometCacheCount);
            }

            CometCacheRebuilds++;
            if (_cometCache.Length < _cometCandidates.Length)
            {
                _cometCache = new CometMarker[_cometCandidates.Length];
            }

            _cometCacheTime = viewingTime;
            _cometCacheRepo = comets;

            // ONE Earth ephemeris + time conversion for the whole sweep. Both depend on the instant
            // alone, and the Earth half is a ~3,500-term VSOP87a series against the few dozen
            // transcendentals of the two Kepler solves it feeds -- so resolving it per comet (which is
            // what the DateTimeOffset overload of TryGetEquatorialJ2000WithMagnitude must do) made
            // Earth essentially the whole cost of this loop: MEASURED at 29 ms per sweep over the real
            // 1,630-candidate SBDB set against 1.6 ms hoisted (fastest of 25 runs, native arm64). Also
            // gives the staleness check its jdTt: staleness is per element set, the instant is not.
            if (!CometEphemeris.TryGetEarthState(viewingTime, out var earth))
            {
                _cometCacheCount = 0;
                return default;
            }
            var jdTt = earth.JdTt;

            var count = 0;
            foreach (var el in _cometCandidates)
            {
                if (el.CatalogIndex is not { } idx)
                {
                    continue;
                }
                if (!CometEphemeris.TryGetEquatorialJ2000WithMagnitude(el, earth, out var ra, out var dec, out var mag)
                    || double.IsNaN(mag))
                {
                    continue;
                }
                // DisplayName, not the bare common name: that field is SBDB's DISCOVERER, so a map
                // showing two comets from the same discoverer labelled both of them identically
                // ("Tempel" is 9P, 10P and six others). The display form carries the designation.
                var label = el.DisplayName;
                _cometCache[count++] = new CometMarker(idx, ra, dec, (float)mag, label,
                    PositionUncertain: el.IsElementSetStale(jdTt));
            }
            _cometCacheCount = count;
            return _cometCache.AsSpan(0, count);
        }

        /// <summary>Number of samples in a comet info-panel vmag sparkline.</summary>
        public const int CometCurveSampleCount = 32;

        // Total span of the info-panel vmag sparkline (centred on the viewing instant), so an
        // approaching/receding perihelion reads as a V. 90 days shows the shoulders of a typical
        // apparition without flattening the interesting part.
        private const double CometCurveWindowDays = 90.0;

        // Info-panel vmag sparkline cache for the selected comet. The curve is stable within a day, so it
        // recomputes only when the selected comet or the viewing DAY changes -- never per frame (which
        // would be CometCurveSampleCount Kepler+VSOP solves every frame for one selection).
        private CatalogIndex _cometCurveIndex;
        private long _cometCurveDayKey = long.MinValue;
        private float[] _cometCurve = [];

        /// <summary>
        /// Cached vmag sparkline for a selected comet: <see cref="CometCurveSampleCount"/> predicted
        /// magnitudes spanning <c>CometCurveWindowDays</c> centred on <paramref name="viewingTime"/> (the
        /// middle sample is "now"). Recomputed only when the comet or the viewing DAY changes. Returns an
        /// empty span when the comet is unknown or has no photometric model.
        /// </summary>
        public ReadOnlySpan<float> GetCometMagnitudeCurveCached(ICometRepository? comets, CatalogIndex index, DateTimeOffset viewingTime)
        {
            if (comets is null || !comets.TryGet(index, out var el) || !el.HasMagnitudeModel)
            {
                return default;
            }

            var dayKey = viewingTime.UtcDateTime.Date.Ticks / TimeSpan.TicksPerDay;
            if (index == _cometCurveIndex && dayKey == _cometCurveDayKey && _cometCurve.Length == CometCurveSampleCount)
            {
                return _cometCurve;
            }

            if (_cometCurve.Length != CometCurveSampleCount)
            {
                _cometCurve = new float[CometCurveSampleCount];
            }

            Span<double> mags = stackalloc double[CometCurveSampleCount];
            var start = viewingTime - TimeSpan.FromDays(CometCurveWindowDays / 2.0);
            var step = TimeSpan.FromDays(CometCurveWindowDays / (CometCurveSampleCount - 1));
            CometEphemeris.SampleMagnitudeCurve(el, start, step, mags);
            for (var i = 0; i < CometCurveSampleCount; i++)
            {
                _cometCurve[i] = (float)mags[i];
            }
            _cometCurveIndex = index;
            _cometCurveDayKey = dayKey;
            return _cometCurve;
        }

        /// <summary>Number of samples along a selected solar-system object's sky path.</summary>
        public const int SkyPathSampleCount = 49;

        // Path window per body kind. The Moon laps in ~27 d and moves ~13 deg/day, so a long window
        // wraps the sky uselessly -- keep it short. Comets move fast near perihelion (medium window);
        // planets crawl, so a longer window is needed to show a meaningful arc (incl. retrograde loops).
        private const double MoonPathWindowDays = 5.0;
        private const double CometPathWindowDays = 45.0;
        private const double PlanetPathWindowDays = 120.0;

        // Selected-object sky-path, sampled OFF the render thread (task #26). A cache miss kicks off a
        // background compute (~49 VSOP/Kepler solves + event detection; a planet path is ~10 ms) and the
        // render thread keeps drawing the last adopted snapshot until it lands -- so a held day-scrub never
        // blocks a frame on the solve. The Task<SelectedPath> is the cross-thread handoff (every field here
        // is render-thread only; the payload travels via the Task result), mirroring the async Milky Way /
        // Tycho-2 buffer swaps. VSOP87a/CometEphemeris are pure (stackalloc locals over static-readonly
        // tables), so the background solve races nothing the render thread also computes.
        private sealed record SelectedPath(
            CatalogIndex Index, long BucketKey, (double RA, double Dec)[] Samples, int Count, ImmutableArray<SkyPathEvent> Events);

        private SelectedPath? _adoptedPath;      // the snapshot the renderer currently draws
        private Task<SelectedPath>? _pathTask;   // in-flight background compute (null when idle)
        private CatalogIndex _pathTaskIndex;
        private long _pathTaskBucketKey = long.MinValue;
        private ImmutableArray<SkyPathEvent> _currentPathEvents = [];

        /// <summary>
        /// Events on the selected-object path that <see cref="GetSelectedPathCached"/> served THIS frame
        /// (stations / retrograde, greatest elongation, opposition, perihelion) -- read it right after that
        /// call for the same object. Empty while a newly-selected object's first sample is still computing.
        /// </summary>
        public IReadOnlyList<SkyPathEvent> SelectedPathEvents => _currentPathEvents;

        // Path cache granularity by body speed. A planet path costs ~10 ms to rebuild (49 x the full
        // VSOP87 series + reduction, ~150 us each -- measured in EphemerisBenchmarks), so it must NOT rebuild
        // every day while scrubbing: a planet's 120-day arc shifts imperceptibly day-to-day, so it only
        // rebuilds when the viewing instant crosses a coarse bucket. The Moon moves fast (short bucket);
        // comets are cheap (~23 us/sample) and move fast, so a 1-day bucket. Within a bucket the reticle
        // still tracks the true live position along the cached arc.
        private static readonly long MoonPathBucketTicks = TimeSpan.FromHours(6).Ticks;
        private static readonly long CometPathBucketTicks = TimeSpan.FromDays(1).Ticks;
        private static readonly long PlanetPathBucketTicks = TimeSpan.FromDays(10).Ticks;

        /// <summary>
        /// Sky path (J2000 RA/Dec samples) for a selected solar-system object over a body-appropriate window
        /// centred on <paramref name="viewingTime"/> -- planets via <see cref="VSOP87a.ReduceJ2000"/>, comets
        /// via <see cref="CometEphemeris.TryGetEquatorialJ2000"/>. Empty span for a non-solar-system index
        /// (fixed stars/DSOs don't move) or an unknown comet. Sampled OFF the render thread: a miss on the
        /// (index, per-body bucket) key dispatches a background compute and this returns the last adopted
        /// snapshot (or empty for a freshly-selected object) until it lands; completion pokes a redraw. The
        /// poll happens here since the getter runs every frame the path is drawn.
        /// </summary>
        public ReadOnlySpan<(double RA, double Dec)> GetSelectedPathCached(ICometRepository? comets, CatalogIndex index, DateTimeOffset viewingTime)
        {
            // Adopt a finished background compute (a faulted/cancelled task is dropped -> "no path").
            if (_pathTask is { IsCompleted: true } done)
            {
                _pathTask = null;
                if (done.IsCompletedSuccessfully)
                {
                    _adoptedPath = done.Result;
                }
            }

            if (!index.IsSolarSystemObject)
            {
                _adoptedPath = null;
                _currentPathEvents = [];
                return default;
            }

            var isComet = index.ToCatalog() == Catalog.Comet;
            var bucketTicks = index == CatalogIndex.Moon ? MoonPathBucketTicks
                : isComet ? CometPathBucketTicks
                : PlanetPathBucketTicks;
            var bucketKey = viewingTime.UtcDateTime.Ticks / bucketTicks;

            // Fast path: the adopted snapshot already matches (index, bucket). Hits REGARDLESS of sample
            // count, so a legitimately empty result (all samples failed to solve) still caches instead of
            // re-dispatching every frame.
            if (_adoptedPath is { } cur && cur.Index == index && cur.BucketKey == bucketKey)
            {
                _currentPathEvents = cur.Events;
                return cur.Samples.AsSpan(0, cur.Count);
            }

            // Not current -- ensure a background compute is running for THIS (index, bucket).
            if (_pathTask is null || _pathTaskIndex != index || _pathTaskBucketKey != bucketKey)
            {
                // Resolve comet elements on the render thread (repo read); pass them by value to the task.
                CometElements cometEl = default;
                if (isComet && (comets is null || !comets.TryGet(index, out cometEl)))
                {
                    // Unknown comet -- adopt an empty snapshot for this key so we neither draw nor re-dispatch.
                    _adoptedPath = new SelectedPath(index, bucketKey, [], 0, []);
                    _pathTask = null;
                    _currentPathEvents = [];
                    return default;
                }

                _pathTaskIndex = index;
                _pathTaskBucketKey = bucketKey;
                var el = cometEl;
                _pathTask = Task.Run(() => ComputeSelectedPath(index, bucketKey, isComet, el, viewingTime));
                // Wake the NeedsRedraw-gated loop when the solve lands so the next frame adopts + draws it.
                _pathTask.ContinueWith(_ => NeedsRedraw = true, TaskScheduler.Default);
            }

            // Draw the last adopted snapshot meanwhile, but only for the SAME object (a slightly-stale bucket
            // of the same path is fine; a different object's arc is not -> empty until the solve lands).
            if (_adoptedPath is { } prev && prev.Index == index)
            {
                _currentPathEvents = prev.Events;
                return prev.Samples.AsSpan(0, prev.Count);
            }

            _currentPathEvents = [];
            return default;
        }

        // Pure, thread-safe path+events solve run on a background thread (task #26): samples
        // SkyPathSampleCount J2000 positions over the body-appropriate window centred on viewingTime, then
        // detects the notable events. Reads only its by-value arguments + the pure VSOP87a / CometEphemeris /
        // SkyPathEventDetector, so it never touches SkyMapState's render-thread-only mutable fields.
        private static SelectedPath ComputeSelectedPath(
            CatalogIndex index, long bucketKey, bool isComet, CometElements cometEl, DateTimeOffset viewingTime)
        {
            var windowDays = index == CatalogIndex.Moon ? MoonPathWindowDays
                : isComet ? CometPathWindowDays
                : PlanetPathWindowDays;
            var start = viewingTime - TimeSpan.FromDays(windowDays / 2.0);
            var step = TimeSpan.FromDays(windowDays / (SkyPathSampleCount - 1));

            var samples = new (double RA, double Dec)[SkyPathSampleCount];
            var count = 0;
            for (var i = 0; i < SkyPathSampleCount; i++)
            {
                var t = start + step * i;
                var ok = isComet
                    ? CometEphemeris.TryGetEquatorialJ2000(cometEl, t, out var ra, out var dec, out _, out _)
                    : VSOP87a.ReduceJ2000(index, t, out ra, out dec, out _);
                if (ok)
                {
                    samples[count++] = (ra, dec);
                }
            }

            var events = ComputePathEvents(index, isComet, cometEl, samples, count, start, step);
            return new SelectedPath(index, bucketKey, samples, count, events);
        }

        // Detects the path's notable events (see SelectedPathEvents), sharing the path's start/step. Only
        // runs when every sample solved (count == SkyPathSampleCount), because the detector assumes an even
        // index->time spacing that dropped samples would break. Planets get a Sun track sampled at the same
        // instants (for greatest elongation / opposition); comets carry their perihelion instant. Pure (only
        // its arguments + the pure VSOP87a/detector), so it runs on the background compute thread.
        private static ImmutableArray<SkyPathEvent> ComputePathEvents(
            CatalogIndex index, bool isComet, in CometElements cometEl,
            (double RA, double Dec)[] samples, int count, DateTimeOffset start, TimeSpan step)
        {
            if (count != SkyPathSampleCount)
            {
                return [];
            }

            var body = ClassifySkyPathBody(index, isComet);

            ReadOnlySpan<(double RA, double Dec)> sun = default;
            if (body is SkyPathBody.InferiorPlanet or SkyPathBody.OuterPlanet)
            {
                var sunSamples = new (double RA, double Dec)[count];
                var sunOk = true;
                for (var i = 0; i < count; i++)
                {
                    if (VSOP87a.ReduceJ2000(CatalogIndex.Sol, start + step * i, out var sunRa, out var sunDec, out _))
                    {
                        sunSamples[i] = (sunRa, sunDec);
                    }
                    else
                    {
                        sunOk = false;
                        break;
                    }
                }
                if (sunOk)
                {
                    sun = sunSamples.AsSpan(0, count);
                }
            }

            DateTimeOffset? perihelion = isComet && !double.IsNaN(cometEl.PerihelionJdTt)
                ? JdTtToUtc(cometEl.PerihelionJdTt)
                : null;

            var results = new List<SkyPathEvent>();
            SkyPathEventDetector.Detect(samples.AsSpan(0, count), sun, start, step, body, perihelion, results);
            return [.. results];
        }

        private static SkyPathBody ClassifySkyPathBody(CatalogIndex index, bool isComet)
        {
            if (isComet)
            {
                return SkyPathBody.Comet;
            }
            if (index == CatalogIndex.Mercury || index == CatalogIndex.Venus)
            {
                return SkyPathBody.InferiorPlanet;
            }
            if (index == CatalogIndex.Mars || index == CatalogIndex.Jupiter || index == CatalogIndex.Saturn
                || index == CatalogIndex.Uranus || index == CatalogIndex.Neptune)
            {
                return SkyPathBody.OuterPlanet;
            }
            return SkyPathBody.Other; // Sun, Moon
        }

        // JD(TT) -> UTC display instant (TT-UTC ~69 s and the OADate epoch offset are far below the sample
        // spacing, so this is precise enough to pin perihelion to the nearest path sample).
        private static DateTimeOffset JdTtToUtc(double jdTt)
            => new(DateTime.SpecifyKind(DateTime.FromOADate(jdTt - 2415018.5), DateTimeKind.Utc));

        private void RebuildCometCandidatesIfNeeded(ICometRepository comets)
        {
            var all = comets.All;
            if (ReferenceEquals(comets, _cometCandidatesRepo) && all.Length == _cometCandidatesAllLength)
            {
                return;
            }

            var candidates = new List<CometElements>();
            foreach (var el in all)
            {
                if (!el.HasMagnitudeModel || el.CatalogIndex is null)
                {
                    continue;
                }
                var q = Math.Max(el.PerihelionDistanceAu, 0.05);
                var peakish = el.AbsoluteMagnitudeM1 + el.SlopeK1 * Math.Log10(q);
                if (peakish <= CometCandidacyMagnitudeLimit)
                {
                    candidates.Add(el);
                }
            }

            _cometCandidates = [.. candidates];
            _cometCandidatesRepo = comets;
            _cometCandidatesAllLength = all.Length;

            // Force the marker cache to rebuild against the new candidate set.
            _cometCacheTime = DateTimeOffset.MinValue;
            _cometCacheRepo = null;
        }

        /// <summary>
        /// Pre-warm the VSOP87a planet ephemeris. The first <see cref="VSOP87a.ReduceJ2000"/>
        /// call pays a one-time ~330 ms JIT + static-table init cost; this is the dominant
        /// stall on the first Sky Atlas open (it lands on the render thread inside
        /// <c>DrawPlanetLabels</c> -> <see cref="GetPlanetPositionsCached"/>). Calling this on a
        /// background thread during startup warm-up pays that cost off the critical path. Any
        /// instant works -- we are warming JIT + static state, not caching a position.
        /// </summary>
        public static void PrewarmPlanetEphemeris()
        {
            var when = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
            foreach (var idx in SkyMapRenderer.PlanetIndices)
            {
                VSOP87a.ReduceJ2000(idx, when, out _, out _, out _);
            }
        }

        /// <summary>
        /// Maps sun altitude to a sky-map background colour, matching the planner's
        /// civil / nautical / astronomical twilight zones but shifted darker so it
        /// reads as "sky, not chart axis". Pass <see cref="double.NaN"/> (no site) to
        /// get the dark-night default.
        /// </summary>
        public static RGBAColor32 SkyBackgroundColorForSunAltitude(double sunAltDeg)
        {
            // Palette anchors (A = fully transparent, 0xFF = opaque):
            //   Day      sun above  5 deg : dusty blue  (darker than real daylight so
            //                               stars stay visible; this is still an app,
            //                               not a simulator)
            //   Golden   sun   0 to  5 deg : purple/magenta
            //   Civil    sun  -6 to  0 deg : dark blue
            //   Nautical sun -12 to -6 deg : darker blue
            //   Astro    sun -18 to -12 deg : very dark blue
            //   Night    sun below -18 deg : almost black
            if (double.IsNaN(sunAltDeg) || sunAltDeg < -18)
                return new RGBAColor32(0x02, 0x03, 0x08, 0xFF); // night (darker than before)
            if (sunAltDeg < -12)
                return new RGBAColor32(0x0A, 0x0C, 0x1C, 0xFF); // astro
            if (sunAltDeg < -6)
                return new RGBAColor32(0x14, 0x14, 0x2A, 0xFF); // nautical
            if (sunAltDeg < 0)
                return new RGBAColor32(0x20, 0x20, 0x38, 0xFF); // civil
            if (sunAltDeg < 5)
                return new RGBAColor32(0x3A, 0x2C, 0x54, 0xFF); // golden hour
            return new RGBAColor32(0x28, 0x34, 0x50, 0xFF);     // daylight dusty blue
        }

        /// <summary>
        /// Formats a <see cref="TimeOffset"/> as a compact signed string showing the
        /// largest two non-zero units, e.g. <c>"+3h"</c>, <c>"-1h 30m"</c>,
        /// <c>"+1w 2d"</c>, <c>"+2d 3h"</c>, <c>"-5h"</c>. Sub-minute magnitudes (and
        /// zero) render as <c>"+0"</c>. Units: weeks (w), days (d), hours (h),
        /// minutes (m). ASCII only -- this string lands in the GLSL-adjacent HUD strip.
        /// </summary>
        public static string FormatOffset(TimeSpan offset)
        {
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var totalMinutes = (long)offset.Duration().TotalMinutes;
            if (totalMinutes == 0)
            {
                return "+0";
            }

            var weeks = totalMinutes / (7 * 24 * 60);
            totalMinutes %= 7 * 24 * 60;
            var days = totalMinutes / (24 * 60);
            totalMinutes %= 24 * 60;
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            // Largest two non-zero units in descending order.
            Span<(long Value, char Unit)> all =
            [
                (weeks, 'w'), (days, 'd'), (hours, 'h'), (minutes, 'm')
            ];

            var result = sign;
            var shown = 0;
            foreach (var (value, unit) in all)
            {
                if (value == 0)
                {
                    continue;
                }
                if (shown > 0)
                {
                    result += " ";
                }
                result += $"{value}{unit}";
                if (++shown == 2)
                {
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Computes the <see cref="TimeOffset"/> that lands the sky on the midnight of the
        /// current observing night, expressed in the site-local frame of
        /// <paramref name="nowLocal"/>. Definition (Stellarium "N"): the upcoming
        /// <c>00:00</c> when it is afternoon/evening (local time &gt;= 12:00), otherwise the
        /// <c>00:00</c> that already started the current night (negative offset, e.g. at
        /// 02:00 jump back two hours). The returned value is a frame-independent duration, so
        /// callers add it directly to the UTC base time.
        /// </summary>
        public static TimeSpan ComputeMidnightOffset(DateTimeOffset nowLocal)
        {
            // >= noon -> tonight rolls into tomorrow's 00:00; before noon -> this night's 00:00.
            var midnightDate = nowLocal.TimeOfDay >= TimeSpan.FromHours(12)
                ? nowLocal.Date.AddDays(1)
                : nowLocal.Date;
            var targetMidnight = new DateTimeOffset(midnightDate, nowLocal.Offset);
            return targetMidnight - nowLocal;
        }

        /// <summary>
        /// Clamp RA to [0, 24) and Dec to [-90, +90] after any modification.
        /// </summary>
        public void NormalizeCenter()
        {
            CenterRA = ((CenterRA % 24.0) + 24.0) % 24.0;
            // Clamp Dec away from poles to avoid gnomonic projection singularity
            CenterDec = Math.Clamp(CenterDec, -89.5, 89.5);
        }

        /// <summary>
        /// Roll about the view axis, in radians, and the view's THIRD degree of freedom.
        /// <para>
        /// Zero means screen-up points along celestial north, so an Equatorial view is north-up at
        /// roll 0 for every declination. Horizon mode carries the roll that puts the local zenith up,
        /// refreshed each frame by <see cref="UpdateRollForReference"/> as the sky turns.
        /// </para>
        /// <para>
        /// It exists because orientation has three degrees of freedom and the centre only carries
        /// two. Re-deriving the missing one per frame as <c>forward x reference</c> is what made the
        /// pole (Equatorial) and the zenith (Horizon) singular: the cross product's LENGTH goes to
        /// zero there, so its direction becomes arbitrarily sensitive to a small pan (the field
        /// swings), and at exact parallelism the old code substituted a hardcoded right vector.
        /// Storing the roll removes the derivation, so no view direction is special.
        /// </para>
        /// <para>
        /// <b>Owned by the user's gestures.</b> A pan rotates the whole frame rigidly and keeps
        /// whatever roll that earns; the mode's reference may only add the amount the reference itself
        /// MOVED (nothing in Equatorial, the sky's rotation in Horizon). It must never servo to the
        /// reference's absolute value, which is what it used to do: near the pole a change of RA is
        /// itself a rotation, so a 100 px pan at Dec -89 legitimately earns 82 degrees of roll, and
        /// erasing that on mouse-up threw the sky 132 px -- further than the gesture had moved it.
        /// <see cref="RequestLevelToReference"/> (the L key) is the only way back to the reference, and
        /// it is deliberate.
        /// </para>
        /// </summary>
        public double CenterRoll { get; set; }

        /// <summary>
        /// How close the view axis may come to the mode's reference direction before
        /// <see cref="UpdateRollForReference"/> stops trusting it, as the sine of the angle between
        /// them (about 5 degrees). Inside that cone the reference cannot say which way is up, so the
        /// roll is left as it is instead of being recomputed from a vanishing cross product.
        /// <para>
        /// Only Horizon mode can reach this: pointing at the zenith really does leave "zenith up"
        /// undefined. Equatorial mode's answer is analytically 0 at every declination (north-up IS
        /// roll 0 in this frame), so it needs no cone, and giving it one was a mistake: a pan then
        /// held a drag's roll inside the cone and snapped it away on the way out, which at Dec 85
        /// flipped the field by 63 degrees in a single frame.
        /// </para>
        /// </summary>
        private const double ReferenceRollLockSin = 0.0872; // sin(5 deg)

        /// <summary>
        /// Exponential time constant for the roll's travel back to the mode's reference, and the
        /// distance at which it stops stepping and lands exactly.
        /// <para>
        /// The approach exists so re-levelling reads as a movement rather than a glitch. A view that
        /// is already level (the overwhelmingly common case, since the target is a constant 0 in
        /// Equatorial mode) is within the snap distance immediately, so this costs it nothing and
        /// leaves it bit-identical.
        /// </para>
        /// <para>
        /// <b>Per SECOND, not per frame.</b> This was a flat 0.25 of the remaining angle per call, so
        /// the travel took a fixed number of FRAMES and therefore a duration that depended entirely on
        /// frame rate: the same ten frames are 0.17 s at 60 fps and a full second at 10 fps, which is
        /// why the re-level read as a smooth settle on the desktop and as the view continuing to turn
        /// by itself on the web build. 0.058 s reproduces the old 0.25-per-frame feel exactly at 60 fps
        /// (0.25 = 1 - exp(-(1/60) / 0.058)) and now means the same thing everywhere else.
        /// </para>
        /// </summary>
        private const double RollRealignTimeConstantSec = 0.058;
        private const double RollRealignSnapRad = 0.0035; // ~0.2 deg

        /// <summary>Nominal frame time used for the very first step, before an interval exists to
        /// measure, and the ceiling on a measured one so a frame the app spent loading cannot turn
        /// into a single jump that is indistinguishable from the snap this exists to avoid.</summary>
        private const double RollRealignNominalFrameSec = 1.0 / 60.0;
        private const double RollRealignMaxFrameSec = 0.25;

        /// <summary>Timestamp of the previous <see cref="UpdateRollForReference"/> call. A monotonic
        /// elapsed read for animation pacing, never a wall-clock read, so it does not belong to
        /// <c>ITimeProvider</c> (same rule the sky map's zoom-flood detector follows).</summary>
        private long _rollRealignTicks;

        /// <summary>
        /// The reference roll seen on the previous frame, and whether one has been seen at all.
        /// <para>
        /// The roll follows the reference's <b>motion</b>, never its absolute value. That distinction
        /// is the whole fix for the pan bug: servoing to the absolute reference means every frame after
        /// a gesture drags the view back toward it, so a pan that legitimately rolled the frame is
        /// undone the instant the button comes up. Tracking the delta instead leaves a gesture alone
        /// and still turns the field as the sky turns, which is the only thing the reference is
        /// actually entitled to do.
        /// </para>
        /// <para>
        /// Cleared whenever the reference is unusable (a drag in progress, an ill-conditioned zenith)
        /// so the motion missed during the gap is never replayed as one jump on the way out.
        /// </para>
        /// </summary>
        private double _lastReferenceRoll;
        private bool _hasReferenceRoll;

        /// <summary>Set by <see cref="RequestLevelToReference"/>; the one case that is allowed to servo
        /// to the reference's absolute value, because the user asked for exactly that.</summary>
        private bool _levelRequested;

        /// <summary>
        /// Level the view to the mode's reference (north-up in Equatorial, zenith-up in Horizon),
        /// easing there over the next few frames rather than snapping.
        /// <para>
        /// This exists because the roll is now owned by the user's gestures and nothing takes it back
        /// automatically. A drag near the pole legitimately rolls the frame by tens of degrees -- at
        /// Dec -89 a 100 px pan earns 82 -- and without a deliberate way back the atlas would simply
        /// stay tilted.
        /// </para>
        /// </summary>
        public void RequestLevelToReference()
        {
            _levelRequested = true;
            NeedsRedraw = true;
        }

        /// <summary>True while a requested re-level is still travelling, for tests and status text.</summary>
        internal bool IsLevelling => _levelRequested;

        /// <summary>
        /// The view frame at <see cref="CenterRA"/> / <see cref="CenterDec"/> with
        /// <see cref="CenterRoll"/> = 0: forward toward the centre, right toward DECREASING RA (the
        /// sky map is east-left) and up toward celestial north.
        /// <para>
        /// This frame is well-conditioned at every declination, including the poles, which is the
        /// whole point: <c>forward x zhat</c> equals <c>cosDec</c> times this right vector, so
        /// normalising it reproduces exactly this frame wherever the old construction worked, and
        /// this one keeps going where that one divided by zero. At the pole it resolves to the limit
        /// along the centre's own meridian, which is what someone panning up a meridian expects.
        /// </para>
        /// </summary>
        public static (Vector3 Forward, Vector3 Right, Vector3 Up) ReferenceFrame(double raHours, double decDeg)
        {
            var (sinRA, cosRA) = Math.SinCos(raHours * Hours2Rad);
            var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDeg));

            var forward = new Vector3((float)(cosDec * cosRA), (float)(cosDec * sinRA), (float)sinDec);
            // Unit and perpendicular to forward for EVERY Dec: right . forward = cosDec * (sinRA *
            // cosRA - cosRA * sinRA) = 0, and its length does not depend on Dec at all.
            var right = new Vector3((float)sinRA, (float)-cosRA, 0f);
            var up = Vector3.Cross(right, forward);
            return (forward, right, up);
        }

        /// <summary>
        /// Compute the J2000 → camera rotation matrix for the current view centre and roll.
        /// The matrix maps the view direction to -Z (camera forward), with X = right and Y = up.
        /// Returns a <see cref="Matrix4x4"/> (column-major layout matches std140 mat4).
        /// <para>
        /// Reads no reference direction: the mode's "up" arrives through <see cref="CenterRoll"/>,
        /// which <see cref="UpdateRollForReference"/> maintains once per frame. So this is a pure
        /// function of three angles and cannot be singular.
        /// </para>
        /// </summary>
        public Matrix4x4 ComputeViewMatrix()
        {
            var (forward, right0, up0) = ReferenceFrame(CenterRA, CenterDec);
            var (sinRoll, cosRoll) = Math.SinCos(CenterRoll);

            // Rotate the frame about the view axis. up stays right x forward, as before.
            var right = (float)cosRoll * right0 + (float)sinRoll * up0;
            var up = Vector3.Cross(right, forward);

            // View matrix: rows are (right, up, -forward)
            // Matrix4x4 constructor takes row-major arguments (M11..M44)
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                -forward.X, -forward.Y, -forward.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        /// <summary>
        /// Refreshes <see cref="CenterRoll"/> so screen-up points along the mode's reference
        /// direction: celestial north in Equatorial (roll 0 by construction), the supplied local
        /// zenith in Horizon. Called once per frame from <see cref="SkyMapUbo.Write"/>, which is
        /// also where the zenith is known.
        /// <para>
        /// Inside <see cref="ReferenceRollLockSin"/> of the reference, or with no usable reference at
        /// all (an invalid site hands Horizon mode a zero zenith, which used to reach the arbitrary
        /// right vector), the roll is KEPT rather than recomputed. That is what stops the field
        /// swinging: near the reference a small pan changes the reference-derived up by a large angle,
        /// while a drag that rotates the whole frame carries its own roll and needs no reference.
        /// </para>
        /// </summary>
        /// <returns>True when the roll is tracking the reference (whether it landed on it or is still
        /// approaching it); false while it is held.</returns>
        /// <param name="deltaSeconds">Seconds since the previous call. Null measures it, which is what
        /// a render loop wants; a caller that steps deterministically (a test, or a host that already
        /// knows its frame time) passes it, because a tight loop measures ~0 and would never
        /// converge.</param>
        public bool UpdateRollForReference(float zenithX = 0f, float zenithY = 0f, float zenithZ = 1f, double? deltaSeconds = null)
        {
            // Stamped before every exit so a held roll (dragging, inside the cone, no usable
            // reference) does not accumulate into one big step the moment the hold ends.
            var elapsed = deltaSeconds ?? MeasureRollFrameSeconds();

            // A pan OWNS the roll while it is happening. It rotates the whole frame rigidly, so
            // re-deriving the roll underneath it is what made the field jump the instant a drag
            // crossed out of the lock cone: 63 degrees in one frame at Dec 85, which reads as the
            // view flipping rather than as a pan.
            if (IsDragging)
            {
                _hasReferenceRoll = false;
                return false;
            }

            double target;
            if (Mode == SkyMapMode.Horizon)
            {
                var reference = new Vector3(zenithX, zenithY, zenithZ);
                var refLen = reference.Length();
                if (refLen < 1e-6f)
                {
                    _hasReferenceRoll = false;
                    return false;
                }
                reference /= refLen;

                var (forward, right0, up0) = ReferenceFrame(CenterRA, CenterDec);

                // Component of the reference perpendicular to the view axis. Its LENGTH is the sine
                // of the angle between them, i.e. exactly the conditioning of the old cross product.
                var perpendicular = reference - forward * Vector3.Dot(reference, forward);
                if (perpendicular.Length() < ReferenceRollLockSin)
                {
                    _hasReferenceRoll = false;
                    return false;
                }

                // up(roll) = cos(roll) * up0 - sin(roll) * right0, so solve for up == perpendicular.
                target = Math.Atan2(-Vector3.Dot(perpendicular, right0), Vector3.Dot(perpendicular, up0));
            }
            else
            {
                // Equatorial: up0 IS celestial north at every declination, the pole included, so the
                // answer is exactly 0. No cross product, no conditioning test, and no cone.
                target = 0.0;
            }

            // The deliberate re-level: the only path allowed to servo to the reference's ABSOLUTE
            // value, because that is precisely what the user asked for.
            if (_levelRequested)
            {
                var toGo = NormalizeSignedAngle(target - CenterRoll);
                if (Math.Abs(toGo) <= RollRealignSnapRad)
                {
                    CenterRoll = target;
                    _levelRequested = false;
                }
                else
                {
                    // Travel the shortest way round at a fixed rate PER SECOND, and keep asking for
                    // frames until it arrives. Snapping straight there is what the flip was.
                    CenterRoll = NormalizeSignedAngle(CenterRoll + toGo * (1.0 - Math.Exp(-elapsed / RollRealignTimeConstantSec)));
                    NeedsRedraw = true;
                }
                _lastReferenceRoll = target;
                _hasReferenceRoll = true;
                return true;
            }

            // Otherwise follow only how far the reference MOVED since the previous frame. In
            // Equatorial that is identically zero -- celestial north does not go anywhere -- so a pan
            // keeps every degree of roll it earned. In Horizon it is the sky's own rotation, about
            // 0.004 degrees per frame, which needs no easing and keeps the horizon level over a
            // session without ever contradicting a gesture.
            if (!_hasReferenceRoll)
            {
                _lastReferenceRoll = target;
                _hasReferenceRoll = true;
                return false;
            }

            var moved = NormalizeSignedAngle(target - _lastReferenceRoll);
            _lastReferenceRoll = target;
            if (moved == 0.0)
            {
                return false;
            }

            CenterRoll = NormalizeSignedAngle(CenterRoll + moved);
            NeedsRedraw = true;
            return true;
        }

        /// <summary>Seconds since the previous call, clamped, and stamps the new instant.</summary>
        private double MeasureRollFrameSeconds()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = _rollRealignTicks;
            _rollRealignTicks = now;
            return previous == 0
                ? RollRealignNominalFrameSec
                : Math.Clamp((now - previous) / (double)Stopwatch.Frequency, 0.0, RollRealignMaxFrameSec);
        }

        /// <summary>Wraps an angle in radians to (-pi, pi], so a roll correction always takes the
        /// short way round rather than most of a turn the other way.</summary>
        internal static double NormalizeSignedAngle(double radians)
        {
            var wrapped = (radians + Math.PI) % Math.Tau;
            if (wrapped < 0)
            {
                wrapped += Math.Tau;
            }
            return wrapped - Math.PI;
        }

        /// <summary>
        /// Decomposes a view frame back into centre RA / Dec / roll, the inverse of
        /// <see cref="ComputeViewMatrix"/>. Used by the pan gesture, which rotates the whole frame
        /// rigidly and then has to store it in the three scalars.
        /// </summary>
        public static (double RaHours, double DecDeg, double Roll) FrameToCenter(Vector3 forward, Vector3 right)
        {
            forward = Vector3.Normalize(forward);
            var decDeg = double.RadiansToDegrees(Math.Asin(Math.Clamp(forward.Z, -1f, 1f)));
            var raHours = Math.Atan2(forward.Y, forward.X) / Hours2Rad;

            var (_, right0, up0) = ReferenceFrame(raHours, decDeg);
            var roll = Math.Atan2(Vector3.Dot(right, up0), Vector3.Dot(right, right0));
            return (raHours, decDeg, roll);
        }

        /// <summary>
        /// Convert RA (hours) and Dec (degrees) to a J2000 unit vector.
        /// Convention: X toward (RA=0h, Dec=0°), Y toward (RA=6h, Dec=0°), Z toward Dec=+90°.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float X, float Y, float Z) RaDecToUnitVec(double raHours, double decDeg)
        {
            var (sinRA, cosRA) = MathF.SinCos((float)(raHours * Hours2RadF));
            var (sinDec, cosDec) = MathF.SinCos(float.DegreesToRadians((float)decDeg));
            return (cosDec * cosRA, cosDec * sinRA, sinDec);
        }

        /// <summary>
        /// Per-star vertex layout for the sky-map star buffer: 5 floats per star
        /// = vec3 unit position + float V magnitude + float B-V colour.
        /// </summary>
        public const int FloatsPerStar = 5;

        /// <summary>
        /// CPU portion of the Tycho-2 star buffer build: streams the full catalog
        /// in chunks via <see cref="ICelestialObjectDB.CopyTycho2Stars"/>, applies
        /// proper-motion propagation when <paramref name="dtJulianYears"/> is
        /// non-zero, converts each surviving star to a unit vector via
        /// <see cref="RaDecToUnitVec"/>, and writes
        /// <see cref="FloatsPerStar"/> floats per star into
        /// <paramref name="destination"/>.
        /// <para>
        /// Returns the number of stars written -- caller is responsible for any
        /// downstream sort + magnitude-lookup + GPU upload steps. Extracted from
        /// <c>VkSkyMapPipeline.BuildStarBuffer</c> so the CPU-bound loop can be
        /// benchmarked in isolation from the Vulkan upload.
        /// </para>
        /// </summary>
        /// <param name="db">DB with Tycho-2 bulk data loaded
        /// (<c>InitDBAsync(waitForTycho2BulkLoad: true)</c>).</param>
        /// <param name="dtJulianYears">Years since J2000.0; <c>0</c> = no
        /// pm propagation, render at J2000 (the prior behaviour).</param>
        /// <param name="destination">Pre-allocated buffer of at least
        /// <c>db.Tycho2StarCount * FloatsPerStar</c> floats.</param>
        /// <returns>Number of stars written (each occupies
        /// <see cref="FloatsPerStar"/> consecutive floats).</returns>
        public static int FillTycho2StarVertices(
            ICelestialObjectDB db, double dtJulianYears, Span<float> destination)
            => FillTycho2StarVertices(db, dtJulianYears, destination, 0, int.MaxValue);

        /// <summary>
        /// As <see cref="FillTycho2StarVertices(ICelestialObjectDB, double, Span{float})"/>, but over
        /// a RANGE of catalog records rather than all of them.
        ///
        /// <para>This is what lets an incrementally-fetched catalog pay per arrival instead of per
        /// rebuild. The full walk visits every record whether or not its region was ever fetched
        /// (absent ones decode to a NaN magnitude and are skipped), so it costs the same at eight
        /// members held as at all 166 -- ~74 ms on the deployed WASM build, on every rebuild. Flatten
        /// the member that just landed and the cost is proportional to what actually changed. Get the
        /// range from <c>Tycho2PartialCatalog.TryGetRecordRange</c>.</para>
        /// </summary>
        /// <param name="startRecord">First catalog record index to read.</param>
        /// <param name="maxRecords">How many records to read at most; the walk still stops at the end
        /// of the catalog.</param>
        public static int FillTycho2StarVertices(
            ICelestialObjectDB db, double dtJulianYears, Span<float> destination,
            int startRecord, int maxRecords)
        {
            var tycCount = db.Tycho2StarCount;
            if (tycCount == 0 || maxRecords <= 0 || startRecord < 0 || startRecord >= tycCount)
            {
                return 0;
            }

            tycCount = maxRecords >= tycCount - startRecord ? tycCount : startRecord + maxRecords;

            // Read Tycho-2 records in chunks -- keeps the temp alloc bounded
            // (~16 MB) while still minimising the number of CopyTycho2Stars calls. Sized to the range
            // when the caller asked for a small one: a per-member flatten reads ~15k records, and a
            // fixed 200k scratch would allocate ~13x the data it reads, on the one WASM thread, once
            // per member.
            var chunkSize = Math.Min(200_000, tycCount - startRecord);
            var chunk = new Tycho2StarLite[chunkSize];

            // Skip per-star pm computation entirely when dt is zero (test frames,
            // missing DATE-OBS) -- avoids 2.5M wasted cos(Dec) calls on the no-op.
            var applyPm = dtJulianYears != 0.0;

            var read = startRecord;
            var written = 0;
            while (read < tycCount)
            {
                var wanted = Math.Min(chunkSize, tycCount - read);
                var n = db.CopyTycho2Stars(chunk.AsSpan(0, wanted), read);
                if (n == 0)
                {
                    break;
                }

                for (var i = 0; i < n; i++)
                {
                    var s = chunk[i];
                    if (float.IsNaN(s.VMag))
                    {
                        continue;
                    }

                    double ra = s.RaHours, dec = s.DecDeg;
                    if (applyPm && (s.PmRaTenthMasPerYr != 0 || s.PmDecTenthMasPerYr != 0))
                    {
                        (ra, dec) = CoordinateUtils.PropagatePm(
                            s.RaHours, s.DecDeg,
                            s.PmRaMasPerYr, s.PmDecMasPerYr,
                            dtJulianYears);
                    }

                    var (x, y, z) = RaDecToUnitVec(ra, dec);
                    var off = written * FloatsPerStar;
                    destination[off]     = x;
                    destination[off + 1] = y;
                    destination[off + 2] = z;
                    destination[off + 3] = s.VMag;
                    destination[off + 4] = float.IsNaN(s.BMinusV) ? 0.65f : s.BMinusV;
                    written++;
                }

                read += n;
            }

            return written;
        }
    }

    public enum SkyMapMode
    {
        Equatorial,
        Horizon
    }

    /// <summary>
    /// A mount slew destination for the sky-map overlay: target J2000 coordinates + the
    /// display name of what is being slewed to. A fresh instance is created per goto so
    /// the renderer can detect a new slew (by reference) and restart its ETA estimate.
    /// </summary>
    public sealed record SlewTargetInfo(string Name, double RaJ2000, double DecJ2000);
}
