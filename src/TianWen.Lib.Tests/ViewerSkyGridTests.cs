using System;
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
    /// One grid, never two -- across the handover, which is where it broke.
    /// </summary>
    /// <remarks>
    /// <para>There are three grid paths and only one may ever run. With no sky behind the frame, the
    /// image quad draws its own. With the sky behind it, GEOMETRY picks between the frame's grid drawn
    /// pane-wide (while the tangent plane still holds) and the map's spherical one (once the view
    /// outgrows it).</para>
    /// <para><b>The quad grid has to stand down for BOTH, and used to stand down for only the first.</b>
    /// Past the tangent threshold the pane-wide grid switched off, the map raised its own, and the
    /// quad grid carried on drawing inside the picture -- two grids of two different things, meeting
    /// at the frame edge at an angle. Reported as "grid line show and hide is out of sync", with the
    /// telling detail that zooming IN fixed it: zoomed in is the side of the handover that was right.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerSkyGridTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 400;
        private const int ImageH = 300;

        private sealed class GridViewer : ImageRendererBase<RgbaImage>
        {
            public GridViewer(RgbaImageRenderer renderer) : base(renderer)
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

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            public RectF32 Pane => ImageAreaRect;
        }

        private static WCS Solved()
        {
            const double scaleDeg = 2.0 / 3600.0;
            return new WCS(5.0, -25.0)
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
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    plane[y, x] = 1000f + (y * ImageW) + x;
                }
            }

            return new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                    0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN));
        }

        private static Task<AstroImageDocument> DocumentAsync(CancellationToken ct)
            => AstroImageDocument.AdoptImageAsync(SyntheticFrame(), DebayerAlgorithm.None, Solved(),
                filePath: "synthetic.fits", cancellationToken: ct);

        private static async Task AttachSkyAsync(GridViewer viewer, RgbaImageRenderer renderer,
            CancellationToken ct)
        {
            var db = await SharedCatalogDB.InitAsync(ct);
            var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            tab.State.ViewDrivenExternally = true;

            viewer.SkyBackdrop = tab;
            viewer.SkyTimeProvider = new FakeTimeProviderWrapper(
                new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero));
            viewer.SkyPlannerState = new PlannerState
            {
                ObjectDb = db,
                SiteLatitude = 48.0,
                SiteLongitude = 11.0,
                SiteTimeZone = TimeSpan.Zero,
                PlanningDate = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero),
            };
        }

        private static ViewerState NewState(bool sky, float zoom) => new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            ShowGrid = true,
            ShowSkyBackdrop = sky,
            ZoomToFit = false,
            Zoom = zoom,
        };

        /// <summary>
        /// With no sky behind it the quad grid is the only grid, so it draws. The control: without
        /// this the fix below could be "never draw a grid" and every other assertion would pass.
        /// </summary>
        [Fact]
        public async Task WithoutTheSkyTheFrameDrawsItsOwnGrid()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            var document = await DocumentAsync(ct);
            var state = NewState(sky: false, zoom: 1f);

            viewer.Render(document, state);

            viewer.PreparedGridWcs.ShouldNotBeNull("the quad grid is the only grid here");
        }

        /// <summary>
        /// <b>Zoomed in, where the frame's tangent plane still holds.</b> The frame's own grid spans
        /// the pane, so the quad grid stands down and the map suppresses its spherical one.
        /// </summary>
        [Fact]
        public async Task ZoomedInTheFrameGridSpansThePaneAndNothingElseDraws()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = NewState(sky: true, zoom: 1f);

            viewer.Render(document, state);

            viewer.PaneWideGrid.ShouldBeTrue("a one-to-one view is well inside the tangent limit");
            viewer.PreparedGridWcs.ShouldBeNull("the pane-wide pass is already drawing this grid");
            viewer.SkyBackdrop.ShouldNotBeNull();
            viewer.SkyBackdrop!.State.DrawOwnGrid.ShouldBeFalse("and the map stands its own down");
        }

        /// <summary>
        /// <b>Zoomed out past the tangent limit, which is the case that was broken.</b> The map draws
        /// its spherical grid, and the quad grid must stand down for that too -- it did not, and the
        /// two disagreed across the frame edge.
        /// </summary>
        [Fact]
        public async Task ZoomedOutTheMapGridTakesOverAndTheFrameGridStandsDown()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);

            // Far enough out that the pane's furthest corner leaves the frame's tangent plane behind.
            // At this plate scale zoom 0.02 is NOT enough -- the corner is about 16 degrees out, still
            // inside the limit -- so the threshold is crossed at roughly 0.01 and this sits past it.
            var state = NewState(sky: true, zoom: 0.006f);
            viewer.Render(document, state);
            viewer.Render(document, state);

            viewer.PaneWideGrid.ShouldBeFalse(
                "the pane has outgrown the frame's tangent plane, so its grid is no longer the right one");
            viewer.SkyBackdrop.ShouldNotBeNull();
            viewer.SkyBackdrop!.State.DrawOwnGrid.ShouldBeTrue("the map's spherical grid takes over");
            viewer.PreparedGridWcs.ShouldBeNull(
                "and the frame's own grid must NOT go on drawing inside the picture beside it");
        }

        /// <summary>
        /// The invariant rather than the two cases: with the sky behind the frame, at any zoom, the
        /// quad grid never draws. Walking the zoom across the handover is what catches a threshold
        /// that moves.
        /// </summary>
        [Theory]
        [InlineData(2f)]
        [InlineData(1f)]
        [InlineData(0.5f)]
        [InlineData(0.2f)]
        [InlineData(0.08f)]
        [InlineData(0.03f)]
        [InlineData(0.01f)]
        public async Task TheQuadGridNeverDrawsWhileTheSkyIsBehindTheFrame(float zoom)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = NewState(sky: true, zoom: zoom);

            viewer.Render(document, state);
            viewer.Render(document, state);


            viewer.PreparedGridWcs.ShouldBeNull($"at zoom {zoom} a second grid is drawn over the picture");
        }

        /// <summary>
        /// <b>The zoom stops where the projection does.</b> The backdrop states its view from the
        /// placement and cannot be asked for more than the whole sky, so past that point the map stops
        /// widening while the photograph's quad goes on shrinking and the frame slides off its own sky
        /// position -- an M8 frame drawn in Scutum with Sagittarius beside it.
        /// </summary>
        [Theory]
        [InlineData(0.01f)]
        [InlineData(0.005f)]
        [InlineData(0.001f)]
        public async Task TheZoomStopsWhereTheProjectionDoes(float asked)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = NewState(sky: true, zoom: asked);

            viewer.Render(document, state);
            viewer.Render(document, state);

            // The field the pane now asks the backdrop for, worked out the way the clamp does.
            var pane = MathF.Max(viewer.Pane.Width, viewer.Pane.Height);
            var arcsec = pane / state.Zoom * (2.0 / 1.0);   // the fixture is 2 arcsec per pixel
            var degrees = arcsec / 3600.0;

            degrees.ShouldBeLessThanOrEqualTo(SkyBackdropView.MaxFieldOfViewDeg + 1e-6,
                $"asked for zoom {asked} and settled at {state.Zoom}, which still demands {degrees:F1} deg");
            state.Zoom.ShouldBeGreaterThanOrEqualTo(asked,
                "the clamp may only ever pull the zoom back IN, never push it further out");
        }

        /// <summary>
        /// And with the grid switched off, nothing draws one whatever the sky is doing.
        /// </summary>
        [Fact]
        public async Task TheGridSwitchStillTurnsEverythingOff()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);

            // Settle the two grid flags FIRST. The map's own ShowGrid defaults to true, so on a
            // viewer's very first render the "whichever moved wins" reconcile reads that as the map
            // having moved and pushes true into the viewer state -- which would quietly undo the
            // switch this test is about.
            var state = NewState(sky: true, zoom: 1f);
            viewer.Render(document, state);

            state.ShowGrid = false;
            viewer.Render(document, state);
            viewer.Render(document, state);

            viewer.PreparedGridWcs.ShouldBeNull();
            viewer.PaneWideGrid.ShouldBeFalse();
            viewer.SkyBackdrop.ShouldNotBeNull();
            viewer.SkyBackdrop!.State.DrawOwnGrid.ShouldBeFalse("G means no grid, on either side of the handover");
        }

        // ------------------------------------------------------------------------------------------
        // Gestures on the sky. With the sky drawn behind the frame, a drag and a wheel are gestures ON
        // THE SKY: the sky position under the pointer follows the pointer, wherever the frame is. They
        // used to be pixel gestures on the frame, which through the projection were worth less and
        // less sky the further the frame sat from the pane's centre -- a pan that crawled near the
        // pole, a zoom that would not keep its point, a touch pan near Crux that "did not work".
        // ------------------------------------------------------------------------------------------

        /// <summary>The sky position under a pane point, read off the backdrop the viewer just drove.</summary>
        private static (double RA, double Dec) SkyUnder(GridViewer viewer, float x, float y)
        {
            var tab = viewer.SkyBackdrop.ShouldNotBeNull();
            var pane = viewer.Pane;
            var view = tab.State.ComputeViewMatrix();
            var pixelsPerRadian = SkyMapProjection.PixelsPerRadian(pane.Height, tab.State.FieldOfViewDeg);
            return SkyMapProjection.UnprojectWithMatrix(x, y, in view, pixelsPerRadian,
                pane.X + (pane.Width * 0.5f), pane.Y + (pane.Height * 0.5f));
        }

        /// <summary>Great-circle separation of two sky positions, in degrees.</summary>
        private static double SeparationDeg((double RA, double Dec) a, (double RA, double Dec) b)
        {
            var (sinA, cosA) = Math.SinCos(double.DegreesToRadians(a.Dec));
            var (sinB, cosB) = Math.SinCos(double.DegreesToRadians(b.Dec));
            var dRa = double.DegreesToRadians((a.RA - b.RA) * 15.0);
            var dot = Math.Clamp((sinA * sinB) + (cosA * cosB * Math.Cos(dRa)), -1.0, 1.0);
            return double.RadiansToDegrees(Math.Acos(dot));
        }

        /// <summary>
        /// A frame pushed far to the right of the pane: about a hundred degrees from the pane's centre
        /// at this zoom, which is the pole from a mid-northern frame. Rendered twice so the placement
        /// and the backdrop it drives have both settled.
        /// </summary>
        private static ViewerState FarFrame(GridViewer viewer, AstroImageDocument document)
        {
            var state = NewState(sky: true, zoom: 0.05f);
            state.PanOffset = (12000f, 0f);
            viewer.Render(document, state);
            viewer.Render(document, state);
            return state;
        }

        /// <summary>
        /// <b>A drag far from the picture moves the sky with the pointer.</b> The position grabbed at
        /// the press is under the pointer when it stops, and the frame travelled further than the
        /// pointer did, which the pixel pan never could.
        /// </summary>
        [Fact]
        public async Task ADragFarFromTheFrameMovesTheSkyWithThePointer()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = FarFrame(viewer, document);

            var pane = viewer.Pane;
            var fromX = pane.X + (pane.Width * 0.5f);
            var fromY = pane.Y + (pane.Height * 0.5f);
            var toX = fromX + 180f;
            var toY = fromY - 120f;
            var grabbed = SkyUnder(viewer, fromX, fromY);
            var panBefore = state.PanOffset;

            viewer.HandleInput(new InputEvent.MouseDown(fromX, fromY));
            viewer.HandleInput(new InputEvent.MouseMove(toX, toY, MouseButton.Left));
            viewer.HandleInput(new InputEvent.MouseUp(toX, toY));
            viewer.Render(document, state);

            SeparationDeg(grabbed, SkyUnder(viewer, toX, toY)).ShouldBeLessThan(0.01,
                "the sky position grabbed at the press is under the pointer where it stopped");

            var dx = state.PanOffset.X - panBefore.X;
            var dy = state.PanOffset.Y - panBefore.Y;
            MathF.Sqrt((dx * dx) + (dy * dy)).ShouldBeGreaterThan(2f * MathF.Sqrt((180f * 180f) + (120f * 120f)),
                "far from the pane's centre the frame travels further than the pointer; a pixel pan moves it exactly as far");
        }

        /// <summary>
        /// <b>A wheel far from the picture keeps the sky under the cursor.</b> The zoom changes and the
        /// sky position under the cursor does not, as in the atlas.
        /// </summary>
        [Fact]
        public async Task AWheelFarFromTheFrameKeepsTheSkyUnderTheCursor()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = FarFrame(viewer, document);

            var pane = viewer.Pane;
            var x = pane.X + (pane.Width * 0.3f);
            var y = pane.Y + (pane.Height * 0.65f);
            var under = SkyUnder(viewer, x, y);
            var zoomBefore = state.Zoom;

            viewer.HandleInput(new InputEvent.Scroll(1f, x, y));
            viewer.Render(document, state);

            state.Zoom.ShouldBeGreaterThan(zoomBefore, "the wheel zoomed in");
            SeparationDeg(under, SkyUnder(viewer, x, y)).ShouldBeLessThan(0.01,
                "the sky position under the cursor stays under the cursor through a zoom");
        }

        /// <summary>
        /// <b>A wheel held at the floor moves nothing.</b> The controller used to clamp at its own floor,
        /// far below the backdrop's, so each notch past the whole sky still shifted the pan for a zoom
        /// the layout then refused: the sky slid sideways under a wheel that was zooming nothing.
        /// </summary>
        [Fact]
        public async Task AWheelAtTheFloorMovesNothing()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new GridViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);
            var document = await DocumentAsync(ct);
            var state = NewState(sky: true, zoom: 0.001f);
            viewer.Render(document, state);
            viewer.Render(document, state);

            var pane = viewer.Pane;
            var zoom = state.Zoom;
            var pan = state.PanOffset;
            zoom.ShouldBeGreaterThan(0.001f, "the fixture starts AT the floor, which is above what was asked");

            viewer.HandleInput(new InputEvent.Scroll(-1f, pane.X + (pane.Width * 0.8f), pane.Y + (pane.Height * 0.7f)));
            viewer.Render(document, state);

            state.Zoom.ShouldBe(zoom);
            state.PanOffset.ShouldBe(pan, "a notch past the floor is a no-op, pan included");
        }
    }
}
