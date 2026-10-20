using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky backdrop's presses go where they are painted (#1513). The viewer paints the layer palette as its own
    /// chrome, so a menu opened over the palette takes its press, and the palette's grip drags through the router, not
    /// the viewer's own handlers, so the picture does not pan under it. A label behind the photograph registering only
    /// where it is seen is pinned on the map's own rig (<c>SkyMapPlanetDiscTests</c>), which projects its planets.
    /// </summary>
    /// <remarks>
    /// Driven through <see cref="InputRouter"/> as <see cref="StandaloneViewerHost"/> wires it, since the router's
    /// order is what was wrong: the map was one child listed wholly over the viewer, so its palette's regions came
    /// after everything the viewer drew, its popovers included, and its labels after the photograph.
    /// </remarks>
    [Collection("UI")]
    public class ViewerSkyPaletteRoutingTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 400;
        private const int ImageH = 300;
        private static readonly DateTimeOffset Instant = new(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);

        private sealed class SkyViewer : ImageRendererBase<RgbaImage>
        {
            public SkyViewer(RgbaImageRenderer renderer)
                : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = 1f;
                FontPath = FontResolver.ResolveSystemFont();
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float rotationRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;
        }

        private sealed record Rig(SkyViewer Viewer, SkyMapTab<RgbaImage> Tab, ViewerState State, AstroImageDocument Document,
            InputRouter Router)
        {
            public void Render() => Viewer.Render(Document, State);

            public IReadOnlyList<ClickableRegion> Regions()
            {
                var regions = new List<ClickableRegion>();
                Viewer.CollectPaintedRegions(regions);
                return regions;
            }

            public ClickableRegion Region(string action)
                => Regions().Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);

            public bool[] Layers() => [.. SkyMapLayers.All.Select(l => l.IsOn(Tab.State))];
        }

        /// <summary>A two-arcsecond solution centred on <paramref name="raHours"/>, <paramref name="decDeg"/>.</summary>
        private static WCS SolvedAt(double raHours, double decDeg)
        {
            const double scaleDeg = 2.0 / 3600.0;
            return new WCS(raHours, decDeg)
            {
                CRPix1 = ImageW / 2.0,
                CRPix2 = ImageH / 2.0,
                CD1_1 = -scaleDeg,
                CD1_2 = 0.0,
                CD2_1 = 0.0,
                CD2_2 = -scaleDeg,
            };
        }

        private static Image SyntheticFrame()
        {
            var plane = new float[ImageH, ImageW];
            return new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                new ImageMeta("synth", Instant, TimeSpan.Zero, FrameType.Light, "",
                    0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN));
        }

        private static async Task<Rig> RigAsync(WCS wcs, CancellationToken ct)
        {
            var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyViewer(renderer);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

            var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            tab.State.ViewDrivenExternally = true;
            viewer.SkyBackdrop = tab;
            viewer.SkyTimeProvider = new FakeTimeProviderWrapper(Instant);
            viewer.SkyPlannerState = new PlannerState
            {
                ObjectDb = await SharedCatalogDB.InitAsync(ct),
                SiteLatitude = 48.0,
                SiteLongitude = 11.0,
                SiteTimeZone = TimeSpan.Zero,
                PlanningDate = Instant,
            };

            var document = await AstroImageDocument.AdoptImageAsync(SyntheticFrame(), DebayerAlgorithm.None, wcs,
                filePath: "synthetic.fits", cancellationToken: ct);
            var state = new ViewerState
            {
                ShowFileList = false,
                ShowInfoPanel = false,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                ShowSkyBackdrop = true,
                ZoomToFit = false,
                Zoom = 1f,
            };

            // As StandaloneViewerHost wires it: the router first, the viewer's own handlers for what it declines.
            var router = new InputRouter(viewer.Ui, new BackgroundTaskTracker(), () => state.NeedsRedraw = true)
            {
                Widgets = () => [viewer],
                Unhandled = viewer.HandleInput,
            };
            var rig = new Rig(viewer, tab, state, document, router);
            rig.Render();
            return rig;
        }

        private static (float X, float Y) Centre(in ClickableRegion r) => (r.X + r.Width / 2f, r.Y + r.Height / 2f);

        [Fact(Timeout = 120_000)]
        public async Task ThePalettesGripDragsThroughTheRouterAndThePictureDoesNotPan()
        {
            var rig = await RigAsync(SolvedAt(5.0, -25.0), TestContext.Current.CancellationToken);
            var (x, y) = Centre(rig.Region(SkyMapLayerPalette.GripAction));
            var (along, pan) = (rig.Tab.State.LayerPalette.OffsetAlong, rig.State.PanOffset);

            rig.Router.Handle(new InputEvent.MouseDown(x, y)).ShouldBeTrue("the grip's region is the viewer's own now");
            rig.Router.Handle(new InputEvent.MouseMove(x, y + 60f, MouseButton.Left)).ShouldBeTrue();
            rig.Router.Handle(new InputEvent.MouseUp(x, y + 60f)).ShouldBeTrue();
            rig.Render();

            rig.Tab.State.LayerPalette.OffsetAlong.ShouldBe(along + 60f, 0.5f, "the palette slid down its edge");
            rig.Tab.State.LayerPalette.IsDragging.ShouldBeFalse("the release ended the drag");
            rig.State.PanOffset.ShouldBe(pan, "a palette dragged over the picture does not also pan it");
        }

        [Fact(Timeout = 120_000)]
        public async Task AMenuOpenOverThePaletteTakesThePressAndNoLayerToggles()
        {
            var rig = await RigAsync(SolvedAt(5.0, -25.0), TestContext.Current.CancellationToken);
            var row = rig.Regions().First(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("SkyMapLayer:"));
            var (x, y) = Centre(row);

            // A menu opened right over the palette's first row, as the toolbar opens one: painted after the palette.
            var items = new[] { "one", "two", "three" }.Select(DropdownItem.Text).ToImmutableArray();
            rig.State.ToolbarDropdown.Open(row.X, row.Y - 4f, row.Width, items);
            rig.Render();
            var before = rig.Layers();

            rig.Viewer.HitTest(x, y).ShouldNotBe(row.Result, "the menu is painted over the palette, so it answers first");
            rig.Router.Handle(new InputEvent.MouseDown(x, y)).ShouldBeTrue();
            rig.Router.Handle(new InputEvent.MouseUp(x, y));

            rig.Layers().ShouldBe(before, "the press was the menu's, not the layer row's under it");
        }
    }
}
