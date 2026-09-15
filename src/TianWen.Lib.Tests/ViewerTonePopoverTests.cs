using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The curves boost and the highlight soft clip share one toolbar button and one popover, and the
/// popover names the display HDR that is neither of them.
/// </summary>
/// <remarks>
/// <para><b>Two buttons until 8.1, one of them misnamed.</b> "HDR" was a soft knee applied after
/// the MTF and inside [0, 1]: it never asks the panel for a nit above SDR white, so the label
/// promised the one thing the viewer cannot do. Folded with "Boost" on the terms Calibrate and
/// SPCC folded into the white-balance popover -- one control, with room to say how its parts
/// relate and what it is not.</para>
/// <para><b>Read off the ARRANGED TREE, not off the window.</b> The panel is one
/// <c>Layout.Node</c> tree that the engine measures and places, so a test asks the engine where
/// things went instead of sweeping the surface for registered regions and re-deriving the panel's
/// own arithmetic to interpret what it found. Its white-balance sibling still sweeps, because that
/// popover still positions itself by hand; closing that gap everywhere is
/// docs/plans/viewer-layout-engine.md. Behaviour -- what a press or a drag DOES -- is still asked
/// of the real input path, at the rect the tree reports.</para>
/// </remarks>
[Collection("UI")]
public class ViewerTonePopoverTests
{
    private const uint WindowW = 900;
    private const uint WindowH = 700;
    private const int ImageW = 8;
    private const int ImageH = 6;

    private sealed class ToneViewer : ImageRendererBase<RgbaImage>
    {
        public ToneViewer(RgbaImageRenderer renderer, SignalBus bus) : base(renderer)
        {
            Bus = bus;
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

        public ImmutableArray<Layout.ArrangedNode<float>> Arranged => ToneLayoutForTest;

        public SliderState Boost => ToneBoostSliderState;

        public SliderState Amount => ToneAmountSliderState;

        public SliderState Knee => ToneKneeSliderState;
    }

    private static async Task<(ToneViewer Viewer, ViewerState State, AstroImageDocument Document)>
        NewViewerAsync(RgbaImageRenderer renderer, CancellationToken ct)
    {
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var viewer = new ToneViewer(renderer, new SignalBus());
        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
        var state = new ViewerState
        {
            ShowFileList = false,
            ShowHistogram = false,
            ShowInfoPanel = true,
            StretchMode = StretchMode.None,
            ZoomToFit = false,
            Zoom = 1f,
        };
        viewer.Render(document, state);
        return (viewer, state, document);
    }

    /// <summary>The tone button's painted rect, which the popover hangs from.</summary>
    private static RectF32 Button(ToneViewer viewer)
    {
        viewer.TryGetPaintedToolbarRect(ToolbarAction.Tone, out var rect)
            .ShouldBeTrue("the folded tone button is on the bar");
        return rect;
    }

    private static void Press(ToneViewer viewer, float x, float y)
    {
        viewer.HandleInput(new InputEvent.MouseDown(x, y));
        viewer.HandleInput(new InputEvent.MouseUp(x, y));
    }

    private static void OpenPanel(ToneViewer viewer, ViewerState state, AstroImageDocument document)
    {
        var button = Button(viewer);
        Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
        state.TonePopover.IsOpen.ShouldBeTrue("the button opens the popover");
        viewer.Render(document, state);
    }

    private static RectF32 Rect(Layout.ArrangedNode<float> n)
        => new RectF32(n.Bounds.X, n.Bounds.Y, n.Bounds.Width, n.Bounds.Height);

    /// <summary>
    /// The popover's own box. The arranged root is the full-window overlay the backdrop fills, and the
    /// content is what the engine placed against the button: the node right after the
    /// <see cref="Layout.Node.Anchored"/>, which pre-order puts at one greater depth (an Anchored is
    /// recorded against the rect it was GIVEN, so its own entry carries the whole window).
    /// </summary>
    private static RectF32 Panel(ToneViewer viewer)
    {
        var arranged = viewer.Arranged;
        for (var i = 0; i < arranged.Length - 1; i++)
        {
            if (arranged[i].Node is Layout.Node.Anchored)
            {
                return Rect(arranged[i + 1]);
            }
        }

        throw new Xunit.Sdk.XunitException("the popover arranged no anchored content");
    }

    /// <summary>Where the engine put the node carrying this button hit.</summary>
    private static RectF32 RegionOf(ToneViewer viewer, string action)
    {
        var found = viewer.Arranged
            .Where(n => n.Node.Hit is HitResult.ButtonHit b && b.Action == action)
            .ToArray();
        found.Length.ShouldBe(1, $"exactly one node states the hit {action}");
        return Rect(found[0]);
    }

    /// <summary>Where the engine put one dial's track, found by the state the leaf points AT rather
    /// than by a key beside it: a <see cref="Layout.Content.Slider"/> carries its caller-owned state,
    /// so identity is the question and there is nothing to keep in step.</summary>
    private static RectF32 DialOf(ToneViewer viewer, SliderState dial)
    {
        var found = viewer.Arranged
            .Where(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Slider s } && ReferenceEquals(s.State, dial))
            .ToArray();
        found.Length.ShouldBe(1, "exactly one leaf declares this dial");
        return Rect(found[0]);
    }

