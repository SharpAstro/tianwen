using TianWen.Lib.Sequencing;
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

    // NO synchronized-output (DEC 2026) wrapper here, deliberately. It used to bracket every frame, and it
    // was only ever a bound on a symptom: writes went straight out, so the top row really was blanked and
    // redrawn once a second as the clock ticked, and holding presentation just hid the gap. The diffing cell
    // buffer (enabled below) removes the cause instead -- a clock tick now emits ONE cell, pinned by
    // Console.Lib's ARepaintedBar_EmitsOnlyTheDigitsThatChanged -- so there is no longer a multi-cell frame
    // to make atomic, and bracketing one cell in a presentation hold buys nothing while asking every
    // terminal to honour a mode they implement with varying fidelity.

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

        // Buffered, DIFFING writes from here on. Console.Lib is immediate-mode by default: a widget writes
        // its whole region straight out as one string of SGR plus padded text, so a clock tick rewrote every
        // cell in the row, padding spaces included -- which is what read as a flash, and what the
        // synchronized-output markers above could only hide rather than prevent. Buffered, Flush emits only
        // the cells that actually changed, and the front buffer doubles as the debug inspector's record of
        // what is on screen (its `screen` / `row` / `cell` verbs report it).
        //
        // Enabled HERE, not at InitAsync: the profile picker above runs before the alternate screen and
        // never calls Flush, so buffering it would leave its prompts sitting in a buffer nothing emits. It
        // is a pattern match because the buffer is a property of the real terminal -- IVirtualTerminal
        // deliberately does not carry it, since every other Console.Lib consumer still writes immediately.
        if (terminal is VirtualTerminal bufferedTerminal)
        {
            bufferedTerminal.EnableCellBuffer();
#if DEBUG
            // Debug builds record what each flush emitted (position + text per run), logged with the paint
            // accounting -- the counts say HOW MUCH went out, this says WHICH cells, which is the question
            // when the count is wrong and the screen looks fine.
            if (bufferedTerminal.CellBuffer is { } diagnosticsBuffer)
            {
                diagnosticsBuffer.CollectFlushDiagnostics = true;
            }
#endif
        }

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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Shared state
        var registry = sp.GetService<IDeviceHub>();
        var appState = new GuiAppState { ActiveProfile = profile, ActiveTab = GuiTab.Equipment, DeviceHub = registry };
        var eqState = new EquipmentTabState();
        var sessionState = new SessionTabState();
        sessionState.InitializeFromProfile(profile, registry);
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();
        // View contexts: the local node plus (later) any observed rigs. Exactly one today; the tabs
        // render contexts.Active while anything owning local hardware reads contexts.Local.
        var contexts = new ViewContexts();
        // TUI has no sky map tab: pass a standalone state so the shared handler still wires.
        var skyMapState = new SkyMapState();

        // Wire shared business logic
        // shutdownToken == cts.Token: the TUI has no separate background-CTS, so the app token doubles as
        // the planetary-capture shutdown signal (cancelled when the TUI exits).
        var signalHandler = new AppSignalHandler(sp, appState, plannerState, sessionState, eqState, contexts, skyMapState, bus, tracker, cts, cts.Token, external);
        signalHandler.OnPlannerEnsureVisible = index =>
        {
            plannerState.SelectedTargetIndex = index;
            plannerState.NeedsRedraw = true;
        };

        // Load saved session configuration for the active profile
        tracker.Run(() => signalHandler.LoadSessionConfigAsync(cts.Token), "Load session config");

        // P3 of docs/plans/mount-safety-limits.md for this host too: a profile's mount safety limits apply to
        // a manual slew with no session running, and only a session enforces them on the mount it leases.
        // Same loop the server and the GUI drive; quitting cancels it, and it skips any mount a run owns.
        tracker.Run(() => sp.GetRequiredService<MountLimitWatcher>().RunAsync(cts.Token), "Mount limit watcher");

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
            // First tab whose tree is SHARED with the GPU surface (HomeBoardLayout) rather than written
            // for the terminal.
            [GuiTab.Home] = new TuiHomeTab(appState, contexts, signalHandler.Rigs, consoleHost.TimeProvider, bus),
            [GuiTab.Equipment] = new TuiEquipmentTab(appState, eqState, contexts, equipmentContent, consoleHost, bus),
            [GuiTab.Planner] = new TuiPlannerTab(appState, plannerState, fontPath, consoleHost.TimeProvider),
            [GuiTab.Session] = new TuiSessionTab(appState, sessionState, plannerState, bus),
            [GuiTab.LiveSession] = new TuiLiveSessionTab(appState, contexts, terminal, consoleHost.TimeProvider, bus),
            [GuiTab.Guider] = new TuiGuiderTab(appState, contexts, terminal, fontPath, consoleHost.TimeProvider),
            [GuiTab.Notifications] = new TuiNotificationsTab(appState),
        };

        // BuildScheduleSignal is now handled inside AppSignalHandler; no host-level subscription needed

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

        // Prevent Ctrl+C from killing the process: it arrives as a regular key event instead
        System.Console.TreatControlCAsInput = true;

