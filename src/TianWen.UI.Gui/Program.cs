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
Task? plannerTask = null;

// Signal subscription for building the schedule
bus.Subscribe<BuildScheduleSignal>(signal =>
{
    if (appState.ActiveProfile is null) return;
    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
    if (transform is not null)
    {
        // Pass filter config and optical design from the first OTA in the active profile
        var profileData = appState.ActiveProfile.Data ?? ProfileData.Empty;
        var availableFilters = profileData.OTAs.Length > 0
            ? EquipmentActions.GetFilterConfig(profileData, 0)
            : null;
        var opticalDesign = profileData.OTAs.Length > 0
            ? profileData.OTAs[0].OpticalDesign
            : OpticalDesign.Unknown;

        PlannerActions.BuildSchedule(plannerState, guiRenderer.SessionState, transform,
            defaultGain: 120, defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(60),
            availableFilters: availableFilters is { Count: > 0 } ? availableFilters : null,
            opticalDesign: opticalDesign);
    }
});

// Load saved session configuration for the active profile
if (appState.ActiveProfile is not null)
{
    tracker.Run(async () =>
    {
        await SessionPersistence.TryLoadAsync(guiRenderer.SessionState, appState.ActiveProfile, external, cts.Token);
    }, "Load session config");
}

if (appState.ActiveProfile is not null)
{
    plannerTask = Task.Run(async () =>
    {
        try
        {
            var objectDb = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
            var transform = TransformFactory.FromProfile(
                appState.ActiveProfile, external.TimeProvider, out _);

            if (transform is not null)
            {
                plannerState.SiteLatitude = transform.SiteLatitude;
                plannerState.SiteLongitude = transform.SiteLongitude;
                plannerState.SiteTimeZone = transform.SiteTimeZone;

                await PlannerActions.ComputeTonightsBestAsync(
                    plannerState, objectDb, transform,
                    plannerState.MinHeightAboveHorizon, cts.Token);

                await PlannerPersistence.TryLoadAsync(plannerState, appState.ActiveProfile, external, cts.Token);

                handlers.SetAutoCompleteCache(objectDb.CreateAutoCompleteList());
            }
            else
            {
                appState.StatusMessage = "Set site coordinates in Equipment tab";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute tonight's best");
            appState.StatusMessage = "Failed to load catalog";
        }
    }, cts.Token);
}

// Auto-discover devices on startup via signal bus
bus.Post(new DiscoverDevicesSignal());

// --- Main event loop via SdlEventLoop ---

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
        // Recompute targets when planning date changes (debounced — skip if already running)
        if (plannerState.NeedsRecompute && appState.ActiveProfile is not null && !plannerState.IsRecomputing)
        {
            plannerState.NeedsRecompute = false;
            plannerState.IsRecomputing = true;
            appState.StatusMessage = "Recomputing...";
            appState.NeedsRedraw = true;
            tracker.Run(async () =>
            {
                try
                {
                    var objectDb = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                    var transform = TransformFactory.FromProfile(
                        appState.ActiveProfile, external.TimeProvider, out _);

                    if (transform is not null)
                    {
                        // Override transform date if planning for a different night
                        // Use noon of the target day so CalculateNightWindow finds the correct evening
                        if (plannerState.PlanningDate is { } pd)
                        {
                            var noon = new DateTimeOffset(pd.Date, pd.Offset).AddHours(12);
                            transform.DateTimeOffset = noon;
                        }

                        // Detect significant site change (>1°) — requires full rescan, not just recompute
                        var siteChanged = Math.Abs(transform.SiteLatitude - plannerState.SiteLatitude) > 1.0
                            || Math.Abs(transform.SiteLongitude - plannerState.SiteLongitude) > 1.0;

                        plannerState.SiteLatitude = transform.SiteLatitude;
                        plannerState.SiteLongitude = transform.SiteLongitude;
                        plannerState.SiteTimeZone = transform.SiteTimeZone;

                        // Fast path: if we already have targets and site didn't change significantly,
                        // just recompute night window + altitude profiles
                        if (plannerState.TonightsBest.Count > 0 && !siteChanged)
                        {
                            PlannerActions.RecomputeForDate(plannerState, transform);
                        }
                        else
                        {
                            await PlannerActions.ComputeTonightsBestAsync(
                                plannerState, objectDb, transform,
                                plannerState.MinHeightAboveHorizon, cts.Token);
                            await PlannerPersistence.TryLoadAsync(plannerState, appState.ActiveProfile, external, cts.Token);
                            handlers.SetAutoCompleteCache(objectDb.CreateAutoCompleteList());
                        }
                        appState.StatusMessage = null;
                    }
                    else
                    {
                        appState.StatusMessage = "Set site coordinates in Equipment tab";
                    }
                }
                finally
                {
                    plannerState.IsRecomputing = false;
                    appState.NeedsRedraw = true;
                }
            }, "Recompute targets");
        }

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

        return appState.NeedsRedraw || plannerState.NeedsRedraw
            || appState.ActiveTextInput is { IsActive: true };
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
    bus.ProcessPending(tracker);
    if (tracker.ProcessCompletions(logger))
    {
        appState.NeedsRedraw = true;
    }

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

    if (!appState.ShuttingDown)
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

    // Global keys (not consumed by text input)
    switch (inputKey)
    {
        case InputKey.Escape:
            RequestQuit();
            return true;
        case InputKey.F11:
            sdlWindow.ToggleFullscreen();
            return true;
    }

    guiRenderer.ActiveTab?.HandleInput(evt);
    return true;
};

// Intercept window close button — behave like abort when session is active
loop.OnQuit = () =>
{
    RequestQuit();
    return appState.ShuttingDown; // true = intercepted (keep loop alive), false = stop
};

loop.Run(cts.Token);

// Final cleanup — drain should complete quickly since we already waited in the loop
cts.Cancel();

if (plannerTask is not null)
{
    try { await plannerTask; } catch (OperationCanceledException) { }
}
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
