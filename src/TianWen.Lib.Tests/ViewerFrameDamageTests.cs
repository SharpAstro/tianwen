using System;
using System.Collections.Generic;
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
    /// Which rects the viewer declares as damaged, so the surface repaints those instead of everything.
    /// </summary>
    /// <remarks>
    /// <para>The contract is SAFE BY DEFAULT: a frame requested without saying what changed must come
    /// back as a full repaint. That is what lets a new input path be written by someone who has never
    /// heard of damage -- they get the old behaviour, not a stale window. Half these tests exist to pin
    /// that direction rather than the narrowing, because narrowing wrongly is the failure that shows on
    /// screen and it is the one nobody would think to test for.</para>
    /// <para>Offline because the inspector cannot help here: it has no bare-move verb (its drag presses a
    /// button, which is a PAN, which is legitimately a full repaint), so the case this whole thing exists
    /// for -- moving the pointer across the image with no button down -- is unreachable from synthetic
    /// input.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerFrameDamageTests
    {
        private const uint SurfaceW = 1000;
        private const uint SurfaceH = 600;
        private const int ImageW = 400;
        private const int ImageH = 300;

        [Fact]
        public async Task AMoveOverTheImageDamagesOnlyWhereAReadoutIsShown()
        {
            var (viewer, state, document) = await NewViewerAsync();
            state.ShowInfoPanel = false;
            viewer.Render(document, state);
            var statusBar = viewer.StatusBar;

            // Inside the pane, so the readout changes.
            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f)).ShouldBeTrue();

            var damage = Take(viewer);
            damage.ShouldNotBeNull("a readout change must NOT ask for a full repaint");
            damage.Count.ShouldBe(1);
            damage[0].ShouldBe(statusBar);
        }

        [Fact]
        public async Task WithTheInfoPanelOpenTheReadoutDamagesThatToo()
        {
            // The info panel lists the per-channel pixel values. Damaging only the status bar would
            // leave those frozen at whatever the pointer last touched while the bar beside them kept
            // counting -- a stale readout that looks like a value bug, not a repaint bug.
            var (viewer, state, document) = await NewViewerAsync();
            state.ShowInfoPanel = true;
            viewer.Render(document, state);

            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f)).ShouldBeTrue();

            var damage = Take(viewer);
            damage.ShouldNotBeNull();
            damage.Count.ShouldBe(2);
            damage.ShouldContain(viewer.StatusBar);
            damage.ShouldContain(viewer.InfoPanel);
        }

        [Fact]
        public async Task AMoveThatChangesNothingAsksForNoFrameAtAll()
        {
            var (viewer, state, document) = await NewViewerAsync();
            viewer.Render(document, state);

            // Twice to the same place: the second changes no readout.
            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f));
            Take(viewer);
            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f)).ShouldBeFalse();

            // Nothing declared and no frame asked for. The host repaints nothing.
            viewer.TryTakeFrameDamage([]).ShouldBeFalse();
        }

        [Fact]
        public async Task APanDamagesEverythingBecauseTheImageItselfMoved()
        {
            var (viewer, state, document) = await NewViewerAsync();
            viewer.Render(document, state);

            // Press starts the pan, so the move is a drag rather than a hover.
            viewer.HandleInput(new InputEvent.MouseDown(500f, 300f, MouseButton.Left, InputModifier.None, 1));
            Take(viewer);
            viewer.HandleInput(new InputEvent.MouseMove(520f, 310f));

            Take(viewer).ShouldBeNull("the image moved, so the pane is not the only thing that changed");
        }

        [Fact]
        public async Task AKeyPressDamagesEverything()
        {
            // The safety property: a path that asks for a frame without declaring anything gets a full
            // repaint. Every input path not specifically taught about damage must behave like this, so
            // this test is really about all of them.
            var (viewer, state, document) = await NewViewerAsync();
            viewer.Render(document, state);

            viewer.HandleInput(new InputEvent.KeyDown(InputKey.T, InputModifier.None));

            Take(viewer).ShouldBeNull();
        }

        /// <summary>
        /// A non-declaring event in the SAME frame as a narrowing one forces the frame full.
        /// </summary>
        /// <remarks>
        /// This is the test that has teeth, and the reason the two above do not. Several events are
        /// dispatched between frames, so a readout move followed by a key press is ordinary -- and
        /// without the guard the frame would repaint the status bar only, silently dropping whatever the
        /// key changed. Asserting a lone key press yields "repaint everything" cannot catch that: with
        /// nothing declared the answer is already "everything", so those tests pass with the guard
        /// deleted. Verified by deleting it.
        /// </remarks>
        [Fact]
        public async Task ANonDeclaringEventAfterANarrowingOneForcesAFullRepaint()
        {
            var (viewer, state, document) = await NewViewerAsync();
            viewer.Render(document, state);

            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f)).ShouldBeTrue();
            viewer.HandleInput(new InputEvent.KeyDown(InputKey.T, InputModifier.None));

            Take(viewer).ShouldBeNull(
                "the key press changed something undeclared, so the readout's narrow region is not enough");
        }

        [Fact]
        public async Task DeclaredDamageIsConsumedByTheFrameThatTakesIt()
        {
            // Or every later frame inherits a region belonging to a change already painted, and the
            // accumulation only grows.
            var (viewer, state, document) = await NewViewerAsync();
            viewer.Render(document, state);

            viewer.HandleInput(new InputEvent.MouseMove(500f, 300f));
            Take(viewer).ShouldNotBeNull();

            viewer.TryTakeFrameDamage([]).ShouldBeFalse("nothing has been declared since");
        }

        /// <summary>Damage for the next frame, or null when the whole surface must be repainted.</summary>
        private static List<RectF32>? Take(DamageViewer viewer)
        {
            var into = new List<RectF32>();
            return viewer.TryTakeFrameDamage(into) ? into : null;
        }

        private static async Task<(DamageViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync()
        {
            var viewer = new DamageViewer(new RgbaImageRenderer(SurfaceW, SurfaceH));
            var plane = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    plane[y, x] = 1000f + y * ImageW + x;
                }
            }

            var document = await AstroImageDocument.AdoptImageAsync(
                new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                    new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                        0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                        RowOrder.TopDown, float.NaN, float.NaN)),
                DebayerAlgorithm.None);

            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

            var state = new ViewerState
            {
                HideChrome = false,
                ShowFileList = false,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                Zoom = 1f,
                ZoomToFit = false,
            };
            return (viewer, state, document);
        }

        private sealed class DamageViewer : ImageRendererBase<RgbaImage>
        {
            public DamageViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
            }

            /// <summary>The arranged rects the damage assertions compare against.</summary>
            public RectF32 StatusBar => StatusBarRect;

            public RectF32 InfoPanel => InfoPanelRect;

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float angleRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

        }
    }
}
