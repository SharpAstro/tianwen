using System.CommandLine;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib;
using TianWen.AI.Imaging;
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

// Anchor the start-up trace FIRST, so the phase before this line -- the AOT runtime's own init and,
// for the packaged build, MSIX activation -- is measured rather than assumed. Nothing in the process
// can see that time except by asking the OS when the process was created, which is what this does.
StartupTrace.Mark("main");

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
StartupTrace.Mark("di");
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.FitsViewer");
var controller = sp.GetRequiredService<ViewerController>();
var tracker = sp.GetRequiredService<BackgroundTaskTracker>();
// Wire the AI enhance pipeline so the Enhance toolbar button (+ 'E' shortcut) is active.
controller.EnhancePipeline = sp.GetRequiredService<SharpenPipeline>();
StartupTrace.Mark("services");

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

// The description leads with the build, because `--help` is the one surface a Store user can reach
// without the app running, and "the viewer from the Store" is not a bug report. BuildInfo.Describe()
// is version + short SHA + build time and flags a mixed build; the install folder names the install
// KIND on Windows (a WindowsApps path IS the Store package), which is the same pair the log banner
// prints. Both are also in the in-app "?" panel, for the user who never opens a terminal.
var rootCommand = new RootCommand(
    $"TianWen FITS Image Viewer (Astro Photo Viewer) {BuildInfo.Describe()} at {BuildInfo.InstallFolder}")
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
StartupTrace.Mark("cli");

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

// The annotated save draws the object overlay, so the controller needs the same catalog the renderer
// is given below. Lazy, so wiring it here still starts nothing.
controller.CelestialObjectDB = celestialObjectDB;

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

StartupTrace.Mark("scan");

// --- One instance per folder, plus one for "nothing open" ---
// A double-click in the shell starts a fresh process, so a folder the user is already looking
// at would get a second window onto it. The channel is keyed on the FOLDER rather than the app:
// a file in a folder already on screen goes to that window, a file anywhere else gets its own.
//
// The one exception is a window with NOTHING open, which holds the empty identity: it has no folder
// to be more specific about, so it adopts a file from anywhere rather than let a second window open
// beside it. A non-empty window never adopts across folders -- that would silently replace the folder
// someone is looking at, which is the whole reason the claim is folder-scoped.
//
// Every other outcome falls through and opens here -- --new-window, the opt-out, a bare launch, or a
// hand-off that failed -- because an extra window is a poor outcome and a double-click that does
// nothing is not an acceptable one.
const string GateScope = "tianwen-fits";
const string SingleInstanceEnvVar = "TIANWEN_FITS_SINGLE_INSTANCE";
InstanceGate? instanceGate = null;
var gateFolder = folderPath;
if (!newWindow
    && !string.Equals(Environment.GetEnvironmentVariable(SingleInstanceEnvVar), "0", StringComparison.Ordinal))
{
    // An instance with NOTHING open answers for the EMPTY identity, which is ChannelFor's own default
    // and cannot collide with a folder (NormalizePathIdentity always returns an absolute path, never
    // ""). That claim is what lets a file reach a window showing nothing: without it such a window
    // holds no claim at all, so every double-click missed it and started a second process -- precisely
    // the cost the hand-off exists to avoid.
    var emptyChannel = InstanceGate.ChannelFor(GateScope);

    if (folderPath is not null)
    {
        var channel = InstanceGate.ChannelFor(GateScope, InstanceGate.NormalizePathIdentity(folderPath));
        instanceGate = InstanceGate.TryClaim(channel, logger);
        if (instanceGate is null)
        {
            // A window already showing this folder beats an empty one: it is the more specific answer,
            // and the file lands in the list the user is already looking at.
            var handoff = initialFilePath ?? folderPath;
            if (InstanceGate.TryHandOff(channel, handoff, TimeSpan.FromSeconds(5), logger))
            {
                logger.LogInformation("Handed {Path} to the instance already showing {Folder}", handoff, folderPath);
                return 0;
            }
        }
    }

    // Nobody was showing this folder, so prefer an EMPTY window over opening a second one. A bare
    // launch (no file at all) deliberately does not reach here: the user asked for a window, so they
    // get one.
    if (initialFilePath is not null
        && InstanceGate.TryHandOff(emptyChannel, initialFilePath, TimeSpan.FromSeconds(5), logger))
    {
        logger.LogInformation("Handed {Path} to the instance with nothing open", initialFilePath);
        // Release the folder claim taken moments ago so the adopter's re-bind can take it. Losing that
        // race is survivable by construction -- PumpInstanceGate keeps going ungated -- which is also
        // the answer for two empty instances: one holds the empty channel, the other just opens what it
        // was given.
        instanceGate?.Dispose();
        return 0;
    }

    if (folderPath is null)
    {
        // This IS the empty instance. Claim the empty identity so a later launch can find it; the
        // PumpInstanceGate re-bind then moves the claim to whatever folder the adopted file is in.
        instanceGate = InstanceGate.TryClaim(emptyChannel, logger);
    }
}

