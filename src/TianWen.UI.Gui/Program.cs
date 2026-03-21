using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using System.Runtime.InteropServices;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Gui;
using TianWen.UI.Shared;
using static SDL3.SDL;

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

var dpiScale = sdlWindow.DisplayScale;

var guiRenderer = new VkGuiRenderer(renderer, (uint)pixW, (uint)pixH)
{
    DpiScale = dpiScale
};

// Event handler setup
var cts = new CancellationTokenSource();
var handlers = new GuiEventHandlers(sp, appState, plannerState, guiRenderer, sdlWindow.Handle, cts, external);
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

// Auto-discover devices on startup
if (appState.ActiveProfile is null)
{
    var eqSt = guiRenderer.EquipmentTab.State;
    eqSt.IsDiscovering = true;
    _ = Task.Run(async () =>
    {
        try
        {
            var dm = sp.GetRequiredService<ICombinedDeviceManager>();
            await dm.CheckSupportAsync(cts.Token);
            await dm.DiscoverAsync(cts.Token);
            eqSt.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                .SelectMany(dm.RegisteredDevices)
                .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Device discovery failed");
        }
        finally
        {
            eqSt.IsDiscovering = false;
            appState.NeedsRedraw = true;
        }
    });
}

// --- Main event loop ---

var needsRedraw = true;
var running = true;

while (running)
{
    var hadEvent = needsRedraw
        ? PollEvent(out Event evt)
        : WaitEventTimeout(out evt, 16);

    if (hadEvent)
    {
        do
        {
            switch ((EventType)evt.Type)
            {
                case EventType.Quit:
                    running = false;
                    break;

                case EventType.WindowResized:
                case EventType.WindowPixelSizeChanged:
                    sdlWindow.GetSizeInPixels(out var rw, out var rh);
                    if (rw > 0 && rh > 0)
                    {
                        var newDpiScale = GetWindowDisplayScale(sdlWindow.Handle);
                        if (newDpiScale <= 0f) newDpiScale = 1f;
                        Console.Error.WriteLine($"[Program] Resize event: pixels={rw}x{rh}, dpiScale={dpiScale:F3} -> {newDpiScale:F3}");
                        dpiScale = newDpiScale;
                        renderer.Resize((uint)rw, (uint)rh);
                        guiRenderer.DpiScale = dpiScale;
                        guiRenderer.Resize((uint)rw, (uint)rh);
                    }
                    needsRedraw = true;
                    break;

                case EventType.WindowExposed:
                    needsRedraw = true;
                    break;

                case EventType.KeyDown:
                    needsRedraw = true;
                    if (!handlers.HandleKeyDown(evt.Key.Scancode, evt.Key.Mod))
                    {
                        // Global keys (not consumed by text input)
                        switch (evt.Key.Scancode)
                        {
                            case Scancode.Escape:
                                running = false;
                                break;
                            case Scancode.F11:
                                sdlWindow.ToggleFullscreen();
                                break;
                            default:
                                // Route to active tab's keyboard handler
                                guiRenderer.ActiveTab?.HandleKeyDown(
                                    evt.Key.Scancode.ToInputKey, evt.Key.Mod.ToInputModifier);
                                break;
                        }
                    }
                    break;

                case EventType.MouseMotion:
                    needsRedraw = true;
                    handlers.HandleMouseMove(evt.Motion.X, evt.Motion.Y);
                    break;

                case EventType.MouseButtonDown:
                    needsRedraw = true;
                    handlers.HandleMouseDown(evt.Button.X, evt.Button.Y, evt.Button.Clicks);
                    break;

                case EventType.MouseButtonUp:
                    handlers.HandleMouseUp();
                    break;

                case EventType.MouseWheel:
                    needsRedraw = true;
                    handlers.HandleMouseWheel(evt.Wheel.Y);
                    break;

                case EventType.TextInput:
                    needsRedraw = true;
                    var textStr = Marshal.PtrToStringUTF8(evt.Text.Text);
                    if (textStr is not null)
                    {
                        handlers.HandleTextInput(textStr);
                    }
                    break;
            }
        } while (PollEvent(out evt));
    }

    // Recompute targets when planning date changes (debounced — skip if already running)
    if (plannerState.NeedsRecompute && appState.ActiveProfile is not null && !plannerState.IsRecomputing)
    {
        plannerState.NeedsRecompute = false;
        plannerState.IsRecomputing = true;
        appState.StatusMessage = "Recomputing...";
        appState.NeedsRedraw = true;
        _ = Task.Run(async () =>
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to recompute targets");
                appState.StatusMessage = "Failed to recompute";
            }
            finally
            {
                plannerState.IsRecomputing = false;
                appState.NeedsRedraw = true;
            }
        }, cts.Token);
    }

    // Force continuous redraw when text input is active (for cursor blink)
    if (appState.NeedsRedraw || plannerState.NeedsRedraw
        || appState.ActiveTextInput is { IsActive: true })
    {
        needsRedraw = true;
    }

    if (!needsRedraw)
    {
        continue;
    }
    needsRedraw = false;

    var bgColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff);
    if (!renderer.BeginFrame(bgColor))
    {
        sdlWindow.GetSizeInPixels(out var sw, out var sh);
        if (sw > 0 && sh > 0)
        {
            renderer.Resize((uint)sw, (uint)sh);
            guiRenderer.Resize((uint)sw, (uint)sh);
        }
        needsRedraw = true;
        continue;
    }

    guiRenderer.Render(appState, plannerState, viewerState, external.TimeProvider);

    renderer.EndFrame();

    if (renderer.FontAtlasDirty)
    {
        needsRedraw = true;
    }
    appState.NeedsRedraw = false;
    plannerState.NeedsRedraw = false;
}

// Cleanup
cts.Cancel();
guiRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

if (plannerTask is not null)
{
    try { await plannerTask; } catch (OperationCanceledException) { }
}

return 0;

// --- Helper extension ---
static class TaskExtensions
{
    public static async Task<TResult> Let<T, TResult>(this T self, Func<T, Task<TResult>> func) => await func(self);
}
