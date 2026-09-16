using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky behind the frame is its own toolbar button and its own key, not a rung of the
    /// annotation ladder.
    /// </summary>
    /// <remarks>
    /// <para><b>Reported as "where's the sky?", and then "its still the tri-state overlay?".</b> As the
    /// fourth rung of <c>O</c> it was wrong three ways at once, and each is asserted here: it could not
    /// be reached without stepping through two annotation states on the way (that half is in
    /// <c>ViewerActionsTests</c>), it shared one button and one mark with three other meanings, and on
    /// an UNSOLVED frame the press did nothing whatever with nothing to say why -- because the backdrop
    /// needs a WCS to place the photograph against and a ladder rung has nowhere to carry a
    /// precondition.</para>
    /// <para>The third is the one worth a test of its own. A disabled toolbar button registers no
    /// clickable region at all, so "dim" and "cannot be pressed" are the same fact here and
    /// <c>TryGetPaintedToolbarRect</c> reports it: it is populated only for enabled buttons.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerSkyButtonTests
    {
        private const uint WindowW = 1400;
        private const uint WindowH = 700;
        private const int ImageW = 64;
        private const int ImageH = 48;

        private sealed class SkyButtonViewer : ImageRendererBase<RgbaImage>
        {
            public SkyButtonViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Bus = new SignalBus();
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
        }

        /// <summary>
        /// Two arcseconds per pixel about the frame's centre. Only the presence of a CD matrix matters
        /// to the button -- that is the whole of what it asks the document.
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

        /// <summary>
        /// Gives the viewer a map to draw, which is what makes the button EXIST -- a host that never
        /// set one (the GUI's image tab, every chromeless embedding) can never draw a backdrop, so
        /// there the button is absent rather than dead.
        /// </summary>
        private static void AttachSky(SkyButtonViewer viewer, RgbaImageRenderer renderer)
            => viewer.SkyBackdrop = new SkyMapTab<RgbaImage>(renderer)
            {
                FontPath = FontResolver.ResolveSystemFont(),
            };

        // Wide enough that the bar cannot run out of room and drop the button being asserted on, which
        // would read as the feature being absent.
        private static ViewerState NewState() => new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            ZoomToFit = false,
            Zoom = 1f,
        };

        /// <summary>
        /// <b>The button is on the bar, and it is a button of its own rather than a state of the
        /// annotation one.</b> "its still the tri-state overlay?" is exactly this assertion failing.
        /// </summary>
        [Fact]
        public async Task TheSkyHasItsOwnButtonBesideTheAnnotationLadder()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyButtonViewer(renderer);
            AttachSky(viewer, renderer);
            var document = await DocumentAsync(Solved(), ct);
            var state = NewState();

            viewer.Render(document, state);

            viewer.TryGetPaintedToolbarRect(ToolbarAction.SkyBackdrop, out var sky)
                .ShouldBeTrue("a solved frame can carry a backdrop, so the button is live");
            viewer.TryGetPaintedToolbarRect(ToolbarAction.Overlays, out var overlays)
                .ShouldBeTrue("and the ladder it left is still there");
            sky.X.ShouldNotBe(overlays.X, "two buttons, not one wearing two meanings");
        }

        /// <summary>
        /// <b>On an unsolved frame it is dim and takes no press.</b> The backdrop has to PLACE the
        /// photograph on the sky, so with no solution it would draw the wrong part of it rather than
        /// nothing -- and as a ladder rung the press was simply swallowed in silence.
        /// </summary>
        [Fact]
        public async Task WithNoSolutionTheButtonIsNotClickable()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyButtonViewer(renderer);
            AttachSky(viewer, renderer);
            var document = await DocumentAsync(wcs: null, ct);
            var state = NewState();

            viewer.Render(document, state);

            viewer.TryGetPaintedToolbarRect(ToolbarAction.SkyBackdrop, out _)
                .ShouldBeFalse("a disabled button registers no clickable region, which is what dim means here");
        }

        /// <summary>
        /// <b>A host that gave the viewer no map at all does not show the button.</b> Hidden and dim
        /// are different statements and this feature needs both: the GUI's image tab and every
        /// chromeless embedding can NEVER draw a backdrop, so a button there would be permanently
        /// dead -- the same rule that hides Enhance without an AI pipeline. An unsolved frame in a
        /// host that CAN draw one is the other case, and stays visible and dim.
        /// </summary>
        [Fact]
        public async Task AHostWithNoSkyMapDoesNotShowTheButtonAtAll()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyButtonViewer(renderer); // deliberately no AttachSky
            var document = await DocumentAsync(Solved(), ct);
            var state = NewState();

            viewer.Render(document, state);

            viewer.PaintedToolbarButtons.ShouldNotContain(b => b.Action == ToolbarAction.SkyBackdrop,
                "a viewer with no sky map never shows a dead sky button");
            viewer.PaintedToolbarButtons.ShouldContain(b => b.Action == ToolbarAction.Overlays,
                "while the rest of the bar is unaffected");
        }

        /// <summary>
        /// Pressing it toggles the backdrop, and pressing it again puts it away.
        /// </summary>
        [Fact]
        public async Task PressingItTurnsTheSkyOnAndOffAgain()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyButtonViewer(renderer);
            AttachSky(viewer, renderer);
            var document = await DocumentAsync(Solved(), ct);
            var state = NewState();

            viewer.Render(document, state);
            viewer.TryGetPaintedToolbarRect(ToolbarAction.SkyBackdrop, out var rect).ShouldBeTrue();
            var (cx, cy) = (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));

            UiRouting.Route(viewer,
                new InputEvent.MouseDown(cx, cy),
                new InputEvent.MouseUp(cx, cy));
            state.ShowSkyBackdrop.ShouldBeTrue("the button is the affordance for the same one line Y runs");

            viewer.Render(document, state);
            viewer.IsToolbarButtonActiveForTest(ToolbarAction.SkyBackdrop, state)
                .ShouldBeTrue("and it lights while the sky is wanted");

            UiRouting.Route(viewer,
                new InputEvent.MouseDown(cx, cy),
                new InputEvent.MouseUp(cx, cy));
            state.ShowSkyBackdrop.ShouldBeFalse("a second press puts it away");
        }

        /// <summary>
        /// <b>Y reaches the sky in ONE press, from any annotation state, and leaves it in one.</b> That
        /// is the whole complaint about the ladder: from nothing it took three presses of a key that
        /// also turned on two layers you had not asked for.
        /// </summary>
        [Fact]
        public async Task TheKeyReachesItInOnePressAndLeavesTheAnnotationAlone()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var viewer = new SkyButtonViewer(renderer);
            AttachSky(viewer, renderer);
            var document = await DocumentAsync(Solved(), ct);
            var state = NewState();
            viewer.Render(document, state);

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.Y));

            state.ShowSkyBackdrop.ShouldBeTrue("one press");
            state.OverlayLevel.ShouldBe(ViewerOverlayLevel.None,
                "and it brought no annotation along that nobody asked for");

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.Y));
            state.ShowSkyBackdrop.ShouldBeFalse("and one press back out");
        }

        /// <summary>
        /// The key is deliberately not one the sky's own layer palette claims. The palette is up
        /// exactly when the backdrop is, so a key it also owns would be swallowed at the one moment the
        /// user wants to switch the sky back off -- which is why G, A, H, C, B, S, O, D, E, M and V are
        /// all unavailable here however well they read.
        /// </summary>
        [Fact]
        public void TheToggleKeyIsNotOneTheSkyPaletteClaims()
        {
            foreach (var layer in SkyMapLayers.All)
            {
                layer.Key.ShouldNotBe(InputKey.Y,
                    $"the palette's {layer.Label} layer would swallow the press that turns the sky off");
            }
        }
    }
}
