using Console.Lib;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using TianWen.Cli.Tui;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

internal class TuiSubCommand(
    IServiceProvider sp,
    IConsoleHost consoleHost,
    PlannerState plannerState,
    ProfileSelector profileSelector)
{
    private readonly Option<bool> _fakeOption = new("--fake", "-f")
    {
        Description = "Include fake/simulated devices and auto-discover on startup"
    };

    public Command Build()
    {
        var tuiCommand = new Command("tui", "Full-screen tabbed TUI (alternate screen)");
        tuiCommand.Options.Add(_fakeOption);
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

        var includeFake = parseResult.GetValue(_fakeOption);

        terminal.EnterAlternateScreen();

        try
        {
            await RunTuiAsync(profile, includeFake, ct);
        }
        catch (Exception ex)
        {
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("TuiSubCommand").LogError(ex, "TUI crashed");
            throw;
        }
        finally
        {
            System.Console.TreatControlCAsInput = false;
            // VirtualTerminal.DisposeAsync handles leaving alternate screen
        }
    }

    private async Task RunTuiAsync(Profile profile, bool includeFake, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        var external = consoleHost.External;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TuiSubCommand");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Shared state
        var registry = sp.GetService<IDeviceHub>();
        var appState = new GuiAppState { ActiveProfile = profile, ActiveTab = GuiTab.Equipment, DeviceHub = registry };
        var eqState = new EquipmentTabState();
        var sessionState = new SessionTabState();
        sessionState.InitializeFromProfile(profile, registry);
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();
        var liveSessionState = new LiveSessionState();
        // TUI has no sky map tab — pass a standalone state so the shared handler still wires.
        var skyMapState = new SkyMapState();

        // Wire shared business logic
        var signalHandler = new AppSignalHandler(sp, appState, plannerState, sessionState, eqState, liveSessionState, skyMapState, bus, tracker, cts, external);
        signalHandler.OnPlannerEnsureVisible = index =>
        {
            plannerState.SelectedTargetIndex = index;
            plannerState.NeedsRedraw = true;
        };

        // Load saved session configuration for the active profile
        tracker.Run(() => signalHandler.LoadSessionConfigAsync(cts.Token), "Load session config");

        // Resolve location from profile
        var transform = Plan.LocationResolver.ResolveFromProfile(consoleHost, profile, consoleHost.TimeProvider);
        if (transform is not null)
        {
            AppSignalHandler.ApplySiteFromTransform(plannerState, transform);
            plannerState.ActiveProfile = profile;
        }

        // Create tabs
        var fontPath = TuiFontPath.Resolve();
        var equipmentContent = new EquipmentContent(consoleHost.DeviceHub);

        var tabs = new Dictionary<GuiTab, ITuiTab>
        {
            [GuiTab.Equipment] = new TuiEquipmentTab(appState, eqState, equipmentContent, consoleHost, bus),
            [GuiTab.Planner] = new TuiPlannerTab(appState, plannerState, fontPath, consoleHost.TimeProvider),
            [GuiTab.Session] = new TuiSessionTab(appState, sessionState, plannerState, bus),
            [GuiTab.LiveSession] = new TuiLiveSessionTab(appState, liveSessionState, terminal, consoleHost.TimeProvider, bus),
            [GuiTab.Guider] = new TuiGuiderTab(appState, liveSessionState, terminal, fontPath, consoleHost.TimeProvider),
        };

        // BuildScheduleSignal is now handled inside AppSignalHandler — no host-level subscription needed

        // Auto-discover devices on startup when --fake is passed
        if (includeFake)
        {
            bus.Post(new DiscoverDevicesSignal(IncludeFake: true));
        }

        // Kick off planner computation in background
        if (transform is not null)
        {
            tracker.Run(() => signalHandler.InitializePlannerAsync(transform, cts.Token), "Compute tonight's best targets");
        }

        // Prevent Ctrl+C from killing the process — it arrives as a regular key event instead
        System.Console.TreatControlCAsInput = true;

        // Build top-level chrome (tab bar only — status shown in each tab's own bar)
        var chromePanel = new Panel(terminal);
        var tabBarVp = chromePanel.Dock(DockStyle.Top, 1);

        var tabBar = new TuiTabBar(tabBarVp);

        var activeTab = tabs[appState.ActiveTab];
        activeTab.BuildPanel(terminal);

        var lastClockSecond = -1;

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

                // Q/Escape/Ctrl+C at top level → quit (only if tab didn't consume it)
                if (!tabConsumed && rawEvt.ToInputEvent is
                    InputEvent.KeyDown(InputKey.Q or InputKey.Escape, _) or
                    InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl))
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

            // Force periodic redraw on live session/guider tab (~2 Hz) for clock, cooling, mount, guide updates
            if (liveSessionState.IsRunning && appState.ActiveTab is GuiTab.LiveSession or GuiTab.Guider)
            {
                liveSessionState.PollSession();
                activeTab.NeedsRedraw = true;
                await Task.Delay(500, cts.Token);
            }
            else if (!appState.NeedsRedraw && !activeTab.NeedsRedraw)
            {
                // Refresh tab bar clock once per second
                var currentSecond = consoleHost.TimeProvider.GetUtcNow().Second;
                if (currentSecond != lastClockSecond)
                {
                    lastClockSecond = currentSecond;
                    appState.NeedsRedraw = true;
                }
                await Task.Delay(16, cts.Token);
            }

            // Signal bus + background tasks + recompute check
            bus.ProcessPending(tracker);
            signalHandler.CheckRecompute();
            tracker.ProcessCompletions(logger);

            // Resize
            if (chromePanel.Recompute())
            {
                chromePanel = new Panel(terminal);
                tabBarVp = chromePanel.Dock(DockStyle.Top, 1);
                tabBar = new TuiTabBar(tabBarVp);
                activeTab.BuildPanel(terminal);
                appState.NeedsRedraw = true;
            }

            // Render — catch exceptions so a render bug never kills a live imaging session
            if (appState.NeedsRedraw || activeTab.NeedsRedraw)
            {
                appState.NeedsRedraw = false;
                try
                {
                    // Terminal title: "TianWen — Profile — Tab"
                    var tabName = appState.ActiveTab switch
                    {
                        GuiTab.Equipment => "Equipment",
                        GuiTab.Planner => "Planner",
                        GuiTab.Session => "Session",
                        GuiTab.LiveSession => liveSessionState.IsRunning ? $"Live \u2014 {LiveSessionActions.PhaseLabel(liveSessionState.Phase)}" : "Live",
                        GuiTab.Guider => "Guider",
                        _ => ""
                    };
                    var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
                    terminal.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes($"\x1b]0;\U0001F52D {profileName} \u2014 {tabName}\x07"));

                    tabBar.Render(appState, consoleHost.TimeProvider, plannerState.SiteTimeZone);
                    activeTab.Render();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Render error on {Tab}", appState.ActiveTab);
                }
            }
        }

        await tracker.DrainAsync();
    }

    private static bool TrySwitchTab(ConsoleInputEvent rawEvt, GuiAppState appState,
        Dictionary<GuiTab, ITuiTab> tabs, ref ITuiTab activeTab, IVirtualTerminal terminal)
    {
        // F-keys for direct switching; Ctrl+letter as a letter-based mnemonic.
        // Digit shortcuts (1..5) were removed in favour of the mnemonic bindings.
        var ctrl = (rawEvt.Modifiers & ConsoleModifiers.Control) != 0;
        var newTab = rawEvt.Key switch
        {
            ConsoleKey.F1 => GuiTab.Equipment,
            ConsoleKey.F2 => GuiTab.Planner,
            ConsoleKey.F3 => GuiTab.Session,
            ConsoleKey.F4 => GuiTab.LiveSession,
            ConsoleKey.F5 => GuiTab.Guider,
            ConsoleKey.E when ctrl => GuiTab.Equipment,
            ConsoleKey.P when ctrl => GuiTab.Planner,
            ConsoleKey.S when ctrl => GuiTab.Session,
            ConsoleKey.L when ctrl => GuiTab.LiveSession,
            ConsoleKey.G when ctrl => GuiTab.Guider,
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
        terminal.Clear(); // Erase sixel pixel artifacts from previous tab
        activeTab.BuildPanel(terminal);
        activeTab.NeedsRedraw = true;
        appState.NeedsRedraw = true;
        return true;
    }
}