    /// <summary>
    /// Whether a dial answers a press: the rect comes from the tree, the ANSWER from the real region
    /// the paint registered, because a dim dial is still laid out and still registers -- it simply
    /// binds no press, so it swallows one instead of letting it reach the backdrop.
    /// </summary>
    private static bool IsDraggable(ToneViewer viewer, SliderState dial)
    {
        var track = DialOf(viewer, dial);
        var x = track.X + (track.Width / 2f);
        var y = track.Y + (track.Height / 2f);
        viewer.HitTest(x, y).ShouldBeOfType<HitResult.SliderStateHit>(
            "a dim dial still registers, so a press on it stops there")
            .State.ShouldBeSameAs(dial);

        return viewer.GetRegisteredRegions()
            .Any(r => r.Result is HitResult.SliderStateHit hit
                && ReferenceEquals(hit.State, dial) && r.OnPress is not null);
    }

    /// <summary>
    /// The fold itself: one button where two stood, and it opens a panel rather than a menu of
    /// presets. There is no longer a separate boost or HDR action for a host to put back by hand.
    /// </summary>
    [Fact]
    public async Task TheBarHasOneToneButtonAndItOpensAPanelRatherThanAMenu()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        Button(viewer).Width.ShouldBeGreaterThan(0f);
        state.TonePopover.IsOpen.ShouldBeFalse("closed until pressed");
        viewer.Arranged.ShouldBeEmpty("a closed popover arranges nothing");

        OpenPanel(viewer, state, document);