#if SIBLING_DEBUG_INSPECTORS
        // Live TUI debug inspector (DEBUG only -- Console.Lib compiles the inspector, and VirtualTerminal's
        // injection queue, out of Release entirely, so this block cannot exist in a release build). It is
        // the terminal counterpart to the GUI's SdlVulkan DebugInspector and shares its transport, so an
        // agent discovers both the same way; the sidecar is Console.Lib.Inspector (see .mcp.json).
        //
        // The cell plane is what a terminal has and a GPU surface does not: `screen` reads back as TEXT off
        // the FRONT buffer -- what was actually emitted -- so an assertion is words ("the tab bar reads
        // Home", "the board header says table (window too small for cards)") instead of a screenshot to
        // eyeball. That only works because the buffer is enabled above; unbuffered there is no screen to
        // report and the cell verbs say so rather than inventing a blank one.
        using var inspector = terminal is VirtualTerminal inspectableTerminal
            ? ConsoleDebugInspector.Attach("TianWen TUI", inspectableTerminal, () => DescribeState(appState, contexts))
            : null;
#endif

        // Build top-level chrome (tab bar only, status shown in each tab's own bar)
        var chromePanel = new Panel(terminal);
        var tabBarVp = chromePanel.Dock(DockStyle.Top, 1);

        var tabBar = new TuiTabBar(tabBarVp);

        var activeTab = tabs[appState.ActiveTab];
        activeTab.Attach(terminal);

        // Paint accounting, reported once a second -- see the finally block at the bottom of the loop.
        var frames = 0;
        var flushedCells = 0L;
        var opaqueCells = 0L;
        var lastPaintReport = consoleHost.TimeProvider.GetUtcNow();

        var lastClockSecond = -1;
        // Last terminal title written. The title is derived from profile + tab, so it changes on a tab
        // switch -- not once a second with the clock, which is how often the render block runs.
        var lastTitle = string.Empty;

        // Main loop
        while (!cts.Token.IsCancellationRequested)
        {
            // Drain all pending input before rendering
            var quit = false;
            while (terminal.HasInput())
            {
                var rawEvt = terminal.TryReadInput();

#if SIBLING_DEBUG_INSPECTORS
                // The event trace the inspector's `inputLog` reports, written BEFORE dispatch so an event
                // that gets swallowed still shows up -- which is the case worth diagnosing. What it changed
                // is the `appState` snapshot below; between them they replace the reproduce-screenshot-guess
                // loop that the tab-bar hit-test and mouse-motion-as-click bugs each cost.
                inspector?.LogInput(rawEvt.Mouse is { } loggedMouse
                    ? $"mouse {loggedMouse.X},{loggedMouse.Y} release={loggedMouse.IsRelease} on {appState.ActiveTab}"
                    : $"{rawEvt.ToInputEvent?.ToString() ?? $"unmapped key={rawEvt.Key}"} on {appState.ActiveTab}");
#endif

                // Tab switching: 1-4 or F1-F4 (skip when editing site, digits go to text input)
                if (!eqState.IsEditingSite && TrySwitchTab(rawEvt, appState, tabs, ref activeTab, terminal, tabBar))
                {
                    continue;
                }

                // Raw mouse events (including motion/drag) go to the tab first so a
                // ScrollableList can consume them: the DIR.Lib InputEvent mapping
                // doesn't carry motion state.
                if (rawEvt.Mouse is { } rawMouse && activeTab.HandleRawMouse(rawMouse))
                {
                    activeTab.NeedsRedraw = true;
                    continue;
                }

                // Delegate to active tab first (e.g. Escape deselects slider before quitting)
                var tabConsumed = false;
                if (rawEvt.ToInputEvent is { } evt)
                {
                    var redrawBefore = activeTab.NeedsRedraw;
                    activeTab.HandleInput(evt);
                    tabConsumed = !redrawBefore && activeTab.NeedsRedraw;
                }

                // Quit at top level (only if the tab didn't consume it). Deliberately NARROW: an unmodified
                // Q, or Ctrl+C. Escape used to quit too, and with any modifier -- but Escape is a reflex key
                // that every tab uses to mean "cancel this", and exiting takes no care of the hardware, so a
                // stray press could drop a cooled camera with no thermal ramp. Ctrl+Q / Shift+Q are likewise
                // no longer exits.
                if (!tabConsumed && rawEvt.ToInputEvent is
                    InputEvent.KeyDown(InputKey.Q, InputModifier.None) or
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
                activeTab.Attach(terminal);
                activeTab.NeedsRedraw = true;
            }

            // Propagate state-level redraw flags to the active tab
            if (plannerState.NeedsRedraw || sessionState.NeedsRedraw || contexts.AnyNeedsRedraw)
            {
                activeTab.NeedsRedraw = true;
                plannerState.NeedsRedraw = false;
                sessionState.NeedsRedraw = false;
                contexts.ClearNeedsRedraw();
            }

            // Refresh every context's telemetry snapshot, not just the visible tab's -- matches the
            // GUI (VkGuiRenderer.RenderContent), and is what keeps a local session current while a
            // remote context is on screen. Free when no session is running.
            contexts.PollAll();
            // The GUI runs this inside its per-frame telemetry poll, which the TUI does not have: a limit's
            // verdict changing class (clear -> warning -> acted, or a driver's own stop) reaches the
            // notification feed -- and so the Home board's note -- from here.
            signalHandler.NotifyLimitTransitions();

            // Force periodic redraw on live session/guider tab (~2 Hz) for clock, cooling, mount, guide updates
            if (contexts.Active.LiveSession.IsRunning && appState.ActiveTab is GuiTab.LiveSession or GuiTab.Guider)
            {
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

#if SIBLING_DEBUG_INSPECTORS
            // Inspector commands run on THIS thread, so a driver's key or click enters the same queue the
            // real stream feeds and is drained by the block above on the next pass -- no synthetic event can
            // land midway through an escape sequence the parser is reading.
            inspector?.Pump();
#endif

            // Signal bus + background tasks + recompute check
            bus.ProcessPending(tracker);
            signalHandler.CheckRecompute();
            tracker.ProcessCompletions(logger);

            // Resize
            if (chromePanel.Recompute())
            {
                // Clear before re-attaching, for the same reason the tab switch below does: a repaint only
                // writes the cells the new arrangement covers, so anything the OLD geometry drew outside it
                // stays on screen. That is what stranded a copy of the bottom bars mid-window after a
                // resize, and what left a second status row one line below the real one -- both were live
                // cells from a previous size, not a double render.
                terminal.Clear();
                chromePanel = new Panel(terminal);
                tabBarVp = chromePanel.Dock(DockStyle.Top, 1);
                tabBar = new TuiTabBar(tabBarVp);
                activeTab.Attach(terminal);
                appState.NeedsRedraw = true;
            }

            // Render: catch exceptions so a render bug never kills a live imaging session
            if (appState.NeedsRedraw || activeTab.NeedsRedraw)
            {
                // Read this BEFORE rendering: the tab clears its own flag as its first act, so asking
                // afterwards always says "clean". The clock ticks once a second and dirties only the app
                // chrome, and repainting the whole tab for a changed digit is what made the screen flicker
                // -- on the chart tabs it also re-emits the Sixel image. Chrome and content are separately
                // dirty, so paint them separately.
                var contentDirty = activeTab.NeedsRedraw;
                appState.NeedsRedraw = false;
                try
                {
                    // Terminal title: "TianWen, Profile, Tab"
                    var tabName = appState.ActiveTab switch
                    {
                        GuiTab.Equipment => "Equipment",
                        GuiTab.Planner => "Planner",
                        GuiTab.Session => "Session",
                        GuiTab.LiveSession => contexts.Active.LiveSession is { IsRunning: true } live
                            ? $"Live \u2014 {LiveSessionActions.PhaseLabel(live.Phase)}"
                            : "Live",
                        GuiTab.Guider => "Guider",
                        GuiTab.Notifications => "Notifications",
                        _ => ""
                    };
                    var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
                    var title = $"\U0001F52D {profileName} \u2014 {tabName}";
                    if (title != lastTitle)
                    {
                        lastTitle = title;
                        terminal.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes($"\x1b]0;{title}\x07"));
                    }

                    // The chrome carries the clock, so it repaints on the tick; the tab only when its own
                    // state moved.
                    tabBar.Render(appState, consoleHost.TimeProvider, plannerState.SiteTimeZone);
                    if (contentDirty)
                    {
                        activeTab.Render();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Render error on {Tab}", appState.ActiveTab);
                }
                finally
                {
                    // Emit the frame, including after a render exception -- a partial frame still has to
                    // reach the terminal, or the screen keeps showing the previous one with no way back.
                    // This IS the diff: the paints above wrote into the cell buffer and nothing has gone out
                    // yet, so a missing Flush is a frozen display.
                    terminal.Flush();

                    // How much of the paint actually reached the terminal, summarised once a second. A
                    // flickering screen is a repaint that is bigger than it should be, and that is not
                    // observable from the paint side: the tab repaints its whole region every frame BY
                    // DESIGN and the diff is supposed to absorb it. Without this the only way to tell a
                    // working diff from a broken one is to stare at the screen and guess.
                    frames++;

                    var nowUtc = consoleHost.TimeProvider.GetUtcNow();
                    if (nowUtc - lastPaintReport >= TimeSpan.FromSeconds(1))
                    {
                        // Cell counts are the terminal's running TOTALS diffed across the interval, never
                        // per-flush values. A frame can flush more than once (that was the flicker:
                        // TerminalViewport flushed per cursor move, shipping the half-painted diff), and a
                        // last-flush read reports only the final one, which is exactly how the first
                        // version of this accounting hid the bug it existed to find. Opaque cells re-emit
                        // every frame no matter what changed, so a high share means the diff is being
                        // bypassed rather than doing badly.
                        var (cellsTotal, opaqueTotal) = terminal is VirtualTerminal flushed
                            ? (flushed.FlushedCellsTotal, flushed.FlushedOpaqueCellsTotal)
                            : (0L, 0L);
                        logger.LogDebug(
                            "TUI paint: {Frames} frames, {Cells} cells emitted ({Opaque} of them opaque) in the last {Elapsed:0.0}s",
                            frames, cellsTotal - flushedCells, opaqueTotal - opaqueCells,
                            (nowUtc - lastPaintReport).TotalSeconds);
                        if (terminal is VirtualTerminal { CellBuffer: { CollectFlushDiagnostics: true } diagBuffer })
                        {
                            // WHICH cells the last flush sent, when the counts alone cannot say. The front
                            // buffer cannot answer this after the fact -- its final state always looks right.
                            logger.LogDebug("TUI paint runs: {Runs}", diagBuffer.LastFlushRuns);
                        }
                        lastPaintReport = nowUtc;
                        frames = 0;
                        flushedCells = cellsTotal;
                        opaqueCells = opaqueTotal;
                    }
                }
            }
        }

        await tracker.DrainAsync();
    }

#if SIBLING_DEBUG_INSPECTORS
    /// <summary>
    /// The inspector's state snapshot. Curated and hand-written rather than serialized off
    /// <see cref="GuiAppState"/>, which holds live driver handles and is not serializable -- and written
    /// with <see cref="System.Text.Json.Utf8JsonWriter"/> rather than a reflective overload, because an
    /// AOT-configured consumer disables reflective JSON and would throw here at runtime.
    /// <para>
    /// Names match the GUI inspector's wherever they mean the same thing, so an agent's expectations carry
    /// from one surface to the other. What is deliberately NOT here is anything already readable off the
    /// cell plane -- the board's chosen shape and the reason for it are printed in its own header, and
    /// asserting the words on screen is stronger than asserting a field that claims to describe them.
    /// </para>
    /// </summary>
    private static string DescribeState(GuiAppState appState, ViewContexts contexts)
    {
        var live = contexts.Active.LiveSession;
        var notes = appState.Notifications;

        var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("activeTab", appState.ActiveTab.ToString());
            WriteStringOrNull(json, "profile", appState.ActiveProfile?.DisplayName);
            // Which context the tabs are rendering, and whether this node's own session is running
            // underneath it -- the two facts needed to interpret every field below.
            json.WriteString("viewContext", contexts.Active.DisplayName);
            json.WriteNumber("viewContextCount", contexts.All.Length);
            json.WriteBoolean("sessionRunning", live.IsRunning);
            json.WriteBoolean("localSessionRunning", contexts.Local.LiveSession.IsRunning);
            json.WriteString("phase", live.Phase.ToString());
            json.WriteString("homeBoardView", appState.HomeBoardView.ToString());
            json.WriteNumber("unreadNotifications", appState.UnreadNotificationCount);
            // Newest entry is at index 0 -- where slew results, plate-solve offsets and errors all land.
            WriteStringOrNull(json, "lastNotification", notes.IsDefaultOrEmpty ? null : notes[0].Message);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a JSON null for an absent value, so a missing profile reads as null rather than "".</summary>
    private static void WriteStringOrNull(System.Text.Json.Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
#endif

    private static bool TrySwitchTab(ConsoleInputEvent rawEvt, GuiAppState appState,
        Dictionary<GuiTab, ITuiTab> tabs, ref ITuiTab activeTab, IVirtualTerminal terminal, TuiTabBar tabBar)
    {
        // F-keys for direct switching; Ctrl+letter as a letter-based mnemonic.
        // Digit shortcuts (1..5) were removed in favour of the mnemonic bindings.
        var ctrl = (rawEvt.Modifiers & ConsoleModifiers.Control) != 0;
        var newTab = rawEvt.Key switch
        {
            ConsoleKey.F1 => GuiTab.Home,
            ConsoleKey.F2 => GuiTab.Equipment,
            ConsoleKey.F3 => GuiTab.Planner,
            ConsoleKey.F4 => GuiTab.Session,
            ConsoleKey.F5 => GuiTab.LiveSession,
            ConsoleKey.F6 => GuiTab.Guider,
            ConsoleKey.F7 => GuiTab.Notifications,
            ConsoleKey.H when ctrl => GuiTab.Home,
            ConsoleKey.E when ctrl => GuiTab.Equipment,
            ConsoleKey.P when ctrl => GuiTab.Planner,
            ConsoleKey.S when ctrl => GuiTab.Session,
            ConsoleKey.L when ctrl => GuiTab.LiveSession,
            ConsoleKey.G when ctrl => GuiTab.Guider,
            ConsoleKey.N when ctrl => GuiTab.Notifications,
            _ => (GuiTab?)null
        };

        // Mouse click: pixels to cells, then ask the bar. It hit-tests its own arranged tree, so the row is
        // part of the question rather than a hardcoded "the bar is row 0" here, and a column the bar did not
        // draw a tab into -- because a narrow terminal left that tab out -- correctly misses.
        if (newTab is null && rawEvt.Mouse is { IsRelease: true } mouse)
        {
            var cellW = terminal.CellSize.Width;
            var cellH = terminal.CellSize.Height;
            if (cellW > 0 && cellH > 0)
            {
                newTab = tabBar.HitTest(mouse.X / cellW, mouse.Y / cellH);
            }
        }

        if (newTab is not { } tab || tab == appState.ActiveTab || !tabs.ContainsKey(tab))
        {
            return false;
        }

        appState.ActiveTab = tab;
        activeTab = tabs[tab];
        terminal.Clear(); // Erase sixel pixel artifacts from previous tab
        activeTab.Attach(terminal);
        activeTab.NeedsRedraw = true;
        appState.NeedsRedraw = true;
        return true;
    }
}
