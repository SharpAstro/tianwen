using DIR.Lib;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Extensions;
using TianWen.Lib.Logging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Gui;
using TianWen.UI.Shared;
using static SDL3.SDL;
using SharpAstro.AppShell;

// DI setup
var services = new ServiceCollection();
services
    .AddLogging(static builder =>
    {
        builder.AddProvider(new FileLoggerProvider("GUI"));
#if SIBLING_DEBUG_INSPECTORS
        builder.SetMinimumLevel(LogLevel.Debug);
#endif
    })
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddPlayerOne()
    .AddToupTek()
    .AddQHY()
    .AddAscom()
    .AddAlpaca()
    .AddMeade()
    .AddOnStep()
    .AddIOptron()
    .AddSkywatcher()
    .AddGemini()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddBuiltInGuider()
    .AddOpenMeteo()
    .AddCanon()
    .AddOpenWeatherMap()
    .AddDevices()
    .AddSessionFactory()
    .AddFitsViewer()
    // Live planetary capture controller (drives the 🪐 tab's video capture + rolling-window stack).
    .AddSingleton<PlanetaryCaptureController>()
    // LAN peer discovery (docs/plans/remote-profile.md): symmetric beacon so the Equipment tab's
    // no-profile screen can list tianwen-server rigs on the LAN. ServicePort 0 -- the GUI serves no
    // inbound channel of its own, it only discovers.
    .AddLanDiscovery(o =>
    {
        o.ServiceName = "tianwen-gui";
        o.ServicePort = 0;
    })
    .AddSingleton(sp => new GuiAppState { DeviceHub = sp.GetService<IDeviceHub>(), PeerTable = sp.GetService<IPeerTable>() })
    // Profile-aware pinned-port provider: any COM port currently referenced by the
    // active profile is excluded from discovery probing. Absent this registration,
    // SerialProbeService falls through to general probing; safe default.
    .AddSingleton<IPinnedSerialPortsProvider, ActiveProfilePinnedSerialPortsProvider>();

var sp = services.BuildServiceProvider();
var appState = sp.GetRequiredService<GuiAppState>();

// Begin beaconing + peer tracking now. AddLanDiscovery only registers the DI graph -- the GUI runs
// a bare ServiceCollection, not a generic Host, so nothing calls LanDiscoveryHostedService for us;
// resolve the concrete LanDiscovery and drive its lifecycle directly (mirrors what the hosted
// service would do). Changed hints a redraw so a rig that appears/disappears while the no-profile
// screen is showing doesn't wait for the 1Hz clock-tick fallback redraw.
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.Gui");
// The renderer's GPU forensics (rejected submits, device loss, recovery, slow GPU frames) default to
// stderr, which only a launcher that redirects it ever keeps: point them at this app's log file,
// before anything touches Vulkan.
SdlVulkanLog.Logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SdlVulkan.Renderer");

var lanDiscovery = sp.GetRequiredService<LanDiscovery>();

// A transport that could not bind the discovery port runs ANNOUNCE-ONLY: this node still beacons but
// will never see a peer, so the Equipment tab's rig list stays empty and looks like a network with
// nothing on it. Exactly one warning, which is what LanDiscoveryHostedService does for the server --
// the GUI drives the lifecycle by hand, so it owes the same line. Known at construction (the bind is
// in UdpLanTransport's ctor), so reading it before StartAsync is deliberate rather than early.
if (lanDiscovery.Degradation is { } lanDegradation)
{
    logger.LogWarning("{Degradation}", lanDegradation);
}

lanDiscovery.Changed += () => appState.NeedsRedraw = true;
await lanDiscovery.StartAsync();

var plannerState = sp.GetRequiredService<PlannerState>();
var viewerState = sp.GetRequiredService<ViewerState>();
var external = sp.GetRequiredService<IExternal>();
var timeProvider = sp.GetRequiredService<ITimeProvider>();

// Dev/test: TIANWEN_NOW anchors the whole system clock to a simulated instant (see StartupTimeOverride).
// Surface it loudly so a shifted "now" is never mistaken for a bug.
if (StartupTimeOverride.TryGet(out var simulatedNow, out var clockOffset))
{
    logger.LogWarning(
        "SIMULATED CLOCK ACTIVE ({EnvVar}={Raw}): now anchored to {SimulatedNow:o} (offset {Offset} from the real clock), advancing at real-time rate. Planner, session and fake devices all use this clock. Dev/test only.",
        StartupTimeOverride.EnvVarName, StartupTimeOverride.RawValue, simulatedNow, clockOffset);
}

// Resolve profile: auto-select if exactly one, otherwise none for now
var profiles = await sp.GetRequiredService<IDeviceDiscovery>()
    .Let(async dm =>
    {
        await dm.CheckSupportAsync(CancellationToken.None);
        await dm.DiscoverOnlyDeviceType(DeviceType.Profile, CancellationToken.None);
        return dm.RegisteredDevices(DeviceType.Profile).OfType<Profile>().ToList();
    });

if (profiles.Count == 1)
{
    appState.ActiveProfile = profiles[0];
}
else if (args.Length >= 2 && args[0] is "--active" or "-a")
{
    var name = args[1];
    appState.ActiveProfile = profiles.FirstOrDefault(p =>
        string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
        (Guid.TryParse(name, out var id) && p.ProfileId == id));
}

