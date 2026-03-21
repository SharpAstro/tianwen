using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
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

var guiRenderer = new VkGuiRenderer(renderer, (uint)pixW, (uint)pixH)
{
    DpiScale = sdlWindow.DisplayScale
};

// Event handler setup
var cts = new CancellationTokenSource();
var tracker = new BackgroundTaskTracker();
var handlers = new GuiEventHandlers(sp, appState, plannerState, guiRenderer, sdlWindow.Handle, cts, external, tracker);
Task? plannerTask = null;

// Wire tab callbacks that need DI/profile access
guiRenderer.PlannerTab.OnBuildSchedule = () =>
{
    if (appState.ActiveProfile is null) return;
    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
    if (transform is not null)
    {
        PlannerActions.BuildSchedule(plannerState, transform,
            defaultGain: 120, defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(60));
    }
};

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

// Auto-discover devices on startup (uses tracker via the wired OnDiscover callback)
if (guiRenderer.EquipmentTab.OnDiscover is { } startupDiscover)
{
    tracker.Run(startupDiscover, "Startup device discovery");
}

// --- Main event loop via SdlEventLoop ---

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff),

    OnResize = (rw, rh) =>
    {
        guiRenderer.DpiScale = sdlWindow.DisplayScale;
        guiRenderer.Resize(rw, rh);
    },

    OnMouseDown = (x, y) =>
    {
        handlers.HandleMouseDown(x, y);
        return true;
    },

    OnMouseMove = (x, y) => handlers.HandleMouseMove(x, y),

    OnMouseUp = () => handlers.HandleMouseUp(),

    OnMouseWheel = (scrollY, _, _) =>
    {
        handlers.HandleMouseWheel(scrollY);
        return true;
    },

    OnTextInput = text => handlers.HandleTextInput(text),

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

                        plannerState.SiteLatitude = transform.SiteLatitude;
                        plannerState.SiteLongitude = transform.SiteLongitude;
                        plannerState.SiteTimeZone = transform.SiteTimeZone;

                        // Fast path: if we already have targets, just recompute night window + profiles
                        // Full rescan only needed on first load
                        if (plannerState.TonightsBest.Count > 0)
                        {
                            PlannerActions.RecomputeForDate(plannerState, transform);
                        }
                        else
                        {
                            await PlannerActions.ComputeTonightsBestAsync(
                                plannerState, objectDb, transform,
                                plannerState.MinHeightAboveHorizon, cts.Token);
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

        return appState.NeedsRedraw || plannerState.NeedsRedraw
            || appState.ActiveTextInput is { IsActive: true };
    },

    OnRender = () => guiRenderer.Render(appState, plannerState, viewerState, external.TimeProvider),

    OnPostFrame = () =>
    {
        tracker.ProcessCompletions(logger);
        appState.NeedsRedraw = false;
        plannerState.NeedsRedraw = false;
    }
};

// OnKeyDown set separately to allow loop.Stop() self-reference
loop.OnKeyDown = (inputKey, inputModifier) =>
{
    if (handlers.HandleKeyDown(inputKey, inputModifier))
    {
        return true;
    }

    // Global keys (not consumed by text input)
    if (inputKey == InputKey.Escape)
    {
        loop.Stop();
        return true;
    }

    guiRenderer.ActiveTab?.HandleKeyDown(inputKey, inputModifier);
    return true;
};

loop.Run(cts.Token);

// Cleanup
cts.Cancel();
guiRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

if (plannerTask is not null)
{
    try { await plannerTask; } catch (OperationCanceledException) { }
}
await tracker.DrainAsync();

return 0;

// --- Helper extension ---
static class TaskExtensions
{
    public static async Task<TResult> Let<T, TResult>(this T self, Func<T, Task<TResult>> func) => await func(self);
}
