using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using static SDL3.SDL;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Gui;

// DI setup
var services = new ServiceCollection();
services
    .AddLogging(static builder => builder
        .AddProvider(new FileLoggerProvider("GUI"))
    )
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddAscom()
    .AddMeade()
    .AddIOptron()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddDevices()
    .AddSessionFactory()
    .AddFitsViewer()
    .AddSingleton<GuiAppState>();

var sp = services.BuildServiceProvider();
var appState = sp.GetRequiredService<GuiAppState>();
var plannerState = sp.GetRequiredService<PlannerState>();
var viewerState = sp.GetRequiredService<ViewerState>();
var external = sp.GetRequiredService<IExternal>();
var logger = external.AppLogger;

// Resolve profile — auto-select if exactly one, otherwise none for now
var profiles = await sp.GetRequiredService<ICombinedDeviceManager>()
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

// SDL3 + Vulkan init
using var sdlWindow = SdlVulkanWindow.Create("TianWen", 1280, 900);


sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var ctx = VulkanContext.Create(sdlWindow.Instance, sdlWindow.Surface, (uint)pixW, (uint)pixH);
var renderer = new VkRenderer(ctx, (uint)pixW, (uint)pixH);

var bus = new SignalBus();
var guiRenderer = new VkGuiRenderer(renderer, (uint)pixW, (uint)pixH, bus)
{
    DpiScale = sdlWindow.DisplayScale
};

// Event handler setup
var cts = new CancellationTokenSource();
var tracker = new BackgroundTaskTracker();
var handlers = new GuiEventHandlers(sp, appState, plannerState, guiRenderer, cts, external, tracker);

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
var signalHandler = handlers.SignalHandler;
tracker.Run(() => signalHandler.LoadSessionConfigAsync(cts.Token), "Load session config");

if (appState.ActiveProfile is not null)
{
    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
    if (transform is not null)
    {
        AppSignalHandler.ApplySiteFromTransform(plannerState, transform);
        tracker.Run(() => signalHandler.InitializePlannerAsync(transform, cts.Token), "Compute tonight's best targets");
    }
    else
    {
        appState.StatusMessage = "Set site coordinates in Equipment tab";
    }
}

// Auto-discover devices on startup via signal bus
bus.Post(new DiscoverDevicesSignal());

// --- Main event loop via SdlEventLoop ---
var _lastSessionRedrawTimestamp = external.TimeProvider.GetTimestamp();

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

    OnMouseUp = (button) => handlers.HandleInput(new InputEvent.MouseUp(0, 0)),

    OnMouseWheel = (scrollY, _, _) =>
    {
        handlers.HandleInput(new InputEvent.Scroll(scrollY, 0, 0));
        return true;
    },

    OnTextInput = text => handlers.HandleInput(new InputEvent.TextInput(text)),

    CheckNeedsRedraw = () =>
    {
        // Recompute targets when site/date changes (shared logic in AppSignalHandler)
        signalHandler.CheckRecompute();

        // During shutdown, show progress and signal ready to stop
        if (appState.ShuttingDown)
        {
            if (!tracker.HasPending)
            {
                appState.ShutdownComplete = true;
            }
            else
            {
                appState.StatusMessage = $"Shutting down\u2026 ({tracker.PendingCount} task{(tracker.PendingCount == 1 ? "" : "s")})";
            }
            return true; // always redraw during shutdown
        }

        // Redraw periodically (every ~500ms) when session is running on the Live tab
        if (guiRenderer.LiveSessionState.IsRunning && appState.ActiveTab == GuiTab.LiveSession)
        {
            var now = external.TimeProvider.GetTimestamp();
            if (external.TimeProvider.GetElapsedTime(_lastSessionRedrawTimestamp, now) >= TimeSpan.FromMilliseconds(500))
            {
                _lastSessionRedrawTimestamp = now;
                return true;
            }
        }

        return appState.NeedsRedraw || plannerState.NeedsRedraw
            || appState.ActiveTextInput is { IsActive: true }
            || (guiRenderer.LiveSessionState.IsRunning
                && appState.ActiveTab is GuiTab.LiveSession or GuiTab.Guider);
    },

    OnRender = () => guiRenderer.Render(appState, plannerState, viewerState, external.TimeProvider),

};

// Request quit — shows abort confirmation if session is running, otherwise shuts down
void RequestQuit()
{
    if (appState.ShuttingDown)
    {
        // Already shutting down, second press — force quit
        loop.Stop();
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

    if (tracker.HasPending)
    {
        // Keep loop alive to show Finalise progress
        appState.ShuttingDown = true;
        appState.ShutdownComplete = false;
        appState.StatusMessage = "Shutting down\u2026";
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
    var redrawBefore = appState.NeedsRedraw;
    bus.ProcessPending(tracker);
    if (tracker.ProcessCompletions(logger))
    {
        appState.NeedsRedraw = true;
    }
    // If a signal handler set NeedsRedraw during ProcessPending, don't clear it
    var signalSetRedraw = !redrawBefore && appState.NeedsRedraw;

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
    }
    plannerState.NeedsRedraw = false;
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
            RequestQuit();
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

// Final cleanup — drain should complete quickly since we already waited in the loop
cts.Cancel();

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
