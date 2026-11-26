using System;
using System.Threading;
using DIR.Lib;
using Microsoft.Extensions.Logging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Everything <c>tianwen-edit</c> does with input and between frames that is not SDL, Vulkan or the window: the input router,
/// the viewer's toolbar policy, the steps that run between frames, the redraw question and the paint of the guided screen.
/// The window and the headless tests construct this one class, as <see cref="StandaloneViewerHost{TSurface}"/> is for
/// <c>tianwen-fits</c>, so the path a test drives is the path the window runs.
/// </summary>
/// <remarks>
/// <para>
/// The viewer inside the canvas is the viewer's own renderer, with its toolbar cut to the controls that are about looking
/// (<see cref="ViewerOffer"/>): no Open file, Enhance, crop or solve, because those are operations here, and no file list.
/// </para>
/// <para>
/// Reading the project is off the render thread (the actions' tracker slot) and its answer is adopted in
/// <see cref="BeforeFrame"/>, where the document may change: the render thread never waits for the runner.
/// </para>
/// </remarks>
public sealed class WorkbenchHost<TSurface>
{
    /// <summary>The viewer's toolbar here: how the picture is shown, and nothing that changes it.</summary>
    public static ToolbarOffer ViewerOffer { get; } = ToolbarOffer.Of(
        ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
        ToolbarAction.Channel, ToolbarAction.Debayer, ToolbarAction.Tone,
        ToolbarAction.Zoom, ToolbarAction.Overlays, ToolbarAction.WhiteBalance);

    private readonly ImageRendererBase<TSurface> _viewer;
    private readonly ViewerState _viewerState;
    private readonly ViewerController _controller;
    private readonly WorkbenchViewDriver<TSurface> _views;
    private readonly BackgroundTaskTracker _tracker;
    private readonly SignalBus _bus;
    private readonly ILogger _logger;
    private readonly CancellationToken _appToken;
    private float _width;
    private float _height;

    public WorkbenchHost(Renderer<TSurface> renderer, ImageRendererBase<TSurface> viewer, ViewerState viewerState,
        ViewerController controller, WorkbenchState state, WorkbenchActions actions, BackgroundTaskTracker tracker,
        SignalBus bus, ILogger logger, CancellationToken appToken)
    {
        _viewer = viewer;
        _viewerState = viewerState;
        _controller = controller;
        _views = new WorkbenchViewDriver<TSurface>(actions, viewerState, controller, viewer);
        _tracker = tracker;
        _bus = bus;
        _logger = logger;
        _appToken = appToken;
        State = state;
        Actions = actions;

        viewerState.ShowFileList = false;
        viewer.Offer = ViewerOffer;
        viewer.AppToken = appToken;
        Screen = new WorkbenchScreen<TSurface>(renderer, viewer) { Bus = bus };

        Router = new InputRouter(Screen.Ui, tracker, () =>
        {
            state.NeedsRedraw = true;
            viewer.RequestFullFrameDamage();
        })
        {
            Widgets = () => [Screen],
            Unhandled = evt => evt is InputEvent.MouseDown down ? HandleUnroutedPress(down) : viewer.HandleInput(evt),
        };
        Router.OpenUrl += url => bus.Post(new OpenUrlSignal(url));

        // A left press opens whichever menu the toolbar button has, a right press falls past it to reverse-cycle, and an action
        // the pure-state handler does not own goes to the controller. The offer above leaves out the ones that need it for
        // an operation (the solve, Enhance, the crop, Open).
        viewer.ToolbarPressPolicy = (viewerSt, action, button) =>
        {
            if (button == MouseButton.Left && viewer.OpenToolbarDropdown(viewerSt, action))
            {
                return true;
            }

            var reverse = button == MouseButton.Right;
            if (!ViewerActions.HandleToolbarAction(viewerSt, controller.Source, action, reverse,
                    split: viewer.Split, hasBeforePixels: viewer.HasBeforeImageTextures, hasCrop: viewer.HasDisplayCrop))
            {
                controller.HandleToolbarAction(action, reverse, appToken);
            }

            return true;
        };

        bus.Subscribe<ResetDisplaySignal>(_ => controller.ResetDisplay());
    }

    /// <summary>The guided screen the host paints.</summary>
    public WorkbenchScreen<TSurface> Screen { get; }

    /// <summary>What the runner said about the project on show, and which step is selected.</summary>
    public WorkbenchState State { get; }

