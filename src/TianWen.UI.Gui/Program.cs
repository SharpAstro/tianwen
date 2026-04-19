using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using static SDL3.SDL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Gui;
using TianWen.UI.Shared;

// DI setup
var services = new ServiceCollection();
services
    .AddLogging(static builder =>
    {
        builder.AddProvider(new FileLoggerProvider("GUI"));
#if DEBUG
        builder.SetMinimumLevel(LogLevel.Debug);
#endif
    })
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddQHY()
    .AddAscom()
    .AddMeade()
    .AddOnStep()
    .AddIOptron()
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
    .AddSingleton(sp => new GuiAppState { DeviceHub = sp.GetService<IDeviceHub>() });

// Replace the null default with the GUI's active-profile-aware provider so that
// any COM port currently referenced by the active profile is excluded from
// discovery probing. Must come after AddDevices() (which registers the default).
services.Replace(ServiceDescriptor.Singleton<IPinnedSerialPortsProvider, ActiveProfilePinnedSerialPortsProvider>());

var sp = services.BuildServiceProvider();
var appState = sp.GetRequiredService<GuiAppState>();
var plannerState = sp.GetRequiredService<PlannerState>();
var viewerState = sp.GetRequiredService<ViewerState>();
var external = sp.GetRequiredService<IExternal>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.Gui");
var timeProvider = sp.GetRequiredService<ITimeProvider>();

// Resolve profile — auto-select if exactly one, otherwise none for now
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

// SDL3 + Vulkan init. Install the native-library resolver before the first
// P/Invoke into SDL3 so a failed DLL load lands in the file logger instead of
// crashing silently behind a WinExe (no stderr visible to the user).
NativeLoaderDiagnostics.Install(logger);

using var sdlWindow = NativeLoaderDiagnostics.InitNative(logger, "SDL3 + Vulkan window",
    () => SdlVulkanWindow.Create("\U0001F52D TianWen", 1280, 900));

sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var ctx = NativeLoaderDiagnostics.InitNative(logger, "Vulkan device",
    () => VulkanContext.Create(sdlWindow.Instance, sdlWindow.Surface, (uint)pixW, (uint)pixH));
var renderer = new VkRenderer(ctx, (uint)pixW, (uint)pixH);

var bus = new SignalBus();
var guiRenderer = new VkGuiRenderer(renderer, (uint)pixW, (uint)pixH, bus, logger)
{
    DpiScale = sdlWindow.DisplayScale
};

// Event handler setup
var cts = new CancellationTokenSource();
var tracker = new BackgroundTaskTracker();
var lastWindowTitle = "\U0001F52D TianWen";
var handlers = new GuiEventHandlers(sp, appState, plannerState, guiRenderer, cts, external, tracker);
handlers.GetClipboardText = GetClipboardText;
handlers.SetClipboardText = text => SetClipboardText(text);

// Signal subscriptions — text input activation/deactivation via SDL
bus.Subscribe<ActivateTextInputSignal>(sig =>
{
    if (appState.ActiveTextInput is { } prev && prev != sig.Input)
    {
        prev.Deactivate();
    }
    sig.Input.Activate();
    appState.ActiveTextInput = sig.Input;
    StartTextInput(sdlWindow.Handle);
    appState.NeedsRedraw = true;
});

bus.Subscribe<DeactivateTextInputSignal>(_ =>
{
    if (appState.ActiveTextInput is { IsActive: true } active)
    {
        active.Deactivate();
        appState.ActiveTextInput = null;
        StopTextInput(sdlWindow.Handle);
        appState.NeedsRedraw = true;
    }
});
// BuildScheduleSignal is now handled inside AppSignalHandler — no host-level subscription needed

// Load saved session configuration + initialize planner (shared logic in AppSignalHandler)
// Separate CTS for non-session background tasks — cancelled on quit without affecting running sessions.
var backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
// ESC quit confirmation: first ESC shows message, second ESC within 3s actually quits
long escConfirmTimestamp = 0;
int _lastShutdownPendingCount = -1;
var signalHandler = handlers.SignalHandler;
tracker.Run(() => signalHandler.LoadSessionConfigAsync(backgroundCts.Token), "Load session config");