// --- One instance for the whole application ---
// Unlike the viewer, this is NOT keyed on anything: a second GUI would poll the same drivers and
// contend for them, and the multi-rig Home tab already exists so one GUI can watch several rigs.
// So a second launch activates the running window and exits.
//
// The payload is empty because there is no document to hand over, and an empty payload is a
// request to activate. A second launch passing --active does NOT switch the running profile:
// that is gated (ProfileSwitchGate refuses while connected or running), so it is logged as
// ignored rather than silently dropped. Acting on it is deferred.
const string GuiGateScope = "tianwen-gui";
const string GuiSingleInstanceEnvVar = "TIANWEN_GUI_SINGLE_INSTANCE";
InstanceGate? instanceGate = null;
if (!string.Equals(Environment.GetEnvironmentVariable(GuiSingleInstanceEnvVar), "0", StringComparison.Ordinal))
{
    var gateChannel = InstanceGate.ChannelFor(GuiGateScope);
    instanceGate = InstanceGate.TryClaim(gateChannel, logger);
    if (instanceGate is null)
    {
        if (args.Length >= 2 && args[0] is "--active" or "-a")
        {
            logger.LogWarning(
                "A TianWen GUI is already running; activating it. The requested profile {Profile} is ignored, "
                + "because switching profiles is refused while a session is connected or running.", args[1]);
        }

        if (InstanceGate.TryHandOff(gateChannel, string.Empty, TimeSpan.FromSeconds(5), logger))
        {
            logger.LogInformation("Activated the running TianWen GUI instead of starting a second one");
            return 0;
        }

        // Nobody answered in time. A second GUI is a poor outcome, but a launch that does nothing
        // at all is worse, so fall through and start.
        logger.LogWarning("An instance holds the GUI channel but did not answer; starting anyway");
    }
}

// SDL3 + Vulkan init. Install the native-library resolver before the first
// P/Invoke into SDL3 so a failed DLL load lands in the file logger instead of
// crashing silently behind a WinExe (no stderr visible to the user).
NativeLoaderDiagnostics.Install(logger);

using var sdlWindow = NativeLoaderDiagnostics.InitNative(logger, "SDL3 + Vulkan window",
    () => SdlVulkanWindow.Create("\U0001F52D TianWen", 1280, 900));

sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var bus = new SignalBus();
// One owner for the GPU trio: created here, disposed at scope end in top-down order (gui, renderer,
// context) by its own Dispose -- and before sdlWindow, whose using is declared above this line.
using var gpu = new GpuStack<VkGuiRenderer>(logger, sdlWindow, (uint)pixW, (uint)pixH,
    r => new VkGuiRenderer(r, (uint)pixW, (uint)pixH, bus, logger) { DpiScale = sdlWindow.DisplayScale });
var renderer = gpu.Renderer;
var guiRenderer = gpu.Top;
// The atlas info panel's picture of the object, fetched on selection and cached on disk.
guiRenderer.ObjectPictureStore = sp.GetRequiredService<IObjectPictureStore>();
// The 🪐 planetary tab renders the live capture/stack via this shared controller (also driven by
// StartVideoCaptureSignal/StopVideoCaptureSignal in AppSignalHandler).
var planetaryCapture = sp.GetRequiredService<PlanetaryCaptureController>();
guiRenderer.PlanetaryCapture = planetaryCapture;

// Event handler setup
using var cts = new CancellationTokenSource();
// Separate CTS for non-session background work (planner init, weather, live planetary capture) -- cancelled
// on quit (RequestQuit) without tearing down the SDL loop early or affecting running sessions. Its token is
// handed to the planetary capture's Start, so quitting cancels the capture (and its token-polling loops).
using var backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
var tracker = new BackgroundTaskTracker();
var lastWindowTitle = "\U0001F52D TianWen";

// Stopping the rig, one sequence for a quit and for a dead display (RigShutdown says why the order matters).
var rigShutdown = new RigShutdown(guiRenderer.ViewContexts.Local.LiveSession, appState.DeviceHub, timeProvider, logger);
var displayLost = false;
var handlers = new GuiEventHandlers(sp, appState, plannerState, guiRenderer, cts, backgroundCts.Token, external, tracker)
{
    GetClipboardText = GetClipboardText,
    SetClipboardText = text => SetClipboardText(text)
};

// The platform's text-input lifecycle, bound ONCE to the focus owner's transition event. This is the only
// place in the app that knows SDL text input exists: every other path moves focus through TextInputFocus and
// gets the Start/Stop for free, so a field can no longer stop taking input while the IME stays up (which is
// what a hand-assigned focus pointer used to produce).
appState.TextInputFocus.FocusChanged += (_, next) =>
{
    if (next is null)
    {
        StopTextInput(sdlWindow.Handle);
    }
    else
    {
        StartTextInput(sdlWindow.Handle);
    }

    appState.NeedsRedraw = true;
};

// The app's entry points to that transition stay signals, so focus changes keep their place in the deferred
// bus ordering alongside everything else a frame does.
bus.Subscribe<ActivateTextInputSignal>(sig => appState.TextInputFocus.Focus(sig.Input));
bus.Subscribe<DeactivateTextInputSignal>(_ => appState.TextInputFocus.Blur());
// Open an external URL in the OS default browser (planner details -> Wikipedia link). Host-level +
// desktop-only on purpose: UseShellExecute routes a URL through the shell (ShellExecute on Windows,
// xdg-open/open on Linux/macOS), which the WASM-shared abstraction layer cannot do. Best-effort.
bus.Subscribe<OpenUrlSignal>(sig =>
{
    try
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sig.Url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to open URL {Url}", sig.Url);
    }
});

// BuildScheduleSignal is now handled inside AppSignalHandler; no host-level subscription needed

// Load saved session configuration + initialize planner (shared logic in AppSignalHandler)
// ESC quit confirmation: first ESC shows message, second ESC within 3s actually quits
long escConfirmTimestamp = 0;
int _lastShutdownPendingCount = -1;
string? _lastShutdownProgress = null;
var signalHandler = handlers.SignalHandler;

