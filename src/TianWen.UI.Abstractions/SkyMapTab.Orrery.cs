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

        /// <summary>How many orrery frames have been drawn, and how long the last one took (CPU, milliseconds).</summary>
        internal int OrreryFrames { get; private set; }

        /// <inheritdoc cref="OrreryFrames"/>
        internal double LastOrreryFrameMs { get; private set; }

        private static readonly RGBAColor32 OrreryBackground = new(0x06, 0x06, 0x10, 0xFF);
        private static readonly RGBAColor32 EarthColour = new(0x5A, 0x8C, 0xD8, 0xFF);
        private static readonly RGBAColor32 CometColour = new(0x9C, 0xE8, 0xD0, 0xFF);

        /// <summary>Opens the orrery on the selected body when it is one of the Sun's (the panel's button, Alt+S); the selection is kept.</summary>
        internal void OpenOrrery()
        {
            var orrery = State.Orrery;
            orrery.Focus = State.Search.InfoPanel is { Index: { } index } && index.IsSolarSystemObject ? index : null;
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
            if (orrery.Focus is { } focus && focus.ToCatalog() == Catalog.Comet
                && plannerState.Comets is { } comets && comets.TryGet(focus, out var elements))
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
            PushClip(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
            var camera = new OrreryCamera(orrery.AzimuthDeg, orrery.ElevationDeg, orrery.Distance, contentRect);
            DrawOrreryOrbits(scene, camera, comet, orrery.Scale, dpiScale);
            DrawOrreryBodies(scene, camera, viewingTime, orrery.Scale, dpiScale);
            PopClip();

            DrawOrreryStrip(contentRect, viewingTime, plannerState.SiteTimeZone,
                plannerState.PlanningDate.HasValue || State.TimeOffset != TimeSpan.Zero);
            RenderTimeMenu(contentRect, BaseFontSize * dpiScale * 0.85f);

            OrreryFrames++;
            LastOrreryFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // A play advances the atlas's own scrub offset by what the wall clock has run since the last frame, so the sky shows the same
        // instant when the view goes back to it; the next frame is asked for here, which on the browser is a coalesced repaint.
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
                var elapsed = timeProvider.GetElapsedTime(orrery.PlayTimestamp, now);
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
            var (rx, _, _) = OrreryScene.Scaled(orrery.Scale, reachAu, 0, 0);
            orrery.Distance = OrreryCamera.DistanceToFrame(rx * 1.15);
        }

        private void DrawOrreryOrbits(OrreryScene scene, in OrreryCamera camera, in CometElements? comet, OrreryScale scale, float dpiScale)
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
        private void DrawOrbitPolyline(double[] points, in OrreryCamera camera, OrreryScale scale, RGBAColor32 colour, int thickness)
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

        private void DrawOrreryBodies(OrreryScene scene, in OrreryCamera camera, DateTimeOffset viewingTime, OrreryScale scale, float dpiScale)
        {
            _orreryHits.Clear();
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
            var (eyeX, eyeY, eyeZ) = camera.Eye;
            Span<double> toObserver = stackalloc double[3];
            Span<double> heliocentric = stackalloc double[3];
            Span<double> pole = stackalloc double[3];
            foreach (var (index, _, x, y, z) in order)
            {
                var body = bodies[index];
                if (!camera.TryProject(x, y, z, out var sx, out var sy))
                {
                    continue;
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
                var labelTop = sy + (radius * (body.Index == CatalogIndex.Saturn ? 2.3f : 1f)) + (3f * dpiScale);
                DrawText(body.Name.AsSpan(), FontPath, sx - (60f * dpiScale), labelTop, 120f * dpiScale, fontSize * 1.4f,
                    fontSize, selected ? palette.Accent : palette.BodyText, TextAlign.Center, TextAlign.Near);
                _orreryHits.Add((body.Index, sx, sy, radius));
            }
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

        private static RGBAColor32 OrreryFlat(CatalogIndex body) => body == CatalogIndex.Earth ? EarthColour : SkyMapRenderer.GetPlanetColor(body);

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
            var stripY = rect.Y + rect.Height - stripH;
            var orrery = State.Orrery;
            var padX = 6f * dpiScale;
            var fill = InfoPanelBg;

            var localTime = viewingTime.ToOffset(siteTimeZone);
            var timeText = $"{localTime:yyyy-MM-dd HH:mm}";
            if (State.TimeOffset != TimeSpan.Zero)
            {
                timeText += $"  ({SkyMapState.FormatOffset(State.TimeOffset)})";
            }

            Layout.Node Button(string label, string action, Action onClick, string tooltip)
                => Layout.Builder.Text(label, fontSize, InfoText, TextAlign.Center, TextAlign.Center)
                    .PadX(padX)
                    .HStar()
                    .Bg(fill).BgHover(GuiTheme.Hover(fill))
                    .Clickable(new HitResult.ButtonHit(action), _ => onClick())
                    .WithTooltip(tooltip);

            ReadOnlySpan<Layout.ButtonGroupOption<OrreryScale>> scales =
            [
                new(OrreryScale.Compressed, "Compressed") { Hit = new HitResult.ButtonHit(OrreryScaleAction + ":Compressed"), Tooltip = "Distances as their square root" },
                new(OrreryScale.True, "True") { Hit = new HitResult.ButtonHit(OrreryScaleAction + ":True"), Tooltip = "True distances" },
            ];
            // A group's segments take the width they are given, so it is the engine's measurement of the longer label, as the sky's
            // own mode switch does it (BuildModeSwitch); unstated, the group took none and painted over its neighbour.
            var widest = Layout.Engine.Measure(
                Layout.Builder.Text("Compressed", fontSize, trim: TextTrim.None).PadX(padX),
                new Layout.Size<float>(float.MaxValue, float.MaxValue), MeasureContext(scale: DesignScale.One)).Width;
            var style = new Layout.ButtonGroupStyle(ModeSelectedFill, InfoPanelBg, ModeSelectedInk, InfoText, GuiTheme.Hover(InfoPanelBg))
            {
                Gap = 2f * dpiScale,
                CornerRadius = 3f * dpiScale,
                InsetFraction = 0.8f,
                SegmentWidth = widest,
            };

            RenderLayout(Layout.Builder.Spacer().Bg(fill), new RectF32(rect.X, stripY, rect.Width, stripH));
            var arranged = RenderLayout(
                Layout.Builder.HStack(
                    Button("Sky", "OrreryClose", CloseOrrery, "Back to the sky"),
                    Layout.Builder.ButtonGroup(scales, orrery.Scale, SwitchOrreryScale, style, fontSize).HStar(),
                    Button(orrery.Playing ? "Pause" : "Play", "OrreryPlay", ToggleOrreryPlay, $"Run time at {orrery.DaysPerSecond:0} days a second"),
                    Layout.Builder.Text("Drag to orbit, wheel to zoom", fontSize, InfoText, TextAlign.Near, TextAlign.Center).PadX(padX).WStar().HStar(),
                    BuildTimeControls(timeText, isTimeShifted ? TimeShiftColor : InfoText, fontSize, dpiScale))
                    .WithGap(4f * dpiScale),
                new RectF32(rect.X + 8, stripY, rect.Width - 16, stripH),
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
            State.Orrery.Scale = scale;
            _orreryFramePending = true;
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
            InputEvent.Scroll(var scrollY, _, _, _) => ZoomOrrery(Math.Pow(0.85, scrollY)),
            InputEvent.Pinch p => ZoomOrrery(1.0 / p.Scale),
            InputEvent.MouseDown(var x, var y, _, _, _) => BeginOrreryDrag(x, y),
            InputEvent.MouseMove(var x, var y) => DragOrrery(x, y),
            InputEvent.MouseUp(var x, var y, _) => EndOrreryDrag(x, y),
            InputEvent.KeyDown(var key, var modifiers) => HandleOrreryKey(key, modifiers),
            _ => false,
        };

        private bool ZoomOrrery(double factor)
        {
            State.Orrery.Distance = Math.Clamp(State.Orrery.Distance * factor, 0.05, 2000);
            State.NeedsRedraw = true;
            return true;
        }

        private bool BeginOrreryDrag(float x, float y)
        {
            var orrery = State.Orrery;
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