        state.ToolbarDropdown.IsOpen.ShouldBeFalse("a popover, not a dropdown of presets");
        state.OverlayOwnsPointer.ShouldBeTrue("an open popover owns the pointer");
        viewer.Arranged.ShouldNotBeEmpty();
    }

    /// <summary>
    /// What the fold is FOR: the panel names display HDR and cannot offer it. Drawn rather than
    /// hidden, because someone hunting for HDR arrives at exactly this control and a menu whose
    /// entries come and go teaches nothing about how to make them available.
    /// </summary>
    [Fact]
    public async Task ThePanelNamesTheDisplayHdrItCannotDoAndSwallowsAPressOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);
        var block = RegionOf(viewer, "ToneDisplayHdr");

        // Inert, and that is the assertion: a press lands on it and the panel stays open, rather
        // than falling through to the backdrop and closing what the user was reading.
        Press(viewer, block.X + (block.Width / 2f), block.Y + (block.Height / 2f));

        state.TonePopover.IsOpen.ShouldBeTrue("the display-HDR block consumes a press without acting");
    }

    /// <summary>
    /// The soft clip is the dial that always applies, so it is always draggable, and dragging it
    /// writes the same field the H ladder does.
    /// </summary>
    [Fact]
    public async Task DraggingTheSoftClipAmountRaisesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        state.HdrAmount.ShouldBe(0f, "a fresh viewer clips nothing");
        OpenPanel(viewer, state, document);

        var track = DialOf(viewer, viewer.Amount);
        var x = track.X + track.Width - 1f;
        var y = track.Y + (track.Height / 2f);
        viewer.HandleInput(new InputEvent.MouseDown(x, y));

        state.HdrAmount.ShouldBeGreaterThan(1f, "the far end of the track is the hardest clip");

        // The gesture is live, so a move with no press between them still tracks.
        viewer.HandleInput(new InputEvent.MouseMove(track.X + 1f, y));
        var dragged = state.HdrAmount;
        dragged.ShouldBeLessThan(0.1f, "the near end of the track is no clip at all");

        // Release ends it, and the assertion is that the NEXT move changes nothing. That is what the
        // drag flag was there to make true, and what a spent capture now makes true with no flag.
        viewer.HandleInput(new InputEvent.MouseUp(track.X + 1f, y));
        viewer.HandleInput(new InputEvent.MouseMove(track.X + track.Width - 1f, y));
        state.HdrAmount.ShouldBe(dragged, "release ends the drag");
    }

    /// <summary>
    /// The knee only means something once the amount is above zero, so it answers no press until
    /// then. A live band over a control that cannot move is a slider that follows the pointer and
    /// changes nothing, which reads as broken rather than as an unmet precondition.
    /// </summary>
    [Fact]
    public async Task TheKneeIsNotDraggableUntilTheAmountIsAboveZero()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);
        IsDraggable(viewer, viewer.Knee).ShouldBeFalse();

        state.HdrAmount = 1f;
        viewer.Render(document, state);

        IsDraggable(viewer, viewer.Knee).ShouldBeTrue();
    }

    /// <summary>
    /// The boost keeps the precondition its own button carried -- detected stars -- and the panel
    /// says so instead of offering a dial that does nothing. The fixture has no detected stars, so
    /// this is the state a freshly opened frame is in.
    /// </summary>
    [Fact]
    public async Task TheBoostIsNotDraggableWithoutDetectedStars()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        document.Stars.ShouldBeNull("the fixture has not been through star detection");
        OpenPanel(viewer, state, document);

        IsDraggable(viewer, viewer.Boost).ShouldBeFalse();
    }

    /// <summary>
    /// The box is the engine's MEASUREMENT of the tree, which is what stops a line running out of
    /// its own panel: the first draft sized itself from a hand-kept union of the strings someone
    /// remembered, and the soft-clip heading was not among them. Asserted as containment rather
    /// than as a pixel count, because the face is the host's.
    /// </summary>
    [Fact]
    public async Task EveryArrangedNodeFitsInsideThePanelAndThePanelFitsOnScreen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);
        var panel = Panel(viewer);

        panel.X.ShouldBeGreaterThanOrEqualTo(0f);
        panel.Right.ShouldBeLessThanOrEqualTo(WindowW, "the panel stays on screen");

        // The backdrop is the full window by construction, so the containment question is about the
        // popover's CONTENT: everything from the anchored node onward.
        foreach (var node in viewer.Arranged.SkipWhile(n => n.Node is not Layout.Node.Anchored).Skip(1))
        {
            var r = Rect(node);
            r.X.ShouldBeGreaterThanOrEqualTo(panel.X - 0.5f);
            r.Right.ShouldBeLessThanOrEqualTo(panel.Right + 0.5f,
                "a line that overflows its own box is the bug the engine measure exists to prevent");
            r.Bottom.ShouldBeLessThanOrEqualTo(panel.Bottom + 0.5f);
        }
    }

    /// <summary>A hidden widget consumes no input: closed, the popover arranges and registers
    /// nothing, so a press where a dial was does nothing.</summary>
    [Fact]
    public async Task AClosedPopoverRegistersNoDials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);
        var track = DialOf(viewer, viewer.Amount);
        var x = track.X + (track.Width / 2f);
        var y = track.Y + (track.Height / 2f);

        state.TonePopover.Close();
        viewer.Render(document, state);

        viewer.Arranged.ShouldBeEmpty();
        viewer.HitTest(x, y).ShouldNotBeOfType<HitResult.SliderStateHit>(
            "a closed panel answers no drag where its dial used to be");
    }

    /// <summary>
    /// <b>An open popover confines hover to its own content, and a CLOSED one gives the pointer back.</b>
    /// </summary>
    /// <remarks>
    /// The claim is made BY BEING PAINTED, which is what makes it self-retiring for the keyboard: a
    /// claimant that is no longer displayed simply declines. A RECT cannot decline, so the frame has to
    /// clear it -- and the engine's own clear is keyed on a frame counter that no host in this codebase
    /// bumps, so the first BeginFrame clears it and every later call returns early. Without the viewer
    /// clearing it itself a popover that had closed went on owning the pointer for the life of the
    /// process, and every hover outside its ghost rect stayed cold.
    /// </remarks>
    [Fact]
    public async Task AClosedPopoverStopsOwningThePointer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        viewer.Ui.PointerOwner.ShouldBeNull("nothing owns the pointer before a popover opens");

        OpenPanel(viewer, state, document);
        var panel = Panel(viewer);
        var owner = viewer.Ui.PointerOwner.ShouldNotBeNull("an open popover owns the pointer");
        owner.X.ShouldBe(panel.X);
        owner.Width.ShouldBe(panel.Width);

        state.TonePopover.Close();
        viewer.Render(document, state);

        viewer.Ui.PointerOwner.ShouldBeNull("and a closed one gives it back");
    }

    /// <summary>Escape closes it, through the one keyboard-claimant check rather than a branch.</summary>
    [Fact]
    public async Task EscapeClosesThePopover()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);

        viewer.HandleInput(new InputEvent.KeyDown(InputKey.Escape));

        state.TonePopover.IsOpen.ShouldBeFalse("Escape closes it");
    }

    /// <summary>
    /// A menu in everything but its contents: a press anywhere else closes it, and a second press
    /// on the button closes what the first opened rather than re-opening it.
    /// </summary>
    [Fact]
    public async Task APressElsewhereClosesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, ct);

        OpenPanel(viewer, state, document);
        var image = viewer.ImageArea;
        Press(viewer, image.X + (image.Width / 2f), image.Y + (image.Height / 2f));
        state.TonePopover.IsOpen.ShouldBeFalse("a press on the picture closes it");

        // A frame is drawn between two presses in the running app, and it matters here: while the
        // panel was open its backdrop covered the whole window, so without a repaint the next press
        // lands on a backdrop that is no longer painted.
        viewer.Render(document, state);
        OpenPanel(viewer, state, document);

        var button = Button(viewer);
        Press(viewer, button.X + (button.Width / 2f), button.Y + (button.Height / 2f));
        state.TonePopover.IsOpen.ShouldBeFalse("a second press on the button closes what the first opened");
    }
}