// Quitting, the one rule the TUI shares (AppQuit): ask first while this computer's session runs, then cancel
// the background work, stop the rig through its runs' own endings and warm the cameras, while the loop shows
// the progress. Recording how recently each watched rig answered goes first, before its mirror goes away.
var appQuit = new AppQuit(appState, guiRenderer.ViewContexts, rigShutdown, tracker, backgroundCts, timeProvider,
    () => signalHandler.FlushRigLastSeenAsync(System.Threading.CancellationToken.None));
tracker.Run(() => signalHandler.LoadSessionConfigAsync(backgroundCts.Token), "Load session config");

// P3 of docs/plans/mount-safety-limits.md, the GUI half: a profile's mount safety limits apply to a MANUAL
// slew with no session running -- the case the config was put on the profile for -- and a session only
// enforces them on the mount it leases. The GUI runs a bare ServiceCollection, so the watcher's loop is
// driven here the way LanDiscovery's lifecycle is above, on the background token: quitting stops it
// without touching a running session, whose leased mount the watcher skips on its own anyway.
tracker.Run(() => sp.GetRequiredService<MountLimitWatcher>().RunAsync(backgroundCts.Token), "Mount limit watcher");

if (appState.ActiveProfile is not null)
{
    // Legacy migration: earlier builds stored site in the Mount URI query string.
    // Copy into ProfileData.Site* on first sight so TransformFactory can find it,
    // then persist so the migration only runs once.
    if (appState.ActiveProfile.Data is { } migrData)
    {
        var (migrated, changed) = EquipmentActions.MigrateSiteFromMountUri(migrData);
        if (changed)
        {
            // Captured rather than read back off the state inside the closure: the background save
            // then writes the profile it just migrated, whatever becomes active in the meantime.
            var migratedProfile = appState.ActiveProfile.WithData(migrated);
            appState.ActiveProfile = migratedProfile;
            tracker.Run(() => migratedProfile.SaveAsync(external, backgroundCts.Token),
                "Persist migrated site coordinates");
            logger.LogInformation("Migrated site coordinates from Mount URI query into ProfileData for profile {ProfileId}.",
                appState.ActiveProfile.ProfileId);
        }
    }

    var transform = TransformFactory.FromProfile(appState.ActiveProfile, timeProvider, out _);
    if (transform is not null)
    {
        AppSignalHandler.ApplySiteFromTransform(plannerState, transform);
        tracker.Run(() => signalHandler.InitializePlannerAsync(transform, backgroundCts.Token), "Compute tonight's best targets");
    }
    else
    {
        appState.AppendNotification(timeProvider.GetUtcNow(),
            NotificationSeverity.Warning, "Set site coordinates in Equipment tab");
    }
}

// Auto-discover devices on startup via signal bus. If the active profile already
// references fake devices (e.g. a dev/testing profile with Fake Mount + cameras),
// opt the first discovery into IncludeFake so those URIs resolve to discovered
// devices without the user having to press Shift+Discover first.
var includeFakeOnStartup = appState.ActiveProfile?.Data is { } profileData && profileData.ReferencesAnyFakeDevice;
bus.Post(new DiscoverDevicesSignal(IncludeFake: includeFakeOnStartup));

// --- Main event loop via SdlEventLoop ---
var _lastSessionRedrawTimestamp = timeProvider.GetTimestamp();
// The shutdown drain's redraw tick (see CheckNeedsRedraw): 0 so its first check draws at once.
long _lastShutdownRedrawTimestamp = 0;
long _lastSlowFrameLogTimestamp = 0;