if (appState.ActiveProfile is not null)
{
    var transform = TransformFactory.FromProfile(appState.ActiveProfile, timeProvider, out _);
    if (transform is not null)
    {
        AppSignalHandler.ApplySiteFromTransform(plannerState, transform);
        tracker.Run(() => signalHandler.InitializePlannerAsync(transform, backgroundCts.Token), "Compute tonight's best targets");
    }
    else
    {
        appState.StatusMessage = "Set site coordinates in Equipment tab";
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

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff),

    OnResize = (rw, rh) =>
    {
        guiRenderer.DpiScale = sdlWindow.DisplayScale;
        guiRenderer.Resize(rw, rh);
    },

    OnMouseDown = (button, x, y, clicks, mods) =>
    {
        if (button == 1) handlers.HandleInput(new InputEvent.MouseDown(x, y, Modifiers: mods, ClickCount: clicks));
        return true;
    },

    OnMouseMove = (x, y) => handlers.HandleInput(new InputEvent.MouseMove(x, y)),

    // SDL's OnMouseUp callback delivers only the button — coords come from the
    // last MouseMove, cached on appState. Without this the click-vs-drag
    // detection in SkyMapTab compares (0, 0) to the real mouse-down position.
    OnMouseUp = (button) =>
    {
        var (mx, my) = appState.MouseScreenPosition;
        handlers.HandleInput(new InputEvent.MouseUp(mx, my));
    },

    OnMouseWheel = (scrollY, mx, my) =>
    {
        handlers.HandleInput(new InputEvent.Scroll(scrollY, mx, my));
        return true;
    },

    OnPinch = (scale, mx, my) =>
    {
        handlers.HandleInput(new InputEvent.Pinch(scale, mx, my));
    },

    OnPinchEnd = () =>
    {
        handlers.HandleInput(new InputEvent.PinchEnd());
    },

    OnTextInput = text => handlers.HandleInput(new InputEvent.TextInput(text)),

    CheckNeedsRedraw = () =>
    {
        // Recompute targets when site/date changes (shared logic in AppSignalHandler)
        signalHandler.CheckRecompute();
        // Sample camera cooler/temperature telemetry while the equipment tab is visible.
        // Internally rate-limited per-camera so calling every frame is cheap.
        signalHandler.PollCameraTelemetry();
        // Sample preview telemetry (camera/focuser/mount) while live session tab is visible
        // and no session is running. Internally rate-limited.
        signalHandler.PollPreviewTelemetry();

        // During shutdown, show progress and signal ready to stop
        if (appState.ShuttingDown)
        {
            if (!tracker.HasPending)
            {
                appState.ShutdownComplete = true;
            }
            else if (tracker.PendingCount != _lastShutdownPendingCount)
            {
                _lastShutdownPendingCount = tracker.PendingCount;
                appState.StatusMessage = _lastShutdownPendingCount == 1
                    ? $"Shutting down\u2026 {tracker.PendingDescriptions.First()}"
                    : $"Shutting down\u2026 ({_lastShutdownPendingCount} tasks)";
            }
            return true; // always redraw during shutdown
        }

        // Redraw periodically (every ~500ms) when session is running on the Live tab
        if (guiRenderer.LiveSessionState.IsRunning && appState.ActiveTab is GuiTab.LiveSession or GuiTab.Guider)
        {
            var now = timeProvider.GetTimestamp();
            if (timeProvider.GetElapsedTime(_lastSessionRedrawTimestamp, now) >= TimeSpan.FromMilliseconds(500))
            {
                _lastSessionRedrawTimestamp = now;
                return true;
            }
        }

        return appState.NeedsRedraw || plannerState.NeedsRedraw
            || guiRenderer.SkyMapState.NeedsRedraw
            || appState.ActiveTextInput is { IsActive: true };
    },

    OnRender = () => guiRenderer.Render(appState, plannerState, viewerState, timeProvider),

};

