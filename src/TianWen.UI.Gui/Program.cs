using System.Runtime.Versioning;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Gui;
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

// Dark title bar on Windows
if (OperatingSystem.IsWindows())
{
    EnableDarkTitleBar(sdlWindow.Handle);
}

GetWindowSize(sdlWindow.Handle, out var logW, out var logH);
var dpiScale = pixW > 0 && logW > 0 ? (float)pixW / logW : 1f;

var guiRenderer = new VkGuiRenderer(renderer, (uint)pixW, (uint)pixH)
{
    DpiScale = dpiScale
};

// Kick off planner computation in the background
var cts = new CancellationTokenSource();
Task? plannerTask = null;

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
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute tonight's best");
            appState.StatusMessage = "Failed to load catalog";
        }
    }, cts.Token);
}

var needsRedraw = true;
var running = true;

while (running)
{
    Event evt;
    var hadEvent = needsRedraw
        ? PollEvent(out evt)
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
                        GetWindowSize(sdlWindow.Handle, out var rlw, out _);
                        dpiScale = rlw > 0 ? (float)rw / rlw : 1f;
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
                    HandleKeyDown(evt.Key.Scancode, evt.Key.Mod);
                    break;

                case EventType.MouseMotion:
                    needsRedraw = true;
                    appState.MouseScreenPosition = (evt.Motion.X * dpiScale, evt.Motion.Y * dpiScale);
                    break;

                case EventType.MouseButtonDown:
                    needsRedraw = true;
                    HandleMouseDown(evt.Button.Button, evt.Button.X * dpiScale, evt.Button.Y * dpiScale);
                    break;

                case EventType.MouseWheel:
                    needsRedraw = true;
                    HandleMouseWheel(evt.Wheel.Y);
                    break;
            }
        } while (PollEvent(out evt));
    }

    if (appState.NeedsRedraw || plannerState.NeedsRedraw)
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

// --- Event handlers ---

void HandleKeyDown(Scancode scancode, Keymod keymod)
{
    switch (scancode)
    {
        case Scancode.Escape:
            running = false;
            break;
        case Scancode.F11:
            sdlWindow.ToggleFullscreen();
            break;
    }

    // Dispatch to active tab
    if (appState.ActiveTab is GuiTab.Planner)
    {
        switch (scancode)
        {
            case Scancode.Up:
                if (plannerState.SelectedTargetIndex > 0)
                {
                    plannerState.SelectedTargetIndex--;
                    plannerState.NeedsRedraw = true;
                }
                break;
            case Scancode.Down:
                if (plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count - 1)
                {
                    plannerState.SelectedTargetIndex++;
                    plannerState.NeedsRedraw = true;
                }
                break;
            case Scancode.Return:
                if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                {
                    var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                    PlannerActions.ToggleProposal(plannerState, target);
                }
                break;
            case Scancode.P:
                if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                {
                    var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                    var propIdx = plannerState.Proposals.FindIndex(p => p.Target == target);
                    if (propIdx >= 0)
                    {
                        PlannerActions.CyclePriority(plannerState, propIdx);
                    }
                }
                break;
            case Scancode.S:
                if (appState.ActiveProfile is not null)
                {
                    var transform = TransformFactory.FromProfile(
                        appState.ActiveProfile, external.TimeProvider, out _);
                    if (transform is not null)
                    {
                        PlannerActions.BuildSchedule(plannerState, transform,
                            defaultGain: 120, defaultOffset: 10,
                            defaultSubExposure: TimeSpan.FromSeconds(120),
                            defaultObservationTime: TimeSpan.FromMinutes(60));
                    }
                }
                break;
        }
    }
}

void HandleMouseDown(byte button, float px, float py)
{
    appState.MouseScreenPosition = (px, py);

    if (button == 1) // Left click
    {
        // Sidebar hit test
        var tab = guiRenderer.HitTestSidebar(px, py);
        if (tab.HasValue)
        {
            appState.ActiveTab = tab.Value;
            appState.NeedsRedraw = true;
            return;
        }

        // Tab-specific hit testing
        if (appState.ActiveTab is GuiTab.Planner)
        {
            var (cl, ct2, cw, ch) = guiRenderer.GetContentArea();
            var targetIdx = guiRenderer.PlannerTab.HitTestTargetList(px, py, cl, ct2, guiRenderer.DpiScale);
            if (targetIdx >= 0)
            {
                plannerState.SelectedTargetIndex = targetIdx;
                plannerState.NeedsRedraw = true;
            }
        }
    }
}

void HandleMouseWheel(float scrollY)
{
    if (appState.ActiveTab is GuiTab.Planner)
    {
        var (cl, _, _, _) = guiRenderer.GetContentArea();
        var pos = appState.MouseScreenPosition;
        var listWidth = 300f * guiRenderer.DpiScale;

        if (pos.X >= cl && pos.X < cl + listWidth)
        {
            guiRenderer.PlannerTab.ScrollOffset = Math.Max(0,
                guiRenderer.PlannerTab.ScrollOffset - (int)scrollY * 3);
            plannerState.NeedsRedraw = true;
        }
    }
}

// --- Windows dark title bar ---

[SupportedOSPlatform("windows")]
static void EnableDarkTitleBar(nint sdlWindowHandle)
{
    var props = GetWindowProperties(sdlWindowHandle);
    var hwnd = GetPointerProperty(props, Props.WindowWin32HWNDPointer, nint.Zero);
    if (hwnd != nint.Zero)
    {
        WindowHelper.EnableDarkTitleBar(hwnd);
    }
}

// --- Helper extension ---
static class TaskExtensions
{
    public static async Task<TResult> Let<T, TResult>(this T self, Func<T, Task<TResult>> func) => await func(self);
}
