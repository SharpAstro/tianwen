using System;
using System.Collections.Generic;
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
    /// exaggerated, labelled size, lit from the Sun as the CAMERA sees it (<see cref="PhysicalEphemeris.ComputeSeenAlong(CatalogIndex, DateTimeOffset, ReadOnlySpan{double}, ReadOnlySpan{double})"/>).
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
        // to black, the Earth the palette's Info, a comet that halfway to the body text.
        private static RGBAColor32 OrreryBackground => GuiTheme.Mix(new RGBAColor32(0, 0, 0, 0xFF), GuiTheme.Palette.ContentBg, 0.5f);
        private static RGBAColor32 EarthColour => GuiTheme.Palette.Info;
        private static RGBAColor32 CometColour => GuiTheme.Mix(GuiTheme.Palette.Info, GuiTheme.Palette.BodyText, 0.5f);

        /// <summary>Opens the orrery on the selected body when it is one of the Sun's (the panel's button, Alt+S); the selection is kept.</summary>
        internal void OpenOrrery()
        {
            var orrery = State.Orrery;
            orrery.Focus = State.Search.InfoPanel is { Index: { } index } && index.IsSolarSystemObject ? index : null;
            orrery.Comet = orrery.Focus is { } focus && focus.ToCatalog() == Catalog.Comet ? focus : null;
            orrery.IsOpen = true;
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

            AdvancePlay(timeProvider);
            var viewingTime = ResolveViewingTime(plannerState, timeProvider);

            CometElements? comet = null;
            if (orrery.Comet is { } cometIndex
                && plannerState.Comets is { } comets && comets.TryGet(cometIndex, out var elements))
            {
                comet = elements;
            }
            var scene = OrreryScene;
            scene.Update(viewingTime, comet);
            if (_orreryFramePending)
            {
                _orreryFramePending = false;
                FrameOrrery(scene);
            }

            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, OrreryBackground);
            var camera = new OrreryCamera(orrery.AzimuthDeg, orrery.ElevationDeg, orrery.Distance, contentRect);
            DrawOrreryBackdrop(plannerState, contentRect, camera, viewingTime);
            PushClip(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
            var scaling = new OrreryScaling(orrery.Scale, OrreryScaling.ReachFor(orrery.Distance));
            DrawOrreryOrbits(scene, camera, comet, scaling, dpiScale);
            DrawOrreryBodies(scene, camera, viewingTime, scaling, dpiScale);
            PopClip();

            DrawOrreryStrip(contentRect, viewingTime, plannerState.SiteTimeZone,
                plannerState.PlanningDate.HasValue || State.TimeOffset != TimeSpan.Zero);
            RenderTimeMenu(contentRect, BaseFontSize * dpiScale * 0.85f, GuiTheme.Palette.PanelBg, GuiTheme.Palette.BodyText);

            OrreryFrames++;
            LastOrreryFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        /// <summary>What the backdrop's stars' and Milky Way's alpha is multiplied by, so orbits and labels stay readable over it.</summary>
        internal const float OrreryBackdropBrightness = 0.2f;

        /// <summary>The Milky Way's opacity behind the orrery, whatever the Sun's altitude at the site (the view is not from the ground).</summary>
        internal const float OrreryMilkyWayAlpha = 0.35f;

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

        private void AdvancePlay(ITimeProvider timeProvider)
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
            orrery.PlayTimestamp = now;
            RequestPlanetFrame();
        }

        // The camera's distance that shows the focus body's orbit (the Earth's for the Moon, the Mars orbit's span for the Sun or no body).
        private void FrameOrrery(OrreryScene scene)
        {
            var orrery = State.Orrery;
            var around = orrery.Focus is CatalogIndex.Moon ? CatalogIndex.Earth : orrery.Focus;
            var reachAu = around is { } index && index != CatalogIndex.Sol && scene.TryFind(index, out var body)
                ? Math.Max(Math.Sqrt((body.X * body.X) + (body.Y * body.Y) + (body.Z * body.Z)), 0.4)
                : 1.6;
            // In AU in both scales: a distance at the reach the camera frames is drawn where it truly is, so the framing is the same.
            orrery.Distance = OrreryCamera.DistanceToFrame(reachAu * 1.15);
        }

        private void DrawOrreryOrbits(OrreryScene scene, in OrreryCamera camera, in CometElements? comet, in OrreryScaling scale, float dpiScale)
        {
            var palette = GuiTheme.Palette;
            var dim = palette.DimText with { Alpha = 0x70 };
            var focus = State.Orrery.Focus;
            var thickness = (int)MathF.Max(1, MathF.Round(dpiScale));
            if (scene.Orbits is { } orbits)
            {
                foreach (var (planet, points) in orbits)
                {
                    DrawOrbitPolyline(points, camera, scale, planet == focus ? palette.Accent : dim, thickness);
                }
            }
            if (comet is { } el)
            {
                DrawOrbitPolyline(scene.CometOrbit(el), camera, scale, el.CatalogIndex == focus ? palette.Accent : CometColour with { Alpha = 0x90 }, thickness);
            }
        }

        // One polyline per run of points in front of the camera; a point behind it breaks the line rather than wrapping to the far side.
        private void DrawOrbitPolyline(double[] points, in OrreryCamera camera, in OrreryScaling scale, RGBAColor32 colour, int thickness)
        {
            var count = points.Length / 3;
            Span<(float X, float Y)> run = count <= 512 ? stackalloc (float, float)[count] : new (float, float)[count];
            var length = 0;
            for (var i = 0; i < count; i++)
            {
                var (x, y, z) = OrreryScene.Scaled(scale, points[3 * i], points[(3 * i) + 1], points[(3 * i) + 2]);
                if (camera.TryProject(x, y, z, out var sx, out var sy))
                {
                    run[length++] = (sx, sy);
                    continue;
                }
                if (length > 1)
                {
                    Renderer.DrawPolyline(run[..length], colour, thickness);
                }
                length = 0;
            }
            if (length > 1)
            {
                Renderer.DrawPolyline(run[..length], colour, thickness);
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

        private void DrawOrreryBodies(OrreryScene scene, in OrreryCamera camera, DateTimeOffset viewingTime, in OrreryScaling scale, float dpiScale)
        {
            _orreryHits.Clear();
            _orreryLabels.Clear();
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
            var (eyeX, eyeY, eyeZ) = camera.Eye;

            // The Earth's place on screen, for the Moon's: at any scale that shows the planets the Moon's drawn offset is a few pixels,
            // so it is held at least the two radii and a label line out from the Earth, along their projected direction.
            var earthOnScreen = scene.TryFind(CatalogIndex.Earth, out var earthBody)
                && scene.Drawn(scale, earthBody) is var (ex, ey, ez)
                && camera.TryProject(ex, ey, ez, out var esx, out var esy)
                ? (esx, esy)
                : ((float, float)?)null;
            var moonReach = ((OrreryRadius(CatalogIndex.Earth) + OrreryRadius(CatalogIndex.Moon) + 4f) * dpiScale) + lineHeight;
            Span<(int Index, float X, float Y, float Radius, double Depth)> labels = stackalloc (int, float, float, float, double)[bodies.Length];
            var labelCount = 0;
            Span<double> toObserver = stackalloc double[3];
            Span<double> heliocentric = stackalloc double[3];
            Span<double> pole = stackalloc double[3];
            foreach (var (index, depth, x, y, z) in order)
            {
                var body = bodies[index];
                if (!camera.TryProject(x, y, z, out var sx, out var sy))
                {
                    continue;
                }
                if (body.Index == CatalogIndex.Moon && earthOnScreen is var (earthX, earthY))
                {
                    var (dx, dy) = (sx - earthX, sy - earthY);
                    var d = MathF.Sqrt((dx * dx) + (dy * dy));
                    if (d < moonReach)
                    {
                        (dx, dy, d) = d < 1e-3f ? (1f, 0f, 1f) : (dx, dy, d);
                        (sx, sy) = (earthX + (dx / d * moonReach), earthY + (dy / d * moonReach));
                    }
                }
                var radius = OrreryRadius(body.Index) * dpiScale;
                var isComet = body.Index.ToCatalog() == Catalog.Comet;
                if (isComet)
                {
                    DrawCometMark(camera, x, y, z, sx, sy, radius);
                }
                else
                {
                    toObserver[0] = eyeX - x;
                    toObserver[1] = eyeY - y;
                    toObserver[2] = eyeZ - z;
                    var norm = Math.Sqrt((toObserver[0] * toObserver[0]) + (toObserver[1] * toObserver[1]) + (toObserver[2] * toObserver[2]));
                    for (var k = 0; k < 3; k++)
                    {
                        toObserver[k] /= norm;
                    }
                    SolarSystemOrbits.EclipticToEquatorial(toObserver);
                    heliocentric[0] = body.X;
                    heliocentric[1] = body.Y;
                    heliocentric[2] = body.Z;
                    SolarSystemOrbits.EclipticToEquatorial(heliocentric);
                    var aspect = PhysicalEphemeris.ComputeSeenAlong(body.Index, viewingTime, heliocentric, toObserver);
                    PhysicalEphemeris.PoleDirection(body.Index, viewingTime, pole);
                    SolarSystemOrbits.EquatorialToEcliptic(pole);
                    var poleScreen = OrreryPoleScreenAngle(camera, x, y, z, pole, norm);
                    if (!TryDrawOrrerySprite(aspect, sx, sy, radius, poleScreen))
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
        private void DrawOrreryLabels(System.Collections.Immutable.ImmutableArray<OrreryBody> bodies,
            Span<(int Index, float X, float Y, float Radius, double Depth)> labels, CatalogIndex? focus, float fontSize, float lineHeight, float dpiScale)
        {
            int Rank(CatalogIndex index) => index == CatalogIndex.Earth ? 0 : index == focus ? 1 : 2;
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
            foreach (var (index, sx, sy, radius, _) in labels)
            {
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
                    // Outward from the Earth: the box is pushed along the Earth-to-Moon direction until it clears the Moon's disc and gap.
                    var (ux, uy) = ((sx - ex) / d, (sy - ey) / d);
                    var t = MathF.Min(
                        MathF.Abs(ux) > 1e-6f ? ((width / 2) + reach + gap) / MathF.Abs(ux) : float.MaxValue,
                        MathF.Abs(uy) > 1e-6f ? ((lineHeight / 2) + reach + gap) / MathF.Abs(uy) : float.MaxValue);
                    var outward = new RectF32(sx + (ux * t) - (width / 2), sy + (uy * t) - (lineHeight / 2), width, lineHeight);
                    place = LabelIsClear(outward, body.Index) ? outward : null;
                }
                else
                {
                    place = LabelIsClear(below, body.Index) ? below
                        : LabelIsClear(above, body.Index) ? above
                        : selected || body.Index == CatalogIndex.Earth ? below : null;
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
            foreach (var (index, x, y, radius) in _orreryHits)
            {
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

        // The screen direction of the body's north pole, degrees from +x toward +y (y down), as PlanetDiscSprite.PoleScreenAngle states
        // one: the pole projected from a point a thousandth of the camera's distance along it. Straight up when the pole points at the camera.
        private static double OrreryPoleScreenAngle(in OrreryCamera camera, double x, double y, double z, ReadOnlySpan<double> pole, double distance)
        {
            var step = distance * 1e-3;
            if (camera.TryProject(x, y, z, out var x0, out var y0)
                && camera.TryProject(x + (pole[0] * step), y + (pole[1] * step), z + (pole[2] * step), out var x1, out var y1)
                && ((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)) > 1e-12f)
            {
                return Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI;
            }
            return -90;
        }

        private bool TryDrawOrrerySprite(in PlanetAspect aspect, float sx, float sy, float radius, double poleScreen)
        {
            PlanetColourMap? map = null;
            var credit = "";
            if (aspect.Planet is not (CatalogIndex.Sol or CatalogIndex.Earth) && PlanetTextures.TryGet(aspect.Planet, out var texture, out credit))
            {
                map = texture;
            }
            var night = GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent : (RGBAColor32?)null;
            // An unmirrored view: the camera's right, up and back toward it make a right-handed frame (OrreryCamera).
            return OrrerySprites.Get(aspect, 2 * radius, PlanetDiscSprite.PoleScreenAngle(0, poleScreen, false), false, map, credit,
                    OrreryFlat(aspect.Planet), night, GuiTheme.PaletteGeneration, PlanetTextures.Version) is { } sprite
                && DrawPlanetSprite(sprite, sx, sy, radius);
        }

        // A disc still building its sprite: the body's own colour, or under Night the tint its sprite will have.
        private static RGBAColor32 OrreryFlat(CatalogIndex body)
            => GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent
                : body == CatalogIndex.Earth ? EarthColour : SkyMapRenderer.GetPlanetColor(body);

        // A comet is a point with a tail pointing away from the Sun, as long as a fifth of its drawn distance from it.
        private void DrawCometMark(in OrreryCamera camera, double x, double y, double z, float sx, float sy, float radius)
        {
            if (camera.TryProject(x * 1.2, y * 1.2, z * 1.2, out var tx, out var ty))
            {
                Renderer.DrawLine(sx, sy, tx, ty, CometColour with { Alpha = 0xA0 }, (int)MathF.Max(1, MathF.Round(radius / 2)));
            }
            Renderer.FillEllipse(new RectInt(
                new PointInt((int)MathF.Round(sx + radius), (int)MathF.Round(sy + radius)),
                new PointInt((int)MathF.Round(sx - radius), (int)MathF.Round(sy - radius))), CometColour);
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

            // The distance scale is ONE toggle naming the mode in force, sized by the longer label so a press does not move the strip.
            var auto = orrery.Scale == OrreryScale.Auto;
            var scaleToggle = Layout.Builder.Text(auto ? "Distances: auto" : "Distances: true", fontSize, ink,
                    TextAlign.Center, TextAlign.Center, widthSample: "Distances: auto")
                .PadX(padX).HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit(OrreryScaleAction),
                    _ => SwitchOrreryScale(auto ? OrreryScale.True : OrreryScale.Auto))
                .WithTooltip(auto ? "Distances compressed as far as the framing needs; press for true distances" : "True distances; press to compress them as the framing needs");

            // ONE tree over the whole rect: the strip is the bottom row of it, its fill and its content one arranged box.
            var strip = Layout.Builder.HStack(
                    Button("Sky", "OrreryClose", CloseOrrery, "Back to the sky"),
                    scaleToggle,
                    // The wider label sized once, so Play and Pause do not move what follows.
                    Layout.Builder.Text(orrery.Playing ? "Pause" : "Play", fontSize, ink, TextAlign.Center, TextAlign.Center, widthSample: "Pause")
                        .PadX(padX).HStar()
                        .Bg(fill).BgHover(GuiTheme.Hover(fill))
                        .Clickable(new HitResult.ButtonHit("OrreryPlay"), _ => ToggleOrreryPlay())
                        .WithTooltip($"Run time at {orrery.DaysPerSecond:0} days a second"),
                    Layout.Builder.Text("Drag to orbit, wheel to zoom", fontSize, ink, TextAlign.Near, TextAlign.Center).PadX(padX).WStar().HStar(),
                    BuildTimeControls(timeText, isTimeShifted ? palette.Accent : ink, fontSize, dpiScale, fill, ink))
                .WithGap(4f * dpiScale)
                .PadX(8f * dpiScale)
                .Bg(fill)
                .RowH(stripH);
            var arranged = RenderLayout(
                Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), strip),
                rect,
                // Device pixels, like the sky's strip.
                scale: DesignScale.One);
            RememberTimeButton(arranged);
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
            State.Orrery.Distance = Math.Clamp(State.Orrery.Distance * factor, 0.05, 2000);
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