StartupTrace.Mark("gate");

// --- SDL3 + Vulkan init ---
// Install the native-library resolver before the first P/Invoke into SDL3 so a
// failed DLL load lands in the file logger instead of crashing silently.
NativeLoaderDiagnostics.Install(logger);

using var sdlWindow = NativeLoaderDiagnostics.InitNative(logger, "SDL3 + Vulkan window",
    () => SdlVulkanWindow.Create("Fits viewer", 1536, 1080));
sdlWindow.GetSizeInPixels(out var pixW, out var pixH);
StartupTrace.Mark("window");

var bus = new SignalBus();
// One owner for the GPU trio (see GpuStack): disposed at scope end top-down -- imageRenderer,
// renderer, context -- still ahead of sdlWindow's own using above.
using var gpu = new GpuStack<VkImageRenderer>(logger, sdlWindow, (uint)pixW, (uint)pixH,
    r => new VkImageRenderer(r, (uint)pixW, (uint)pixH)
    {
        Bus = bus,
        DpiScale = sdlWindow.DisplayScale,
        CelestialObjectDB = celestialObjectDB,
        // The viewer starts its own background work (colour calibration): give it the same tracker and
        // logger the controller uses, so it is drained at shutdown and its failures reach the app log.
        Tracker = tracker,
        Logger = logger,
        // SharpenPipeline is registered (AddRcAstroAi above), so surface the Enhance toolbar button.
        EnhanceAvailable = true,
        // The clipboard is a platform service, so the renderer takes it as a callback: the image
        // context menu (right-click) copies coordinates and pixel values through this.
        SetClipboardText = text => SetClipboardText(text),
        // What the "?" panel reports under "AI enhancement". A delegate, not a value: each RC product
        // license check launches a process, and AddRcAstroAi deliberately defers that to first use so
        // that composing services spawns nothing. The panel asks once, on first open, via the
        // tracker's keyed slot.
        AiCapabilityProbe = async ct =>
            (await AiCapabilities.ProbeAsync(
                sp.GetRequiredService<IModelResolver>(),
                sp.GetService<IRcAstroCli>(),
                ct)).Describe(),
        // Cache the image content in an offscreen layer, so a redraw that only changes the chrome
        // blits instead of re-running the demosaic + stretch over the whole pane. The renderer owns
        // ONE set of layer targets, so exactly one viewer per renderer may claim them; this process
        // has exactly one viewer, which is what makes the claim unambiguous here and why the GUI (two
        // embedded viewers on a shared renderer) does not set it.
        UseCachedImageLayer = true
    });
var renderer = gpu.Renderer;
var imageRenderer = gpu.Top;
StartupTrace.Mark("gpu");