    /// <summary>What the window does: open a project, choose an image or a step, copy a command line.</summary>
    public WorkbenchActions Actions { get; }

    /// <summary>The router every key and press goes through.</summary>
    public InputRouter Router { get; }

    /// <summary>True while the runner is being asked, the viewer is loading a file or any background work is pending.</summary>
    public bool IsBusy => State.IsReading || _tracker.HasPending || _controller.IsLoadPending || _viewerState.RequestedFilePath is not null;

    /// <summary>Opens a project folder (or its <c>project.json</c>) and shows one of its images (the first by default).</summary>
    public void OpenProject(string folderOrFile, string? image = null) => Actions.OpenProject(folderOrFile, image);

    /// <summary>The window was resized.</summary>
    public void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        _viewer.Resize(width, height);
        State.NeedsRedraw = true;
    }

    /// <summary>One pointer event from the platform: a press, a move, a release and the wheel all go through the router.</summary>
    public bool HandlePointer(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseDown down:
                _viewerState.MouseScreenPosition = (down.X, down.Y);
                Router.Handle(down);
                return true;
            case InputEvent.MouseMove or InputEvent.MouseUp or InputEvent.Scroll:
                return Router.Handle(evt);
            default:
                return _viewer.HandleInput(evt);
        }
    }

    /// <summary>A key press, through the router.</summary>
    public bool HandleKeyDown(InputEvent.KeyDown keyEvent)
    {
        Router.Handle(keyEvent);
        return true;
    }

    /// <summary>The release half, which the viewer reads.</summary>
    public bool HandleKeyUp(InputEvent.KeyUp keyEvent)
    {
        _viewer.HandleInput(keyEvent);
        return true;
    }

    /// <summary>A touchscreen pinch, forwarded to the viewer.</summary>
    public void HandlePinch(float scale, float x, float y, PinchSource source)
        => _viewer.HandleInput(new InputEvent.Pinch(scale, x, y) { Source = source });

    /// <summary>The end of a pinch.</summary>
    public void HandlePinchEnd() => _viewer.HandleInput(new InputEvent.PinchEnd());

    /// <summary>
    /// The work that runs BETWEEN frames, before the frame's command buffer exists: the document may only change here (see
    /// <see cref="StandaloneViewerHost{TSurface}.BeforeFrame"/> for why).
    /// </summary>
    public void BeforeFrame()
    {
        _tracker.ProcessCompletions(_logger);
        Actions.TryAdoptStatus();
        _controller.HandleFileRequest(_appToken);
        _views.Advance();

        if (_viewerState.NeedsReprocess)
        {
            ViewerActions.Reprocess(_viewerState);
        }

        if (_viewerState.NeedsTextureUpdate)
        {
            _viewer.RequestFullFrameDamage();
        }
    }

    /// <summary>Whether this iteration of the loop should draw a frame. Every step with a side effect is evaluated before any is combined.</summary>
    public bool WantsFrame()
    {
        var skyMoved = _viewer.TakeSkyRedrawRequest();
        var tooltipDue = Router.TooltipNeedsFrame;
        if (tooltipDue)
        {
            _viewer.RequestFullFrameDamage();
        }

        return skyMoved || tooltipDue || State.NeedsRedraw || _viewerState.NeedsRedraw || _viewerState.NeedsTextureUpdate
            || _viewerState.RequestedFilePath is not null || _controller.IsLoadPending;
    }

    /// <summary>Paints the screen and the viewer in its canvas.</summary>
    public void Paint() => Screen.Render(State, Actions, _viewerState, _controller.Source, _width, _height);

    /// <summary>Paints the router's due tooltip over everything, last.</summary>
    public void PaintTooltip() => _viewer.RenderTooltip(Router.Tooltip);

    /// <summary>The work after a frame is drawn.</summary>
    public void AfterFrame()
    {
        _bus.ProcessPending();
        State.NeedsRedraw = false;
        _viewerState.NeedsRedraw = false;
        Router.AfterPaint();
        _controller.ReleaseCompletedTasks();
    }

    private bool HandleUnroutedPress(InputEvent.MouseDown down)
    {
        if (down.Button == MouseButton.Right && _viewer.TryOpenImageContextMenu(_viewerState, down.X, down.Y))
        {
            return true;
        }

        if (down.Button is MouseButton.Left or MouseButton.Middle)
        {
            _viewer.BeginViewportPan(down.X, down.Y);
        }

        return true;
    }
}