// One literal for the window's clear and for the image pane the viewer tab hosts, for the reason stated
// on ImageRendererBase.CanvasBackground: a partial frame is not cleared, so the pane paints its own ground
// and the two colours have to be the same one.
var canvasBackground = new RGBAColor32(0x12, 0x12, 0x18, 0xff);
guiRenderer.CanvasBackground = canvasBackground;

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = canvasBackground,

    OnResize = (rw, rh) =>
    {
        guiRenderer.DpiScale = sdlWindow.DisplayScale;
        guiRenderer.Resize(rw, rh);
    },

    // The renderer's swapchain recovery isn't sticking; it kept wedging + recovering, meaning the
    // current workload keeps re-saturating the GPU (the classic case: a runaway sky-map zoom producing
    // a multi-second frame). Shed load so the GPU can drain: leave the heavy view for the cheap
    // Notifications tab and reset the sky-map zoom so the runaway frame stops being submitted. Runs on
    // the render thread; hardware + session control run on a background task and are unaffected by a
    // render stall, so this only changes what's on screen, never what the mount/camera are doing.
    OnRenderDegraded = () =>
    {
        var wasTab = appState.ActiveTab;
        var fovAtStall = guiRenderer.SkyMapState.FieldOfViewDeg; // capture for the trace before resetting
        appState.ActiveTab = GuiTab.Notifications;
        guiRenderer.SkyMapState.FieldOfViewDeg = 60.0; // reset a runaway zoom to a sane default
        guiRenderer.SkyMapState.NeedsRedraw = true;
        appState.NeedsRedraw = true;
        appState.RecordNotification(timeProvider.GetUtcNow(), NotificationSeverity.Warning,
            $"Display recovered from a GPU stall on the {wasTab} view (sky-map FOV was {fovAtStall:F0} deg) - switched to a safe view and reset the zoom. Hardware and session control were unaffected.");
    },

    // The renderer gave the device up (SdlVulkan.Renderer 7.48 declares a device that keeps refusing work
    // dead, which is the Adreno's actual failure). The loop stops after this; what follows loop.Run then
    // keeps the night alive without a window instead of ending it (P0a, #743).
    OnGpuWedged = () =>
    {
        displayLost = true;
        logger.LogCritical("The GPU was declared wedged: this window cannot draw again. Runs go on without it; closing the window stops the rig.");
    },

    // One pointer callback: the loop synthesizes the InputEvents with the real release coordinates
    // on MouseUp (the previous hand-wired OnMouseUp reconstructed them from a cached position, 
    // without them the click-vs-drag detection in SkyMapTab compared (0, 0) to the real mouse-down
    // position). Presses route left-button only, as before; everything else flows through.
    OnPointerInput = evt =>
    {
        // The pointer's appearance comes from the regions painted last frame (active tab first, then the
        // chrome), never from a predicate here: each region states its own kind, so a hyperlink asks for
        // the hand and a text field for the I-beam without this host knowing either exists. The previous
        // form could only ever answer "link?", which is why every text field in the GUI showed an arrow.
        // Non-dispatching, so hover never fires a click handler; cheap and idempotent, since
        // SetSystemCursor no-ops when the cursor is already active.
        if (evt is InputEvent.MouseMove(var mx, var my))
        {
            sdlWindow.SetSystemCursor((guiRenderer.CursorAt(mx, my) ?? CursorKind.Default).ToSystemCursor);
        }

        return evt switch
        {
            InputEvent.MouseDown { Button: not MouseButton.Left } => true,
            _ => handlers.HandleInput(evt),
        };
    },

    OnPinch = (scale, mx, my, source) =>
    {
        handlers.HandleInput(new InputEvent.Pinch(scale, mx, my) { Source = source });
    },

    OnPinchEnd = () =>
    {
        handlers.HandleInput(new InputEvent.PinchEnd());
    },

    OnTextInput = text => handlers.HandleInput(new InputEvent.TextInput(text)),

    // The IME's in-progress composition. This goes STRAIGHT to the focused field rather than through
    // handlers.HandleInput, because it is not input to be interpreted: nothing may bind it, consume it, or
    // treat it as a shortcut. It is the input method telling us what to draw until it commits, and the
    // committed characters then arrive through OnTextInput above like any other text.
    OnTextEditing = (text, cursor, length) =>
    {
        if (appState.TextInputFocus.Current is { } field)
        {
            field.SetComposition(text, cursor, length);
            appState.NeedsRedraw = true;
        }
    },

    CheckNeedsRedraw = () =>
    {
        // First, and above the early returns below: this is the only place a hand-off from a
        // later launch is noticed, and CheckNeedsRedraw is the one callback that runs on every
        // loop iteration including the idle polls.
        PumpInstanceGate();

        // Recompute targets when site/date changes (shared logic in AppSignalHandler)
        signalHandler.CheckRecompute();
        // Skip telemetry / preview polls during shutdown: they're pointless while we're
        // disconnecting, and they spawn short-lived tracker tasks that race with the
        // long-running warm/disconnect task. The shutdown banner shows the first pending
        // task when only one is active, falling back to "(N tasks)" otherwise, without
        // this gate the count flips 1 <-> 2 every 2s and the banner flickers between
        // "Shutting down... Disconnecting <camera>" and "Shutting down... (2 tasks)".
        if (!appState.ShuttingDown)
        {
            // Sample camera cooler/temperature telemetry while the equipment tab is visible.
            // Internally rate-limited per-camera so calling every frame is cheap.
            signalHandler.PollCameraTelemetry();
            // Sample preview telemetry (camera/focuser/mount) while live session tab is visible
            // and no session is running. Internally rate-limited.
            signalHandler.PollPreviewTelemetry();
        }

        // During shutdown, show progress and signal ready to stop. This is where the cameras WARM, so it
        // can run for minutes, and it used to ask for a frame on every loop iteration, which rendered the
        // whole GUI flat out for the length of the warm-up. It still needs frames: signals (the warm-up
        // progress) and task completions are processed in OnPostFrame, which runs only after a frame, and
        // so is the Stop once everything has finished. So: one frame when the pending set changes, one
        // when it empties (whose post-frame stops the loop), and a 500 ms tick in between, the cadence a
        // running session already uses for its progress. NeedsRedraw is not consulted here: OnPostFrame
        // leaves it set for the whole shutdown, so it would answer true on every iteration.
        if (appState.ShuttingDown)
        {
            var shutdownNow = timeProvider.GetTimestamp();
            var shutdownChanged = false;
            // What the rig's stop is doing now ("Finalising the session...", "Warming <camera>"), which says
            // more than a task count and moves as the stop does.
            var progressNow = appQuit.Progress;
            if (!tracker.HasPending)
            {
                appState.ShutdownComplete = true;
                shutdownChanged = true;
            }
            else if (tracker.PendingCount != _lastShutdownPendingCount || !ReferenceEquals(progressNow, _lastShutdownProgress))
            {
                _lastShutdownPendingCount = tracker.PendingCount;
                _lastShutdownProgress = progressNow;
                appState.StatusMessage = progressNow is { } stopping
                    ? $"Shutting down\u2026 {stopping}"
                    : _lastShutdownPendingCount == 1
                        ? $"Shutting down\u2026 {tracker.PendingDescriptions.First()}"
                        : $"Shutting down\u2026 ({_lastShutdownPendingCount} tasks)";
                shutdownChanged = true;
            }

            if (shutdownChanged
                || timeProvider.GetElapsedTime(_lastShutdownRedrawTimestamp, shutdownNow) >= TimeSpan.FromMilliseconds(500))
            {
                _lastShutdownRedrawTimestamp = shutdownNow;
                return true;
            }
            return false;
        }

        // The status-bar wall clock (HH:mm:ss) is shown on EVERY tab, so we need at
        // least a 1 Hz redraw on all tabs to make it tick. This used to be gated to the
        // Live Session / Guider / Sky Map tabs, which is exactly why the clock on the
        // Planner (and other tabs) only updated when an input event happened to force a
        // frame. 500ms while a session OR a polar-alignment routine is running (progress
        // bars, per-rung "Probing 200ms (3/8)" status, axis-error needles, locked-exposure
        // indicator); 1s otherwise (clock tick + sky-map LST advance).
        {
            var now = timeProvider.GetTimestamp();
            // The faster cadence is driven by the LOCAL run even when it is off screen: its progress
            // bars, per-rung status and notifications still have to surface at 2 Hz while a remote
            // context is displayed. (A remote session's own cadence follows once a mirror exists.)
            var liveState = guiRenderer.ViewContexts.Local.LiveSession;
            var inPolarRoutine = liveState.Mode == LiveSessionMode.PolarAlign
                && liveState.PolarPhase != PolarAlignmentPhase.Idle;
            var interval = liveState.IsRunning || inPolarRoutine
                ? TimeSpan.FromMilliseconds(500)
                : TimeSpan.FromSeconds(1);
            if (timeProvider.GetElapsedTime(_lastSessionRedrawTimestamp, now) >= interval)
            {
                _lastSessionRedrawTimestamp = now;
                return true;
            }
        }

        // Live-session / planner state changes (progress callbacks fired from
        // the thread pool, etc.) push their redraw flag here -- treat them as
        // first-class triggers so per-rung polar status updates surface within
        // a frame instead of waiting for the periodic tick to catch them.
        // A deferred chart-texture upload (planner) leaves a freshly-uploaded texture
        // undrawn until the next frame; force that follow-up frame so the chart doesn't
        // lag one selection behind. Gated to the Planner tab so it can't spin a redraw
        // loop while another tab is active (the flag clears once RenderChart draws it).
        // A frame a widget asked for at a later time (the atlas's settling hover) is taken first, so the
        // request clears even on an iteration another trigger would have drawn anyway.
        var widgetFrameDue = guiRenderer.TakeDueFrameRequest();
        return widgetFrameDue || appState.NeedsRedraw || plannerState.NeedsRedraw
            || guiRenderer.SkyMapState.NeedsRedraw
            || guiRenderer.ViewContexts.AnyNeedsRedraw
            || (appState.ActiveTab == GuiTab.Planner && guiRenderer.PlannerChartPendingDraw)
            // A focused field needs a frame only when its caret FLIPS, twice a second: the blink is on
            // the clock now (DIR.Lib CaretBlink). It used to be counted in frames, so a focused field
            // asked for one on every iteration and the whole GUI rendered continuously while it had the
            // keyboard.
            || (appState.ActiveTextInput is { IsActive: true }
                && CaretBlink.PhaseAt(timeProvider.GetTimestamp(), timeProvider.TimestampFrequency) != guiRenderer.PaintedCaretPhase);
    },

    OnRender = () =>
    {
        // Count the frame BEFORE painting it. DIR.Lib gates "are this widget's registered regions current"
        // on Ui.FrameId, and a host that never moves it leaves that test always true, so a widget the host
        // has stopped drawing goes on answering with whatever it last painted. That is not theoretical
        // here: the chrome's children are ALL the tabs, not just the active one, so with the counter
        // parked the Tab ring spanned every tab that had ever been on screen and the keyboard could land
        // in a field on a tab nobody can see. Counting is the opt-in DIR.Lib documents for exactly this,
        // and it is one line rather than a rule the tab ring would otherwise have to special-case.
        guiRenderer.Ui.FrameId++;

        var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
        guiRenderer.Render(appState, plannerState, viewerState, timeProvider);
        var renderElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(renderStart);

        // Everything that can only be decided once the frame exists: a field that stopped being drawn
        // loses the keyboard (scroll one out of a culled list, or switch tabs away from it, and typing
        // would go on editing a box nobody can see), a field that asked for the keyboard as it appeared
        // gets it, and a tooltip whose region has gone expires. Asked AFTER the paint, of everything the
        // frame actually painted -- doing it before, or of one surface when the frame draws two, does the
        // opposite of what it is for. Only reached on a frame that rendered (CheckNeedsRedraw gates this).
        handlers.AfterPaint();

        // Tell the platform where the caret is, so an input method puts its candidate window beside the text
        // rather than over it. SDL does not track our caret and has no other way to find out, so without this
        // a CJK IME has nothing to anchor to. Sent after the paint, from the rect the caret was actually drawn
        // at (the BlurIfUnpainted above guarantees a focused field was painted this frame), and only while a
        // field is focused -- there is no area to report otherwise, and text input is stopped anyway.
        if (appState.TextInputFocus.Current is not null && guiRenderer.FocusedCaretRect is { Width: > 0 } caret)
        {
            sdlWindow.SetTextInputArea(caret.UpperLeft.X, caret.UpperLeft.Y, (int)caret.Width, (int)caret.Height, 0);
        }

        // Only log frames that take meaningfully long, and rate-limit to once per
        // ~250ms so we don't drown SEQ during sustained slowness. The threshold is
        // 20ms (~50fps target); below that the user can't perceive the cost.
        if (renderElapsed.TotalMilliseconds > 20.0)
        {
            var nowTicks = timeProvider.GetTimestamp();
            if (timeProvider.GetElapsedTime(_lastSlowFrameLogTimestamp, nowTicks).TotalMilliseconds > 250.0)
            {
                _lastSlowFrameLogTimestamp = nowTicks;
                logger.LogInformation(
                    "Slow frame: render={RenderMs:F1}ms tab={ActiveTab}",
                    renderElapsed.TotalMilliseconds, appState.ActiveTab);
            }
        }
    },

};