// The sky the photograph came from, drawn behind it on the context ladder's top rung (O, three
// times). The map is the SAME class the GUI's atlas tab is -- renderer-agnostic, already on this
// app's compile path -- pointed at the frame every render instead of at a site's zenith.
//
// Nothing here starts: the pipeline is built on the map's first draw, the catalog is the lazy above
// (which the ladder warms when a rung needs it), and the milky-way texture decodes off-thread. A
// viewer that never reaches the top rung pays for none of it.
var skyBackdrop = new VkSkyMapTab(renderer) { Bus = bus, Logger = logger };
skyBackdrop.State.ViewDrivenExternally = true;
// Defaults chosen for a sky drawn BEHIND a picture rather than for an atlas: the grid and the
// objects are the ladder's own lower rungs and are already drawn over the frame by the viewer, so
// switching the map's copies on would double every label. The palette is how any of them is changed.
skyBackdrop.State.ShowGrid = false;
// The viewer draws the coordinate grid itself, across the whole pane, from the frame's own WCS -- so
// the map's coarse celestial one is not an alternative, it is a second grid. Its row says so.
skyBackdrop.State.GridDrawnByHost = true;
skyBackdrop.State.ShowObjectOverlay = false;
skyBackdrop.State.ShowAltAzGrid = false;
skyBackdrop.State.ShowLayerPalette = true;

// The map reads its catalog, site and instant off a PlannerState. The viewer has no planner, so this
// is a carrier: the site and the instant are re-stated per frame from the photograph's own header,
// and the catalog arrives when the lazy lands.
var skyPlannerState = new PlannerState();
imageRenderer.SkyBackdrop = skyBackdrop;
imageRenderer.SkyPlannerState = skyPlannerState;
imageRenderer.SkyTimeProvider = sp.GetRequiredService<TianWen.Lib.Devices.ITimeProvider>();
// Registered on the viewer state so a pointer over the palette does not also count as a pointer over
// the picture -- see ViewerState.OverlayOwnsPointer, which is the one place that rule is stated.
state.SkyLayerPalette = skyBackdrop.State.LayerPalette;

// Once-only latches for the start-up trace. The three phases after "gpu" are all inside per-frame
// callbacks, and only their FIRST run is start-up.
var firstPrepareTraced = false;
var firstBeforeFrameTraced = false;
var firstFrameTraced = false;
var firstRedrawCheckTraced = false;
var firstRenderCallbackTraced = false;
// How many attempts the first PAINTED frame took. A BeginFrame that reports a resize abandons the
// frame and the loop retries, re-running OnBeforeFrame and the pre-render-pass hook each time, so a
// count above one says the window was still settling and the time went on frames nobody saw.
var frameAttempts = 0;
var prepareCalls = 0;
var resizeCalls = 0;

using var cts = new CancellationTokenSource();
imageRenderer.AppToken = cts.Token;

// The cached image layer is a render pass of its own, and render passes cannot nest -- so it has
// to be recorded before the main one opens, which is what this hook is. PrepareFrame decides the
// layout, placement and uniforms here rather than inside Render; it is idempotent, so the Render
// call below finds the work already done instead of repeating it.
renderer.OnPreRenderPass = _ =>
{
    prepareCalls++;
    imageRenderer.PrepareFrame(controller.Source, state);
    imageRenderer.PrepareCachedImageLayer();

    if (!firstPrepareTraced)
    {
        firstPrepareTraced = true;
        StartupTrace.Mark("prepare");
    }
};


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
var firstDocumentTraced = false;
controller.FileLoaded += name =>
{
    if (!firstDocumentTraced)
    {
        firstDocumentTraced = true;
        StartupTrace.Mark("document");
        StartupTrace.Log(logger);
    }
    SetWindowTitle(sdlWindow.Handle, Path.GetFileName(name));
};

// --- Main event loop via SdlEventLoop ---

// Which repaint path each frame took; see the AppState block below for why they are counted.
var partialFrames = 0;
var fullFrames = 0;

