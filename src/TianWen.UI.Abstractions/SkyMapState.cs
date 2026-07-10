using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
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

        /// <summary>Full viewport vertical field of view in degrees, range [0.5, 180].</summary>
        public double FieldOfViewDeg { get; set; } = 60.0;

        /// <summary>Display mode: equatorial (RA/Dec grid) or horizon (Alt/Az grid).
        /// Defaults to Horizon — keeps the horizon line horizontal and zenith up, which
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
        /// Show the catalog object overlay (Messier / NGC / IC / named stars) — same
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
        /// <summary>View matrix at drag start — needed for correct unproject during drag.</summary>
        public Matrix4x4 DragStartViewMatrix { get; set; }

        /// <summary>FOV at the start of a pinch gesture, for absolute scale application.</summary>
        public double PinchStartFov { get; set; }

        /// <summary>True while a two-finger pinch is active — suppresses drag.</summary>
        public bool IsPinching { get; set; }

        /// <summary>
        /// User-controlled base magnitude limit floor. Brighter = lower number.
        /// Keyboard + / − adjusts this; the effective limit sent to the GPU also
        /// grows as the user zooms in — see <see cref="EffectiveMagnitudeLimit"/>.
        /// Must stay in sync with the Milky Way bake's <c>--min-mag</c>: stars
        /// fainter than this limit contribute to the diffuse texture, brighter
        /// ones are drawn as point sprites. A mismatch produces halos.
        /// </summary>
        public float MagnitudeLimit { get; set; } = 8.5f;

        /// <summary>
        /// FOV-aware magnitude limit (Stellarium-style <c>computeRCMag</c> analogue).
        /// As the user zooms in (FOV shrinks) the effective limit grows, revealing
        /// fainter stars that live in the Tycho-2 regime at high zoom.
        /// <para>
        /// Formula: <c>base + max(0, log10(60 / fov) * 2.5)</c>. Pinned so widening
        /// the view never dips below the user's chosen floor.
        /// </para>
        /// </summary>
        /// <returns>Effective magnitude cutoff for the GPU vertex shader.</returns>
        public float EffectiveMagnitudeLimit
        {
            get
            {
                var fov = Math.Max(0.1, FieldOfViewDeg);
                var zoomBonus = Math.Max(0.0, Math.Log10(60.0 / fov) * 2.5);
                return MagnitudeLimit + (float)zoomBonus;
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

        // Planet positions at the current viewingTime. Keyed on the exact
        // DateTimeOffset — SkyMapTab feeds viewingTime from _cachedLiveTime which
        // is quantized to 1 s (or jumps in bulk when the planner date shifts),
        // so bit equality is sufficient for the 59 out of 60 frames per second
        // that carry an identical viewingTime. Planets move at most ~0.5"/s
        // (the Moon; planets much slower), so even at 1 deg FOV the per-frame
        // drift is deeply sub-pixel; 1 s refresh is already over-accurate.
        private DateTimeOffset _planetCacheTime = DateTimeOffset.MinValue;
        private readonly (CatalogIndex Index, double RA, double Dec)[] _planetCache
            = new (CatalogIndex, double, double)[SkyMapRenderer.PlanetIndices.Length];
        private int _planetCacheCount;

        /// <summary>
        /// Planet J2000 RA/Dec positions at <paramref name="viewingTime"/>, cached
        /// until viewingTime changes. Entries for bodies whose VSOP87a reduction
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
            if (viewingTime == _planetCacheTime)
            {
                return _planetCache.AsSpan(0, _planetCacheCount);
            }

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
        /// J2000 RA/Dec, predicted total magnitude, and the short display label (common name if SBDB has
        /// one, else the canonical designation). The comet analogue of the planet cache tuple.
        /// </summary>
        public readonly record struct CometMarker(CatalogIndex Index, double RA, double Dec, float VMag, string Label);

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

        // Per-viewingTime marker cache (positions + magnitudes for the candidate set), keyed on the exact
        // viewingTime + repository identity, mirroring the planet cache. Zoom-independent: it holds every
        // candidate with a finite magnitude, and each consumer (draw / click / info panel) applies its own
        // magnitude limit -- so zooming never invalidates it.
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
        public ReadOnlySpan<CometMarker> GetCometPositionsCached(ICometRepository? comets, DateTimeOffset viewingTime)
        {
            if (comets is null)
            {
                return default;
            }

            RebuildCometCandidatesIfNeeded(comets);

            if (viewingTime == _cometCacheTime && ReferenceEquals(comets, _cometCacheRepo))
            {
                return _cometCache.AsSpan(0, _cometCacheCount);
            }

            if (_cometCache.Length < _cometCandidates.Length)
            {
                _cometCache = new CometMarker[_cometCandidates.Length];
            }

            var count = 0;
            foreach (var el in _cometCandidates)
            {
                if (el.CatalogIndex is not { } idx)
                {
                    continue;
                }
                if (!CometEphemeris.TryGetEquatorialJ2000WithMagnitude(el, viewingTime, out var ra, out var dec, out var mag)
                    || double.IsNaN(mag))
                {
                    continue;
                }
                var label = el.CommonName is { Length: > 0 } cn ? cn : el.Designation.ToCanonical();
                _cometCache[count++] = new CometMarker(idx, ra, dec, (float)mag, label);
            }
            _cometCacheCount = count;
            _cometCacheTime = viewingTime;
            _cometCacheRepo = comets;
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

        // Selected-object sky-path cache (RA/Dec samples), keyed on (index, viewing DAY) exactly like the
        // vmag sparkline: sampling is stable within a day, so it recomputes only on selection / day change,
        // never per frame. The per-frame cost is then just projecting these samples + a polyline.
        private CatalogIndex _pathIndex;
        private long _pathBucketKey = long.MinValue;
        private (double RA, double Dec)[] _pathSamples = [];
        private int _pathCount;

        // Notable events along the current path (stations, greatest elongation / opposition, perihelion),
        // recomputed with the path (same bucket) and read by the renderer to annotate the arc. The Sun
        // track is scratch, sampled at the same instants only for planets (elongation/opposition).
        private readonly List<SkyPathEvent> _pathEvents = [];
        private (double RA, double Dec)[] _sunSamples = [];

        /// <summary>
        /// Events detected on the most recently built selected-object path (see
        /// <see cref="GetSelectedPathCached"/>): stations / retrograde, greatest elongation, opposition,
        /// perihelion. Valid for whatever selection + bucket the last <see cref="GetSelectedPathCached"/>
        /// call resolved, so read it right after that call for the same object.
        /// </summary>
        public IReadOnlyList<SkyPathEvent> SelectedPathEvents => _pathEvents;

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
        /// Cached sky path (J2000 RA/Dec samples) for a selected solar-system object over a body-appropriate
        /// window centred on <paramref name="viewingTime"/> -- planets via <see cref="VSOP87a.ReduceJ2000"/>,
        /// comets via <see cref="CometEphemeris.TryGetEquatorialJ2000"/>. Returns an empty span for a
        /// non-solar-system index (fixed stars/DSOs don't move) or an unknown comet. Recomputed only when the
        /// selection changes or the viewing instant crosses a per-body cache bucket (planets rebuild rarely
        /// since their arc barely moves; see the bucket constants). Samples where the ephemeris fails are omitted.
        /// </summary>
        public ReadOnlySpan<(double RA, double Dec)> GetSelectedPathCached(ICometRepository? comets, CatalogIndex index, DateTimeOffset viewingTime)
        {
            if (!index.IsSolarSystemObject)
            {
                return default;
            }

            var isComet = index.ToCatalog() == Catalog.Comet;
            var bucketTicks = index == CatalogIndex.Moon ? MoonPathBucketTicks
                : isComet ? CometPathBucketTicks
                : PlanetPathBucketTicks;
            var bucketKey = viewingTime.UtcDateTime.Ticks / bucketTicks;
            // Hit on (index, bucket) REGARDLESS of sample count: a legitimately empty result (all samples
            // failed to solve) must still cache, or it would re-sample ~49 ephemerides every frame. A real
            // solar-system index is never default(CatalogIndex), so the initial default key still misses.
            if (index == _pathIndex && bucketKey == _pathBucketKey)
            {
                return _pathSamples.AsSpan(0, _pathCount);
            }

            CometElements cometEl = default;
            if (isComet && (comets is null || !comets.TryGet(index, out cometEl)))
            {
                _pathCount = 0;
                _pathIndex = index;
                _pathBucketKey = bucketKey;
                _pathEvents.Clear();
                return default;
            }

            if (_pathSamples.Length < SkyPathSampleCount)
            {
                _pathSamples = new (double, double)[SkyPathSampleCount];
            }

            var windowDays = index == CatalogIndex.Moon ? MoonPathWindowDays
                : isComet ? CometPathWindowDays
                : PlanetPathWindowDays;
            var start = viewingTime - TimeSpan.FromDays(windowDays / 2.0);
            var step = TimeSpan.FromDays(windowDays / (SkyPathSampleCount - 1));

            var count = 0;
            for (var i = 0; i < SkyPathSampleCount; i++)
            {
                var t = start + step * i;
                var ok = isComet
                    ? CometEphemeris.TryGetEquatorialJ2000(cometEl, t, out var ra, out var dec, out _, out _)
                    : VSOP87a.ReduceJ2000(index, t, out ra, out dec, out _);
                if (ok)
                {
                    _pathSamples[count++] = (ra, dec);
                }
            }

            _pathCount = count;
            _pathIndex = index;
            _pathBucketKey = bucketKey;

            ComputePathEvents(index, isComet, cometEl, start, step, count);

            return _pathSamples.AsSpan(0, count);
        }

        // Recompute the path's notable events (see SelectedPathEvents), sharing the path's start/step. Only
        // runs when every sample solved (count == SkyPathSampleCount), because the detector assumes an even
        // index->time spacing that dropped samples would break. Planets get a Sun track sampled at the same
        // instants (for greatest elongation / opposition); comets carry their perihelion instant.
        private void ComputePathEvents(CatalogIndex index, bool isComet, in CometElements cometEl, DateTimeOffset start, TimeSpan step, int count)
        {
            _pathEvents.Clear();
            if (count != SkyPathSampleCount)
            {
                return;
            }

            var body = ClassifySkyPathBody(index, isComet);

            ReadOnlySpan<(double RA, double Dec)> sun = default;
            if (body is SkyPathBody.InferiorPlanet or SkyPathBody.OuterPlanet)
            {
                if (_sunSamples.Length < count)
                {
                    _sunSamples = new (double, double)[count];
                }
                var sunOk = true;
                for (var i = 0; i < count; i++)
                {
                    if (VSOP87a.ReduceJ2000(CatalogIndex.Sol, start + step * i, out var sunRa, out var sunDec, out _))
                    {
                        _sunSamples[i] = (sunRa, sunDec);
                    }
                    else
                    {
                        sunOk = false;
                        break;
                    }
                }
                if (sunOk)
                {
                    sun = _sunSamples.AsSpan(0, count);
                }
            }

            DateTimeOffset? perihelion = isComet && !double.IsNaN(cometEl.PerihelionJdTt)
                ? JdTtToUtc(cometEl.PerihelionJdTt)
                : null;

            SkyPathEventDetector.Detect(_pathSamples.AsSpan(0, count), sun, start, step, body, perihelion, _pathEvents);
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
        /// Compute the J2000 → camera rotation matrix for the current view center.
        /// The matrix maps the view direction to -Z (camera forward), with X = right and Y = up.
        /// In equatorial mode, "up" is toward the celestial north pole.
        /// In horizon mode, "up" is toward the local zenith (horizon stays horizontal).
        /// Returns a <see cref="Matrix4x4"/> (column-major layout matches std140 mat4).
        /// </summary>
        /// <param name="zenithX">J2000 X component of the local zenith (only used in Horizon mode).</param>
        /// <param name="zenithY">J2000 Y component of the local zenith.</param>
        /// <param name="zenithZ">J2000 Z component of the local zenith.</param>
        public Matrix4x4 ComputeViewMatrix(float zenithX = 0f, float zenithY = 0f, float zenithZ = 1f)
        {
            var (sinRA, cosRA) = Math.SinCos(CenterRA * Hours2Rad);
            var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(CenterDec));

            // Forward direction: unit vector toward (CenterRA, CenterDec)
            var fx = (float)(cosDec * cosRA);
            var fy = (float)(cosDec * sinRA);
            var fz = (float)sinDec;

            // "Up" reference direction depends on mode:
            // Equatorial: celestial north pole (0, 0, 1)
            // Horizon: local zenith (cosLat*cosLST, cosLat*sinLST, sinLat)
            float upRefX, upRefY, upRefZ;
            if (Mode == SkyMapMode.Horizon)
            {
                upRefX = zenithX;
                upRefY = zenithY;
                upRefZ = zenithZ;
            }
            else
            {
                upRefX = 0f;
                upRefY = 0f;
                upRefZ = 1f;
            }

            // Right = forward × upRef, then normalize
            var rx = fy * upRefZ - fz * upRefY;
            var ry = fz * upRefX - fx * upRefZ;
            var rz = fx * upRefY - fy * upRefX;
            var rLen = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rLen > 1e-6f)
            {
                rx /= rLen;
                ry /= rLen;
                rz /= rLen;
            }
            else
            {
                // Forward is parallel to up reference — pick an arbitrary right vector
                rx = 1f;
                ry = 0f;
                rz = 0f;
            }

            // Up = right × forward (already unit length since right ⊥ forward and both unit)
            var ux = ry * fz - rz * fy;
            var uy = rz * fx - rx * fz;
            var uz = rx * fy - ry * fx;

            // View matrix: rows are (right, up, -forward)
            // Matrix4x4 constructor takes row-major arguments (M11..M44)
            return new Matrix4x4(
                rx,  ry,  rz,  0f,
                ux,  uy,  uz,  0f,
                -fx, -fy, -fz, 0f,
                0f,  0f,  0f,  1f);
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
        {
            var tycCount = db.Tycho2StarCount;
            if (tycCount == 0)
            {
                return 0;
            }

            // Read Tycho-2 records in chunks -- keeps the temp alloc bounded
            // (~16 MB) while still minimising the number of CopyTycho2Stars calls.
            const int chunkSize = 200_000;
            var chunk = new Tycho2StarLite[chunkSize];

            // Skip per-star pm computation entirely when dt is zero (test frames,
            // missing DATE-OBS) -- avoids 2.5M wasted cos(Dec) calls on the no-op.
            var applyPm = dtJulianYears != 0.0;

            var read = 0;
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