// A quit (the window's close button, Esc twice): the shared rule, AppQuit.
void RequestQuit() => appQuit.Request();

// Set separately to allow loop.Stop() self-reference
loop.OnPostFrame = () =>
{
    // Any signal processed this post-frame may have mutated state that the just-rendered
    // frame doesn't reflect: preserve NeedsRedraw so the loop renders again on the next
    // iteration. The previous "redrawBefore == false" gate was buggy: when the click that
    // triggered this frame's render also set NeedsRedraw=true, we wrongly cleared it.
    var processedAny = bus.ProcessPending(tracker);
    if (tracker.ProcessCompletions(logger))
    {
        appState.NeedsRedraw = true;
    }
    var signalSetRedraw = processedAny;

    // During shutdown, stop the loop once all tasks (Finalise) have completed
    if (appState.ShutdownComplete)
    {
        loop.Stop();
        return;
    }

    // After the signals above: a dismissed confirmation withdraws the quit, a confirmed one goes on to stop
    // the rig once the session has ended (AppQuit.Tick).
    appQuit.Tick();

    if (!appState.ShuttingDown && !signalSetRedraw)
    {
        appState.NeedsRedraw = false;
        // Signals processed during OnPostFrame (e.g. SkyMapClickSelectSignal)
        // mutate SkyMapState but the current frame already ran. Only clear the
        // flag when no signal fired this post-frame so the mutation drives the
        // next render.
        guiRenderer.SkyMapState.NeedsRedraw = false;
        // Same contract for the live-session flag: the polar progress callback
        // (running on a thread-pool continuation) sets this from outside the
        // frame, so we have to consume it once we've actually rendered, or it
        // would re-trigger the redraw loop on every iteration.
        guiRenderer.ViewContexts.ClearNeedsRedraw();
    }
    plannerState.NeedsRedraw = false;

    // Update window title (only when changed). The title names what you are LOOKING at: the active
    // tab's icon + name exactly as the sidebar draws them (TabTitleChrome, so the Live Session glyph
    // follows the mode), the profile it runs -- prefixed with WHICH rig, when that is not this
    // computer -- and, while a session runs, its phase + target after a separating middle dot.
    var ls = guiRenderer.ViewContexts.Active.LiveSession;
    var viewed = guiRenderer.ViewContexts.Active;

    // The profile label comes from the home board's card for the context on screen, so the title and the
    // card can never disagree about what a rig runs -- and a remote rig's profile costs no second lookup.
    string? viewedProfile = null;
    foreach (var card in appState.HomeCards)
    {
        if (card.IsViewed)
        {
            viewedProfile = card.Subtitle;
            break;
        }
    }

    var (tabIcon, tabLabel) = guiRenderer.TabTitleChrome(appState.ActiveTab);
    // The local rig is implied by the window itself, so only a remote rig names itself.
    var context = viewed.IsLocal
        ? viewedProfile
        : viewedProfile is { Length: > 0 } p ? $"{viewed.DisplayName} - {p}" : viewed.DisplayName;
    var newTitle = context is { Length: > 0 }
        ? $"{tabIcon} {tabLabel} - {context}"
        : $"{tabIcon} {tabLabel}";
    if (ls.IsRunning)
    {
        newTitle = ls.ActiveObservation is { Target: var target }
            ? $"{newTitle} · {LiveSessionActions.PhaseLabel(ls.Phase)} - {target.Name}"
            : $"{newTitle} · {LiveSessionActions.PhaseLabel(ls.Phase)}";
    }
    if (newTitle != lastWindowTitle)
    {
        lastWindowTitle = newTitle;
        SetWindowTitle(sdlWindow.Handle, newTitle);
    }
};