// One literal for the window's clear and the image pane's own ground. They have to agree: a full frame
// clears the window to this, a partial one does not clear at all and the pane fills its own rect, so two
// values would make the letterbox change colour with the repaint path rather than with anything visible.
var canvasBackground = new RGBAColor32(0x1a, 0x1a, 0x1a, 0xff);
imageRenderer.CanvasBackground = canvasBackground;

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = canvasBackground,

    OnResize = (rw, rh) =>
    {
        resizeCalls++;
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

    // A touchscreen pinch is its own input path -- a finger gesture, not a wheel -- so it has to be
    // wired even though OnPointerInput above already forwards everything it carries. Without these two
    // the events were raised and dropped, which is exactly what "pinch zoom does nothing" looked like.
    // The viewer decides what to do with the source; this host only forwards.
    OnPinch = (scale, mx, my, source) =>
        imageRenderer.HandleInput(new InputEvent.Pinch(scale, mx, my) { Source = source }),

    OnPinchEnd = () => imageRenderer.HandleInput(new InputEvent.PinchEnd()),

    OnDropFile = (path) => { if (path is not null) ViewerActions.HandleFileDrop(state, path); },

    // TickPlayback() FIRST (and always, via || short-circuit order): it runs every loop iteration --
    // including the idle WaitEventTimeout polls -- which is how SER playback stays frame-paced without
    // busy-spinning. It decodes the next frame ahead off the render thread and returns true only when a
    // frame should actually be shown now (or a seek is resolving); between frames it returns false so
    // the loop idles and the GPU/disk go quiet (mirroring the standalone viewer's low-idle behaviour).
    CheckNeedsRedraw = () =>
    {
        if (!firstRedrawCheckTraced)
        {
            firstRedrawCheckTraced = true;
            // Everything from "wire" to here is SDL: the loop's first PollEvent and the burst of
            // window events it drains before the app is asked whether to draw.
            StartupTrace.Mark("events");
        }

        // BOTH of these must run on EVERY iteration, so neither may sit on the right of a ||:
        // TickPlayback paces SER playback (see the note above), and the gate pump is the only
        // place a hand-off from a later launch is noticed. Evaluate them, then combine.
        var handedOff = PumpInstanceGate();
        var playback = controller.TickPlayback();
        // Same rule for the blink: it paces the file-list step from this call, so it may not sit on the
        // right of a || either.
        var blinked = controller.TickBlink();
        // And the same rule again for the sky: the ask is CONSUMED by reading it, so on the right of a
        // || a frame requested by the star buffer landing would be swallowed by an unrelated true and
        // never drawn. The map keeps its own flag because it has its own off-thread work.
        var skyMoved = imageRenderer.TakeSkyRedrawRequest();
        return handedOff || playback || blinked || skyMoved
            || state.NeedsRedraw || state.NeedsTextureUpdate || state.RequestedFilePath is not null
            || controller.IsLoadPending;
    },

    OnRender = () =>
    {
        if (!firstRenderCallbackTraced)
        {
            firstRenderCallbackTraced = true;
            // "prepare" to here is the renderer's own frame open: acquiring a swapchain image
            // (which can wait on the presentation engine) and beginning the pass.
            StartupTrace.Mark("acquire");
        }

        // Everything that can SWAP the document (finished background work, a file request, an enhance
        // result) runs in OnBeforeFrame, before this frame's command buffer exists; the texture upload
        // it implies runs inside PrepareFrame, from the pre-render-pass hook. Both used to happen here,
        // AFTER the cached-layer pre-pass had already bound the previous document's views into this
        // very command buffer -- see the remarks on PrepareFrame for what that cost.

        // Which repaint path the frame took. Counted because a partial frame is IDENTICAL on screen to
    // a full one -- that is the point -- so no screenshot can tell them apart and this is the only
    // evidence that the damage path is engaging at all.
    if (renderer.LastFrameWasPartial) { partialFrames++; } else { fullFrames++; }

    imageRenderer.Render(controller.Source, state);

        // The trace is written here rather than at the end of the set-up above because the window
        // being on screen is what the user is waiting for, and a file opened from the shell then
        // adds a second line once its pixels arrive (the load is deliberately deferred so the
        // window can appear first).
        if (!firstFrameTraced)
        {
            firstFrameTraced = true;
            StartupTrace.Mark("first-frame");
            StartupTrace.Log(logger);
            logger.LogInformation(
                "startup: the first painted frame took {Attempts} frame attempts, {Prepares} pre-render passes, {Resizes} resizes",
                frameAttempts, prepareCalls, resizeCalls);
        }

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
bus.Subscribe<AutoCropSignal>(_ =>
    controller.HandleToolbarAction(ToolbarAction.AutoCrop, reverse: false, cts.Token));

// The "?" panel's two action rows (the user guide, and a prepared bug report). Host-level and
// desktop-only on purpose: UseShellExecute routes a URL through the shell, which the WASM-shared
// abstraction layer cannot do, so the widget posts a signal and the host decides. Best-effort,
// exactly as the GUI's own subscription is: a browser that will not open must not take the viewer
// down with it.
bus.Subscribe<OpenUrlSignal>(sig =>
{
    try
    {
        using var _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(sig.Url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to open URL {Url}", sig.Url);
    }
});

// The Save dropdown's two rows. The annotation is read off the renderer here rather than reached for
// inside the controller, because the renderer is what holds it -- so a plate-solve verification or a
// polar-alignment overlay lands in the saved file too.
bus.Subscribe<SaveImageSignal>(sig =>
    controller.SaveImage(sig.WithOverlays, sig.PngDepth, cts.Token, imageRenderer.Annotation));

// Damage: hand the renderer the rects this frame changed, so it preserves the previous frame and
// repaints only those. Anything that asked for a frame without saying what moved comes back false
// here and the whole surface is repainted, which is what the viewer always did.
//
// Declared BEFORE BeginFrame, because the render pass is chosen from it: by the time OnRender runs
// the pass is already open and the decision is made.
var frameDamage = new List<RectF32>();
loop.OnBeforeFrame = () =>
{
    frameAttempts++;
    if (!firstBeforeFrameTraced)
    {
        firstBeforeFrameTraced = true;
        // Everything between "gpu" and here is the event loop reaching its first frame at all: the
        // window shown, the first events pumped, the redraw gate answering yes.
        StartupTrace.Mark("loop");
    }

    // The document may only change BETWEEN frames. A swap recreates the channel textures, and the
    // recreate destroys the previous views; done inside a frame it invalidated whatever this frame's
    // command buffer had already recorded against them (the cached-layer pre-pass), and the GPU
    // faulted on submit -- the LiveKernelEvent 141 that took the Store viewer down on 2026-08-27,
    // "VkImageView was destroyed" under the validation layer. So every step that can replace
    // controller.Source runs here, before BeginFrame, and the upload itself runs in PrepareFrame.
    //
    // Retire finished background work (the file dialog, a plate solve) and log anything that
    // faulted. Deliberately NOT gated on tracker.HasPending in CheckNeedsRedraw: that would spin
    // the render loop for the whole of a multi-second solve on a GPU we want quiet. Each guarded
    // operation flags a redraw in its onFinally instead, so the loop wakes exactly once, here.
    tracker.ProcessCompletions(logger);

    controller.HandleFileRequest(cts.Token);

    // Apply a finished AI-enhance result (swaps in the enhanced document + flags a texture
    // re-upload). No-op until the background enhance task completes.
    controller.TryApplyPendingEnhance(cts.Token);
    controller.TryApplyPendingCrop();

    if (state.NeedsReprocess)
    {
        ViewerActions.Reprocess(state);
    }

    // A frame that swaps its document repaints everything, whatever narrowing a mouse move in the
    // same tick may have declared: stale pixels from the previous document are the one failure the
    // damage tracking must never produce.
    if (state.NeedsTextureUpdate)
    {
        imageRenderer.RequestFullFrameDamage();
    }

    frameDamage.Clear();
    if (imageRenderer.TryTakeFrameDamage(frameDamage))
    {
        foreach (var r in frameDamage)
        {
            renderer.AddFrameDamage(r.X, r.Y, r.Width, r.Height);
        }
    }
    else
    {
        renderer.MarkFullFrameDamage();
    }
};

// OnKeyDown wired separately: imageRenderer.HandleInput handles F11 via signal bus
loop.OnKeyDown = keyEvent =>
{
    imageRenderer.HandleInput(keyEvent);
    return true;
};

// The release half. Bound because holding Space suspends a blink, which is the one binding here whose
// meaning lasts as long as the key is down; SdlEventLoop dispatches key-up only to a host that asks.
loop.OnKeyUp = keyEvent =>
{
    imageRenderer.HandleInput(keyEvent);
    return true;
};

#if SIBLING_DEBUG_INSPECTORS
// Live UI debug inspector (DEBUG only -- compiled out of Release). Exposes this process to the
// SdlVulkan.Renderer.Inspector MCP sidecar so an agent can discover it, read the clickable-region
// tree, and screenshot the window. The viewer renders its chrome through the single imageRenderer
// widget, which is a COMPOSITE (it hosts the sky map), so the whole UI is its painted regions rather
// than only the ones it registered itself. This block is the only wiring.
using var debugInspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "FitsViewer",
    WindowTitle = () => "Fits viewer",
    // PaintedRegions, not GetRegisteredRegions: the sky map the viewer hosts registers its layer
    // palette on ITSELF, so the widget's own list is no longer the whole UI and an agent asking for
    // the tree would find a panel it can see in a screenshot and cannot address.
    GetRegions = () => imageRenderer.PaintedRegions(),
    GetLayout = () => imageRenderer.GetCapturedLayout(),
    // The cached image layer is not observable any other way: when it works, the frame is
    // byte-identical to a re-render, so a screenshot cannot show it and a frame-time average cannot
    // distinguish "not helping" from "never engaged". These three are what make a measurement
    // interpretable from outside the process.
    AppState = w =>
    {
        var layer = imageRenderer.CachedLayerStats;
        w.Set("cachedLayerEnabled", layer.Enabled);
        w.Set("cachedLayerRenders", layer.Renders);
        w.Set("cachedLayerBlits", layer.Blits);
        w.Set("cachedLayerLastMiss", layer.LastMiss);
        w.Set("partialFrames", partialFrames);
        w.Set("fullFrames", fullFrames);
        w.Set("zoom", state.Zoom);
        w.Set("file", controller.Source is AstroImageDocument d ? System.IO.Path.GetFileName(d.FilePath) : "");

        // The driven sky view, which is otherwise unmeasurable from outside: a backdrop pointing at
        // the wrong place, turned the wrong way or drawn at the wrong scale still LOOKS like a star
        // field, and a screenshot of one is indistinguishable from a screenshot of the other. These
        // are the four numbers that say which, against what the frame's own header implies.
        w.Set("skyBackdrop", state.ShowSkyBackdrop);
        w.Set("skyCentreRaHours", skyBackdrop.State.CenterRA);
        w.Set("skyCentreDecDeg", skyBackdrop.State.CenterDec);
        w.Set("skyFovDeg", skyBackdrop.State.FieldOfViewDeg);
        w.Set("skyRollDeg", double.RadiansToDegrees(skyBackdrop.State.CenterRoll));
        w.Set("skyMirrored", skyBackdrop.State.MirrorView);
        w.Set("skySite", imageRenderer.SkySite.Describe());
        w.Set("skyAtCaptureTime", imageRenderer.SkyIsAtCaptureTime);
    },
});
#endif

StartupTrace.Mark("wire");
loop.Run(cts.Token);

// Cleanup. The window and the Vulkan context can only be destroyed on THIS thread, so the drain
// below must neither be awaited (the continuation would land on a pool thread and the teardown
// from there deadlocks) nor blocked on (the window would stop responding for the duration).
// ShutdownDrain keeps the loop pumping instead; see its remarks for what each spelling breaks.
cts.Cancel();
ShutdownDrain.PumpUntilComplete(loop, controller.ShutdownAsync(), logger);
instanceGate?.Dispose();
// The GPU trio is disposed by `using var gpu` at scope end, top-down, ahead of sdlWindow's using.

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
        // Restores only if minimised, then raises. SdlVulkanWindow implements
        // IActivatableWindow, so this is AppShell's own rule, not a local copy of it; the
        // reason it is not simply "restore, then raise" is on WindowActivation.
        sdlWindow.Activate();
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
                    split: imageRenderer.Split, hasBeforePixels: imageRenderer.HasBeforeImageTextures,
                    hasCrop: imageRenderer.HasDisplayCrop))
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

        // An unclaimed right press on the image is the context menu. Deliberately after every hit
        // branch above, so a right-click that already meant something (reverse-cycling a toolbar
        // button) still means it; only a press that would otherwise start a pan opens a menu. The
        // embedded dispatcher (ImageRendererBase.HandleViewerMouseDown) carries the same call.
        if (down.Button == MouseButton.Right && imageRenderer.TryOpenImageContextMenu(state, px, py))
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