// Request quit — shows abort confirmation if session is running, otherwise shuts down
void RequestQuit()
{
    if (appState.ShuttingDown)
    {
        // Already shutting down — refuse to quit while warm-up is in progress.
        // Same pattern as SessionPhase.Finalising: cameras must complete their
        // thermal ramp for sensor safety.
        appState.StatusMessage = "Warming cameras\u2026 please wait";
        appState.NeedsRedraw = true;
        return;
    }

    var liveState = guiRenderer.LiveSessionState;

    // During Finalising, can't do anything — just wait (warmup must complete)
    if (liveState.Phase is SessionPhase.Finalising)
    {
        appState.StatusMessage = "Warming cameras\u2026 please wait";
        appState.NeedsRedraw = true;
        return;
    }

    if (liveState.IsRunning && !liveState.ShowAbortConfirm)
    {
        // Session running — show abort confirmation (same as pressing Escape in Live Session tab)
        liveState.ShowAbortConfirm = true;
        appState.QuitRequested = true;
        appState.ActiveTab = GuiTab.LiveSession;
        appState.NeedsRedraw = true;
        return;
    }

    // No session running, or abort already confirmed — proceed with shutdown
    if (liveState is { SessionCts: { } sessionCts2 })
    {
        sessionCts2.Cancel();
    }

    // Cancel non-session background tasks (planner init, weather fetch) immediately
    backgroundCts.Cancel();

    // Out-of-session safety: enumerate hub-connected cameras and queue disconnect tasks.
    // Each task checks safety inside (async), then either disconnects cleanly or does a
    // warm-up ramp. CancellationToken.None: thermal ramp must complete for camera safety.
    var cameraCount = 0;
    if (appState.DeviceHub is { } hubAtQuit)
    {
        foreach (var (uri, driver) in hubAtQuit.ConnectedDevices)
        {
            if (driver is not TianWen.Lib.Devices.ICameraDriver) continue;

            var capUri = uri;
            var camName = hubAtQuit.TryGetDeviceFromUri(capUri, out var dev) && dev is not null
                ? dev.DisplayName : capUri.Host;
            cameraCount++;
            tracker.Run(async () =>
            {
                try
                {
                    var safety = await EquipmentActions.GetDisconnectSafetyAsync(hubAtQuit, capUri, System.Threading.CancellationToken.None);
                    if (safety == EquipmentActions.DisconnectSafety.Safe)
                    {
                        await hubAtQuit.DisconnectAsync(capUri, System.Threading.CancellationToken.None);
                    }
                    else
                    {
                        await EquipmentActions.WarmAndDisconnectAsync(hubAtQuit, capUri, logger, System.Threading.CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Shutdown disconnect failed for {Uri}", capUri);
                }
            }, $"Disconnecting {camName}");
        }
    }

    if (tracker.HasPending)
    {
        // Keep loop alive to show Finalise / warm-up progress
        appState.ShuttingDown = true;
        appState.ShutdownComplete = false;
        appState.StatusMessage = cameraCount > 0
            ? $"Disconnecting {cameraCount} camera{(cameraCount == 1 ? "" : "s")}\u2026"
            : "Shutting down\u2026";
        appState.NeedsRedraw = true;
    }
    else
    {
        loop.Stop();
    }
}

// Set separately to allow loop.Stop() self-reference
loop.OnPostFrame = () =>
{
    // Any signal processed this post-frame may have mutated state that the just-rendered
    // frame doesn't reflect — preserve NeedsRedraw so the loop renders again on the next
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

    // After abort confirmation, if quit was requested, proceed to shutdown
    if (appState.QuitRequested && !guiRenderer.LiveSessionState.IsRunning && !appState.ShuttingDown)
    {
        appState.QuitRequested = false;
        RequestQuit();
    }

    if (!appState.ShuttingDown && !signalSetRedraw)
    {
        appState.NeedsRedraw = false;
        // Signals processed during OnPostFrame (e.g. SkyMapClickSelectSignal)
        // mutate SkyMapState but the current frame already ran. Only clear the
        // flag when no signal fired this post-frame so the mutation drives the
        // next render.
        guiRenderer.SkyMapState.NeedsRedraw = false;
    }
    plannerState.NeedsRedraw = false;

    // Update window title with session state (only when changed)
    var ls = guiRenderer.LiveSessionState;
    var newTitle = ls.IsRunning
        ? ls.ActiveObservation is { Target: var target }
            ? $"\U0001F52D {LiveSessionActions.PhaseLabel(ls.Phase)} - {target.Name}"
            : $"\U0001F52D {LiveSessionActions.PhaseLabel(ls.Phase)}"
        : "\U0001F52D TianWen";
    if (newTitle != lastWindowTitle)
    {
        lastWindowTitle = newTitle;
        SetWindowTitle(sdlWindow.Handle, newTitle);
    }
};

loop.OnKeyDown = (inputKey, inputModifier) =>
{
    if (appState.ShuttingDown) return false; // ignore keys during shutdown

    var evt = new InputEvent.KeyDown(inputKey, inputModifier);
    if (handlers.HandleInput(evt))
    {
        return true;
    }

    // Route to active tab first — tab may consume Escape (e.g. dismiss abort confirm)
    if (guiRenderer.ActiveTab?.HandleInput(evt) == true)
    {
        return true;
    }

    // Global keys (not consumed by active tab or text input)
    switch (inputKey)
    {
        case InputKey.Escape:
            var now2 = timeProvider.GetTimestamp();
            if (escConfirmTimestamp != 0
                && timeProvider.GetElapsedTime(escConfirmTimestamp, now2) < TimeSpan.FromSeconds(3))
            {
                // Second ESC within 3s — actually quit
                escConfirmTimestamp = 0;
                RequestQuit();
            }
            else
            {
                // First ESC — show confirmation message
                escConfirmTimestamp = now2;
                appState.StatusMessage = "Press ESC again to quit";
                appState.NeedsRedraw = true;
            }
            return true;
        case InputKey.F11:
            sdlWindow.ToggleFullscreen();
            return true;
    }

    return true;
};

// Intercept window close button — behave like abort when session is active
loop.OnQuit = () =>
{
    RequestQuit();
    // Intercept if session is running (including Finalise), showing abort confirm, or shutting down
    return appState.ShuttingDown || appState.QuitRequested || guiRenderer.LiveSessionState.IsRunning;
};

loop.Run(cts.Token);

// Final cleanup — drain should complete quickly since we already waited in the loop.
// Use a timeout so force-quit (second X press) doesn't hang on warm-up tasks.
cts.Cancel();

// Drain completes quickly — warm-up already finished while the loop was alive.
await tracker.DrainAsync();

guiRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

return 0;

// --- Helper extension ---
static class TaskExtensions
{
    public static async Task<TResult> Let<T, TResult>(this T self, Func<T, Task<TResult>> func) => await func(self);
}