loop.OnKeyDown = evt =>
{
    if (appState.ShuttingDown) return false; // ignore keys during shutdown

    if (handlers.HandleInput(evt))
    {
        return true;
    }

    // Route to active tab first: tab may consume Escape (e.g. dismiss abort confirm)
    if (guiRenderer.ActiveTab?.HandleInput(evt) == true)
    {
        return true;
    }

    // Global keys (not consumed by active tab or text input)
    switch (evt.Key)
    {
        case InputKey.Escape:
            var now2 = timeProvider.GetTimestamp();
            if (escConfirmTimestamp != 0
                && timeProvider.GetElapsedTime(escConfirmTimestamp, now2) < TimeSpan.FromSeconds(3))
            {
                // Second ESC within 3s: actually quit
                escConfirmTimestamp = 0;
                RequestQuit();
            }
            else
            {
                // First ESC: show confirmation message
                escConfirmTimestamp = now2;
                appState.StatusMessage = "Press ESC again to quit";
                appState.NeedsRedraw = true;
            }
            return true;
        case InputKey.F11:
            sdlWindow.ToggleFullscreen();
            return true;
        // F12 = dark-adaptation mode, matching SharpCap's night-vision key so it is the one an
        // observer already has in their fingers at the mount. Posted rather than applied here: the
        // theme is app state, so the handler that owns it lives in AppSignalHandler.
        case InputKey.F12:
            bus.Post(new ToggleNightModeSignal());
            return true;
    }

    return true;
};

// Intercept window close button: behave like abort when session is active
loop.OnQuit = () =>
{
    RequestQuit();
    // Intercept if session is running (including Finalise), showing abort confirm, or shutting down
    return appState.ShuttingDown || appState.QuitRequested || guiRenderer.ViewContexts.Local.LiveSession.IsRunning;
};

