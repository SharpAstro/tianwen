using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The solar system in 3D (docs/plans/atlas-planet-detail.md, A5, #1039): a MODE of the atlas, opened from a solar-system body's
    /// info panel or by Alt+S, at the atlas's own instant. The Sun at the centre, the planets on their orbits (cached polylines,
    /// <see cref="OrreryScene"/>), each body its textured disc (A4's sprites, <see cref="PlanetSpriteCache.OrreryChannel"/>) at an
    /// exaggerated, labelled size, lit from the Sun as the CAMERA sees it (<see cref="PlanetView"/>: ray-traced on a GPU host through
    /// <see cref="DrawPlanetBody"/>, the CPU sprite of the same view elsewhere).
    /// A drag orbits the camera about the Sun, the wheel zooms, the strip switches the scale, plays time and steps it as the sky's does.
    /// A CPU perspective projection (<see cref="OrreryCamera"/>) feeds the 2D primitives every host has, so no host needs anything new.
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        private OrreryScene? _orreryScene;
        private PlanetSpriteCache? _orrerySprites;
        private bool _orreryFramePending;
        private readonly List<(CatalogIndex Index, float X, float Y, float Radius)> _orreryHits = [];

        /// <summary>The orrery's positions and orbits, for the tests that count their builds.</summary>
        internal OrreryScene OrreryScene => _orreryScene ??= new OrreryScene(RequestPlanetFrame);

        /// <summary>The orrery's disc sprites: a cache of their own, so the map's and the panel's sprites of a body are not evicted by it.</summary>
        internal PlanetSpriteCache OrrerySprites => _orrerySprites ??= new PlanetSpriteCache(RequestPlanetFrame, PlanetSpriteCache.OrreryChannel);

        /// <summary>Where each body was drawn in the last orrery frame and how big: what a click picks from, and what the tests read.</summary>
        internal IReadOnlyList<(CatalogIndex Index, float X, float Y, float Radius)> OrreryDrawn => _orreryHits;

        private readonly List<PlanetView> _orreryViews = [];

        /// <summary>The view each disc of the last orrery frame was traced from (its camera and Sun in the body's frame): what the tests read.</summary>
        internal IReadOnlyList<PlanetView> OrreryViews => _orreryViews;

        private readonly List<(CatalogIndex Index, RectF32 Rect)> _orreryLabels = [];

        /// <summary>The labels the last orrery frame drew, each with the box its text was drawn in: what the tests read.</summary>
        internal IReadOnlyList<(CatalogIndex Index, RectF32 Rect)> OrreryLabels => _orreryLabels;

        /// <summary>The backdrop view the last orrery frame drew its stars through, or null when it drew none: what the tests read.</summary>
        internal SkyOrreryBackdrop? LastOrreryBackdrop { get; private set; }

        /// <summary>How many orrery frames have been drawn, and how long the last one took (CPU, milliseconds).</summary>
        internal int OrreryFrames { get; private set; }

        /// <inheritdoc cref="OrreryFrames"/>
        internal double LastOrreryFrameMs { get; private set; }

        // Every colour from the palette (CLAUDE.md, "Colour Theme"), so Night spends no blue: space is the content background halfway
        // to black, the Earth the palette's Info while its map loads, a comet that halfway to the body text.
        private static RGBAColor32 OrreryBackground => GuiTheme.Mix(new RGBAColor32(0, 0, 0, 0xFF), GuiTheme.Palette.ContentBg, 0.5f);
        private static RGBAColor32 EarthColour => GuiTheme.Palette.Info;
        private static RGBAColor32 CometColour => GuiTheme.Mix(GuiTheme.Palette.Info, GuiTheme.Palette.BodyText, 0.5f);

        // A comet's orbit is a dim grey (A2 of #1611: thirty coloured orbits crowded the planets out): the dim text halfway to the body
        // text, a neutral in the light and dark palettes, drawn weaker than any planet's (OrreryOrbitColour). Never used under Night.
        private static RGBAColor32 CometOrbitColour => GuiTheme.Mix(GuiTheme.Palette.DimText, GuiTheme.Palette.BodyText, 0.5f);

        /// <summary>Opens the orrery on the selected body when it is one of the Sun's (the panel's button, Alt+S); the selection is kept.</summary>
        internal void OpenOrrery()
        {
            var orrery = State.Orrery;
            orrery.Focus = State.Search.InfoPanel is { Index: { } index } && index.IsSolarSystemObject ? index : null;
            orrery.Comet = orrery.Focus is { } focus && focus.ToCatalog() == Catalog.Comet ? focus : null;
            orrery.IsOpen = true;
            orrery.CentredOnFocus = false;
            orrery.Dragging = false;
            _orreryFramePending = true;
            State.PictureExpanded = false;
            State.NeedsRedraw = true;
        }

        /// <summary>Back to the sky (Escape, the strip's Sky): the selection and the view are as they were.</summary>
        internal void CloseOrrery()
        {
            State.Orrery.IsOpen = false;
            State.Orrery.Playing = false;
            State.Orrery.Dragging = false;
            State.Orrery.Pinching = false;
            State.NeedsRedraw = true;
        }

        private void ToggleOrrery()
        {
            if (State.Orrery.IsOpen)
            {
                CloseOrrery();
            }
            else
            {
                OpenOrrery();
            }
        }

        /// <summary>
        /// Alt+S, declared on the painted tree like F3 (<see cref="RenderSearchBinding"/>): never an arm in the key switch, where
        /// <see cref="SkyMapLayers.TryToggleByKey"/>, which ignores modifiers, would also toggle the Milky Way. While the orrery is
        /// open, Escape is declared beside it.
        /// </summary>
        private void RenderOrreryBinding(RectF32 contentRect)
        {
            RenderLayout(
                Layout.Builder.Spacer().Stretch()
                    .WithShortcut(InputKey.S, InputModifier.Alt)
                    .Activatable(_ => ToggleOrrery()),
                contentRect);
            if (State.Orrery.IsOpen)
            {
                RenderLayout(
                    Layout.Builder.Spacer().Stretch()
                        .WithShortcut(InputKey.Escape)
                        .Activatable(_ => CloseOrrery()),
                    contentRect);
            }
        }

        private void RenderOrrery(PlannerState plannerState, RectF32 contentRect, ITimeProvider timeProvider)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            BeginFrame();
            _plannerState = plannerState;
            _timeProvider = timeProvider;
            _contentHeight = contentRect.Height;
            _contentWidth = contentRect.Width;
            _contentX = contentRect.X;
            _contentY = contentRect.Y;
            State.LastContentRect = contentRect;
            var orrery = State.Orrery;
            var dpiScale = DpiScale;

            var baseTime = ResolveBaseTime(plannerState, timeProvider);
            AdvancePlay(timeProvider, baseTime);
            var viewingTime = baseTime + State.TimeOffset;
            _lastViewingTime = viewingTime;

            // The comets the app knows (#1611): the brightest at the viewing instant (ranked per month) while the atlas's comet layer is
            // on, and always the one the view was opened on, the focused one and the pinned ones.
            var scene = OrreryScene;
            scene.Update(viewingTime, plannerState.Comets, new OrreryCometChoice(State.ShowComets, orrery.Comet, orrery.Focus, PinnedCatalogIndices(plannerState)));
            if (_orreryFramePending)
            {
                _orreryFramePending = false;
                FrameOrrery(scene);
            }

            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, OrreryBackground);
            var scaling = new OrreryScaling(orrery.Scale, OrreryScaling.ReachFor(orrery.Distance));
            var camera = new OrreryCamera(orrery.AzimuthDeg, orrery.ElevationDeg, orrery.Distance, contentRect, OrreryCentre(scene, scaling));
            DrawOrreryBackdrop(plannerState, contentRect, camera, viewingTime);
            PushClip(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);

            DrawOrreryOrbits(scene, camera, scaling, dpiScale);
            DrawOrreryBodies(scene, camera, viewingTime, scaling, contentRect, plannerState.Comets, dpiScale);
            PopClip();

            DrawOrreryStrip(contentRect, viewingTime, plannerState.SiteTimeZone,
                plannerState.PlanningDate.HasValue || State.TimeOffset != TimeSpan.Zero);
            RenderTimeMenu(contentRect, BaseFontSize * dpiScale * 0.85f, GuiTheme.Palette.PanelBg, GuiTheme.Palette.BodyText);
            RenderDropUp(SpeedMenu, contentRect, BaseFontSize * dpiScale * 0.85f, GuiTheme.Palette.PanelBg, GuiTheme.Palette.BodyText);

            OrreryFrames++;
            LastOrreryFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        /// <summary>What the backdrop's STARS' alpha is multiplied by, so orbits and labels stay readable over them; the Milky Way has its own opacity.</summary>
        internal const float OrreryBackdropBrightness = 0.35f;

        /// <summary>The Milky Way's opacity behind the orrery, whatever the Sun's altitude at the site (the view is not from the ground).</summary>
        internal const float OrreryMilkyWayAlpha = 0.6f;

        // The sky map's own stars and Milky Way at infinity, drawn FIRST, through the sky's own rendering with the orrery camera's view
        // (SkyOrreryBackdrop): the camera's rotation and the ecliptic's tilt, no translation, so they turn as the camera orbits and never
        // move with the planets; the gnomonic projection at the orrery's focal length; the map's culls at the orrery's field; dimmed, and
        // Night-tinted. Every host draws it through its own RenderSkyMap (the GPU pipelines; on the CPU surface, SkyMapRenderer's field).
        private void DrawOrreryBackdrop(PlannerState plannerState, RectF32 rect, in OrreryCamera camera, DateTimeOffset viewingTime)
        {
            LastOrreryBackdrop = null;
            if (!State.Orrery.ShowBackdrop || plannerState.ObjectDb is not { } db)
            {
                return;
            }
            var view = camera.EquatorialView(out var axis);
            var focal = camera.FocalPx;
            var diagonalDeg = 2 * Math.Atan(Math.Sqrt((rect.Width * rect.Width) + (rect.Height * rect.Height)) / 2 / focal) * 180 / Math.PI;
            var night = GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent : (RGBAColor32?)null;
            var backdrop = new SkyOrreryBackdrop(view, focal, axis, diagonalDeg, OrreryCamera.FieldOfViewDeg,
                OrreryMilkyWayAlpha, OrreryBackdropBrightness, night);
            var (lat, lon) = (plannerState.SiteLatitude, plannerState.SiteLongitude);
            State.Backdrop = backdrop;
            try
            {
                RenderSkyMap(db, rect, viewingTime, lat, lon, Lib.Astrometry.SOFA.SiteContext.Create(lat, lon, viewingTime), SkyMapDrawPhase.Backdrop);
            }
            finally
            {
                State.Backdrop = null;
            }
            LastOrreryBackdrop = backdrop;
        }

        // A play advances the atlas's own scrub offset by what the wall clock has run since the last frame, so the sky shows the same
        // instant when the view goes back to it; the next frame is asked for here, which on the browser is a coalesced repaint.
        /// <summary>
        /// The most wall-clock time one played frame steps by: a frame comes at least ten times a second while the view is on screen,
        /// so a longer gap is time spent elsewhere (another tab, a minimised window), which must not jump the clock by years.
        /// </summary>
        internal static readonly TimeSpan MaxPlayStep = TimeSpan.FromMilliseconds(100);

        // Play stops AT OrreryState.PlayEnd, the end of the ephemeris's range, the frame drawn there: checked before the offset is
        // added to the base, since at ten years a second the sum would leave DateTimeOffset's range within minutes and throw.
        private void AdvancePlay(ITimeProvider timeProvider, DateTimeOffset baseTime)
        {
            var orrery = State.Orrery;
            if (!orrery.Playing)
            {
                return;
            }
            var now = timeProvider.GetTimestamp();
            if (orrery.PlayTimestamp != 0)
            {
                var elapsed = TimeSpan.FromTicks(Math.Min(timeProvider.GetElapsedTime(orrery.PlayTimestamp, now).Ticks, MaxPlayStep.Ticks));
                State.TimeOffset += TimeSpan.FromDays(orrery.DaysPerSecond * elapsed.TotalSeconds);
            }
            var endOffset = OrreryState.PlayEnd - baseTime;
            if (State.TimeOffset >= endOffset)
            {
                State.TimeOffset = endOffset;
                orrery.Playing = false;
                orrery.PlayTimestamp = 0;
                State.NeedsRedraw = true;
                return;
            }
            orrery.PlayTimestamp = now;
            RequestPlanetFrame();
        }

        /// <summary>The camera's nearest and farthest: framing a reach of a quarter of an AU, and of fifty (Neptune's whole orbit and room).</summary>
        internal static readonly double MinOrreryDistance = OrreryCamera.DistanceToFrame(0.25), MaxOrreryDistance = OrreryCamera.DistanceToFrame(50);

        /// <summary>The smallest a body is drawn, design pixels: a dot, never a disc, below which its disc cap would put it.</summary>
        internal const float OrreryDotRadius = 1.5f;

        /// <summary>A comet's head, design pixels: a dot, never a disc.</summary>
        internal const float OrreryCometRadius = 2.5f;

        /// <summary>A disc's on-screen radius is at most this share of its screen distance to the nearest drawn body (to the Sun's edge).</summary>
        internal const float OrreryDiscCapShare = 0.4f;

        // What the camera looks at: the focus body's DRAWN place while the view is centred on it (the Earth's for the Moon, which is
        // drawn beside it), else the Sun.
        private (double X, double Y, double Z) OrreryCentre(OrreryScene scene, in OrreryScaling scaling)
        {
            var orrery = State.Orrery;
            var on = orrery.Focus is CatalogIndex.Moon ? CatalogIndex.Earth : orrery.Focus;
            return orrery.CentredOnFocus && on is { } index && index != CatalogIndex.Sol && scene.TryFind(index, out var body)
                ? scene.Drawn(scaling, body)
                : default;
        }

        private const string OrreryCentreAction = "OrreryCentre";

        // The camera's distance that shows the focus body's orbit (the Earth's for the Moon, the Mars orbit's span for the Sun or no body).
        private void FrameOrrery(OrreryScene scene)
        {
            var orrery = State.Orrery;
            var around = orrery.Focus is CatalogIndex.Moon ? CatalogIndex.Earth : orrery.Focus;
            var reachAu = around is { } index && index != CatalogIndex.Sol && scene.TryFind(index, out var body)
                ? Math.Max(Math.Sqrt((body.X * body.X) + (body.Y * body.Y) + (body.Z * body.Z)), 0.4)
                : 1.6;
            // In AU in both scales: a distance at the reach the camera frames is drawn where it truly is, so the framing is the same.
            orrery.Distance = Math.Clamp(OrreryCamera.DistanceToFrame(reachAu * 1.15), MinOrreryDistance, MaxOrreryDistance);
        }

        // Every orbit in its body's colour (OrreryOrbitColour), the selected planet's in the accent; the comets' from the orbits the
        // scene keeps by index, built when the set was read, never here, in a dim grey. The comet the view is about (the focused one,
        // else the one it was opened on) is drawn LAST, over every other orbit, in the accent at full strength and a pixel thicker.
        private void DrawOrreryOrbits(OrreryScene scene, in OrreryCamera camera, in OrreryScaling scale, float dpiScale)
        {
            _orreryOrbitsDrawn.Clear();
            OrbitSegmentsDrawn = 0;
            var accent = GuiTheme.Palette.Accent;
            var focus = State.Orrery.Focus;
            var highlighted = focus is { } focused && focused.ToCatalog() == Catalog.Comet ? focused : State.Orrery.Comet;
            var thickness = (int)MathF.Max(1, MathF.Round(dpiScale));
            if (scene.Orbits is { } orbits)
            {
                foreach (var (planet, points) in orbits)
                {
                    var colour = planet == focus ? accent : OrreryOrbitColour(planet);
                    DrawOrbitPolyline(scene.ScaledOrbit(points, scale), camera, colour, thickness);
                    _orreryOrbitsDrawn.Add((planet, colour, thickness));
                }
            }
            foreach (var comet in scene.CometSet)
            {
                if (comet.CatalogIndex is { } index && index != highlighted && scene.TryGetCometOrbit(index, out var points))
                {
                    var colour = OrreryOrbitColour(index);
                    DrawOrbitPolyline(scene.ScaledOrbit(points, scale), camera, colour, thickness);
                    _orreryOrbitsDrawn.Add((index, colour, thickness));
                }
            }
            if (highlighted is { } about && scene.TryGetCometOrbit(about, out var aboutPoints))
            {
                DrawOrbitPolyline(scene.ScaledOrbit(aboutPoints, scale), camera, accent, thickness + 1);
                _orreryOrbitsDrawn.Add((about, accent, thickness + 1));
            }
        }

        /// <summary>
        /// How far an orbit's drawn line may stray from a sample it skips, design pixels: a stretch of samples is drawn as ONE segment while
        /// every sample inside it lies within this of the segment, so an orbit costs what its curvature on screen asks, not its sample count.
        /// </summary>
        internal const float OrbitTolerance = 0.35f;

        /// <summary>Segments in the orbit polylines the last orrery frame handed the renderer: what the tests read.</summary>
        internal int OrbitSegmentsDrawn { get; private set; }

        // An orbit's SCALED points (OrreryScene.ScaledOrbit), one polyline per run of them in front of the camera; a point behind it breaks the line rather than wrapping to the far side.
        // A segment wholly outside the view (its box off it) breaks it too, and the samples between two drawn vertices are skipped while
        // each lies within OrbitTolerance of the segment joining them: the line's cost on a GPU host is per segment (each is a box of its
        // own), and thirty comets' 361-point orbits drawn whole made its frame six times dearer (#1611; "A5: the comets" in the plan has
        // the numbers). The skipped samples wait in the run's own buffer past its last vertex, so nothing is allocated.
        private void DrawOrbitPolyline(double[] points, in OrreryCamera camera, RGBAColor32 colour, int thickness)
        {
            var count = points.Length / 3;
            Span<(float X, float Y)> run = count <= 512 ? stackalloc (float, float)[count] : new (float, float)[count];
            var length = 0;
            var pending = 0;
            var tolerance = OrbitTolerance * DpiScale;
            var (left, top) = (_contentX - thickness, _contentY - thickness);
            var (right, bottom) = (_contentX + _contentWidth + thickness, _contentY + _contentHeight + thickness);

            for (var i = 0; i < count; i++)
            {
                if (!camera.TryProject(points[3 * i], points[(3 * i) + 1], points[(3 * i) + 2], out var sx, out var sy))
                {
                    Flush(run, ref length, ref pending);
                    continue;
                }
                if (length == 0)
                {
                    run[length++] = (sx, sy);
                    continue;
                }
                var (px, py) = run[length + pending - 1];
                if (MathF.Max(px, sx) < left || MathF.Min(px, sx) > right || MathF.Max(py, sy) < top || MathF.Min(py, sy) > bottom)
                {
                    // Off the view: the run ends at the last sample, and starts again here.
                    Flush(run, ref length, ref pending);
                    run[length++] = (sx, sy);
                    continue;
                }
                if (!Straight(run, length, pending, sx, sy, tolerance))
                {
                    // The last sample becomes a vertex; the stretch from it starts with this one.
                    run[length] = run[length + pending - 1];
                    length++;
                    pending = 0;
                }
                run[length + pending++] = (sx, sy);
            }
            Flush(run, ref length, ref pending);

            // Whether every sample waiting after the last vertex lies within tolerance of the segment from that vertex to (qx, qy).
            static bool Straight(Span<(float X, float Y)> run, int length, int pending, float qx, float qy, float tolerance)
            {
                var (kx, ky) = run[length - 1];
                var (dx, dy) = (qx - kx, qy - ky);
                var span = MathF.Sqrt((dx * dx) + (dy * dy));
                for (var j = length; j < length + pending; j++)
                {
                    var (wx, wy) = (run[j].X - kx, run[j].Y - ky);
                    var off = span < 1e-3f ? MathF.Sqrt((wx * wx) + (wy * wy)) : MathF.Abs((wx * dy) - (wy * dx)) / span;
                    if (off > tolerance || (wx * dx) + (wy * dy) < 0 || (wx * dx) + (wy * dy) > span * span)
                    {
                        return false;
                    }
                }
                return true;
            }

            void Flush(Span<(float X, float Y)> run, ref int length, ref int pending)
            {
                if (pending > 0)
                {
                    run[length] = run[length + pending - 1];
                    length++;
                    pending = 0;
                }
                if (length > 1)
                {
                    Renderer.DrawPolyline(run[..length], colour, thickness);
                    OrbitSegmentsDrawn += length - 1;
                }
                length = 0;
            }
        }

        /// <summary>
        /// The globe's drawn radius, design pixels: an exaggeration with the planets in their true order of size, the Sun largest.
        /// The sizes are said on screen by the labels, never as a scale.
        /// </summary>
        internal static float OrreryRadius(CatalogIndex body) => body switch
        {
            CatalogIndex.Sol => 14f,
            CatalogIndex.Jupiter => 10f,
            CatalogIndex.Saturn => 8.5f,
            CatalogIndex.Uranus or CatalogIndex.Neptune => 7f,
            CatalogIndex.Earth or CatalogIndex.Venus => 6f,
            CatalogIndex.Mars => 5f,
            CatalogIndex.Mercury => 4f,
            CatalogIndex.Moon => 3.5f,
            _ => 3f,
        };

        private void DrawOrreryBodies(OrreryScene scene, in OrreryCamera camera, DateTimeOffset viewingTime, in OrreryScaling scale,
            RectF32 rect, ICometRepository? comets, float dpiScale)
        {
            _orreryHits.Clear();
            _orreryViews.Clear();
            _orreryLabels.Clear();
            _orreryTails.Clear();
            var bodies = scene.Bodies;
            Span<(int Index, double Depth, double X, double Y, double Z)> order = stackalloc (int, double, double, double, double)[bodies.Length];
            for (var i = 0; i < bodies.Length; i++)
            {
                var (x, y, z) = scene.Drawn(scale, bodies[i]);
                order[i] = (i, camera.Depth(x, y, z), x, y, z);
            }
            // The painter's order: far first.
            order.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

            var palette = GuiTheme.Palette;
            var focus = State.Orrery.Focus;
            var fontSize = BaseFontSize * dpiScale * 0.85f;
            var lineHeight = fontSize * 1.4f;

            // The Moon is placed on SCREEN: a steady reach from the Earth's drawn place (the two radii, a gap and a label line), turned
            // by its geocentric ecliptic longitude, so it goes round the Earth on an even circle once a month. Placed in the world instead,
            // at a fraction of the Earth's distance, it was foreshortened by the camera's elevation, long across the view and short along
            // it, and the floor that kept it off the Earth clipped the short half: 28 to 68 px over a month zoomed in, a spiral in Play.
            // The reach comes in to half the screen gap to the nearest neighbouring orbit as drawn (Venus's or Mars's), so the Moon never
            // sits on another planet's orbit; it is hidden only where even its nearest place, clear of the Earth's disc, would not fit.
            var earthOnScreen = scene.TryFind(CatalogIndex.Earth, out var earthBody)
                && scene.Drawn(scale, earthBody) is var (ex, ey, ez)
                && camera.TryProject(ex, ey, ez, out var esx, out var esy)
                ? (esx, esy)
                : ((float, float)?)null;
            var moonNearest = (OrreryRadius(CatalogIndex.Earth) + OrreryRadius(CatalogIndex.Moon) + 4f) * dpiScale;
            var moonReach = moonNearest + lineHeight;
            if (earthOnScreen is var (gex, gey))
            {
                moonReach = MathF.Min(moonReach, 0.5f * NeighbourOrbitGap(scene, camera, scale, gex, gey));
            }
            LastMoonReach = moonReach;
            var moonOnScreen = earthOnScreen is var (mex, mey) && moonReach >= moonNearest && scene.TryFind(CatalogIndex.Moon, out var moonBody)
                && OrreryMoonScreenDirection(camera, scene.Drawn(scale, earthBody), earthBody, moonBody) is var (ux, uy)
                ? (mex + (ux * moonReach), mey + (uy * moonReach))
                : ((float, float)?)null;
            Span<(int Index, float X, float Y, float Radius, double Depth)> labels = stackalloc (int, float, float, float, double)[bodies.Length];
            var labelCount = 0;
            Span<double> right = stackalloc double[3];
            Span<double> toward = stackalloc double[3];
            Span<double> toSun = stackalloc double[3];

            // First where every body lands, then how big each may be: zoomed out, the orbits shrink into the Sun and a disc at its
            // fixed size would pile onto it and its neighbours, so a disc is at most OrreryDiscCapShare of its screen distance to the
            // nearest drawn body (to the Sun's EDGE: the Sun keeps its size), and a dot of OrreryDotRadius below that. A body in front
            // of the Sun (a transit) is so a dot, never a disc across it. A comet is a dot of its own size (OrreryCometRadius), and never
            // the neighbour a planet's disc is capped by, so the planets' discs are the same size with comets beside them or without.
            Span<(bool Shown, float X, float Y)> placed = stackalloc (bool, float, float)[order.Length];
            Span<bool> isComet = stackalloc bool[order.Length];
            var sunRadius = OrreryRadius(CatalogIndex.Sol) * dpiScale;
            var sunAt = -1;
            for (var slot = 0; slot < order.Length; slot++)
            {
                var (index, _, x, y, z) = order[slot];
                var shown = camera.TryProject(x, y, z, out var sx, out var sy);
                if (bodies[index].Index == CatalogIndex.Moon)
                {
                    // On screen beside the Earth, or not at all.
                    shown = moonOnScreen.HasValue;
                    (sx, sy) = moonOnScreen ?? (sx, sy);
                }
                placed[slot] = (shown, sx, sy);
                isComet[slot] = bodies[index].Index.ToCatalog() == Catalog.Comet;
                if (shown && bodies[index].Index == CatalogIndex.Sol)
                {
                    sunAt = slot;
                }
            }

            // The tails first, under every head and disc.
            for (var slot = 0; slot < order.Length; slot++)
            {
                if (isComet[slot] && placed[slot] is (true, var hx, var hy))
                {
                    var (index, _, x, y, z) = order[slot];
                    DrawCometTail(camera, bodies[index], x, y, z, hx, hy, dpiScale);
                }
            }

            for (var slot = 0; slot < order.Length; slot++)
            {
                var (index, depth, x, y, z) = order[slot];
                var body = bodies[index];
                if (placed[slot] is not (true, var sx, var sy))
                {
                    continue;
                }
                var radius = OrreryRadius(body.Index) * dpiScale;
                if (isComet[slot])
                {
                    radius = OrreryCometRadius * dpiScale;
                }
                else if (body.Index != CatalogIndex.Sol)
                {
                    for (var j = 0; j < order.Length; j++)
                    {
                        if (j == slot || !placed[j].Shown || isComet[j])
                        {
                            continue;
                        }
                        var apart = MathF.Sqrt(((placed[j].X - sx) * (placed[j].X - sx)) + ((placed[j].Y - sy) * (placed[j].Y - sy)));
                        radius = MathF.Min(radius, OrreryDiscCapShare * (j == sunAt ? apart - sunRadius : apart));
                    }
                    radius = MathF.Max(radius, 0);
                }
                var dot = radius <= OrreryDotRadius * dpiScale;
                if (dot)
                {
                    radius = OrreryDotRadius * dpiScale;
                }
                if (isComet[slot])
                {
                    Renderer.FillEllipse(new RectInt(
                        new PointInt((int)MathF.Round(sx + radius), (int)MathF.Round(sy + radius)),
                        new PointInt((int)MathF.Round(sx - radius), (int)MathF.Round(sy - radius))), CometColour);
                    // On screen with an element set a revolution or more old: ask for the current apparition (single-flight, a
                    // no-op once upgraded, so asking per drawn comet per frame is free), only for a comet that is drawn.
                    if (body.ElementsStale && comets is not null && rect.Contains(sx, sy))
                    {
                        comets.RequestCurrentApparition(body.Index);
                    }
                }
                else
                {
                    // The body as the camera sees it along the ray it is drawn on: the camera's right and the direction back toward
                    // it, and the Sun's TRUE direction from the body (which the compressed scale keeps), all turned to J2000 equatorial.
                    camera.RayFrame(sx, sy, right, toward);
                    SolarSystemOrbits.EclipticToEquatorial(right);
                    SolarSystemOrbits.EclipticToEquatorial(toward);
                    toSun[0] = -body.X;
                    toSun[1] = -body.Y;
                    toSun[2] = -body.Z;
                    SolarSystemOrbits.EclipticToEquatorial(toSun);
                    var view = PlanetView.FromEquatorial(body.Index, viewingTime, right, toward, toSun);
                    _orreryViews.Add(view);
                    if (dot || !TryDrawOrreryDisc(view, viewingTime, sx, sy, radius))
                    {
                        Renderer.FillEllipse(new RectInt(
                            new PointInt((int)MathF.Round(sx + radius), (int)MathF.Round(sy + radius)),
                            new PointInt((int)MathF.Round(sx - radius), (int)MathF.Round(sy - radius))), OrreryFlat(body.Index));
                    }
                }

                var selected = body.Index == focus;
                if (selected)
                {
                    var ring = radius + (4f * dpiScale);
                    Renderer.DrawEllipse((sx, sy), (ring, 0f), (0f, ring), palette.Accent, MathF.Max(1f, dpiScale * 1.5f));
                }
                _orreryHits.Add((body.Index, sx, sy, radius));
                labels[labelCount++] = (index, sx, sy, radius, depth);
            }

            DrawOrreryLabels(bodies, labels[..labelCount], focus, fontSize, lineHeight, dpiScale);
        }

        // The labels go down after every disc, so no disc covers one: the Earth's first (it always wins over the Moon's, so the two
        // never trade places as the Moon circles), then the focus body's, then nearest first. A label is placed under its body, else
        // over it, wherever its box (the engine's measure of the name) is clear of every disc but its own and of the labels already
        // placed; a label with nowhere clear is left out, never printed over another body. The focus body's and the Earth's are drawn
        // under their bodies whatever they meet. The Moon's is the exception: always on the far side of the Moon from the Earth, or not at all.
        // Every label lies inside the view (A2 of #1611: the real catalogue's captures cut names at the edges): a body drawn outside it
        // has none, and a candidate box is shifted sideways into the view (trimmed where wider) before its clear test, while one
        // outside it above or below is no candidate at all, so the other side is tried; the forced ones are held inside it too.
        private void DrawOrreryLabels(System.Collections.Immutable.ImmutableArray<OrreryBody> bodies,
            Span<(int Index, float X, float Y, float Radius, double Depth)> labels, CatalogIndex? focus, float fontSize, float lineHeight, float dpiScale)
        {
            // The planets (the Sun and the Moon with them) outrank the comets; of the comets, the focused one and the one the view was
            // opened on come first.
            var opened = State.Orrery.Comet;
            int Rank(CatalogIndex index) => index == CatalogIndex.Earth ? 0
                : index.ToCatalog() == Catalog.Comet ? (index == focus || index == opened ? 3 : 4)
                : index == focus ? 1 : 2;
            labels.Sort((a, b) => Rank(bodies[a.Index].Index).CompareTo(Rank(bodies[b.Index].Index)) is var byRank and not 0
                ? byRank
                : a.Depth.CompareTo(b.Depth));
            (float X, float Y)? earth = null;
            foreach (var (index, x, y, _, _) in labels)
            {
                if (bodies[index].Index == CatalogIndex.Earth)
                {
                    earth = (x, y);
                }
            }
            var palette = GuiTheme.Palette;
            var gap = 3f * dpiScale;
            var context = MeasureContext(scale: DesignScale.One);
            var (left, top, right, bottom) = (_contentX, _contentY, _contentX + _contentWidth, _contentY + _contentHeight);
            RectF32? InView(RectF32 box)
            {
                if (box.Y < top || box.Y + box.Height > bottom)
                {
                    return null;
                }
                var w = MathF.Min(box.Width, right - left);
                return new RectF32(Math.Clamp(box.X, left, right - w), box.Y, w, box.Height);
            }
            foreach (var (index, sx, sy, radius, _) in labels)
            {
                if (sx < left || sx > right || sy < top || sy > bottom)
                {
                    continue;
                }
                var body = bodies[index];
                var reach = radius * (body.Index == CatalogIndex.Saturn ? 2.3f : 1f);
                var width = Layout.Engine.Measure(Layout.Builder.Text(body.Name, fontSize, trim: TextTrim.None),
                    new Layout.Size<float>(float.MaxValue, float.MaxValue), context).Width;
                var below = new RectF32(sx - (width / 2), sy + reach + gap, width, lineHeight);
                var above = new RectF32(sx - (width / 2), sy - reach - gap - lineHeight, width, lineHeight);
                var selected = body.Index == focus;
                RectF32? place;
                if (body.Index == CatalogIndex.Moon && earth is var (ex, ey) && MathF.Sqrt(((sx - ex) * (sx - ex)) + ((sy - ey) * (sy - ey))) is > 0 and var d)
                {
                    // Outward from the Earth: the box is pushed along the Earth-to-Moon direction until it clears the Moon's disc and gap,
                    // and a line further each time it would meet the Earth's label or another disc, at most twice: a Moon come in close
                    // to the Earth (its reach fitted to the gap to the next orbit) keeps its label beyond it.
                    var (ux, uy) = ((sx - ex) / d, (sy - ey) / d);
                    var t = MathF.Min(
                        MathF.Abs(ux) > 1e-6f ? ((width / 2) + reach + gap) / MathF.Abs(ux) : float.MaxValue,
                        MathF.Abs(uy) > 1e-6f ? ((lineHeight / 2) + reach + gap) / MathF.Abs(uy) : float.MaxValue);
                    place = null;
                    for (var step = 0; step < 3 && place is null; step++, t += lineHeight)
                    {
                        var outward = InView(new RectF32(sx + (ux * t) - (width / 2), sy + (uy * t) - (lineHeight / 2), width, lineHeight));
                        place = outward is { } box && LabelIsClear(box, body.Index) ? outward : null;
                    }
                }
                else
                {
                    var (under, over) = (InView(below), InView(above));
                    place = under is { } u && LabelIsClear(u, body.Index) ? under
                        : over is { } o && LabelIsClear(o, body.Index) ? over
                        : selected || body.Index == CatalogIndex.Earth
                            ? under ?? over ?? InView(new RectF32(below.X, Math.Clamp(below.Y, top, MathF.Max(top, bottom - lineHeight)), below.Width, below.Height))
                            : null;
                }
                if (place is not { } rect)
                {
                    continue;
                }
                // The measured box decides the placement; the text is drawn centred in it widened by the gap each side, so a
                // second measure inside DrawText a pixel wider at a fractional scale cannot clip the last letter.
                DrawText(body.Name.AsSpan(), FontPath, rect.X - gap, rect.Y, rect.Width + (2 * gap), rect.Height,
                    fontSize, selected ? palette.Accent : palette.BodyText, TextAlign.Center, TextAlign.Near);
                _orreryLabels.Add((body.Index, rect));
            }
        }

        private bool LabelIsClear(RectF32 rect, CatalogIndex owner)
        {
            static bool Overlap(RectF32 a, RectF32 b)
                => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
            // A comet's head never takes a planet's label (R1 Q1 of PR #1631): every head is placed before any label, so a head on both
            // of a planet's boxes would drop it. A comet's own label keeps clear of the planets' discs and of the other heads.
            var ownerIsComet = owner.ToCatalog() == Catalog.Comet;
            foreach (var (index, x, y, radius) in _orreryHits)
            {
                if (!ownerIsComet && index.ToCatalog() == Catalog.Comet)
                {
                    continue;
                }
                if (index != owner && Overlap(rect, new RectF32(x - radius, y - radius, 2 * radius, 2 * radius)))
                {
                    return false;
                }
            }
            foreach (var (_, placed) in _orreryLabels)
            {
                if (Overlap(rect, placed))
                {
                    return false;
                }
            }
            return true;
        }

        // Which way the ecliptic's anticlockwise sense turns on screen: -1 (up the screen, the camera above the ecliptic) or 1 (below).
        // Kept from the last frame where the plane is seen edge on and the sense cannot be read.
        private float _moonTurnSense = -1f;

        /// <summary>The Moon's screen reach from the Earth in the last frame, pixels: what the tests hold its drawn distance to.</summary>
        internal float LastMoonReach { get; private set; }

        // The unit screen direction from the Earth's drawn place to the Moon: the Moon's geocentric ecliptic longitude, measured from the
        // ecliptic direction the view draws LEVEL (its screen image has no vertical part) and laid off on screen with the plane's sense
        // of turn, so equal steps of longitude are equal steps round the Earth. The level direction comes from the screen images of the
        // ecliptic's x and y axes a thousandth of the camera's distance out from the Earth.
        private (float X, float Y)? OrreryMoonScreenDirection(in OrreryCamera camera, (double X, double Y, double Z) earthDrawn,
            in OrreryBody earth, in OrreryBody moon)
        {
            var (cx, cy, cz) = camera.Eye;
            var reach = 1e-3 * Math.Sqrt(((earthDrawn.X - cx) * (earthDrawn.X - cx)) + ((earthDrawn.Y - cy) * (earthDrawn.Y - cy)) + ((earthDrawn.Z - cz) * (earthDrawn.Z - cz)));
            if (!camera.TryProject(earthDrawn.X, earthDrawn.Y, earthDrawn.Z, out var x0, out var y0)
                || !camera.TryProject(earthDrawn.X + reach, earthDrawn.Y, earthDrawn.Z, out var xa, out var ya)
                || !camera.TryProject(earthDrawn.X, earthDrawn.Y + reach, earthDrawn.Z, out var xb, out var yb))
            {
                return null;
            }
            var (ax, ay, bx, by) = ((double)(xa - x0), (double)(ya - y0), (double)(xb - x0), (double)(yb - y0));
            // cos(level) ay + sin(level) by = 0: the direction whose image is level, turned half round where that image points left.
            var level = Math.Atan2(-ay, by);
            if ((Math.Cos(level) * ax) + (Math.Sin(level) * bx) < 0)
            {
                level += Math.PI;
            }
            // A quarter turn on from it, in the plane: the vertical part of that one's image says which way the plane turns on screen.
            var across = (-Math.Sin(level) * ay) + (Math.Cos(level) * by);
            if (Math.Abs(across) > 1e-3 * Math.Sqrt((ax * ax) + (ay * ay) + (bx * bx) + (by * by)))
            {
                _moonTurnSense = across < 0 ? -1f : 1f;
            }
            var turn = Math.Atan2(moon.Y - earth.Y, moon.X - earth.X) - level;
            return ((float)Math.Cos(turn), _moonTurnSense * (float)Math.Sin(turn));
        }

        // The screen distance from (x, y) to the nearer of the Earth's neighbouring orbits as drawn, Venus's and Mars's: the distance to
        // every segment between two of an orbit's samples that project. Before the orbits are built, the distance to the two planets.
        private static float NeighbourOrbitGap(OrreryScene scene, in OrreryCamera camera, in OrreryScaling scale, float x, float y)
        {
            var best = float.PositiveInfinity;
            var orbits = scene.Orbits;
            foreach (var planet in (ReadOnlySpan<CatalogIndex>)[CatalogIndex.Venus, CatalogIndex.Mars])
            {
                if (orbits is not null && orbits.TryGetValue(planet, out var points))
                {
                    var count = points.Length / 3;
                    var (havePrevious, px, py) = (false, 0f, 0f);
                    for (var i = 0; i <= count; i++)
                    {
                        var j = i % count;
                        var (wx, wy, wz) = OrreryScene.Scaled(scale, points[3 * j], points[(3 * j) + 1], points[(3 * j) + 2]);
                        if (!camera.TryProject(wx, wy, wz, out var sx, out var sy))
                        {
                            havePrevious = false;
                            continue;
                        }
                        if (havePrevious)
                        {
                            best = MathF.Min(best, DistanceToSegment(x, y, px, py, sx, sy));
                        }
                        (havePrevious, px, py) = (true, sx, sy);
                    }
                }
                else if (scene.TryFind(planet, out var body) && scene.Drawn(scale, body) is var (bx, by, bz)
                    && camera.TryProject(bx, by, bz, out var bsx, out var bsy))
                {
                    best = MathF.Min(best, MathF.Sqrt(((bsx - x) * (bsx - x)) + ((bsy - y) * (bsy - y))));
                }
            }
            return best;
        }

        private static float DistanceToSegment(float x, float y, float x0, float y0, float x1, float y1)
        {
            var (dx, dy) = (x1 - x0, y1 - y0);
            var length = (dx * dx) + (dy * dy);
            var t = length == 0 ? 0 : Math.Clamp((((x - x0) * dx) + ((y - y0) * dy)) / length, 0, 1);
            var (ox, oy) = (x0 + (t * dx) - x, y0 + (t * dy) - y);
            return MathF.Sqrt((ox * ox) + (oy * oy));
        }

        // The body's disc: ray-traced by a GPU host in the camera's own frame (DrawPlanetBody: nothing is built, so Play rebuilds
        // nothing), else the CPU sprite of the same view (the CPU surface: the tests and the terminal), turned as the view states its pole.
        private bool TryDrawOrreryDisc(in PlanetView view, DateTimeOffset viewingTime, float sx, float sy, float radius)
        {
            PlanetColourMap? map = null;
            var credit = "";
            if (PlanetTextures.TryGet(view.Body, out var texture, out credit))
            {
                map = texture;
            }
            var night = GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent : (RGBAColor32?)null;
            if (TracesPlanetBodies)
            {
                if (!DrawPlanetBody(view, sx, sy, radius, map, PlanetTextures.Version, OrreryFlat(view.Body), night))
                {
                    return false;
                }
                PlanetBodiesTraced++;
                return true;
            }
            // An unmirrored view: the camera's right, up and back toward it make a right-handed frame (OrreryCamera).
            return OrrerySprites.Get(view.ToAspect(viewingTime), 2 * radius, view.PoleScreenAngleDeg, false, map, credit,
                    OrreryFlat(view.Body), night, GuiTheme.PaletteGeneration, PlanetTextures.Version) is { } sprite
                && DrawPlanetSprite(sprite, sx, sy, radius);
        }

        // A disc still building its sprite: the body's own colour, or under Night the tint its sprite will have. The Earth's is the
        // palette's Info only until its day map has loaded (#1573).
        private static RGBAColor32 OrreryFlat(CatalogIndex body)
            => GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent
                : body == CatalogIndex.Earth ? EarthColour : SkyMapRenderer.GetPlanetColor(body);

        // A comet's tail: a pale fan from the head AWAY from the Sun on screen, as long as the comet is active (OrreryTailLength) and
        // foreshortened by how nearly it points along the line of sight, to no less than OrreryTailMinForeshortening. The direction is
        // the projection of a point a little further out on the comet's radial line (the scaled frame keeps every direction from the
        // Sun), so on screen it lies along the line from the Sun's drawn place through the head. Three nested wedges of one colour build
        // a brighter core, at alphas 0x70, 0x90 and 0xC0 (at half those the real catalogue's tails did not read, A2 of #1611), all
        // triangles (DrawTriangles), the primitive every host fills: Vulkan, WebGL and the CPU surface.
        private void DrawCometTail(in OrreryCamera camera, in OrreryBody body, double x, double y, double z, float sx, float sy, float dpiScale)
        {
            var r = Math.Sqrt((body.X * body.X) + (body.Y * body.Y) + (body.Z * body.Z));
            var length = OrreryTailLength(r) * dpiScale;
            if (length < 1 || !camera.TryProject(x * 1.05, y * 1.05, z * 1.05, out var ox, out var oy))
            {
                return;
            }
            var (dx, dy) = (ox - sx, oy - sy);
            var d = MathF.Sqrt((dx * dx) + (dy * dy));
            if (d < 1e-3f)
            {
                return;
            }
            // sin of the angle between the radial line and the ray from the camera: a tail pointing at the camera is seen end on.
            var (ex, ey, ez) = camera.Eye;
            var (vx, vy, vz) = (x - ex, y - ey, z - ez);
            var dn = Math.Sqrt((x * x) + (y * y) + (z * z)) * Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
            var (cx, cy, cz) = ((y * vz) - (z * vy), (z * vx) - (x * vz), (x * vy) - (y * vx));
            length *= (float)Math.Max(OrreryTailMinForeshortening, Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz)) / dn);
            if (length < 1)
            {
                return;
            }
            var (ux, uy) = (dx / d, dy / d);
            var colour = CometColour;
            Span<float> wedge = stackalloc float[6];
            foreach (var (share, spread, alpha) in (ReadOnlySpan<(float, float, byte)>)[(1f, 0.32f, 0x70), (0.7f, 0.2f, 0x90), (0.4f, 0.1f, 0xC0)])
            {
                var (tipX, tipY) = (sx + (ux * length * share), sy + (uy * length * share));
                var half = MathF.Max(OrreryCometRadius * dpiScale, spread * length * share);
                wedge[0] = sx;
                wedge[1] = sy;
                wedge[2] = tipX - (uy * half);
                wedge[3] = tipY + (ux * half);
                wedge[4] = tipX + (uy * half);
                wedge[5] = tipY - (ux * half);
                Renderer.DrawTriangles(wedge, colour with { Alpha = alpha });
            }
            _orreryTails.Add((body.Index, sx, sy, sx + (ux * length), sy + (uy * length)));
        }

        private readonly List<(CatalogIndex Index, float HeadX, float HeadY, float TipX, float TipY)> _orreryTails = [];

        /// <summary>The comets' tails the last orrery frame drew, head to tip: what the tests read.</summary>
        internal IReadOnlyList<(CatalogIndex Index, float HeadX, float HeadY, float TipX, float TipY)> OrreryTails => _orreryTails;

        private readonly List<(CatalogIndex Index, RGBAColor32 Colour, int Thickness)> _orreryOrbitsDrawn = [];

        /// <summary>The orbits the last orrery frame drew, in the order drawn, each with its colour and thickness: what the tests read.</summary>
        internal IReadOnlyList<(CatalogIndex Index, RGBAColor32 Colour, int Thickness)> OrreryOrbitsDrawn => _orreryOrbitsDrawn;

        /// <summary>The longest a comet's tail is drawn, design pixels: at <see cref="OrreryTailFullAu"/> or closer to the Sun.</summary>
        internal const float OrreryTailMaxLength = 48f;

        /// <summary>The distance from the Sun (AU) inside which a tail is drawn at its full length.</summary>
        internal const double OrreryTailFullAu = 0.7;

        /// <summary>The distance from the Sun (AU) beyond which a comet draws no tail: water ice stops subliming at about 3 AU.</summary>
        internal const double OrreryTailOffAu = 3;

        /// <summary>The shortest a tail is drawn inside <see cref="OrreryTailFloorAu"/>, design pixels: a tail always reads as one.</summary>
        internal const float OrreryTailMinLength = 14f;

        /// <summary>The distance from the Sun (AU) out to which a tail keeps <see cref="OrreryTailMinLength"/>; past it the floor tapers to nothing at <see cref="OrreryTailOffAu"/>.</summary>
        internal const double OrreryTailFloorAu = 2.5;

        /// <summary>The least share of its length a tail keeps when seen end on, so a tail pointing at the camera is short, never lost.</summary>
        internal const double OrreryTailMinForeshortening = 0.4;

        /// <summary>
        /// A comet's tail length at heliocentric distance <paramref name="r"/> AU, design pixels before foreshortening: its activity as
        /// the sunlight it gets, <c>1/r^2</c>, less what it gets at <see cref="OrreryTailOffAu"/>, as a share of the activity at
        /// <see cref="OrreryTailFullAu"/> (held at 1 closer in), its square root times <see cref="OrreryTailMaxLength"/>: 33 px at
        /// 1 AU, 20 at 1.5 (A2 of #1611; the linear share gave 22 and 8, and the real comets' tails did not read). Never below
        /// <see cref="OrreryTailMinLength"/> inside <see cref="OrreryTailFloorAu"/>, the floor then falling linearly to nothing at 3 AU,
        /// so a tail never pops in or out.
        /// </summary>
        internal static float OrreryTailLength(double r)
        {
            static double Activity(double au) => Math.Max(0, (1 / (au * au)) - (1 / (OrreryTailOffAu * OrreryTailOffAu)));
            var share = Math.Min(1, Activity(Math.Max(r, 1e-3)) / Activity(OrreryTailFullAu));
            var floor = OrreryTailMinLength * Math.Clamp((OrreryTailOffAu - r) / (OrreryTailOffAu - OrreryTailFloorAu), 0, 1);
            return (float)Math.Max(OrreryTailMaxLength * Math.Sqrt(share), floor);
        }

        /// <summary>
        /// The colour <paramref name="body"/>'s orbit is drawn in when it is not the selected one (A1 of #1611): a planet's is its body's
        /// own (<see cref="OrreryFlat"/>, the Earth's the palette's Info) taken a fifth toward the dim text, at the dim alpha 0x70; a
        /// comet's is a dim grey from the palette (<see cref="CometOrbitColour"/>) at 0x40, always weaker than a planet's (A2 of #1611).
        /// Under Night every orbit is the palette's tint, as every body is, so no orbit spends blue.
        /// </summary>
        internal static RGBAColor32 OrreryOrbitColour(CatalogIndex body)
        {
            var palette = GuiTheme.Palette;
            var night = GuiTheme.State == UiThemeState.Night;
            return body.ToCatalog() == Catalog.Comet
                ? (night ? palette.Accent : CometOrbitColour) with { Alpha = 0x40 }
                : (night ? palette.Accent : GuiTheme.Mix(palette.DimText, OrreryFlat(body), 0.8f)) with { Alpha = 0x70 };
        }

        private const string OrreryScaleAction = "OrreryScale";

        private void DrawOrreryStrip(RectF32 rect, DateTimeOffset viewingTime, TimeSpan siteTimeZone, bool isTimeShifted)
        {
            var dpiScale = DpiScale;
            var fontSize = BaseFontSize * dpiScale * 0.85f;
            var stripH = 24f * dpiScale;
            var orrery = State.Orrery;
            var padX = 6f * dpiScale;
            var palette = GuiTheme.Palette;
            var fill = palette.PanelBg;
            var ink = palette.BodyText;

            var localTime = viewingTime.ToOffset(siteTimeZone);
            var timeText = $"{localTime:yyyy-MM-dd HH:mm}";
            if (State.TimeOffset != TimeSpan.Zero)
            {
                timeText += $"  ({SkyMapState.FormatOffset(State.TimeOffset)})";
            }

            Layout.Node Button(string label, string action, Action onClick, string tooltip)
                => Layout.Builder.Text(label, fontSize, ink, TextAlign.Center, TextAlign.Center)
                    .PadX(padX)
                    .HStar()
                    .Bg(fill).BgHover(GuiTheme.Hover(fill))
                    .Clickable(new HitResult.ButtonHit(action), _ => onClick())
                    .WithTooltip(tooltip);

            // The labels give way to the buttons, never the other way round: each is a content-capped Star, read in full while the
            // strip has room (the hint takes the slack) and trimming its end toward this floor once the buttons have taken the row,
            // so at 1.5x on a narrow view every button still shows (#1584). Device pixels, as the tree is.
            var labelMin = OrreryLabelMinEm * fontSize;

            // The distance scale is ONE toggle naming the mode in force, sized by the longer label so a press does not move the strip.
            var auto = orrery.Scale == OrreryScale.Auto;
            var scaleToggle = Layout.Builder.Text(auto ? "Distances: auto" : "Distances: true", fontSize, ink,
                    TextAlign.Center, TextAlign.Center, TextTrim.End, widthSample: "Distances: auto")
                .PadX(padX).WStarUpToContent(min: labelMin).HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit(OrreryScaleAction),
                    _ => SwitchOrreryScale(auto ? OrreryScale.True : OrreryScale.Auto))
                .WithTooltip(auto ? "Distances compressed as far as the framing needs; press for true distances" : "True distances; press to compress them as the framing needs");

            // What the camera looks at, ONE toggle naming it, while a body other than the Sun is in focus; sized by the longer label.
            Layout.Node? centreToggle = null;
            if (orrery.Focus is { } focus && focus != CatalogIndex.Sol && OrreryScene.TryFind(focus, out var focusBody))
            {
                var centred = orrery.CentredOnFocus;
                var onBody = $"Centre: {focusBody.Name}";
                centreToggle = Layout.Builder.Text(centred ? onBody : "Centre: Sun", fontSize, ink,
                        TextAlign.Center, TextAlign.Center, TextTrim.End, widthSample: onBody.Length >= "Centre: Sun".Length ? onBody : "Centre: Sun")
                    .PadX(padX).WStarUpToContent(min: labelMin).HStar()
                    .Bg(fill).BgHover(GuiTheme.Hover(fill))
                    .Clickable(new HitResult.ButtonHit(OrreryCentreAction), _ => ToggleOrreryCentre())
                    .WithTooltip(centred ? $"The camera follows {focusBody.Name}; press to look at the Sun" : $"The camera looks at the Sun; press to follow {focusBody.Name}");
            }

            // How fast Play runs, ONE label button beside it naming the speed in force, its mark saying a press opens the speeds
            // (a drop-up, as the time's landmarks are). Sized by the widest name, so a pick does not move what follows. The long form
            // ("1 week/s") is a content-capped Star like the other labels; where the strip leaves it short of its width the button
            // takes the compact form instead ("1w/s", no mark), Auto at its own width sample, so it names the speed whole at any
            // scale and never reads "1..." (round 2 of #1613).
            var speedName = PlaySpeedName(orrery.DaysPerSecond);
            var speedLabel = $"{speedName}/s";
            Layout.Node SpeedButton(bool compact)
            {
                var text = compact
                    ? Layout.Builder.Text($"{PlaySpeedShortName(orrery.DaysPerSecond)}/s", fontSize, ink, TextAlign.Center, TextAlign.Center,
                        widthSample: PlaySpeedCompactWidthSample)
                    : Layout.Builder.Text(speedLabel, fontSize, ink, TextAlign.Center, TextAlign.Center, TextTrim.End,
                        widthSample: PlaySpeedWidthSample).WStarUpToContent();
                // The compact form drops the mark: with it the button took 78 px at 1.5x in the app's face and the time kept 29 px
                // past its date and time, short of its two ems; the tooltip and the drop-up still say what a press does.
                var button = (compact
                        ? Layout.Builder.HStack(text)
                        : Layout.Builder.HStack(text, FormRowLayout.StepMark("▲", fontSize, ink)).WithGap(4f * dpiScale))
                    .CrossCenter()
                    .PadX(padX);
                return (compact ? button : button.WStarUpToContent(min: labelMin)).HStar()
                    .Bg(fill).BgHover(GuiTheme.Hover(fill))
                    .Clickable(new HitResult.ButtonHit(OrrerySpeedAction), _ => OpenSpeedMenu(_speedButtonRect, fontSize))
                    .WithTooltip($"How fast Play runs: a second is {speedName} on the atlas's clock");
            }

            // ONE tree over the whole rect: the strip is the bottom row of it, its fill and its content one arranged box.
            Layout.Node Strip(bool compactSpeed) => Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), Layout.Builder.HStack(
                [
                    Button("Sky", "OrreryClose", CloseOrrery, "Back to the sky"),
                    scaleToggle,
                    .. centreToggle is { } centre ? (ReadOnlySpan<Layout.Node>)[centre] : [],
                    // The wider label sized once, so Play and Pause do not move what follows.
                    Layout.Builder.Text(orrery.Playing ? "Pause" : "Play", fontSize, ink, TextAlign.Center, TextAlign.Center, widthSample: "Pause")
                        .PadX(padX).HStar()
                        .Bg(fill).BgHover(GuiTheme.Hover(fill))
                        .Clickable(new HitResult.ButtonHit("OrreryPlay"), _ => ToggleOrreryPlay())
                        .WithTooltip($"Run time at {speedName} a second"),
                    SpeedButton(compactSpeed),
                    Layout.Builder.Text("Drag to orbit, wheel to zoom", fontSize, ink, TextAlign.Near, TextAlign.Center).PadX(padX).WStar().HStar(),
                    // Direct children of the strip, so the time (a content-capped Star) gives way here as the labels do.
                    .. BuildTimeControls(timeText, isTimeShifted ? palette.Accent : ink, fontSize, dpiScale, fill, ink).AsSpan(),
                ])
                // The time controls' own gap on the sky strip (TimeControlsGroup). At 4 the thirteen gaps took 78 px of a 900 px
                // strip at 1.5x, and in the app's own face (DejaVu Sans) the time kept 23 px past its date and time.
                .WithGap(TimeControlGapUnits * dpiScale)
                .PadX(8f * dpiScale)
                .Bg(fill)
                .RowH(stripH));

            // Arranged with the long form first; the engine's answer decides the form, and only the winner is painted. Device pixels,
            // like the sky's strip.
            var arranged = ArrangeLayout(Strip(compactSpeed: false), rect, scale: DesignScale.One);
            if (!SpeedReadsWhole(arranged, speedLabel))
            {
                arranged = ArrangeLayout(Strip(compactSpeed: true), rect, scale: DesignScale.One);
            }
            PaintLayout(arranged, scale: DesignScale.One);
            RememberTimeButton(arranged);
        }

        /// <summary>
        /// The speed button's width sample, wider than every one of the fifteen names in the label's form: two digits before the
        /// widest unit word (the m of "months" is the widest letter). A sample exactly as wide as "4 months/s" still trimmed it, as
        /// it did "12 hours/s" and "10 years/s"; <c>EachSpeedRunsItsSpanInAPlayedSecond</c> holds every name untrimmed at 1x.
        /// </summary>
        internal const string PlaySpeedWidthSample = "00 months/s";

        /// <summary>
        /// The compact form's width sample, at least as wide as every short name in the label's form: "2mo/s" is the widest in a
        /// proportional face (the m), and every one is five characters or fewer in a monospace one.
        /// </summary>
        internal const string PlaySpeedCompactWidthSample = "0mo/s";

        /// <summary>
        /// Whether the arranged long-form speed leaf got its whole width: its width SAMPLE's, not the name in force, on purpose, so the
        /// form is a property of the strip (its scale, face and the rest of the row) and never of the pick, and a pick from "1 week"
        /// to "10 years" does not swap the form under the pointer.
        /// </summary>
        /// <remarks>An escape hatch: the engine has no node that offers a shorter form of its text (CollapseBelow only removes one),
        /// so the strip is arranged twice when short. A text node with alternates in DIR.Lib would let the painter's own fit choose.</remarks>
        private bool SpeedReadsWhole(ImmutableArray<Layout.ArrangedNode<float>> arranged, string speedLabel)
        {
            foreach (var node in arranged)
            {
                if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value == speedLabel)
                {
                    return node.Bounds.Width >= StripContentWidth(node.Node) - 0.5f;
                }
            }
            return false;
        }

        /// <summary>The compact name a rate goes by in a short strip: its entry's <see cref="OrreryPlaySpeed.ShortName"/>, else its days.</summary>
        internal static string PlaySpeedShortName(double daysPerSecond)
        {
            foreach (var speed in OrreryState.PlaySpeeds)
            {
                if (speed.DaysPerSecond == daysPerSecond)
                {
                    return speed.ShortName;
                }
            }
            return $"{daysPerSecond:0.##}d";
        }

        /// <summary>The name a rate goes by in the strip: its entry in <see cref="OrreryState.PlaySpeeds"/>, else its days.</summary>
        internal static string PlaySpeedName(double daysPerSecond)
        {
            foreach (var speed in OrreryState.PlaySpeeds)
            {
                if (speed.DaysPerSecond == daysPerSecond)
                {
                    return speed.Name;
                }
            }
            return $"{daysPerSecond:0.##} days";
        }

        /// <summary>
        /// Opens the speeds above <paramref name="anchor"/> (the speed button's arranged rect, device pixels), the one in force
        /// highlighted. A pick sets the rate and nothing else: Play keeps running if it was.
        /// </summary>
        private void OpenSpeedMenu(RectF32 anchor, float fontSize)
        {
            var orrery = State.Orrery;
            var items = ImmutableArray.CreateBuilder<DropdownItem<double>>(OrreryState.PlaySpeeds.Length);
            var current = -1;
            foreach (var speed in OrreryState.PlaySpeeds)
            {
                if (speed.DaysPerSecond == orrery.DaysPerSecond)
                {
                    current = items.Count;
                }
                items.Add(new DropdownItem<double>($"1 second = {speed.Name}", speed.DaysPerSecond));
            }
            // As wide as the widest row MEASURES, a row being its label padded half an em each side as the dropdown builds it, and
            // never narrower than the button it opened from. The half em mirrors DIR.Lib's Layout.Builder.Dropdown rowPadding: a
            // stopgap until Dropdown can size its list to its rows itself (which would also retire OpenTimeMenu's eight ems).
            var width = anchor.Width;
            foreach (var item in items)
            {
                width = MathF.Max(width, StripContentWidth(Layout.Builder.Text(item.Label, fontSize).PadX(fontSize * 0.5f)));
            }
            SpeedMenu.Open(anchor.X, anchor.Y, width, items.MoveToImmutable(), item =>
            {
                orrery.DaysPerSecond = item.Value;
                State.NeedsRedraw = true;
            });
            SpeedMenu.HighlightIndex = current;
        }

        /// <summary>The narrowest a strip label (the distance scale, the centre) gives way to under pressure, in ems of its font.</summary>
        internal const float OrreryLabelMinEm = 3f;

        /// <summary>How wide a strip node's own content measures, in the device pixels the strip is arranged in (its cap as a
        /// content-capped Star): the tab's own measure context, so a test reads a label's content without building one, and the
        /// strip decides the speed button's form by it (<see cref="SpeedReadsWhole"/>).</summary>
        internal float StripContentWidth(Layout.Node node)
            => Layout.Engine.Measure(node, new Layout.Size<float>(float.MaxValue, float.MaxValue), MeasureContext(scale: DesignScale.One)).Width;

        /// <summary>The text a strip leaf shows in its arranged width: what the painter's own fit (its <see cref="TextTrim"/>) makes of
        /// it, so a test reads a trimmed label as drawn.</summary>
        internal string StripShownText(Layout.ArrangedNode<float> arranged)
        {
            if (arranged.Node is not Layout.Node.Leaf { Content: Layout.Content.Text text })
            {
                return "";
            }
            var ctx = MeasureContext(scale: DesignScale.One);
            return TextFit.ForWidth(Renderer, text.Value, ctx.FontPath, ctx.Fallback, text.FontSize * ctx.FontScale, arranged.Bounds.Width, text.Trim).Text;
        }

        private void SwitchOrreryScale(OrreryScale scale)
        {
            if (State.Orrery.Scale == scale)
            {
                return;
            }
            // No re-framing: the camera's distance is AU in both scales, and the reach it frames is drawn where it is in both.
            State.Orrery.Scale = scale;
            State.NeedsRedraw = true;
        }

        private void ToggleOrreryCentre()
        {
            // No re-framing: the distance is the camera's from what it looks at, so the view keeps its zoom.
            State.Orrery.CentredOnFocus = !State.Orrery.CentredOnFocus;
            State.NeedsRedraw = true;
        }

        private void ToggleOrreryPlay()
        {
            var orrery = State.Orrery;
            orrery.Playing = !orrery.Playing;
            orrery.PlayTimestamp = 0;
            State.NeedsRedraw = true;
            if (orrery.Playing)
            {
                RequestPlanetFrame();
            }
        }

        private bool HandleOrreryInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, _, _, _) => ScrollOrrery(scrollY),
            InputEvent.Pinch p => PinchOrrery(p.Scale),
            InputEvent.PinchEnd => EndOrreryPinch(),
            InputEvent.MouseDown(var x, var y, _, _, _) => BeginOrreryDrag(x, y),
            InputEvent.MouseMove(var x, var y) => DragOrrery(x, y),
            InputEvent.MouseUp(var x, var y, _) => EndOrreryDrag(x, y),
            InputEvent.KeyDown(var key, var modifiers) => HandleOrreryKey(key, modifiers),
            _ => false,
        };

        // A pinch is handled as the sky's (HandlePinchZoom): its first event undoes the drag the first finger's press began and cancels
        // it, so the release is no click; and a wheel event during it or just after is the touchpad's duplicate of the same gesture
        // (the shared _sincePinch grace), so the zoom is applied once.
        private bool PinchOrrery(float scale)
        {
            _sincePinch.Restart();
            var orrery = State.Orrery;
            if (!orrery.Pinching)
            {
                orrery.Pinching = true;
                if (orrery.Dragging)
                {
                    (orrery.AzimuthDeg, orrery.ElevationDeg) = orrery.DragStartAngles;
                    orrery.Dragging = false;
                }
            }
            return ZoomOrrery(1.0 / scale);
        }

        private bool EndOrreryPinch()
        {
            State.Orrery.Pinching = false;
            return true;
        }

        private bool ScrollOrrery(float scrollY)
            => State.Orrery.Pinching || (_sincePinch.IsRunning && _sincePinch.ElapsedMilliseconds < PinchWheelGraceMs)
                || ZoomOrrery(Math.Pow(0.85, scrollY));

        private bool ZoomOrrery(double factor)
        {
            State.Orrery.Distance = Math.Clamp(State.Orrery.Distance * factor, MinOrreryDistance, MaxOrreryDistance);
            State.NeedsRedraw = true;
            return true;
        }

        private bool BeginOrreryDrag(float x, float y)
        {
            var orrery = State.Orrery;
            if (orrery.Pinching)
            {
                return true;
            }
            orrery.Dragging = true;
            orrery.DragStart = (x, y);
            orrery.DragStartAngles = (orrery.AzimuthDeg, orrery.ElevationDeg);
            return true;
        }

        /// <summary>Degrees the camera turns per pixel of drag, before the DPI scale.</summary>
        internal const double OrreryDegreesPerPixel = 0.35;

        private bool DragOrrery(float x, float y)
        {
            var orrery = State.Orrery;
            if (!orrery.Dragging)
            {
                return false;
            }
            var perPixel = OrreryDegreesPerPixel / Math.Max(DpiScale, 0.5f);
            orrery.AzimuthDeg = orrery.DragStartAngles.Az - ((x - orrery.DragStart.X) * perPixel);
            orrery.ElevationDeg = Math.Clamp(orrery.DragStartAngles.El + ((y - orrery.DragStart.Y) * perPixel),
                -OrreryCamera.MaxElevationDeg, OrreryCamera.MaxElevationDeg);
            State.NeedsRedraw = true;
            return true;
        }

        // A press that did not move is a click: it puts the highlight on the body under it (the atlas's selection is left as it is).
        private bool EndOrreryDrag(float x, float y)
        {
            var orrery = State.Orrery;
            if (!orrery.Dragging)
            {
                return false;
            }
            orrery.Dragging = false;
            var slop = 4f * DpiScale;
            if (MathF.Abs(x - orrery.DragStart.X) <= slop && MathF.Abs(y - orrery.DragStart.Y) <= slop)
            {
                var best = float.MaxValue;
                CatalogIndex? picked = null;
                foreach (var (index, bx, by, radius) in _orreryHits)
                {
                    var d = MathF.Sqrt(((x - bx) * (x - bx)) + ((y - by) * (y - by)));
                    if (d <= radius + (6f * DpiScale) && d < best)
                    {
                        (best, picked) = (d, index);
                    }
                }
                if (picked is { } pick)
                {
                    orrery.Focus = pick;
                    // The Sun has nothing to centre on but itself: the camera goes back to it, and the toggle with it.
                    if (pick == CatalogIndex.Sol)
                    {
                        orrery.CentredOnFocus = false;
                    }
                }
            }
            State.NeedsRedraw = true;
            return true;
        }

        // The time keys work here as on the sky (Up/Down an hour, Shift ten minutes, Left/Right a day, 0 back to live); the sky's layer
        // letters do not, since there are no layers to toggle.
        private bool HandleOrreryKey(InputKey key, InputModifier modifiers)
        {
            var fineStep = (modifiers & InputModifier.Shift) != 0;
            switch (key)
            {
                case InputKey.Up:
                    StepTime(fineStep ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(1));
                    return true;
                case InputKey.Down:
                    StepTime(fineStep ? TimeSpan.FromMinutes(-10) : TimeSpan.FromHours(-1));
                    return true;
                case InputKey.Right:
                    StepTime(TimeSpan.FromDays(1));
                    return true;
                case InputKey.Left:
                    StepTime(TimeSpan.FromDays(-1));
                    return true;
                case InputKey.D0:
                    ResetTimeOffset();
                    return true;
                default:
                    return false;
            }
        }
    }
}
