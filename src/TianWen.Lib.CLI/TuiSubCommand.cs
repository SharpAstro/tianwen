using Console.Lib;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.CLI.Tui;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI;

internal class TuiSubCommand(
    IServiceProvider sp,
    IConsoleHost consoleHost,
    PlannerState plannerState,
    ProfileSelector profileSelector)
{
    public Command Build()
    {
        var tuiCommand = new Command("tui", "Full-screen tabbed TUI (alternate screen)");
        tuiCommand.SetAction(TuiActionAsync);
        return tuiCommand;
    }

    private async Task TuiActionAsync(ParseResult parseResult, CancellationToken ct)
    {
        // Profile selection (interactive picker if needed)
        var terminal = consoleHost.Terminal;
        await terminal.InitAsync();

        var profile = await profileSelector.ResolveProfileAsync(parseResult, true, ct);
        if (profile is null)
        {
            return;
        }

        terminal.EnterAlternateScreen();

        try
        {
            await RunTuiAsync(profile, ct);
        }
        finally
        {
            // VirtualTerminal.DisposeAsync handles leaving alternate screen
        }
    }

    private async Task RunTuiAsync(Profile profile, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        var external = consoleHost.External;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Shared state
        var appState = new GuiAppState { ActiveProfile = profile, ActiveTab = GuiTab.Equipment };
        var eqState = new EquipmentTabState();
        var sessionState = new SessionTabState();
        sessionState.InitializeFromProfile(profile);
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();

        // Load saved session configuration for the active profile
        tracker.Run(async () =>
        {
            await SessionPersistence.TryLoadAsync(sessionState, profile, external, cts.Token);
        }, "Load session config");

        var liveSessionState = new LiveSessionState();

        // Wire shared business logic
        var signalHandler = new AppSignalHandler(sp, appState, plannerState, sessionState, eqState, liveSessionState, bus, tracker, cts, external);
        signalHandler.OnPlannerEnsureVisible = index =>
        {
            plannerState.SelectedTargetIndex = index;
            plannerState.NeedsRedraw = true;
        };

        // Resolve location from profile
        var transform = Plan.LocationResolver.ResolveFromProfile(consoleHost, profile, external.TimeProvider);
        if (transform is not null)
        {
            plannerState.SiteLatitude = transform.SiteLatitude;
            plannerState.SiteLongitude = transform.SiteLongitude;
            plannerState.SiteTimeZone = transform.SiteTimeZone;
            plannerState.ActiveProfile = profile;
        }

        // Create tabs
        var fontPath = TuiFontPath.Resolve();
        var equipmentContent = new EquipmentContent(consoleHost.DeviceUriRegistry);

        var tabs = new Dictionary<GuiTab, ITuiTab>
        {
            [GuiTab.Equipment] = new TuiEquipmentTab(appState, eqState, equipmentContent, bus),
            [GuiTab.Planner] = new TuiPlannerTab(plannerState,
                transform ?? new TianWen.Lib.Astrometry.SOFA.Transform(external.TimeProvider), fontPath, bus),
            [GuiTab.Session] = new TuiSessionTab(appState, sessionState, plannerState, bus),
            [GuiTab.LiveSession] = new TuiLiveSessionTab(appState, liveSessionState, bus),
        };

        // Subscribe to BuildScheduleSignal (same logic as GPU Program.cs)
        bus.Subscribe<BuildScheduleSignal>(_ =>
        {
            if (appState.ActiveProfile is null || transform is null) return;
            var profileData = appState.ActiveProfile.Data ?? ProfileData.Empty;
            var availableFilters = profileData.OTAs.Length > 0
                ? EquipmentActions.GetFilterConfig(profileData, 0)
                : null;
            var opticalDesign = profileData.OTAs.Length > 0
                ? profileData.OTAs[0].OpticalDesign
                : OpticalDesign.Unknown;

            PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                defaultGain: 120, defaultOffset: 10,
                defaultSubExposure: TimeSpan.FromSeconds(120),
                defaultObservationTime: TimeSpan.FromMinutes(60),
                availableFilters: availableFilters is { Count: > 0 } ? availableFilters : null,
                opticalDesign: opticalDesign);
        });

        // Kick off planner computation in background
        if (transform is not null)
        {
            var objectDb = sp.GetRequiredService<ICelestialObjectDB>();
            tracker.Run(async () =>
            {
                await objectDb.InitDBAsync(cts.Token);
                signalHandler.SetAutoCompleteCache(objectDb.CreateAutoCompleteList());
                await PlannerActions.ComputeTonightsBestAsync(
                    plannerState, objectDb, transform,
                    plannerState.MinHeightAboveHorizon, cts.Token);
                await PlannerPersistence.TryLoadAsync(plannerState, appState.ActiveProfile, external, cts.Token);
                plannerState.SelectedTargetIndex = 0;
                plannerState.NeedsRedraw = true;
            }, "Compute tonight's best targets");
        }

        // Build top-level chrome (tab bar + status bar)
        var chromePanel = new Panel(terminal);
        var tabBarVp = chromePanel.Dock(DockStyle.Top, 1);
        var statusBarVp = chromePanel.Dock(DockStyle.Bottom, 1);

        var tabBar = new TuiTabBar(tabBarVp);
        var statusBar = new TextBar(statusBarVp);

        var activeTab = tabs[appState.ActiveTab];
        activeTab.BuildPanel(terminal);

        // Main loop
        while (!cts.Token.IsCancellationRequested)
        {
            // Drain all pending input before rendering
            var quit = false;
            while (terminal.HasInput())
            {
                var rawEvt = terminal.TryReadInput();

                // Tab switching: 1-4 or F1-F4 (skip when editing site — digits go to text input)
                if (!eqState.IsEditingSite && TrySwitchTab(rawEvt, appState, tabs, ref activeTab, terminal))
                {
                    continue;
                }

                // Delegate to active tab first (e.g. Escape deselects slider before quitting)
                var tabConsumed = false;
                if (rawEvt.ToInputEvent is { } evt)
                {
                    var redrawBefore = activeTab.NeedsRedraw;
                    if (activeTab.HandleInput(evt))
                    {
                        quit = true;
                        break;
                    }
                    tabConsumed = !redrawBefore && activeTab.NeedsRedraw;
                }

                // Q/Escape at top level → quit (only if tab didn't consume it)
                if (!tabConsumed && rawEvt.ToInputEvent is InputEvent.KeyDown(InputKey.Q or InputKey.Escape, _))
                {
                    quit = true;
                    break;
                }
            }

            if (quit)
            {
                break;
            }

            // Check if a signal handler changed the active tab (e.g. StartSessionSignal → LiveSession)
            if (tabs.TryGetValue(appState.ActiveTab, out var newActiveTab) && newActiveTab != activeTab)
            {
                activeTab = newActiveTab;
                activeTab.BuildPanel(terminal);
                activeTab.NeedsRedraw = true;
            }

            // Propagate state-level redraw flags to the active tab
            if (plannerState.NeedsRedraw || sessionState.NeedsRedraw || liveSessionState.NeedsRedraw)
            {
                activeTab.NeedsRedraw = true;
                plannerState.NeedsRedraw = false;
                sessionState.NeedsRedraw = false;
                liveSessionState.NeedsRedraw = false;
            }

            if (!appState.NeedsRedraw && !activeTab.NeedsRedraw)
            {
                await Task.Delay(16, cts.Token);
            }

            // Signal bus + background tasks
            bus.ProcessPending(tracker);
            tracker.ProcessCompletions(external.AppLogger);

            // Resize
            if (chromePanel.Recompute())
            {
                chromePanel = new Panel(terminal);
                tabBarVp = chromePanel.Dock(DockStyle.Top, 1);
                statusBarVp = chromePanel.Dock(DockStyle.Bottom, 1);
                tabBar = new TuiTabBar(tabBarVp);
                statusBar = new TextBar(statusBarVp);
                activeTab.BuildPanel(terminal);
                appState.NeedsRedraw = true;
            }

            // Render
            if (appState.NeedsRedraw || activeTab.NeedsRedraw)
            {
                appState.NeedsRedraw = false;
                tabBar.Render(appState, external.TimeProvider, plannerState.SiteTimeZone);
                activeTab.Render();

                var statusMsg = appState.StatusMessage ?? "";
                statusBar.Text($" {statusMsg}");
                statusBar.Render();
            }
        }

        await tracker.DrainAsync();
    }

    private static bool TrySwitchTab(ConsoleInputEvent rawEvt, GuiAppState appState,
        Dictionary<GuiTab, ITuiTab> tabs, ref ITuiTab activeTab, IVirtualTerminal terminal)
    {
        var newTab = rawEvt.Key switch
        {
            ConsoleKey.D1 or ConsoleKey.F1 => GuiTab.Equipment,
            ConsoleKey.D2 or ConsoleKey.F2 => GuiTab.Planner,
            ConsoleKey.D3 or ConsoleKey.F3 => GuiTab.Session,
            ConsoleKey.D4 or ConsoleKey.F4 => GuiTab.LiveSession,
            _ => (GuiTab?)null
        };

        // Mouse click on tab bar (row 0)
        if (newTab is null && rawEvt.Mouse is { IsRelease: true } mouse)
        {
            var cellH = terminal.CellSize.Height;
            if (cellH > 0 && mouse.Y / cellH == 0)
            {
                var cellW = terminal.CellSize.Width;
                var col = cellW > 0 ? mouse.X / cellW : 0;
                newTab = TuiTabBar.HitTestTab(col);
            }
        }

        if (newTab is not { } tab || tab == appState.ActiveTab || !tabs.ContainsKey(tab))
        {
            return false;
        }

        appState.ActiveTab = tab;
        activeTab = tabs[tab];
        activeTab.BuildPanel(terminal);
        appState.NeedsRedraw = true;
        return true;
    }
}