#if SIBLING_DEBUG_INSPECTORS
// Live UI debug inspector (DEBUG only -- compiled out of Release entirely). Exposes this running
// process to the SdlVulkan.Renderer.Inspector MCP sidecar so an agent can discover it, read the
// widget tree, screenshot the window, inject input, and post a curated set of signals. All of the
// in-process machinery lives in the framework under #if SIBLING_DEBUG_INSPECTORS; this block is the only wiring.
using var debugInspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "TianWen",
    WindowTitle = () => lastWindowTitle,
    // Chrome + navigation rail + active tab, rebuilt each frame and composed by the RENDERER: which
    // widgets make up a frame is its knowledge, not this host's, and assembling the list here meant a
    // new child widget silently dropped out of the inspector (its controls just stop appearing).
    // Read on the render thread inside the inspector's command pump.
    GetRegions = () => guiRenderer.InspectorRegions(),
    GetLayout = () =>
    {
        // Full arranged layout tree (chrome + active tab), the structural counterpart to GetRegions
        // (which only exposes the clickable subset). Both rebuilt each frame, read on the render thread.
        var nodes = new List<Layout.ArrangedNode<float>>();
        nodes.AddRange(guiRenderer.GetCapturedLayout());
        if (guiRenderer.ActiveTab is PixelWidgetBase<VulkanContext> activeTab)
        {
            nodes.AddRange(activeTab.GetCapturedLayout());
        }
        return nodes;
    },
    AppState = s =>
    {
        // Curated snapshot (NOT a full GuiAppState dump -- it holds non-serializable handles).
        // The framework owns serialization; we just declare named values.
        var contexts = guiRenderer.ViewContexts;
        var ls = contexts.Active.LiveSession;
        s.Set("activeTab", appState.ActiveTab.ToString());
        // Which context the tabs are rendering, and whether this node's own session is running
        // underneath it -- the two facts an agent needs to interpret every field below.
        s.Set("viewContext", contexts.Active.DisplayName);
        s.Set("viewContextCount", contexts.All.Length);
        s.Set("localSessionRunning", contexts.Local.LiveSession.IsRunning);
        s.Set("profile", appState.ActiveProfile?.DisplayName);
        s.Set("status", appState.StatusMessage);
        s.Set("sessionRunning", ls.IsRunning);
        s.Set("phase", ls.Phase.ToString());
        s.Set("unreadNotifications", appState.UnreadNotificationCount);

        // Live Session tab mode (Preview / PolarAlign / Planetary / Flats). Mode entry is local UI
        // state (not a bus signal), so expose it here for read-back; the flat run itself is driven by
        // posting StartFlats (which runs regardless of the visible mode) and observed via phase +
        // these fields.
        s.Set("liveSessionMode", ls.Mode.ToString());
        s.Set("flatRunActive", ls.FlatsCts is not null);
        s.Set("flatStatus", ls.FlatStatusMessage);

        // Sky-map viewport: lets the inspector frame the view deterministically (via the
        // SkyMapSetView signal) and read back where it landed instead of eyeballing a screenshot.
        var sky = guiRenderer.SkyMapState;
        s.Set("mapCenterRaHours", sky.CenterRA);
        s.Set("mapCenterDec", sky.CenterDec);
        s.Set("mapFovDeg", sky.FieldOfViewDeg);
        s.Set("mapMode", sky.Mode.ToString());
        s.Set("mapShowObjectOverlay", sky.ShowObjectOverlay);
        s.Set("mapShowDarkNebulae", sky.ShowDarkNebulae);
        // Time scrub offset (Stellarium-style): minutes for assertions, formatted for readability.
        s.Set("mapTimeOffsetMinutes", sky.TimeOffset.TotalMinutes);
        s.Set("mapTimeOffset", SkyMapState.FormatOffset(sky.TimeOffset));

        // Mount marker (the believed-pointing reticle): the Solve & Sync witness. After a
        // sync the believed pointing jumps to truth, so polling this RA/Dec before/after
        // shows the marker move; the arcmin offset itself surfaces in lastNotification.
        if (sky.MountOverlay is { } mo)
        {
            s.Set("mountConnected", true);
            s.Set("mountName", mo.DisplayName);
            s.Set("mountRaJ2000", mo.RaJ2000);
            s.Set("mountDecJ2000", mo.DecJ2000);
            s.Set("mountSlewing", mo.IsSlewing);
            s.Set("mountTracking", mo.IsTracking);
        }
        else
        {
            s.Set("mountConnected", false);
        }

        // Newest notification text, where the Solve & Sync arcmin offset, slew results,
        // and error messages all land. Newest entry is at index 0.
        var notes = appState.Notifications;
        s.Set("lastNotification", notes.IsDefaultOrEmpty ? null : notes[0].Message);
    },
    // Every postable *Signal in this assembly is source-generated into the directory (SignalDirectory, by
    // DIR.Lib's SignalDirectoryGenerator) with no runtime reflection, so the inspector can list + post any
    // of them by name -- no hand-maintained list. Fully qualified because DIR.Lib generates its own
    // DIR.Lib.SignalDirectory for its chrome signals, so the bare name is ambiguous. DEBUG-only; nothing is
    // generated in Release. BuildFactories still takes an optional `overrides` map for a signal that ever
    // needs a curated JSON shape -- none do today (the generated factories derive keys from the parameter
    // names and honour each signal's declared ctor defaults), so it is omitted.
    SignalFactories = TianWen.UI.Abstractions.SignalDirectory.BuildFactories(bus),
});
#endif

Exception? loopFault = null;
try
{
    loop.Run(cts.Token);
}
catch (Exception ex)
{
    // An exception out of a render, an input handler or a post-frame step is an app bug (the renderer
    // resolves the frame and rethrows it on purpose). It must not end the night either: the window can
    // no longer be trusted to draw, so it goes the way a dead GPU does. It used to crash the process
    // here, with no drain and no Finalise.
    loopFault = ex;
    logger.LogCritical(ex, "The GUI's event loop failed: this window stops drawing, and runs go on without it.");
}

if (displayLost || loopFault is not null)
{
    RunWithoutDisplay(loopFault is null ? "Display lost" : "Display failed");
}

