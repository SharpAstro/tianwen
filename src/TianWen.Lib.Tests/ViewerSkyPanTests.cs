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
    /// The pan is confined to the viewport EXCEPT while the sky is drawn behind the frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves are requirements, and each one is the other's bug.</b> With the sky behind
    /// it, confining the pan to the picture is what stops the sky BESIDE the photograph being brought to
    /// the middle of the pane -- reported as "the panning is clipped to the image being on screen,
    /// might be overly aggressive". Without it, letting go turns an ordinary viewer into one whose
    /// image can be flung off into the chrome and lost: "for panning probably when its not in Sky mode
    /// it should behave like before".</para>
    /// <para>So the interesting case is neither of those two but the THIRD: the sky switched on over a
    /// frame with no astrometric solution. Nothing is drawn behind it then -- there is no answer to
    /// where the photograph is -- so there is no content beside the picture to pan to, and the clamp
    /// has to stay. That is what makes <c>SkyBackdropActive</c> rather than
    /// <see cref="ViewerState.ShowSkyBackdrop"/> the gate, and it is the half a test written from the
    /// two reports alone would miss.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerSkyPanTests
    {
        // Chrome-free and roomy, with an image far SMALLER than the pane at the test zoom: the slack is
        // then positive on both axes, so a confined pan has a bound well inside the drag below and an
        // unconfined one runs straight past it. Zoomed IN the clamp is the other case of the same
        // arithmetic and needs no second fixture.
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 400;
        private const int ImageH = 300;

        /// <summary>Far enough that no viewport slack in this fixture could accommodate it.</summary>
        private const float DragDistance = 2000f;

        private sealed class PanViewer : ImageRendererBase<RgbaImage>
        {
            public PanViewer(RgbaImageRenderer renderer)
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

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            public RectF32 ImageArea => ImageAreaRect;
        }

        /// <summary>
        /// A solution the frame could plausibly carry: two arcseconds per pixel about the frame's own
        /// centre, drawn chart-oriented (see <see cref="SkyBackdropViewTests"/> for why CD2_2 is
        /// negative). The numbers do not matter here -- only that the WCS HAS a CD matrix, which is
        /// what the gate asks of it.
        /// </summary>
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

        private static Task<AstroImageDocument> DocumentAsync(WCS? wcs, CancellationToken ct)
            => AstroImageDocument.AdoptImageAsync(SyntheticFrame(), DebayerAlgorithm.None, wcs,
                filePath: "synthetic.fits", cancellationToken: ct);

        private static ViewerState NewState() => new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            // An explicit magnification rather than Fit, so the drawn size is a number the slack
            // arithmetic above can be reasoned about.
            ZoomToFit = false,
            Zoom = 1f,
        };

        private static PanViewer NewViewer(RgbaImageRenderer renderer)
        {
            var viewer = new PanViewer(renderer);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            return viewer;
        }

        /// <summary>
        /// Everything the backdrop needs to be considered drawn: a map, a catalog carrier, a clock. The
        /// frame's own solution is the fourth and is supplied per test, because its absence is a case.
        /// </summary>
        private static async Task AttachSkyAsync(PanViewer viewer, RgbaImageRenderer renderer,
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

        /// <summary>
        /// Drags from the middle of the image pane by <see cref="DragDistance"/> on both axes and
        /// renders, which is where the layout applies the clamp and writes the result back.
        /// </summary>
        private static (float X, float Y) DragFarAndSettle(PanViewer viewer, ViewerState state,
            AstroImageDocument document)
        {
            viewer.Render(document, state);

            var area = viewer.ImageArea;
            var x = area.X + (area.Width / 2f);
            var y = area.Y + (area.Height / 2f);

            viewer.HandleInput(new InputEvent.MouseDown(x, y));
            viewer.HandleInput(new InputEvent.MouseMove(x + DragDistance, y + DragDistance));
            viewer.HandleInput(new InputEvent.MouseUp(x + DragDistance, y + DragDistance));

            // The clamp lives in the layout pass, not in the input handler -- the pan is written back
            // from there so a drag held against the edge cannot accumulate hidden offset. So the
            // question is only answerable after another frame.
            viewer.Render(document, state);
            return state.PanOffset;
        }

        /// <summary>
        /// An ordinary solved frame with no sky behind it: the drag is confined, and the picture stays
        /// inside the pane it was dragged in.
        /// </summary>
        [Fact]
        public async Task WithoutTheSkyTheDragIsConfinedToTheViewport()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            var state = NewState();
            var document = await DocumentAsync(Solved(), ct);

            var pan = DragFarAndSettle(viewer, state, document);

            var area = viewer.ImageArea;
            var slackX = area.Width - (ImageW * state.Zoom);
            var slackY = area.Height - (ImageH * state.Zoom);
            slackX.ShouldBeGreaterThan(0f, "the fixture is meant to leave the image smaller than the pane");

            pan.X.ShouldBeLessThanOrEqualTo((slackX / 2f) + 0.5f);
            pan.Y.ShouldBeLessThanOrEqualTo((slackY / 2f) + 0.5f);
            pan.X.ShouldBeLessThan(DragDistance, "the drag must not have been taken at face value");
        }

        /// <summary>
        /// <b>The report.</b> With the sky drawn behind the frame the same drag is honoured in full:
        /// what surrounds the photograph is real content, and the clamp would stop it being brought to
        /// the middle of the pane.
        /// </summary>
        [Fact]
        public async Task WithTheSkyBehindItTheDragGoesWhereItIsPut()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);

            var state = NewState();
            state.ShowSkyBackdrop = true;
            var document = await DocumentAsync(Solved(), ct);

            var pan = DragFarAndSettle(viewer, state, document);

            pan.X.ShouldBe(DragDistance, 0.5f);
            pan.Y.ShouldBe(DragDistance, 0.5f);
        }

        /// <summary>
        /// The sky switched on over a frame that was never solved. Nothing is drawn behind it, so the
        /// clamp stays: turning the pan loose over blank ground is how the picture is lost with no way
        /// of telling where it went.
        /// </summary>
        [Fact]
        public async Task WithTheSkyOnButNoSolutionThePanStaysConfined()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = NewViewer(renderer);
            await AttachSkyAsync(viewer, renderer, ct);

            var state = NewState();
            state.ShowSkyBackdrop = true;
            var document = await DocumentAsync(wcs: null, ct);

            var pan = DragFarAndSettle(viewer, state, document);

            pan.X.ShouldBeLessThan(DragDistance, "an unsolved frame keeps the confined pan");
            pan.Y.ShouldBeLessThan(DragDistance);
        }
    }
}
