using System.CommandLine;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.FitsViewer;
using TianWen.UI.Shared;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.AI.Imaging.RcAstro;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static SDL3.SDL;
using SharpAstro.AppShell;

// DI setup, before args processing so logger is available for early errors
var services = new ServiceCollection();
services
    // Debug in a Debug build: the viewer was the ONLY app not to state a level, so it ran at the
    // factory default (Information) while the GUI, CLI and server all ask for more. Every per-solver
    // plate-solve line is Debug, so a failed solve left nothing in the file to read.
#if DEBUG
    .AddFileLogging("FitsViewer", LogLevel.Debug)
#else
    .AddFileLogging("FitsViewer")
#endif
    .AddFitsViewer()
    .AddExternal()
    .AddAstrometry()
    // RC-preferred AI enhancers (sxt/nxt/bxt when the rc-astro CLI is installed + licensed, else the
    // SETI Astro ONNX baseline). Registers SharpenPipeline for the viewer's Enhance action.
    .AddRcAstroAi()
    .AddSingleton<BackgroundTaskTracker>()
    .AddSingleton<ViewerController>();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.FitsViewer");
var controller = sp.GetRequiredService<ViewerController>();
var tracker = sp.GetRequiredService<BackgroundTaskTracker>();
// Wire the AI enhance pipeline so the Enhance toolbar button (+ 'E' shortcut) is active.
controller.EnhancePipeline = sp.GetRequiredService<SharpenPipeline>();

// --- Command-line definition ---
var pathArg = new Argument<string?>("path")
{
    Description = "File or folder to open",
    Arity = ArgumentArity.ZeroOrOne
};

// --register [FITS]: optional value, defaults to "FITS" when specified without a group name
var registerOption = new Option<string?>("--register")
{
    Description = "Register file associations for an extension group (default: FITS)",
    Arity = ArgumentArity.ZeroOrOne
};

// Forces a new window even when an instance already has this folder open, for the times the
// whole point is to compare two views of one directory side by side.
var newWindowOption = new Option<bool>("--new-window")
{
    Description = "Open a new window even if an instance already has this folder open"
};

var rootCommand = new RootCommand("TianWen FITS Image Viewer")
{
    pathArg,
    registerOption,
    newWindowOption
};

// SetAction captures parsed values; --help/--version bypass this action automatically
var actionCalled = false;
string? registerGroup = null;
string? inputArg = null;
var newWindow = false;

rootCommand.SetAction((parseResult, _) =>
{
    actionCalled = true;

    // ZeroOrOne arity: GetValue returns null both when not specified and when
    // specified without a value. Check raw args to detect the flag's presence.
    if (Array.Exists(args, a => a is "--register"))
    {
        registerGroup = parseResult.GetValue(registerOption) ?? "FITS";
    }

    inputArg = parseResult.GetValue(pathArg);
    newWindow = parseResult.GetValue(newWindowOption);
    return Task.CompletedTask;
});

var parsedResult = rootCommand.Parse(args);
if (parsedResult.Errors.Count > 0)
{
    foreach (var error in parsedResult.Errors)
    {
        logger.LogError("{Error}", error.Message);
    }
    return 1;
}

await parsedResult.InvokeAsync();

// --help/--version bypass SetAction: exit cleanly
if (!actionCalled)
{
    return 0;
}

// --register: register file associations and exit (before SDL init)
if (registerGroup is not null)
{
    return FileAssociationRegistrar.Register(registerGroup, logger);
}

string? initialFilePath = null;
string? folderPath = null;

if (inputArg is not null)
{
    if (Directory.Exists(inputArg))
    {
        folderPath = Path.GetFullPath(inputArg);
    }
    else if (File.Exists(inputArg))
    {
        initialFilePath = Path.GetFullPath(inputArg);
        folderPath = Path.GetDirectoryName(initialFilePath);
    }
    else
    {
        logger.LogError("Path not found: {InputPath}", inputArg);
        return 1;
    }
}

// Lazy-initialized catalog DB: starts init on first access, safe to pass around immediately
var celestialObjectDB = new DotNext.Threading.AsyncLazy<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(async (ct) =>
{
    var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
    await db.InitDBAsync(cancellationToken: ct);
    return db;
});

