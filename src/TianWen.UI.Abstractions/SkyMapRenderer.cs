using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// CPU software renderer for the sky map. Draws stars, constellation lines, RA/Dec grid,
    /// and planets onto an <see cref="RgbaImage"/> pixel buffer. The buffer is then uploaded
    /// as a GPU texture by the Vulkan layer for cached rendering.
    /// </summary>
    public static class SkyMapRenderer
    {
        // Colors — Stellarium-inspired color scheme
        private static readonly RGBAColor32 BackgroundColor      = new(0x05, 0x05, 0x0C, 0xFF);
        private static readonly RGBAColor32 GridColor            = new(0x30, 0x60, 0xA0, 0xB0);
        private static readonly RGBAColor32 BoundaryColor        = new(0xAA, 0x44, 0x44, 0x80); // red, like Stellarium
        private static readonly RGBAColor32 ConstellationLabel   = new(0x70, 0x90, 0xC0, 0xE0);
        private static readonly RGBAColor32 FigureColor          = new(0x40, 0x80, 0xDD, 0x90); // blue stick figures, semi-transparent
        private static readonly RGBAColor32 PlanetColor          = new(0xFF, 0xDD, 0x44, 0xFF);
        private static readonly RGBAColor32 PlanetLabelColor     = new(0xFF, 0xEE, 0x88, 0xFF);

        /// <summary>
        /// Render the full sky map to the given pixel buffer.
        /// </summary>
        public static void Render(
            RgbaImage image,
            SkyMapState state,
            ICelestialObjectDB db,
            ITimeProvider timeProvider,
            double siteLat,
            double siteLon,
            string fontPath,
            IReadOnlyList<CatalogIndex>? pinnedTargets = null)
        {
            var w = image.Width;
            var h = image.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            image.Clear(BackgroundColor);

            var ppr = SkyMapProjection.PixelsPerRadian(h, state.FieldOfViewDeg);
            var cx = w * 0.5f;
            var cy = h * 0.5f;
            var cRA = state.CenterRA;
            var cDec = state.CenterDec;
            var site = SiteContext.Create(siteLat, siteLon, timeProvider);

            // Draw layers back to front

            // Meridian: full great circle through both poles at RA = LST and RA = LST+12h
            if (site.IsValid)
            {
                var antiLst = (site.LST + 12.0) % 24.0;
                var steps = Math.Clamp((int)(300 * 60.0 / Math.Max(state.FieldOfViewDeg, 1)), 100, 600);
                DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, MeridianColor, steps,
                    i => (site.LST, -90.0 + i * 180.0 / steps));
                DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, MeridianColor, steps,
                    i => (antiLst, 90.0 - i * 180.0 / steps));
            }

            if (state.ShowGrid)
            {
                DrawGrid(image, cRA, cDec, ppr, cx, cy, w, h, state.FieldOfViewDeg);
            }

            if (state.ShowConstellationBoundaries)
            {
                DrawConstellationBoundaries(image, cRA, cDec, ppr, cx, cy, w, h);
            }

            if (state.ShowConstellationFigures)
            {
                DrawConstellationFigures(image, db, cRA, cDec, ppr, cx, cy, w, h);
            }

            DrawStars(image, db, cRA, cDec, ppr, cx, cy, w, h,
                state.EffectiveMagnitudeLimit, state.FieldOfViewDeg,
                state.ShowHorizon ? site : default);

            if (state.ShowPlanets)
            {
                DrawPlanets(image, db, site, cRA, cDec, ppr, cx, cy, w, h);
            }

            // Horizon drawn last so it's visible on top of everything
            if (state.ShowHorizon)
            {
                DrawHorizonLine(image, site, cRA, cDec, ppr, cx, cy, w, h);
            }
        }

        // ── Horizon ──

        private static readonly RGBAColor32 HorizonLine     = new(0x80, 0x40, 0x20, 0xFF);
        private static readonly RGBAColor32 CardinalColor   = new(0xCC, 0x88, 0x44, 0xFF);
        private static readonly RGBAColor32 MeridianColor   = new(0x30, 0xDD, 0x30, 0xA0);

        /// <summary>
        /// Draw the horizon as a line and cardinal point markers. No fill — keeps rendering simple.
        /// </summary>
        private static void DrawHorizonLine(
            RgbaImage image,
            SiteContext site,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h)
        {
            if (!site.IsValid)
            {
                return;
            }

            // Trace the horizon curve and draw it as connected line segments
            const int steps = 120;
            var prevX = float.NaN;
            var prevY = float.NaN;

            for (var i = 0; i <= steps; i++)
            {
                var ra = i * 24.0 / steps;
                var decHorizon = site.HorizonDec(ra);

                if (SkyMapProjection.Project(ra, decHorizon, cRA, cDec, ppr, cx, cy, out var sx, out var sy)
                    && sx >= -200 && sx < w + 200 && sy >= -200 && sy < h + 200)
                {
                    if (!float.IsNaN(prevX))
                    {
                        DrawLine(image, (int)prevX, (int)prevY, (int)sx, (int)sy, HorizonLine);
                    }
                    prevX = sx;
                    prevY = sy;
                }
                else
                {
                    prevX = float.NaN;
                }
            }

            // Cardinal points: N (az=0), E (az=90), S (az=180), W (az=270)
            ReadOnlySpan<(double Az, string Label)> cardinals =
                [(0, "N"), (90, "E"), (180, "S"), (270, "W")];

            foreach (var (az, _) in cardinals)
            {
                var (sinAz, cosAz) = Math.SinCos(az * Math.PI / 180.0);
                var dec = Math.Asin(cosAz * site.CosLat) * 180.0 / Math.PI;
                var ha = Math.Atan2(sinAz, cosAz * site.SinLat) * 12.0 / Math.PI;
                var ra = ((site.LST - ha) % 24.0 + 24.0) % 24.0;

                if (SkyMapProjection.Project(ra, dec, cRA, cDec, ppr, cx, cy, out var sx, out var sy)
                    && sx >= 0 && sx < w && sy >= 0 && sy < h)
                {
                    FillCircle(image, (int)sx, (int)sy, 4, CardinalColor);
                }
            }
        }


        // ── Grid ──

        /// <summary>
        /// Multi-scale grid: draw multiple scales simultaneously with finer lines fading in as
        /// you zoom. Lines are always at fixed RA/Dec values — they never shift when zooming.
        /// Step count adapts to FOV to keep lines continuous.
        /// </summary>
        private static void DrawGrid(
            RgbaImage image,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h, double fov)
        {
            // Multiple grid scales — coarse always visible, fine fades in
            // (raStepHours, decStepDeg, minFov to show, maxFov for full opacity)
            ReadOnlySpan<(double RaStep, double DecStep, double MinFov, double MaxFov)> scales =
            [
                (6.0,  30.0,  30.0, 999.0), // coarse: always visible above 30° FOV
                (3.0,  15.0,  10.0, 120.0),
                (1.0,  10.0,   3.0,  40.0),
                (0.5,   5.0,   1.0,  15.0),
                (10.0 / 60.0, 1.0, 0.2, 5.0), // 10min RA, 1° Dec for deep zoom
            ];

            // Adaptive step count: more steps at higher zoom for smooth lines
            var lineSteps = Math.Max(60, (int)(200 * 60.0 / Math.Max(fov, 1)));
            lineSteps = Math.Min(lineSteps, 600); // cap to avoid excessive draw calls

            foreach (var (raStep, decStep, minFov, maxFov) in scales)
            {
                if (fov > maxFov)
                {
                    continue;
                }

                // Fade in: full alpha when FOV < minFov*2, zero when FOV >= maxFov
                var fade = fov < minFov * 2
                    ? 1.0
                    : Math.Clamp((maxFov - fov) / (maxFov - minFov * 2), 0, 1);
                if (fade < 0.05)
                {
                    continue;
                }
                // Scale alpha by fade factor
                var alpha = (byte)(0xB0 * fade);
                var color = new RGBAColor32(0x30, 0x60, 0xA0, alpha);

                // RA lines (constant RA, varying Dec — great circles)
                for (var ra = 0.0; ra < 24.0; ra += raStep)
                {
                    DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, color, lineSteps,
                        i => (ra, -90.0 + i * 180.0 / lineSteps));
                }

                // Dec lines (constant Dec, varying RA — small circles)
                for (var dec = -90.0 + decStep; dec < 90.0; dec += decStep)
                {
                    DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, color, lineSteps,
                        i => (i * 24.0 / lineSteps, dec));
                }
            }
        }

        /// <summary>
        /// Draw a projected line by sampling N points via a coordinate function,
        /// projecting each, and connecting with Bresenham lines. Handles clipping
        /// and discontinuities (points behind the projection).
        /// </summary>
        private static void DrawProjectedLine(
            RgbaImage image,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h,
            RGBAColor32 color, int steps,
            Func<int, (double RA, double Dec)> coordFunc)
        {
            var prevX = float.NaN;
            var prevY = float.NaN;
            var margin = Math.Max(w, h); // generous margin for off-screen line segments

            for (var i = 0; i <= steps; i++)
            {
                var (ra, dec) = coordFunc(i);
                if (SkyMapProjection.Project(ra, dec, cRA, cDec, ppr, cx, cy, out var sx, out var sy)
                    && sx >= -margin && sx < w + margin && sy >= -margin && sy < h + margin)
                {
                    if (!float.IsNaN(prevX))
                    {
                        // Skip if the segment is absurdly long (wrapping artifact)
                        var dx = sx - prevX;
                        var dy = sy - prevY;
                        if (dx * dx + dy * dy < w * w + h * h)
                        {
                            DrawLine(image, (int)prevX, (int)prevY, (int)sx, (int)sy, color);
                        }
                    }
                    prevX = sx;
                    prevY = sy;
                }
                else
                {
                    prevX = float.NaN;
                    prevY = float.NaN;
                }
            }
        }

        // ── Stars ──

        /// <summary>
        /// Draw stars by iterating the HIP catalog via the fast O(1) Tycho-2 cross-reference.
        /// ~118k HIP stars cover all naked-eye stars and most telescope targets.
        /// </summary>
        private static void DrawStars(
            RgbaImage image,
            ICelestialObjectDB db,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h,
            float magLimit, double fovDeg,
            SiteContext site)
        {
            var hipCount = db.HipStarCount;

            for (var hip = 1; hip <= hipCount; hip++)
            {
                if (!db.TryLookupHIP(hip, out var ra, out var dec, out var vMag, out var bv))
                {
                    continue;
                }

                if (float.IsNaN(vMag) || vMag > magLimit)
                {
                    continue;
                }

                // Skip stars below the horizon (when horizon is enabled)
                if (site.IsValid && !site.IsAboveHorizon(ra, dec))
                {
                    continue;
                }

                if (!SkyMapProjection.Project(ra, dec, cRA, cDec, ppr, cx, cy, out var sx, out var sy))
                {
                    continue;
                }

                if (sx < -5 || sx >= w + 5 || sy < -5 || sy >= h + 5)
                {
                    continue;
                }

                var radius = SkyMapProjection.StarRadius(vMag, fovDeg);
                var (r, g, b) = SkyMapProjection.StarColor(bv);
                var iRadius = Math.Max(1, (int)(radius + 0.5f));

                // Dim halo for brighter stars (radius > 2) — gives soft glow effect
                if (iRadius > 2)
                {
                    var haloAlpha = (byte)Math.Min(120, 40 + iRadius * 10);
                    FillCircle(image, (int)sx, (int)sy, iRadius + 1, new RGBAColor32(r, g, b, haloAlpha));
                }

                // Bright core
                FillCircle(image, (int)sx, (int)sy, iRadius, new RGBAColor32(r, g, b, 0xFF));
            }
        }

        // ── Constellation Lines ──

        // ── Constellation Figures ──

        /// <summary>
        /// Draw constellation stick figures by resolving HIP star numbers to RA/Dec
        /// via the celestial object database, then connecting consecutive stars with lines.
        /// </summary>
        private static void DrawConstellationFigures(
            RgbaImage image,
            ICelestialObjectDB db,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h)
        {
            foreach (var constellation in System.Enum.GetValues<Constellation>())
            {
                // Skip Serpens sub-parts to avoid drawing twice (Serpens composes from them)
                if (constellation is Constellation.SerpensCaput or Constellation.SerpensCauda)
                {
                    continue;
                }

                var figure = constellation.Figure;
                if (figure.IsDefaultOrEmpty)
                {
                    continue;
                }

                foreach (var polyline in figure)
                {
                    var prevX = float.NaN;
                    var prevY = float.NaN;

                    foreach (var hip in polyline)
                    {
                        // Fast O(1) HIP → RA/Dec via the Tycho-2 cross-reference array
                        if (!db.TryLookupHIP(hip, out var ra, out var dec, out _, out _))
                        {
                            prevX = float.NaN;
                            continue;
                        }

                        if (!SkyMapProjection.Project(ra, dec, cRA, cDec, ppr, cx, cy,
                            out var sx, out var sy))
                        {
                            prevX = float.NaN;
                            continue;
                        }

                        // Draw if either endpoint is within the guard band so lines
                        // crossing the viewport edge are not clipped entirely.
                        // DrawLine clips per-pixel, so off-screen coordinates are safe.
                        if (!float.IsNaN(prevX)
                            && ((sx >= -50 && sx < w + 50 && sy >= -50 && sy < h + 50)
                                || (prevX >= -50 && prevX < w + 50 && prevY >= -50 && prevY < h + 50)))
                        {
                            DrawLine(image, (int)prevX, (int)prevY, (int)sx, (int)sy, FigureColor);
                        }

                        prevX = sx;
                        prevY = sy;
                    }
                }
            }
        }

        // ── Constellation Boundaries ──

        /// <summary>
        /// Draw IAU constellation boundary edges. Each edge is a single line segment between
        /// two points, drawn as a parallel (constant Dec) or meridian (constant RA) arc.
        /// Data from Stellarium's modern sky culture boundary edges (Barbier/Delporte, B1875).
        /// </summary>
        private static void DrawConstellationBoundaries(
            RgbaImage image,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h)
        {
            foreach (var edge in ConstellationEdges.Edges)
            {
                if (edge.Type == ConstellationEdges.EdgeType.Parallel)
                {
                    // Constant Dec arc from RA1 to RA2 — interpolate along RA
                    var raRange = edge.RA2 - edge.RA1;
                    // Handle RA wrapping (e.g., 23h to 1h)
                    if (raRange < -12) raRange += 24;
                    if (raRange > 12) raRange -= 24;
                    var steps = Math.Max(5, (int)(Math.Abs(raRange) * 8));
                    DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, BoundaryColor, steps,
                        i => (edge.RA1 + i * raRange / steps, edge.Dec1));
                }
                else if (edge.Type == ConstellationEdges.EdgeType.Meridian)
                {
                    // Constant RA arc from Dec1 to Dec2 — interpolate along Dec
                    var decRange = edge.Dec2 - edge.Dec1;
                    var steps = Math.Max(5, (int)(Math.Abs(decRange) / 2));
                    DrawProjectedLine(image, cRA, cDec, ppr, cx, cy, w, h, BoundaryColor, steps,
                        i => (edge.RA1, edge.Dec1 + i * decRange / steps));
                }
                else
                {
                    // Straight line — just draw between the two endpoints
                    if (SkyMapProjection.Project(edge.RA1, edge.Dec1, cRA, cDec, ppr, cx, cy, out var x1, out var y1)
                        && SkyMapProjection.Project(edge.RA2, edge.Dec2, cRA, cDec, ppr, cx, cy, out var x2, out var y2))
                    {
                        DrawLine(image, (int)x1, (int)y1, (int)x2, (int)y2, BoundaryColor);
                    }
                }
            }
        }


        // ── Planets ──

        internal static readonly CatalogIndex[] PlanetIndices =
        [
            CatalogIndex.Sol,
            CatalogIndex.Mercury,
            CatalogIndex.Venus,
            CatalogIndex.Mars,
            CatalogIndex.Jupiter,
            CatalogIndex.Saturn,
            CatalogIndex.Uranus,
            CatalogIndex.Neptune,
            CatalogIndex.Moon
        ];

        private static void DrawPlanets(
            RgbaImage image,
            ICelestialObjectDB db,
            SiteContext site,
            double cRA, double cDec, double ppr,
            float cx, float cy, int w, int h)
        {
            if (!site.IsValid)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow; // planets need current time, not cached

            foreach (var planetIdx in PlanetIndices)
            {
                if (!TianWen.Lib.Astrometry.VSOP87.VSOP87a.Reduce(
                        planetIdx, now, site.Latitude, site.Longitude,
                        out var ra, out var dec, out _, out _, out _))
                {
                    continue;
                }

                // VSOP87a returns RA in degrees for planets, convert to hours
                var raHours = ra / 15.0;

                if (!SkyMapProjection.Project(raHours, dec, cRA, cDec, ppr, cx, cy, out var sx, out var sy))
                {
                    continue;
                }

                if (sx < -10 || sx >= w + 10 || sy < -10 || sy >= h + 10)
                {
                    continue;
                }

                // Draw planet as a larger colored dot
                FillCircle(image, (int)sx, (int)sy, 4, PlanetColor);

                // Draw a ring around it
                DrawCircleOutline(image, (int)sx, (int)sy, 6, PlanetColor);
            }
        }

        // ── Drawing primitives ──

        /// <summary>
        /// Bresenham line drawing with alpha blending.
        /// </summary>
        private static void DrawLine(RgbaImage image, int x0, int y0, int x1, int y1, RGBAColor32 color)
        {
            var dx = Math.Abs(x1 - x0);
            var dy = Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < image.Width && y0 >= 0 && y0 < image.Height)
                {
                    image.BlendPixelAt(x0, y0, color);
                }

                if (x0 == x1 && y0 == y1)
                {
                    break;
                }

                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        /// <summary>
        /// Draw a soft (anti-aliased) star as a radial gradient: bright center fading to transparent edge.
        /// Produces round-looking stars even at small sizes.
        /// </summary>
        private static void DrawSoftStar(RgbaImage image, int cx, int cy, float radius, byte r, byte g, byte b)
        {
            var w = image.Width;
            var h = image.Height;
            var ir = (int)MathF.Ceiling(radius) + 1; // integer radius with 1px margin for fade

            for (var dy = -ir; dy <= ir; dy++)
            {
                var py = cy + dy;
                if (py < 0 || py >= h)
                {
                    continue;
                }

                for (var dx = -ir; dx <= ir; dx++)
                {
                    var px = cx + dx;
                    if (px < 0 || px >= w)
                    {
                        continue;
                    }

                    // Distance from center
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > radius + 0.5f)
                    {
                        continue;
                    }

                    // Smooth falloff: full brightness inside radius-0.5, fade to 0 at radius+0.5
                    var alpha = dist < radius - 0.5f
                        ? 1.0f
                        : Math.Clamp(1.0f - (dist - radius + 0.5f), 0f, 1f);

                    var a = (byte)(alpha * 255);
                    if (a > 0)
                    {
                        image.BlendPixelAt(px, py, new RGBAColor32(r, g, b, a));
                    }
                }
            }
        }

        /// <summary>
        /// Fill a circle using the midpoint algorithm.
        /// </summary>
        private static void FillCircle(RgbaImage image, int cx, int cy, int radius, RGBAColor32 color)
        {
            if (radius <= 1)
            {
                if (cx >= 0 && cx < image.Width && cy >= 0 && cy < image.Height)
                {
                    image.BlendPixelAt(cx, cy, color);
                }
                return;
            }

            var x = radius;
            var y = 0;
            var decisionOver2 = 1 - x;

            while (y <= x)
            {
                // Draw horizontal spans for filled circle
                image.DrawHLine(cx - x, cx + x, cy + y, color);
                image.DrawHLine(cx - x, cx + x, cy - y, color);
                image.DrawHLine(cx - y, cx + y, cy + x, color);
                image.DrawHLine(cx - y, cx + y, cy - x, color);

                y++;
                if (decisionOver2 <= 0)
                {
                    decisionOver2 += 2 * y + 1;
                }
                else
                {
                    x--;
                    decisionOver2 += 2 * (y - x) + 1;
                }
            }
        }

        /// <summary>
        /// Draw a 1px circle outline using the midpoint algorithm.
        /// </summary>
        private static void DrawCircleOutline(RgbaImage image, int cx, int cy, int radius, RGBAColor32 color)
        {
            var x = radius;
            var y = 0;
            var decisionOver2 = 1 - x;
            var w = image.Width;
            var h = image.Height;

            while (y <= x)
            {
                PlotIfInBounds(cx + x, cy + y);
                PlotIfInBounds(cx - x, cy + y);
                PlotIfInBounds(cx + x, cy - y);
                PlotIfInBounds(cx - x, cy - y);
                PlotIfInBounds(cx + y, cy + x);
                PlotIfInBounds(cx - y, cy + x);
                PlotIfInBounds(cx + y, cy - x);
                PlotIfInBounds(cx - y, cy - x);

                y++;
                if (decisionOver2 <= 0)
                {
                    decisionOver2 += 2 * y + 1;
                }
                else
                {
                    x--;
                    decisionOver2 += 2 * (y - x) + 1;
                }
            }

            void PlotIfInBounds(int px, int py)
            {
                if (px >= 0 && px < w && py >= 0 && py < h)
                {
                    image.BlendPixelAt(px, py, color);
                }
            }
        }
    }
}
