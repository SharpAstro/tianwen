using System.Threading;
using DIR.Lib;
using Microsoft.Extensions.Logging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Everything <c>tianwen-fits</c> does with input and between frames that is not SDL, Vulkan or the
/// window: the input router and what it falls back to, this host's toolbar policy, the steps that may
/// swap the document, the redraw question and the signals that reach the controller.
/// </summary>
/// <remarks>
/// <para>
/// It used to be written inline in <c>Program.cs</c>, where no test could reach it. The tests drove the
/// viewer through a COPY of this wiring (<c>UiRouting.RouterFor</c>), so a host that wired its router
/// differently from the copy passed every test: that is how a routed press on four regions of this host
/// did nothing while the suite was green. The host and the tests now construct this one class, so the
/// path a test drives is the path the window runs.
/// </para>
/// <para>
/// The GUI's viewer tab is a different host with a different policy (it cycles every stateful toolbar
/// button and has no reverse), which is why this is the STANDALONE viewer's host and not "the" viewer
/// host.
/// </para>
/// </remarks>
public sealed class StandaloneViewerHost<TSurface>
{
    private readonly ImageRendererBase<TSurface> _viewer;
    private readonly ViewerState _state;
    private readonly ViewerController _controller;
    private readonly BackgroundTaskTracker _tracker;
    private readonly SignalBus _bus;
    private readonly ILogger _logger;
    private readonly CancellationToken _appToken;

    public StandaloneViewerHost(ImageRendererBase<TSurface> viewer, ViewerState state, ViewerController controller,
        BackgroundTaskTracker tracker, SignalBus bus, ILogger logger, CancellationToken appToken)
    {
        _viewer = viewer;
        _state = state;
        _controller = controller;
        _tracker = tracker;
        _bus = bus;
        _logger = logger;
        _appToken = appToken;

        // Keys go through the engine's router, which answers the three things every surface answers the
        // same way -- an overlay that claimed the keyboard, a chord declared on a painted node, the focused
        // field -- before anything of this viewer's own runs. Presses route the same way: the regions the
        // last paint declared answer first, and whatever they decline reaches HandleUnroutedPress.
        //
        // A frame the ROUTER asks for repaints the whole surface. It asks for a changed hover, a tooltip, a
        // dial's drag or a focus change, none of which it can bound, and it asks BEFORE the viewer's own move
        // handler runs, which narrows a move's damage to the pixel readout: without the full frame a
        // button crossed while the pointer changed picture pixel kept its old fill.
        Router = new InputRouter(viewer.Ui, tracker, () =>
        {
            state.NeedsRedraw = true;
            viewer.RequestFullFrameDamage();
        })
        {
            Widgets = () => [viewer],
            Unhandled = evt => evt is InputEvent.MouseDown down
                ? HandleUnroutedPress(down)
                : viewer.HandleInput(evt),
        };

        // A link -- the object panel's Wikipedia and sky-atlas links, a picture's credit -- opens through the
        // signal the "?" menu's rows already post, which Program.cs answers with the OS browser. The GUI wires
        // its own router the same way (GuiEventHandlerBase). This host had no subscriber, so every link in the
        // viewer drew, took the hand pointer, swallowed the press and opened nothing (2026-09-18).
        Router.OpenUrl += url => bus.Post(new OpenUrlSignal(url));

        // THIS host's toolbar policy, which is not the embedded one. A left press opens whichever menu the
        // button has (OpenToolbarDropdown answers false for a button with none, so no parallel list of which
        // buttons are dropdowns is needed here); a right press falls past it so power users can still
        // reverse-cycle without summoning the popup; and an action the pure-state handler does not own goes
        // to the controller, which is where the DI-dependent ones live.
        //
        // The GUI's viewer tab wants none of that -- it cycles every stateful button and has no reverse --
        // and the two policies used to be two press WALKS, which is how single-click selection stayed broken
        // here after the embedded one was fixed. One hook, two policies, one walk.
        viewer.ToolbarPressPolicy = (viewerState, action, button) =>
        {
            if (button == MouseButton.Left && viewer.OpenToolbarDropdown(viewerState, action))
            {
                return;
            }

            var reverse = button == MouseButton.Right;
            if (!ViewerActions.HandleToolbarAction(viewerState, controller.Document, action, reverse,
                    split: viewer.Split, hasBeforePixels: viewer.HasBeforeImageTextures,
                    hasCrop: viewer.HasDisplayCrop))
            {
                controller.HandleToolbarAction(action, reverse, appToken);
            }
        };
        viewer.AppToken = appToken;

        // Signal subscriptions for the app-level actions the controller owns. The ones that need the
        // window or the shell (exit, fullscreen, opening a URL) stay with the host that has them.
        bus.Subscribe<PlateSolveSignal>(_ =>
            controller.HandleToolbarAction(ToolbarAction.PlateSolve, reverse: false, appToken));
        bus.Subscribe<EnhanceImageSignal>(_ =>
            controller.HandleToolbarAction(ToolbarAction.Enhance, reverse: false, appToken));
        bus.Subscribe<AutoCropSignal>(_ =>
            controller.HandleToolbarAction(ToolbarAction.AutoCrop, reverse: false, appToken));
        bus.Subscribe<OpenFileSignal>(_ =>
            controller.HandleToolbarAction(ToolbarAction.Open, reverse: false, appToken));

        // The Save dropdown's two rows. The annotation is read off the renderer here rather than reached for
        // inside the controller, because the renderer is what holds it -- so a plate-solve verification or a
        // polar-alignment overlay lands in the saved file too.
        bus.Subscribe<SaveImageSignal>(sig =>
            controller.SaveImage(sig.WithOverlays, sig.PngDepth, appToken, viewer.Annotation));
    }