// Scan folder for supported image files
if (folderPath is not null)
{
    ViewerActions.ScanFolder(state, folderPath, initialFilePath is not null ? Path.GetFileName(initialFilePath) : null);
}

// If no specific file was given, try to open the first image in the folder
if (initialFilePath is null && state.ImageFileNames.Count > 0 && folderPath is not null)
{
    initialFilePath = Path.Combine(folderPath, state.ImageFileNames[0]);
    state.SelectedFileIndex = 0;
}

if (initialFilePath is not null)
{
    // Defer loading so the window appears immediately with a status message
    state.RequestedFilePath = initialFilePath;
}

// --- One instance per folder ---
// A double-click in the shell starts a fresh process, so a folder the user is already looking
// at would get a second window onto it. The channel is keyed on the FOLDER rather than the app:
// a file in a folder already on screen goes to that window, a file anywhere else gets its own.
//
// Every other outcome falls through and opens here -- no folder, --new-window, the opt-out, or a
// hand-off that failed -- because an extra window is a poor outcome and a double-click that does
// nothing is not an acceptable one.
const string GateScope = "tianwen-fits";
const string SingleInstanceEnvVar = "TIANWEN_FITS_SINGLE_INSTANCE";
InstanceGate? instanceGate = null;
var gateFolder = folderPath;
if (folderPath is not null && !newWindow
    && !string.Equals(Environment.GetEnvironmentVariable(SingleInstanceEnvVar), "0", StringComparison.Ordinal))
{
    var channel = InstanceGate.ChannelFor(GateScope, InstanceGate.NormalizePathIdentity(folderPath));
    instanceGate = InstanceGate.TryClaim(channel, logger);
    if (instanceGate is null)
    {
        var handoff = initialFilePath ?? folderPath;
        if (InstanceGate.TryHandOff(channel, handoff, TimeSpan.FromSeconds(5), logger))
        {
            logger.LogInformation("Handed {Path} to the instance already showing {Folder}", handoff, folderPath);
            return 0;
        }
    }
}

// --- SDL3 + Vulkan init ---
// Install the native-library resolver before the first P/Invoke into SDL3 so a
// failed DLL load lands in the file logger instead of crashing silently.
NativeLoaderDiagnostics.Install(logger);

using var sdlWindow = NativeLoaderDiagnostics.InitNative(logger, "SDL3 + Vulkan window",
    () => SdlVulkanWindow.Create("Fits viewer", 1536, 1080));
sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var ctx = NativeLoaderDiagnostics.InitNative(logger, "Vulkan device",
    () => VulkanContext.Create(sdlWindow.Instance, sdlWindow.Surface, (uint)pixW, (uint)pixH));
var renderer = new VkRenderer(ctx, (uint)pixW, (uint)pixH);

var bus = new SignalBus();
var imageRenderer = new VkImageRenderer(renderer, (uint)pixW, (uint)pixH)
{
    Bus = bus,
    DpiScale = sdlWindow.DisplayScale,
    CelestialObjectDB = celestialObjectDB,
    // The viewer starts its own background work (colour calibration): give it the same tracker and
    // logger the controller uses, so it is drained at shutdown and its failures reach the app log.
    Tracker = tracker,
    Logger = logger,
    // SharpenPipeline is registered (AddRcAstroAi above), so surface the Enhance toolbar button.
    EnhanceAvailable = true
};

var cts = new CancellationTokenSource();
imageRenderer.AppToken = cts.Token;

// Kick off DB init eagerly so it is ready when the user toggles overlays. Tracked rather than
// discarded: the catalog decode is the slowest thing the viewer starts, and a discarded task that
// throws (a missing or corrupt catalog) would leave the overlays permanently and silently empty.
tracker.RunGuarded(
    async ct => await celestialObjectDB.WithCancellation(ct),
    cts.Token,
    logger,
    "Celestial object DB init",
    onError: _ => state.StatusMessage = "Object catalog unavailable");

// Wire title update from controller
controller.FileLoaded += name => SetWindowTitle(sdlWindow.Handle, Path.GetFileName(name));

