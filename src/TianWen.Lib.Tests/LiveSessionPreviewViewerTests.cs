using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Live Session preview pane IS the viewer (step 4 of P1, docs/plans/live-session-preview.md): the viewer's own
/// toolbar, offering what a preview can use, its presses and keys the viewer's, and the solve its host's, run by the
/// node for the OTA whose frame is on show. The tab used to draw a smaller toolbar of its own and copy the viewer's
/// pan, zoom and keys.
/// </summary>
[Collection("UI")]
public class LiveSessionPreviewViewerTests
{
    private const int SurfaceW = 1600;
    private const int SurfaceH = 900;

    private static Image Frame()
    {
        var plane = new float[30, 40];
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                plane[y, x] = 100f + x + (y * 40);
            }
        }
        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([plane], BitDepth.Float32, maxValue: 4000f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private sealed record Pane(LiveSessionTab<RgbaImage> Tab, ViewerE2E.Surface Viewer, LiveSessionState State,
        SignalBus Bus, InputRouter Router, List<int> Solves);

    private static Pane PreviewPane(RgbaImageRenderer renderer, int otaCount = 1, int frameOta = 0)
    {
        var bus = new SignalBus();
        var viewer = new ViewerE2E.Surface(renderer, bus);
        var tab = new LiveSessionTab<RgbaImage>(renderer)
        {
            DpiScale = 1f,
            FontPath = FontResolver.ResolveSystemFont(),
            Bus = bus,
            PreviewView = viewer,
        };
        var state = new LiveSessionState { Mode = LiveSessionMode.Preview };
        state.ResizePreviewArrays(otaCount);
        var frames = new Image?[otaCount];
        frames[frameOta] = Frame();
        state.LastCapturedImages = frames;
        var solves = new List<int>();
        bus.Subscribe<PlateSolvePreviewSignal>(signal => solves.Add(signal.OtaIndex));
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [tab] };
        var pane = new Pane(tab, viewer, state, bus, router, solves);
        Render(pane);
        return pane;
    }

    private static void Render(Pane pane)
        => pane.Tab.Render(pane.State, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());

    private static void Click(Pane pane, RectF32 rect)
    {
        var (x, y) = (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));
        pane.Router.Handle(new InputEvent.MouseDown(x, y));
        pane.Router.Handle(new InputEvent.MouseUp(x, y));
        pane.Bus.ProcessPending();
    }

    /// <summary>The pane shows the viewer's toolbar, exactly what the preview offers, in the viewer's order.</summary>
    [Fact]
    public void The_preview_shows_the_viewers_own_toolbar_with_what_a_preview_offers()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pane = PreviewPane(renderer);

        pane.Viewer.PaintedToolbarButtons.Select(button => button.Action).ToArray()
            .ShouldBe(ToolbarOffer.LivePreview.Buttons.Select(button => button.Action).ToArray());
        pane.Viewer.TryGetPaintedToolbarRect(ToolbarAction.StretchToggle, out _).ShouldBeTrue("a live frame is a picture");
    }

    /// <summary>
    /// The [Live] button (#1111) starts a live view of its OTA's camera at the settings the Preview's still would take:
    /// its exposure, gain and binning. Without a live view to drive (a host that wired none) it is dim and starts nothing.
    /// </summary>
    [Fact]
    public void Its_Live_button_starts_a_live_view_at_the_settings_a_still_would_take()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pane = PreviewPane(renderer);
        var starts = new List<StartLiveViewSignal>();
        pane.Bus.Subscribe<StartLiveViewSignal>(signal => starts.Add(signal));
        (pane.State.PreviewExposureSeconds[0], pane.State.PreviewGain[0], pane.State.PreviewBinning[0]) = (0.5, 120, 2);

        Render(pane);
        ClickLive(pane);
        starts.ShouldBeEmpty("no live view is wired, so the button is dim");

        pane.Tab.LiveView = new LiveViewController(new SystemTimeProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveViewController>.Instance);
        Render(pane);
        ClickLive(pane);

        starts.ShouldBe([new StartLiveViewSignal(0, 0.5, 120, 2)]);

        static void ClickLive(Pane pane)
        {
            var live = pane.Tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "PreviewLive0" });
            Click(pane, new RectF32(live.X, live.Y, live.Width, live.Height));
        }
    }

    /// <summary>
    /// The Lens row (#681) while a camera that drives its own lens is live: a click is ONE step (what a user expects of a
    /// focus button), a button held repeats its step once it has been held for the hold delay and is lit while it does, and
    /// letting go ends it. Driven over a clock the test advances, so the repeat is counted, never timed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task A_lens_button_clicked_is_one_step_and_held_repeats_lit_until_let_go()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pane = PreviewPane(renderer);
        var clock = new FakeTimeProviderWrapper { ExternalTimePump = true };
        var live = new LiveViewController(clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveViewController>.Instance);
        var sent = live.Began(0, new LiveViewStateDto { Camera = "Canon EOS 6D", Running = true, CanDriveLens = true }).Reader;
        pane.Tab.LiveView = live;
        Render(pane);

        var far = pane.Tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "LensFarSmall" });
        var (x, y) = (far.X + (far.Width / 2f), far.Y + (far.Height / 2f));

        // A click: one step, and nothing more however long the clock runs on.
        pane.Router.Handle(new InputEvent.MouseDown(x, y));
        pane.Router.Handle(new InputEvent.MouseUp(x, y));
        (await sent.ReadAsync(ct)).ShouldBe(LensFocusStep.FarSmall);
        clock.Advance(LiveViewController.LensHoldDelay * 3);
        sent.TryRead(out _).ShouldBeFalse("a click is one step");
        live.HeldLensStep.ShouldBeNull();

        // Held: a step at once, the same again once the hold delay has passed and on every repeat after, the button lit.
        pane.Router.Handle(new InputEvent.MouseDown(x, y));
        (await sent.ReadAsync(ct)).ShouldBe(LensFocusStep.FarSmall);
        live.HeldLensStep.ShouldBe(LensFocusStep.FarSmall);
        Render(pane);
        var nodes = new List<Layout.ArrangedNode<float>>();
        pane.Tab.CollectPaintedNodes(nodes);
        nodes.Single(n => n.Node.Hit is HitResult.ButtonHit { Action: "LensFarSmall" }).Node.Background
            .ShouldBe(GuiTheme.PrimaryButtonBg, "a held button is lit while it repeats");
        clock.Advance(LiveViewController.LensHoldDelay);
        (await sent.ReadAsync(ct)).ShouldBe(LensFocusStep.FarSmall);
        clock.Advance(LiveViewController.LensRepeatInterval);
        (await sent.ReadAsync(ct)).ShouldBe(LensFocusStep.FarSmall);

        // Let go: no more steps, and the button is no longer lit.
        pane.Router.Handle(new InputEvent.MouseUp(x, y));
        live.HeldLensStep.ShouldBeNull();
        clock.Advance(LiveViewController.LensRepeatInterval * 5);
        sent.TryRead(out _).ShouldBeFalse("letting go ends the repeat");
    }

    /// <summary>
    /// The viewer's Solve button solves through the node, for the OTA whose frame is on show, and stands down while
    /// that solve runs. It replaced the Solve button each OTA column carried.
    /// </summary>
    [Fact]
    public void Its_Solve_button_asks_the_node_to_solve_the_frame_on_show_and_waits_for_it()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pane = PreviewPane(renderer, otaCount: 2, frameOta: 1);
        pane.Viewer.TryGetPaintedToolbarRect(ToolbarAction.PlateSolve, out var solve).ShouldBeTrue("an unsolved frame can be solved");

        Click(pane, solve);

        pane.Solves.ToArray().ShouldBe(new[] { 1 }, "the OTA whose frame is on show");

        pane.State.PreviewPlateSolving[1] = true;
        Render(pane);
        pane.Viewer.TryGetPaintedToolbarRect(ToolbarAction.PlateSolve, out _).ShouldBeFalse("not again while it solves");
    }

    /// <summary>
    /// The keys are the viewer's, the ones its tooltips name: P solves through the host, T switches the stretch. A key
    /// whose button the preview does not offer is not the viewer's (S, the stars), and the window's keys never reach
    /// it: Escape through the viewer would quit the app.
    /// </summary>
    [Fact]
    public void The_keys_are_the_viewers_and_the_windows_keys_stay_the_windows()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pane = PreviewPane(renderer);
        var exits = 0;
        pane.Bus.Subscribe<RequestExitSignal>(_ => exits++);
        var viewerState = pane.Viewer.State.ShouldNotBeNull();
        var stretch = viewerState.StretchMode;
        var stars = viewerState.ShowStarOverlay;

        pane.Tab.HandleInput(new InputEvent.KeyDown(InputKey.P, InputModifier.None)).ShouldBeTrue();
        pane.Tab.HandleInput(new InputEvent.KeyDown(InputKey.T, InputModifier.None)).ShouldBeTrue();
        pane.Tab.HandleInput(new InputEvent.KeyDown(InputKey.S, InputModifier.None)).ShouldBeFalse("no stars on offer yet");
        pane.Tab.HandleInput(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None));
        pane.Bus.ProcessPending();

        pane.Solves.ToArray().ShouldBe(new[] { 0 });
        viewerState.StretchMode.ShouldNotBe(stretch, "T is the viewer's stretch toggle");
        viewerState.ShowStarOverlay.ShouldBe(stars);
        exits.ShouldBe(0, "Escape is the window's");
    }

    /// <summary>With more than one OTA, a picker row says whose frame it is; with one, there is none.</summary>
    [Fact]
    public void Only_more_than_one_OTA_gets_the_picker_row()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var single = PreviewPane(renderer);
        var pickerless = single.Viewer.PaintedToolbarButtons.First().Rect.Y;

        using var renderer2 = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var pair = PreviewPane(renderer2, otaCount: 2);
        pair.Viewer.PaintedToolbarButtons.First().Rect.Y.ShouldBeGreaterThan(pickerless, "the toolbar sits under the picker");
    }
}
