using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the Live Session tab draws over and inside itself must answer the pointer, found in the ZWO live check of
/// #813 (2026-09-28): the planetary view's Start did nothing and lit late, and the quit's message ran out of its card.
/// </summary>
public class LiveSessionTabCompositionTests
{
    private const int SurfaceW = 1280;
    private const int SurfaceH = 800;

    private static readonly RectF32 StartButton = new RectF32(40f, 80f, 120f, 28f);

    /// <summary>
    /// The smallest planetary view: one Start button registered on the view ITSELF, as <c>VkPlanetaryTab</c> registers
    /// its controls, which only a router reaching the view through the tab can find.
    /// </summary>
    private sealed class StubPlanetaryView(RgbaImageRenderer renderer) : PixelWidgetBase<RgbaImage>(renderer), IPlanetaryViewWidget<RgbaImage>
    {
        public int Starts { get; private set; }

        public PixelWidgetBase<RgbaImage> Widget => this;

        public void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser, RectF32 contentRect)
        {
            BeginFrame();
            RenderLayout(Layout.Builder.Text("Start", 14f)
                    .BgHover(new RGBAColor32(0x40, 0x40, 0x40, 0xff))
                    .Clickable(new HitResult.ButtonHit("PlanetaryCaptureToggle"), _ => Starts++),
                StartButton);
        }
    }

    private static (LiveSessionTab<RgbaImage> Tab, StubPlanetaryView View) PlanetaryTab(RgbaImageRenderer renderer)
    {
        var view = new StubPlanetaryView(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var tab = new LiveSessionTab<RgbaImage>(renderer)
        {
            DpiScale = 1f,
            FontPath = FontResolver.ResolveSystemFont(),
            PlanetaryView = view,
        };
        tab.Render(new LiveSessionState { Mode = LiveSessionMode.Planetary }, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());
        return (tab, view);
    }

    [Fact]
    public void APressOnAControlOfThePlanetaryViewReachesIt()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var (tab, view) = PlanetaryTab(renderer);
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [tab] };
        var (x, y) = (StartButton.X + StartButton.Width / 2f, StartButton.Y + StartButton.Height / 2f);

        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));

        view.Starts.ShouldBe(1, "the router finds the view's own regions through the tab that painted it");
    }

    [Fact]
    public void MovingOntoAControlOfThePlanetaryViewDrawsItLitAtOnce()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var (tab, view) = PlanetaryTab(renderer);
        var frames = 0;
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => frames++) { Widgets = () => [tab] };
        var (x, y) = (StartButton.X + StartButton.Width / 2f, StartButton.Y + StartButton.Height / 2f);

        router.Handle(new InputEvent.MouseMove(StartButton.X - 20f, y));
        frames = 0;
        router.Handle(new InputEvent.MouseMove(x, y));

        view.Pointer.ShouldBe((x, y), "the view lights its button against the pointer it is handed as it paints");
        frames.ShouldBeGreaterThan(0, "the move changed what the button shows, so it asks for the frame itself");
    }

    /// <summary>The planetary view as the GUI's is: the viewer itself, drawing the capture's own state.</summary>
    private sealed class PlanetaryViewer(RgbaImageRenderer renderer)
        : ViewerE2E.Surface(renderer, new SignalBus()), IPlanetaryViewWidget<RgbaImage>
    {
        public PixelWidgetBase<RgbaImage> Widget => this;

        public void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser, RectF32 contentRect)
        {
            if (controller is null)
            {
                return;
            }
            SetContentRegion(contentRect);
            Render(controller.Source, controller.ViewerState);
        }
    }

    /// <summary>
    /// In Planetary mode the keys go to the planetary view, the viewer on screen, and never the hidden preview: F and R
    /// used to zoom a viewer nobody could see (found in the ZWO live check, 2026-09-28). They are the viewer's own keys
    /// now, so the view answers them itself.
    /// </summary>
    [Fact]
    public void FAndRZoomThePlanetaryViewOnScreenNotTheHiddenPreview()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var planetary = new ViewerState();
        var controller = new PlanetaryCaptureController(planetary, new SystemTimeProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanetaryCaptureController>.Instance);
        var tab = new LiveSessionTab<RgbaImage>(renderer)
        {
            DpiScale = 1f,
            FontPath = FontResolver.ResolveSystemFont(),
            PlanetaryView = new PlanetaryViewer(renderer) { FontPath = FontResolver.ResolveSystemFont() },
            PlanetaryCapture = controller,
        };
        tab.Render(new LiveSessionState { Mode = LiveSessionMode.Planetary }, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());
        planetary.ZoomToFit = false;
        planetary.Zoom = 3f;

        tab.HandleInput(new InputEvent.KeyDown(InputKey.F, InputModifier.None)).ShouldBeTrue();
        planetary.ZoomToFit.ShouldBeTrue("F fits the view on screen");

        tab.HandleInput(new InputEvent.KeyDown(InputKey.R, InputModifier.None)).ShouldBeTrue();
        (planetary.ZoomToFit, planetary.Zoom).ShouldBe((false, 1f), "R shows it at 1:1");
    }

    /// <summary>The planetary view as the GUI's paints its left side: the capture panel's own tree, in its 280 units.</summary>
    private sealed class PanelView(RgbaImageRenderer renderer) : PixelWidgetBase<RgbaImage>(renderer), IPlanetaryViewWidget<RgbaImage>
    {
        private readonly PlanetaryControlPanel _panel = new PlanetaryControlPanel();

        public PixelWidgetBase<RgbaImage> Widget => this;

        public void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser, RectF32 contentRect)
        {
            BeginFrame();
            _panel.FollowCamera(focuser);
            RenderLayout(_panel.Build(new NodePlanetaryCapture(), focuser, default, bus: null),
                new RectF32(contentRect.X, contentRect.Y, PlanetaryControlPanel.BaseWidth, contentRect.Height));
        }
    }

    /// <summary>
    /// The mode menu hangs over the capture panel, which scrolls and so registers a region over its whole column: a press
    /// on a menu row there used to reach the panel, and the mode never changed (#1635 review round 1, B1). The tab's own
    /// regions are asked first, as everything it paints over its view is its own (DIR.Lib 11.25).
    /// </summary>
    [Fact]
    public void AModeChosenFromTheMenuOverTheCapturePanelIsTaken()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var state = new LiveSessionState { Mode = LiveSessionMode.Planetary };
        var tab = new LiveSessionTab<RgbaImage>(renderer)
        {
            DpiScale = 1f,
            FontPath = FontResolver.ResolveSystemFont(),
            PlanetaryView = new PanelView(renderer) { FontPath = FontResolver.ResolveSystemFont() },
        };
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [tab] };
        void Paint() => tab.Render(state, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());
        void Press(ClickableRegion region)
        {
            var (x, y) = (region.X + region.Width / 2f, region.Y + region.Height / 2f);
            router.Handle(new InputEvent.MouseDown(x, y));
            router.Handle(new InputEvent.MouseUp(x, y));
        }
        List<ClickableRegion> Regions()
        {
            var regions = new List<ClickableRegion>();
            tab.CollectPaintedRegions(regions);
            return regions;
        }

        Paint();
        Press(Regions().Single(static r => r.Result is HitResult.ButtonHit { Action: "ModePill" }));
        Paint();
        state.ModeDropdown.IsOpen.ShouldBeTrue("the pill opens the menu");
        var preview = Regions().Single(static r => r.Result is HitResult.ListItemHit { Index: 0 });
        preview.X.ShouldBeLessThan(PlanetaryControlPanel.BaseWidth, "the row lies over the capture panel, which is the case at stake");
        Press(preview);

        state.Mode.ShouldBe(LiveSessionMode.Preview);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void TheQuitsMessageStaysInsideItsCard(float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
        var dialog = QuitDialog.DevicesConnected(1, aCameraNeedsWarming: true);
        tab.Render(new LiveSessionState { QuitDialog = dialog }, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());

        var regions = tab.GetRegisteredRegions();
        var other = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "QuitOther" });
        var @default = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "QuitDefault" });
        var nodes = new List<Layout.ArrangedNode<float>>();
        tab.CollectPaintedNodes(nodes);

        foreach (var line in new[] { dialog.Title, dialog.Message, dialog.KeyHint })
        {
            var text = nodes.Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == line);
            text.Bounds.X.ShouldBeGreaterThanOrEqualTo(other.X - 0.5f, $"'{line}' starts inside the card");
            (text.Bounds.X + text.Bounds.Width).ShouldBeLessThanOrEqualTo(@default.X + @default.Width + 0.5f,
                $"'{line}' ends inside the card, no further right than its answers");
            var content = (Layout.Content.Text)((Layout.Node.Leaf)text.Node).Content;
            renderer.MeasureText(line, tab.FontPath, content.FontSize * dpiScale).Width
                .ShouldBeLessThanOrEqualTo(text.Bounds.Width + 0.5f, $"'{line}' is given its whole width, not trimmed");
        }
    }
}