    /// <summary>The router every key and press goes through.</summary>
    public InputRouter Router { get; }

    /// <summary>
    /// One pointer event from the platform. A press, a move and a release all go through the router, and
    /// whatever it does not claim reaches the viewer's own input path exactly as before; the wheel goes
    /// straight to the viewer.
    /// </summary>
    /// <remarks>
    /// The move and the release have to reach the router because a press ARMS gestures there: a
    /// <c>Layout.Content.Slider</c>'s press returns a capture the router holds, and only the router feeds
    /// it the moves and ends it on the release. This host used to route the press alone, so every popover
    /// dial jumped to where it was pressed and never followed the drag (2026-09-18, the tone popover's
    /// Boost), while the popover tests passed through a copy of the routing that sent every event to a
    /// router. With no capture held the router passes a move or release to <c>Unhandled</c>, which is the
    /// viewer, so the pan, the readout and the file-list gestures see what they always saw.
    /// </remarks>
    public bool HandlePointer(InputEvent evt) => evt switch
    {
        InputEvent.MouseDown down => RoutePress(down),
        InputEvent.MouseMove or InputEvent.MouseUp => Router.Handle(evt),
        _ => _viewer.HandleInput(evt),
    };

    /// <summary>A key press, through the router.</summary>
    public bool HandleKeyDown(InputEvent.KeyDown keyEvent)
    {
        Router.Handle(keyEvent);
        return true;
    }

    /// <summary>
    /// The release half. Bound because holding Space suspends a blink, which is the one binding here whose
    /// meaning lasts as long as the key is down.
    /// </summary>
    public bool HandleKeyUp(InputEvent.KeyUp keyEvent)
    {
        _viewer.HandleInput(keyEvent);
        return true;
    }

    /// <summary>
    /// A touchscreen pinch is its own input path -- a finger gesture, not a wheel. The viewer decides what
    /// to do with the source; this host only forwards.
    /// </summary>
    public void HandlePinch(float scale, float x, float y, PinchSource source)
        => _viewer.HandleInput(new InputEvent.Pinch(scale, x, y) { Source = source });

    /// <summary>The end of a pinch.</summary>
    public void HandlePinchEnd() => _viewer.HandleInput(new InputEvent.PinchEnd());

    /// <summary>A file dropped on the window: the same entry point a hand-off from a later launch uses.</summary>
    public void HandleDropFile(string path) => ViewerActions.HandleFileDrop(_state, path);