// --- Main event loop via SdlEventLoop ---

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = new RGBAColor32(0x1a, 0x1a, 0x1a, 0xff),

    OnResize = (rw, rh) =>
    {
        imageRenderer.DpiScale = sdlWindow.DisplayScale;
        imageRenderer.Resize(rw, rh);
    },

    // One pointer callback: the loop synthesizes the InputEvents (real release coordinates on
    // MouseUp: the previous hand-wired OnMouseUp had to reconstruct them from a cached position,
    // and before that shipped MouseUp(0, 0)). Presses go through the bespoke dispatch below
    // (toolbar dropdowns + DI-dependent actions); move/release/wheel flow straight into the
    // shared viewer input path.
    OnPointerInput = evt =>
    {
        var handled = evt switch
        {
            InputEvent.MouseDown down => HandleMouseDown(down),
            _ => imageRenderer.HandleInput(evt),
        };

        // Cursor feedback happens HERE, on the move, and not at the end of OnRender where it used to
        // live. OnRender is gated by CheckNeedsRedraw, and a move that changes no pixel requests no
        // redraw -- so on that path the cursor was simply never recomputed and the pointer kept whatever
        // kind it last had. The dead zone is every part of the window that repaints for nothing: the
        // letterbox around the image, empty file-list space, the gap beside a panel.
        //
        // That is what made the file-list divider look unresizable. Unless the image happens to sit
        // flush against the divider, the approach to the handle crosses letterbox, no frame is drawn,
        // no resize cursor appears -- and a handle with no cursor reads as not being a handle. The PRESS
        // worked the whole time (HandleMouseDown hit-tests directly), which is why it came alive "once
        // I started dragging it".
        //
        // Setting a system cursor needs no frame at all, and the regions it asks are last frame's --
        // exactly what the query wants. This is the shape TianWen.UI.Gui already uses.
        if (evt is InputEvent.MouseMove)
        {
            UpdateCursor();
        }

        return handled;
    },

    OnDropFile = (path) => { if (path is not null) ViewerActions.HandleFileDrop(state, path); },

    // TickPlayback() FIRST (and always, via || short-circuit order): it runs every loop iteration --
    // including the idle WaitEventTimeout polls -- which is how SER playback stays frame-paced without
    // busy-spinning. It decodes the next frame ahead off the render thread and returns true only when a
    // frame should actually be shown now (or a seek is resolving); between frames it returns false so
    // the loop idles and the GPU/disk go quiet (mirroring the standalone viewer's low-idle behaviour).
    CheckNeedsRedraw = () =>
    {
        // BOTH of these must run on EVERY iteration, so neither may sit on the right of a ||:
        // TickPlayback paces SER playback (see the note above), and the gate pump is the only
        // place a hand-off from a later launch is noticed. Evaluate them, then combine.
        var handedOff = PumpInstanceGate();
        var playback = controller.TickPlayback();
        return handedOff || playback
            || state.NeedsRedraw || state.NeedsTextureUpdate || state.RequestedFilePath is not null
            || controller.IsLoadPending;
    },

    OnRender = () =>
    {
        // Retire finished background work (the file dialog, a plate solve) and log anything that
        // faulted. Deliberately NOT gated on tracker.HasPending in CheckNeedsRedraw: that would spin
        // the render loop for the whole of a multi-second solve on a GPU we want quiet. Each guarded
        // operation flags a redraw in its onFinally instead, so the loop wakes exactly once, here.
        tracker.ProcessCompletions(logger);

        controller.HandleFileRequest(cts.Token);

        // Apply a finished AI-enhance result (swaps in the enhanced document + flags a texture
        // re-upload). No-op until the background enhance task completes.
        controller.TryApplyPendingEnhance(cts.Token);

        if (state.NeedsReprocess)
        {
            ViewerActions.Reprocess(state);
        }

        if (controller.Source is not null && state.NeedsTextureUpdate)
        {
            imageRenderer.UploadDocumentTextures(controller.Source, state);
        }

        imageRenderer.Render(controller.Source, state);

        // Also after a paint, not only on move: a repaint can change which regions sit under a
        // STATIONARY pointer (a dropdown opening over the handle, a panel toggled by a key).
        UpdateCursor();
    },

    OnPostFrame = () =>
    {
        bus.ProcessPending();
        state.NeedsRedraw = false;
        controller.ReleaseCompletedTasks();
    }
};

