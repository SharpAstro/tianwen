using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
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
        /// Current mount pointing for the reticle overlay. Null when no mount is connected
        /// or its coordinates can't be read. Populated by the event loop from polled
        /// <c>LiveSessionState.PreviewMountState</c> (preview mode) or <c>session.MountState</c>
        /// (session running). RA/Dec are J2000 when available; native coords are used as a
        /// fallback for mounts where the J2000 conversion hasn't been populated yet.
        /// </summary>
        public SkyMapMountOverlay? MountOverlay { get; set; }

        /// <summary>Toggle the mount reticle (<c>[M]</c> key).</summary>
        public bool ShowMountOverlay { get; set; } = true;

        /// <summary>
        /// Pre-computed mosaic panel centres for pinned targets whose catalog shape
        /// exceeds the sensor FOV. Populated by the event loop alongside the mount
        /// overlay. Each entry is the RA/Dec centre of one panel. The sensor FOV
        /// (from <see cref="SkyMapMountOverlay.SensorFovDeg"/>) defines the rectangle
        /// size for every panel. Empty when no mosaic-worthy targets are pinned or
        /// no camera is connected.
        /// </summary>
        public ImmutableArray<(double RA, double Dec, string Name, int Row, int Col)> MosaicPanels { get; set; } = [];

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
    }

    public enum SkyMapMode
    {
        Equatorial,
        Horizon
    }
}