    /// <summary>
    /// The work that must run BETWEEN frames, before this frame's command buffer exists.
    /// </summary>
    /// <remarks>
    /// The document may only change between frames. A swap recreates the channel textures, and the
    /// recreate destroys the previous views; done inside a frame it invalidated whatever this frame's
    /// command buffer had already recorded against them (the cached-layer pre-pass), and the GPU faulted on
    /// submit -- the LiveKernelEvent 141 that took the Store viewer down on 2026-08-27, "VkImageView was
    /// destroyed" under the validation layer. So every step that can replace <c>controller.Source</c> runs
    /// here, and the upload itself runs in PrepareFrame.
    /// </remarks>
    public void BeforeFrame()
    {
        // Retire finished background work (the file dialog, a plate solve) and log anything that faulted.
        // Deliberately NOT gated on tracker.HasPending in WantsFrame: that would spin the render loop for
        // the whole of a multi-second solve on a GPU we want quiet. Each guarded operation flags a redraw in
        // its onFinally instead, so the loop wakes exactly once, here.
        _tracker.ProcessCompletions(_logger);

        _controller.HandleFileRequest(_appToken);

        // Apply a finished AI-enhance result (swaps in the enhanced document + flags a texture re-upload).
        // No-op until the background enhance task completes.
        _controller.TryApplyPendingEnhance(_appToken);
        _controller.TryApplyPendingCrop();

        if (_state.NeedsReprocess)
        {
            ViewerActions.Reprocess(_state);
        }

        // A frame that swaps its document repaints everything, whatever narrowing a mouse move in the same
        // tick may have declared: stale pixels from the previous document are the one failure the damage
        // tracking must never produce.
        if (_state.NeedsTextureUpdate)
        {
            _viewer.RequestFullFrameDamage();
        }
    }

    /// <summary>Whether this iteration of the loop should draw a frame.</summary>
    /// <remarks>
    /// Every step with a side effect is EVALUATED before anything is combined, so none may sit on the right
    /// of a ||. TickPlayback paces SER playback (it decodes the next frame ahead off the render thread and
    /// returns true only when a frame should be shown now), TickBlink paces the file-list step, and the sky
    /// request is CONSUMED by reading it -- on the right of a || a frame the star buffer asked for would be
    /// swallowed by an unrelated true and never drawn. A host adding its own step (the instance gate) owes
    /// it the same rule.
    /// </remarks>
    public bool WantsFrame()
    {
        var playback = _controller.TickPlayback();
        var blinked = _controller.TickBlink();
        var skyMoved = _viewer.TakeSkyRedrawRequest();
        return playback || blinked || skyMoved
            || _state.NeedsRedraw || _state.NeedsTextureUpdate || _state.RequestedFilePath is not null
            || _controller.IsLoadPending;
    }

    /// <summary>The work after a frame is drawn.</summary>
    public void AfterFrame()
    {
        _bus.ProcessPending();
        _state.NeedsRedraw = false;
        _controller.ReleaseCompletedTasks();
    }

    // The press walk this host used to carry is gone: every region it branched on now acts for itself --
    // a toolbar button through ToolbarPressPolicy, the file-list divider and its rows, the transport scrub,
    // and a Content.Slider through the engine's own drag. What is left is genuinely this host's and reaches
    // the router's Unhandled: the context menu on an unclaimed right press, and the pan.
    //
    // The order that mattered in the walk is the region stack's now -- topmost wins, later registration
    // wins -- and the one thing that is NOT automatic is that a region with a hit and no handler still
    // SWALLOWS the press. That was a live bug on four regions before this, so the rule to keep is:
    // registering a hit without a handler means "nothing happens here", never "someone else will do it".
    private bool RoutePress(InputEvent.MouseDown down)
    {
        _state.MouseScreenPosition = (down.X, down.Y);
        Router.Handle(down);
        return true; // every press consumed, as the old always-true OnMouseDown lambda was
    }

    private bool HandleUnroutedPress(InputEvent.MouseDown down)
    {
        var (px, py) = (down.X, down.Y);

        // An unclaimed right press on the image is the context menu. Only a press that would otherwise start
        // a pan opens one; a right press that already meant something was consumed by its region.
        if (down.Button == MouseButton.Right && _viewer.TryOpenImageContextMenu(_state, px, py))
        {
            return true;
        }

        // Left or middle starts panning (the PanZoomController gesture on the renderer; move and release
        // continue through the viewer's HandleInput).
        if (down.Button is MouseButton.Left or MouseButton.Middle)
        {
            _viewer.BeginViewportPan(px, py);
        }

        return true;
    }
}