// Signal subscriptions for app-level actions
bus.Subscribe<RequestExitSignal>(_ => loop.Stop());
bus.Subscribe<ToggleFullscreenSignal>(_ => sdlWindow.ToggleFullscreen());
bus.Subscribe<PlateSolveSignal>(_ =>
    controller.HandleToolbarAction(ToolbarAction.PlateSolve, reverse: false, cts.Token));
bus.Subscribe<EnhanceImageSignal>(_ =>
    controller.HandleToolbarAction(ToolbarAction.Enhance, reverse: false, cts.Token));

// OnKeyDown wired separately: imageRenderer.HandleInput handles F11 via signal bus
loop.OnKeyDown = (inputKey, inputModifier) =>
{
    imageRenderer.HandleInput(new InputEvent.KeyDown(inputKey, inputModifier));
    return true;
};

#if SIBLING_DEBUG_INSPECTORS
// Live UI debug inspector (DEBUG only -- compiled out of Release). Exposes this process to the
// SdlVulkan.Renderer.Inspector MCP sidecar so an agent can discover it, read the clickable-region
// tree, and screenshot the window. The viewer renders all chrome through the single imageRenderer
// widget, so its registered regions are the whole UI. This block is the only wiring.
using var debugInspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "FitsViewer",
    WindowTitle = () => "Fits viewer",
    GetRegions = () => imageRenderer.GetRegisteredRegions(),
    GetLayout = () => imageRenderer.GetCapturedLayout(),
});
#endif

loop.Run(cts.Token);

// Cleanup. The window and the Vulkan context can only be destroyed on THIS thread, so the drain
// below must neither be awaited (the continuation would land on a pool thread and the teardown
// from there deadlocks) nor blocked on (the window would stop responding for the duration).
// ShutdownDrain keeps the loop pumping instead; see its remarks for what each spelling breaks.
cts.Cancel();
ShutdownDrain.PumpUntilComplete(loop, controller.ShutdownAsync(), logger);
instanceGate?.Dispose();
imageRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

return 0;

// Drains hand-offs from later launches, and keeps the claimed channel pointing at the folder
// the window is actually showing.
//
// The folder is NOT fixed for the life of the process: the open dialog and a file drop both
// rescan. Rather than have each of those tell the gate, this polls ViewerState.CurrentFolder,
// which ScanFolder sets as its first statement, so every path that can change folders is
// covered, including ones added later, and nothing in TianWen.UI.Abstractions knows a gate exists.
//
// Re-binding disposes synchronously, which joins the accept thread. That is milliseconds in
// practice (Dispose wakes it by connecting to itself) and it keeps the release strictly before
// the next claim; doing it off-thread would let a fast A to B to A trip over its own release and
// silently run ungated.
bool PumpInstanceGate()
{
    if (instanceGate is null)
    {
        return false;
    }

    var folder = state.CurrentFolder;
    if (folder is not null && !string.Equals(folder, gateFolder, StringComparison.Ordinal))
    {
        gateFolder = folder;
        instanceGate.Dispose();
        instanceGate = InstanceGate.TryClaim(
            InstanceGate.ChannelFor(GateScope, InstanceGate.NormalizePathIdentity(folder)), logger);
        if (instanceGate is null)
        {
            // Another window already owns the folder we just moved to. Running ungated is right:
            // claiming nothing is honest, and the other window keeps answering for it.
            return false;
        }
    }

    var applied = false;
    while (instanceGate.TryDequeue(out var request))
    {
        // The same entry point a drag and drop uses, so a handed-off file and a dropped one
        // cannot diverge: it scans the folder, selects the file and flags a redraw.
        ViewerActions.HandleFileDrop(state, request.Payload);
        // Restores only if minimised, then raises. The rule and the reason it is not simply
        // "restore, then raise" live in SharpAstro.AppShell's WindowActivation.
        sdlWindow.ActivateForHandoff();
        applied = true;
    }

    return applied;
}

// --- Event handlers ---

