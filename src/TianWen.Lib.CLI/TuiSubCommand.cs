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
    ViewerState viewerState,
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
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();

        // Wire shared business logic
        var signalHandler = new AppSignalHandler(sp, appState, plannerState, eqState, bus, cts, external);

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
            [GuiTab.Equipment] = new TuiEquipmentTab(consoleHost, appState, equipmentContent),
            [GuiTab.Planner] = new TuiPlannerTab(consoleHost, plannerState,
                transform ?? new TianWen.Lib.Astrometry.SOFA.Transform(external.TimeProvider), fontPath),
        };

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
                plannerState.SelectedTargetIndex = 0;
                tabs[GuiTab.Planner].NeedsRedraw = true;
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
            // Input
            if (terminal.HasInput())
            {
                var rawEvt = terminal.TryReadInput();

                // Tab switching: 1-4 or F1-F4
                if (TrySwitchTab(rawEvt, appState, tabs, ref activeTab, terminal))
                {
                    continue;
                }

                // Q/Escape at top level → quit
                if (rawEvt.ToInputEvent is InputEvent.KeyDown(InputKey.Q or InputKey.Escape, _))
                {
                    break;
                }

                // Delegate to active tab
                if (rawEvt.ToInputEvent is { } evt && activeTab.HandleInput(evt))
                {
                    break;
                }
            }
            else
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
            ConsoleKey.D4 or ConsoleKey.F4 => GuiTab.Viewer,
            _ => (GuiTab?)null
        };

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
