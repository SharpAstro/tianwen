using System.Runtime.Versioning;
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

var dpiScale = GetWindowDisplayScale(sdlWindow.Handle);
if (dpiScale <= 0f) dpiScale = 1f;

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

// Auto-discover devices on startup (Equipment tab is default when no profile)
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
                        dpiScale = GetWindowDisplayScale(sdlWindow.Handle);
                        if (dpiScale <= 0f) dpiScale = 1f;
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

                case EventType.TextInput:
                    needsRedraw = true;
                    var textStr = Marshal.PtrToStringUTF8(evt.Text.Text);
                    if (textStr is not null)
                    {
                        var activeInput = guiRenderer.EquipmentTab.State.ActiveTextInput
                            ?? guiRenderer.EquipmentTab.State.ProfileNameInput;
                        activeInput.InsertText(textStr);
                    }
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
    // Route to text input if active
    var eqState = guiRenderer.EquipmentTab.State;
    var eqInput = eqState.ActiveTextInput ?? (eqState.ProfileNameInput.IsActive ? eqState.ProfileNameInput : null);
    if (eqInput is { IsActive: true })
    {
        var inputKey = scancode switch
        {
            Scancode.Backspace => TextInputKey.Backspace,
            Scancode.Delete => TextInputKey.Delete,
            Scancode.Left => TextInputKey.Left,
            Scancode.Right => TextInputKey.Right,
            Scancode.Home => TextInputKey.Home,
            Scancode.End => TextInputKey.End,
            Scancode.Return => TextInputKey.Enter,
            Scancode.Escape => TextInputKey.Escape,
            _ => (TextInputKey?)null
        };

        // Tab cycles between site fields
        if (scancode == Scancode.Tab && eqState.IsEditingSite)
        {
            eqState.ActiveTextInput = eqState.ActiveTextInput == eqState.LatitudeInput ? eqState.LongitudeInput
                : eqState.ActiveTextInput == eqState.LongitudeInput ? eqState.ElevationInput
                : eqState.LatitudeInput;
            appState.NeedsRedraw = true;
            return;
        }

        if (inputKey.HasValue && eqInput.HandleKey(inputKey.Value))
        {
            if (eqInput.IsCommitted)
            {
                if (eqState.IsEditingSite)
                {
                    // Enter in site field → save site
                    HandleEquipmentClick(new EquipmentHitResult(EquipmentHitType.SaveSiteButton));
                }
                else if (eqInput.Text.Length > 0)
                {
                    // Trigger profile creation
                    HandleEquipmentClick(new EquipmentHitResult(EquipmentHitType.CreateButton));
                }
            }
            else if (eqInput.IsCancelled)
            {
                if (eqState.IsEditingSite)
                {
                    eqState.IsEditingSite = false;
                    eqState.LatitudeInput.Deactivate();
                    eqState.LongitudeInput.Deactivate();
                    eqState.ElevationInput.Deactivate();
                    eqState.ActiveTextInput = null;
                }
                else
                {
                    eqState.IsCreatingProfile = false;
                    eqState.ProfileNameInput.Clear();
                }
                eqInput.Deactivate();
                StopTextInput(sdlWindow.Handle);
            }
            appState.NeedsRedraw = true;
            return;
        }
        return; // swallow all keys when text input is active
    }

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
        var tab = guiRenderer.HitTestSidebar(px, py, appState);
        if (tab.HasValue)
        {
            appState.ActiveTab = tab.Value;
            appState.NeedsRedraw = true;

            // Auto-discover devices when switching to Equipment tab
            if (tab.Value is GuiTab.Equipment && guiRenderer.EquipmentTab.State.DiscoveredDevices.Count == 0)
            {
                HandleEquipmentClick(new EquipmentHitResult(EquipmentHitType.DiscoverButton));
            }

            return;
        }

        // Tab-specific hit testing
        var (cl, ct2, cw, ch) = guiRenderer.GetContentArea();

        if (appState.ActiveTab is GuiTab.Planner)
        {
            var targetIdx = guiRenderer.PlannerTab.HitTestTargetList(px, py, cl, ct2, guiRenderer.DpiScale);
            if (targetIdx >= 0)
            {
                plannerState.SelectedTargetIndex = targetIdx;
                plannerState.NeedsRedraw = true;
            }
        }
        else if (appState.ActiveTab is GuiTab.Equipment)
        {
            var hit = guiRenderer.EquipmentTab.HitTest(px, py, cl, ct2, guiRenderer.DpiScale);
            if (hit is not null)
            {
                HandleEquipmentClick(hit);
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

// --- Equipment tab click handling ---

void HandleEquipmentClick(EquipmentHitResult hit)
{
    var eqTab = guiRenderer.EquipmentTab;
    var eqState = eqTab.State;

    switch (hit.Type)
    {
        case EquipmentHitType.CreateButton:
            if (!eqState.IsCreatingProfile)
            {
                // Enter creation mode
                eqState.IsCreatingProfile = true;
                eqState.ProfileNameInput.Activate();
                StartTextInput(sdlWindow.Handle);
            }
            else if (eqState.ProfileNameInput.Text.Length > 0)
            {
                // Create the profile
                var name = eqState.ProfileNameInput.Text;
                _ = Task.Run(async () =>
                {
                    var profile = await EquipmentActions.CreateProfileAsync(name, external, cts.Token);
                    appState.ActiveProfile = profile;
                    eqState.IsCreatingProfile = false;
                    eqState.ProfileNameInput.Deactivate();
                    eqState.ProfileNameInput.Clear();
                    StopTextInput(sdlWindow.Handle);
                    appState.NeedsRedraw = true;
                });
            }
            break;

        case EquipmentHitType.TextInput:
            if (eqState.IsEditingSite)
            {
                // Cycle focus: if no active input, activate latitude
                if (eqState.ActiveTextInput is null)
                {
                    eqState.ActiveTextInput = eqState.LatitudeInput;
                }
                StartTextInput(sdlWindow.Handle);
            }
            else if (!eqState.ProfileNameInput.IsActive)
            {
                eqState.ProfileNameInput.Activate();
                eqState.ActiveTextInput = eqState.ProfileNameInput;
                StartTextInput(sdlWindow.Handle);
            }
            break;

        case EquipmentHitType.ProfileSlot when hit.Slot is not null:
            // Toggle assignment mode
            eqState.ActiveAssignment = eqState.ActiveAssignment == hit.Slot ? null : hit.Slot;
            appState.NeedsRedraw = true;
            break;

        case EquipmentHitType.DeviceRow when hit.DeviceIndex >= 0 && hit.DeviceIndex < eqState.DiscoveredDevices.Count:
            if (eqState.ActiveAssignment is { } target && appState.ActiveProfile is { } profile)
            {
                var device = eqState.DiscoveredDevices[hit.DeviceIndex];
                var data = profile.Data ?? ProfileData.Empty;

                var newData = target switch
                {
                    AssignTarget.ProfileLevel { Field: "Mount" } => EquipmentActions.AssignMount(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "Guider" } => EquipmentActions.AssignGuider(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "GuiderCamera" } => EquipmentActions.AssignGuiderCamera(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "GuiderFocuser" } => EquipmentActions.AssignGuiderFocuser(data, device.DeviceUri),
                    AssignTarget.OTALevel otaTarget => EquipmentActions.AssignDeviceToOTA(data, otaTarget.OtaIndex,
                        device.DeviceType, device.DeviceUri),
                    _ => data
                };

                var updated = profile.WithData(newData);
                _ = Task.Run(async () =>
                {
                    await updated.SaveAsync(external, cts.Token);
                    appState.ActiveProfile = updated;
                    eqState.ActiveAssignment = null;
                    appState.NeedsRedraw = true;
                });
            }
            break;

        case EquipmentHitType.DiscoverButton:
            if (!eqState.IsDiscovering)
            {
                eqState.IsDiscovering = true;
                appState.StatusMessage = "Discovering devices...";
                appState.NeedsRedraw = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var dm = sp.GetRequiredService<ICombinedDeviceManager>();
                        await dm.CheckSupportAsync(cts.Token);
                        await dm.DiscoverAsync(cts.Token);
                        eqState.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                            .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                            .SelectMany(dm.RegisteredDevices)
                            .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Device discovery failed");
                        appState.StatusMessage = "Discovery failed";
                    }
                    finally
                    {
                        eqState.IsDiscovering = false;
                        appState.StatusMessage = null;
                        appState.NeedsRedraw = true;
                    }
                });
            }
            break;

        case EquipmentHitType.AddOtaButton when appState.ActiveProfile is { } p:
            var d = p.Data ?? ProfileData.Empty;
            var newOta = new OTAData(
                Name: $"Telescope #{d.OTAs.Length}",
                FocalLength: 1000,
                Camera: NoneDevice.Instance.DeviceUri,
                Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null,
                Aperture: null, OpticalDesign: OpticalDesign.Unknown);
            var newD = EquipmentActions.AddOTA(d, newOta);
            var updatedP = p.WithData(newD);
            _ = Task.Run(async () =>
            {
                await updatedP.SaveAsync(external, cts.Token);
                appState.ActiveProfile = updatedP;
                appState.NeedsRedraw = true;
            });
            break;

        case EquipmentHitType.EditSiteButton:
            var eqSt = guiRenderer.EquipmentTab.State;
            eqSt.IsEditingSite = true;
            // Pre-fill with existing site values if available
            if (appState.ActiveProfile?.Data is { } pd2)
            {
                var existingSite = EquipmentActions.GetSiteFromMount(pd2.Mount);
                if (existingSite.HasValue)
                {
                    eqSt.LatitudeInput.Activate(existingSite.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    eqSt.LongitudeInput.Activate(existingSite.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    eqSt.ElevationInput.Activate(existingSite.Value.Elev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                }
                else
                {
                    eqSt.LatitudeInput.Activate();
                    eqSt.LongitudeInput.Activate();
                    eqSt.ElevationInput.Activate();
                }
            }
            StartTextInput(sdlWindow.Handle);
            break;

        case EquipmentHitType.SaveSiteButton when appState.ActiveProfile is { } siteProfile:
            var eqSt2 = guiRenderer.EquipmentTab.State;
            if (double.TryParse(eqSt2.LatitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLat) &&
                double.TryParse(eqSt2.LongitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLon))
            {
                double? sElev = double.TryParse(eqSt2.ElevationInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : null;
                var sData = siteProfile.Data ?? ProfileData.Empty;
                var newSiteData = EquipmentActions.SetSite(sData, sLat, sLon, sElev);
                var updatedSite = siteProfile.WithData(newSiteData);
                _ = Task.Run(async () =>
                {
                    await updatedSite.SaveAsync(external, cts.Token);
                    appState.ActiveProfile = updatedSite;
                    eqSt2.IsEditingSite = false;
                    eqSt2.LatitudeInput.Deactivate();
                    eqSt2.LongitudeInput.Deactivate();
                    eqSt2.ElevationInput.Deactivate();
                    eqSt2.ActiveTextInput = null;
                    StopTextInput(sdlWindow.Handle);
                    appState.NeedsRedraw = true;
                });
            }
            else
            {
                appState.StatusMessage = "Invalid latitude or longitude";
            }
            break;
    }

    appState.NeedsRedraw = true;
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