// Asked of the regions painted last frame rather than computed from geometry. What this replaces: an
// X-band test around the file-list edge, plus a "not while a dropdown is open" term because the dropdown
// draws over that band. That is one term per overlay, and every overlay added later silently invalidated
// it (see DIR.Lib's CursorKind remarks) -- the region list already knows what is on top, so both terms
// are gone. A region that states its own kind wins (a text field carries the I-beam itself); the Split's
// divider states none, since Layout.Builder.Split has no cursor parameter yet, so its hit maps here. An
// open dropdown's full-viewport backdrop is registered above everything, so it answers the hit and the
// handle underneath correctly stops claiming the pointer.
//
// The drag is the one genuine state term: once the grab starts the cursor stays the resize cursor
// wherever the pointer travels, which no region under it can express.
void UpdateCursor()
{
    var (mx, my) = state.MouseScreenPosition;
    var cursor = state.IsResizingFileList
        ? CursorKind.ResizeEW
        : imageRenderer.HitTestCursor(mx, my)
            ?? (imageRenderer.HitTest(mx, my) is ResizeHandleHit ? CursorKind.ResizeEW : CursorKind.Default);
    sdlWindow.SetSystemCursor(cursor.ToSystemCursor);
}

bool HandleMouseDown(InputEvent.MouseDown down)
{
    var (px, py) = (down.X, down.Y);
    state.MouseScreenPosition = (px, py);

    if (down.Button is MouseButton.Left or MouseButton.Right)
    {
        // Hit test: base class handles pure state actions (file list, toggles)
        var hit = imageRenderer.HitTestAndDispatch(px, py);

        if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
        {
            // Left-click on any dropdown-capable toolbar button opens the
            // overlay; right-click falls through so power users
            // can still reverse-cycle without summoning the popup. The set of
            // dropdown actions is encoded in OpenToolbarDropdown's switch; 
            // it returns false for non-dropdown actions so we never need a
            // parallel "is dropdown action" list here.
            if (down.Button == MouseButton.Left && imageRenderer.OpenToolbarDropdown(state, toolbarAction))
            {
                return true;
            }

            // Base handles pure state; controller handles DI-dependent actions
            var reverse = down.Button == MouseButton.Right;
            if (!ViewerActions.HandleToolbarAction(state, controller.Document, toolbarAction, reverse,
                    split: imageRenderer.Split, hasBeforePixels: imageRenderer.HasBeforeImageTextures))
            {
                controller.HandleToolbarAction(toolbarAction, reverse, cts.Token);
            }
            return true;
        }

        if (hit is ResizeHandleHit { Id: "FileList" })
        {
            state.IsResizingFileList = true;
            state.NeedsRedraw = true;
            return true;
        }

        if (hit is TransportScrubHit)
        {
            imageRenderer.BeginScrubAt(px);
            return true;
        }

        if (hit is WhiteBalanceSliderHit { Channel: var wbChannel })
        {
            imageRenderer.BeginWhiteBalanceDragAt(wbChannel, px);
            return true;
        }

        if (hit is WaveletSliderHit { Band: var waveletBand })
        {
            imageRenderer.BeginWaveletDragAt(waveletBand, px);
            return true;
        }

        // A file-list row registers a region (so it has a cursor, a hover tooltip and is visible to
        // the inspector) but must NOT be claimed here: the press has to continue to the scroll
        // controller below, which owns drag-to-scroll and fires selection on the tap RELEASE.
        //
        // This is the SECOND copy of this dispatcher -- ImageRendererBase.HandleViewerMouseDown is the
        // embedded one -- and fixing only that one is why single-click selection stayed broken here:
        // the standalone viewer never runs it. Any new hit type that needs to fall through has to be
        // excluded in both.
        if (hit is not null && hit is not HitResult.ListItemHit { ListId: ImageRendererBase<VkTexture>.FileListId })
        {
            return true; // OnClick already handled it (e.g. HistogramLog, PlayPause)
        }

        // Unclaimed left press over the file list arms the scroll controller (drag-to-scroll / thumb
        // grab); select fires on the tap release, routed through HandleViewerMouseUp via OnPointerInput.
        if (down.Button == MouseButton.Left && imageRenderer.HandleFileListInput(down))
        {
            return true;
        }
    }

    // Left or middle mouse button starts panning (the PanZoomController gesture on the renderer;
    // move/release continue through imageRenderer.HandleInput)
    if (down.Button is MouseButton.Left or MouseButton.Middle)
    {
        imageRenderer.BeginViewportPan(px, py);
    }
    return true; // every press consumed (matches the old always-true OnMouseDown lambda)
}