// P0a (#743): the window cannot draw any more, and the rig must not notice. The runs the old tail cut off
// mid-ramp (it cancelled `cts`, which the session is linked to, then drained for at most 5 s) go on to
// their own end instead, the window stays pumped so it can still be closed, and closing it means "stop the
// rig": abort, Finalise in full, then the cameras. Only then does the ordinary teardown below run.
void RunWithoutDisplay(string why)
{
    // What serves only the window, and the planetary capture (interactive, meaningless unseen). The runs
    // are the RigShutdown's.
    backgroundCts.Cancel();

    using var stopRig = new CancellationTokenSource();
    string? progress = null;
    var tail = Task.Run(() => rigShutdown.StopAsync(RigShutdownMode.DisplayLost, stopRig.Token,
        p => Volatile.Write(ref progress, p)));

    var local = guiRenderer.ViewContexts.Local.LiveSession;
    SetHeadlessTitle(HeadlessTitle(why,
        local.IsRunning ? RigShutdown.SessionGoesOn
        : local.FlatsCts is not null ? RigShutdown.FlatRunGoesOn
        : "Stopping the rig"));

    // Nothing of the app runs now: no frame is drawn, and no input, signal, telemetry poll or chrome has a
    // window to serve. The loop only keeps the window's events pumping, which SdlVulkan.Renderer 7.49 makes
    // safe on a wedged window (it stays inert), so the window is still movable and closable.
    loop.OnRender = null;
    loop.OnPostFrame = null;
    loop.OnKeyDown = null;
    loop.OnPointerInput = null;
    loop.OnTextInput = null;
    loop.OnTextEditing = null;
    loop.OnPinch = null;
    loop.OnPinchEnd = null;
    loop.OnResize = null;
    loop.OnRenderDegraded = null;

    string? shownProgress = null;
    loop.CheckNeedsRedraw = () =>
    {
        // Once per loop iteration, on this thread: the loop stops itself when the stop has finished.
        if (tail.IsCompleted)
        {
            loop.Stop();
            return false;
        }

        if (Volatile.Read(ref progress) is { } now && !ReferenceEquals(now, shownProgress))
        {
            shownProgress = now;
            SetHeadlessTitle(HeadlessTitle(why, now));
        }
        return false;
    };

    loop.OnQuit = () =>
    {
        // Asking reports itself through the progress (RigShutdown.StoppingTheRig), on this thread, so the
        // next check above shows it and nothing reported earlier can overwrite it.
        stopRig.Cancel();
        // Always intercepted: the window stays until the stop completes, since a warm-up must not be cut.
        return true;
    };

    // No deadline: a session left to finish runs until its own end, dawn included.
    loop.Run(CancellationToken.None);

    if (tail.Exception is { } stopFault)
    {
        logger.LogError(stopFault.GetBaseException(), "Stopping the rig without a display failed.");
    }
}

// The title is the only thing a window that cannot draw still shows, so while a run is left to finish it
// also says what closing does. Once the rig is stopping, closing again changes nothing, so the hint goes.
static string HeadlessTitle(string why, string progress)
    => progress is RigShutdown.SessionGoesOn or RigShutdown.FlatRunGoesOn
        ? $"{why}. {progress}. Close this window to stop the rig."
        : $"{why}. {progress}";

// Needs no GPU (SDL's own window title), so it works on a window that can no longer draw.
void SetHeadlessTitle(string title)
{
    lastWindowTitle = title;
    sdlWindow.SetTitle(title);
    logger.LogWarning("{Title}", title);
}

// Final cleanup: drain should complete quickly since we already waited in the loop.
// Use a timeout so force-quit (second X press) doesn't hang on warm-up tasks.
cts.Cancel();

// Drain completes quickly: warm-up already finished while the loop was alive. Bound it anyway so a
// straggler that won't cancel (e.g. a slow network discovery) can't leave the window Not Responding while
// the process exits -- the timeout the comment above always promised but never actually had.
// One drain, pumped rather than awaited or blocked on. The window and the Vulkan context can only
// be destroyed on THIS thread: awaiting here resumes the disposals on whichever thread finished the
// last task (a deadlock, since this thread is blocked inside async Main waiting for it), and
// blocking instead would leave the window Not Responding for the whole drain -- which is exactly
// the failure the 5s timeout below was reaching for and could not fix on its own.
async Task DrainShutdownAsync()
{
    try
    {
        // Bounded via the injected TimeProvider (FakeTimeProvider-controllable), not the raw system clock.
        await tracker.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), timeProvider.System);
    }
    catch (TimeoutException)
    {
        logger.LogWarning("Shutdown: background tasks did not drain within 5s; abandoning them.");
    }

    // Stop + dispose the planetary capture (cancels the capture loop; bounded drain of any in-flight stack).
    await planetaryCapture.DisposeAsync();

    // Say bye so peers drop us promptly (expiry is the fallback for an unclean exit).
    await lanDiscovery.SendByeAsync();
}

ShutdownDrain.PumpUntilComplete(loop, DrainShutdownAsync(), logger);
lanDiscovery.Dispose();

// The GPU trio (guiRenderer, renderer and the Vulkan context) is disposed by `using var gpu` at
// scope end, top-down, still ahead of sdlWindow's own using.

// Raises the window when another launch asks for it. The payload is empty for this app
// (there is no document to open), so activation is the whole of the work.
void PumpInstanceGate()
{
    if (instanceGate is null)
    {
        return;
    }

    while (instanceGate.TryDequeue(out _))
    {
        // Restores only if minimised, then raises. SdlVulkanWindow implements
        // IActivatableWindow, so this is AppShell's own rule, not a local copy of it; the
        // reason it is not simply "restore, then raise" is on WindowActivation.
        sdlWindow.Activate();
        appState.NeedsRedraw = true;
    }
}

return 0;