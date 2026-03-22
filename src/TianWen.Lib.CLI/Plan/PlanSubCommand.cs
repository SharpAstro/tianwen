using Console.Lib;
using DIR.Lib;
using System.CommandLine;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Plan;

internal class PlanSubCommand(
    IConsoleHost consoleHost,
    PlannerState plannerState,
    ICelestialObjectDB objectDb,
    ProfileSelector profileSelector,
    Option<bool> interactiveOption
)
{
    public Command Build()
    {
        var planCommand = new Command("plan", "Observation planner — show tonight's best targets and build a schedule");
        planCommand.SetAction(PlanActionAsync);

        return planCommand;
    }

    internal async Task PlanActionAsync(ParseResult parseResult, CancellationToken ct)
    {
        var interactive = parseResult.GetValue(interactiveOption);

        // Profile is required — it provides the site location via mount URI
        var profile = await profileSelector.ResolveProfileAsync(parseResult, interactive, ct);
        if (profile is null)
        {
            return;
        }

        // Extract location from profile's mount URI
        var transform = LocationResolver.ResolveFromProfile(consoleHost, profile, consoleHost.External.TimeProvider);
        if (transform is null)
        {
            return;
        }

        plannerState.SiteLatitude = transform.SiteLatitude;
        plannerState.SiteLongitude = transform.SiteLongitude;
        plannerState.SiteTimeZone = transform.SiteTimeZone;
        plannerState.ActiveProfile = profile;

        // Compute tonight's best — show inline progress in non-interactive mode
        await PlannerActions.ComputeTonightsBestAsync(
            plannerState, objectDb, transform,
            plannerState.MinHeightAboveHorizon, ct,
            onProgress: interactive ? null : msg => System.Console.Error.Write($"\r{msg.PadRight(60)}"));

        if (interactive)
        {
            await RunInteractiveAsync(transform, ct);
        }
        else
        {
            await RunNonInteractiveAsync();
        }
    }

    private async Task RunNonInteractiveAsync()
    {
        var terminal = consoleHost.Terminal;
        if (!System.Console.IsInputRedirected)
        {
            await terminal.InitAsync();
        }

        // Header
        var siteLabel = $"{plannerState.SiteLatitude:F1}°{(plannerState.SiteLatitude >= 0 ? "N" : "S")}, {plannerState.SiteLongitude:F1}°{(plannerState.SiteLongitude >= 0 ? "E" : "W")}";
        var darkLocal = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
        var twLocal = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
        var nightHours = (plannerState.AstroTwilight - plannerState.AstroDark).TotalHours;

        consoleHost.WriteScrollable($"\nTonight's Best Targets ({darkLocal:yyyy-MM-dd}, {siteLabel})");
        consoleHost.WriteScrollable($"Astro dark: {darkLocal:HH:mm} — Astro twilight: {twLocal:HH:mm} ({nightHours:F1}h)");
        consoleHost.WriteScrollable($"Profile: {plannerState.ActiveProfile?.DisplayName ?? "none"}\n");

        // Target table
        foreach (var line in PlannerActions.FormatTonightsBestLines(plannerState))
        {
            consoleHost.WriteScrollable(line);
        }

        // Altitude chart
        consoleHost.WriteScrollable("");

        if (terminal.HasSixelSupport)
        {
            RenderSixelChart(terminal);
        }
        else
        {
            foreach (var line in AsciiAltitudeChart.Render(plannerState))
            {
                consoleHost.WriteScrollable(line);
            }
        }
    }

    private void RenderSixelChart(IVirtualTerminal terminal)
    {
        var pixelSize = terminal.PixelSize;
        var chartW = (int)pixelSize.Width;
        var chartH = Math.Min((int)(pixelSize.Height / 2), 400);

        if (chartW < 100 || chartH < 50)
        {
            foreach (var line in AsciiAltitudeChart.Render(plannerState))
            {
                consoleHost.WriteScrollable(line);
            }
            return;
        }

        var renderer = new RgbaImageRenderer((uint)chartW, (uint)chartH);

        renderer.FillRectangle(
            new RectInt(new PointInt(chartW, chartH), new PointInt(0, 0)),
            new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));

        var fontPath = ResolveFontPath();
        AltitudeChartRenderer.Render(renderer, plannerState, fontPath);

        var surface = (RgbaImage)renderer.Surface;
        using var ms = new MemoryStream();
        SixelEncoder.Encode(surface.Pixels, surface.Width, surface.Height, 4, ms);
        ms.Position = 0;
        ms.CopyTo(terminal.OutputStream);
        terminal.Flush();
    }

    internal async Task RunInteractiveAsync(TianWen.Lib.Astrometry.SOFA.Transform transform, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        await terminal.InitAsync();
        terminal.EnterAlternateScreen();

        try
        {
            await RunInteractiveLoopAsync(transform, ct);
        }
        finally
        {
            // DisposeAsync on VirtualTerminal will leave alternate screen
        }
    }

    private async Task RunInteractiveLoopAsync(TianWen.Lib.Astrometry.SOFA.Transform transform, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        plannerState.NeedsRedraw = true;

        // Build panel layout
        var panel = new Panel(terminal);
        var topVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var detailVp = panel.Dock(DockStyle.Bottom, 8);
        var leftVp = panel.Dock(DockStyle.Left, 32);
        var fillVp = panel.Fill();

        var topBar = new TextBar(topVp);
        var statusBar = new TextBar(bottomVp);
        var targetList = new ScrollableList<TargetListItem>(leftVp);
        var detailWidget = new MarkdownWidget(detailVp);

        var canvasPixelSize = fillVp.PixelSize;
        var canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
        var canvas = new Canvas<RgbaImage>(fillVp, canvasRenderer);

        panel.Add(topBar).Add(statusBar).Add(targetList).Add(detailWidget).Add(canvas);

        var fontPath = ResolveFontPath();

        while (!ct.IsCancellationRequested)
        {
            if (terminal.HasInput())
            {
                var rawEvt = terminal.TryReadInput();
                if (rawEvt.ToInputEvent is { } evt && HandleInput(evt, transform, targetList))
                {
                    return;
                }
            }
            else
            {
                await Task.Delay(16, ct);
            }

            if (panel.Recompute())
            {
                canvasPixelSize = fillVp.PixelSize;
                canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
                panel = new Panel(terminal);
                topVp = panel.Dock(DockStyle.Top, 1);
                bottomVp = panel.Dock(DockStyle.Bottom, 1);
                detailVp = panel.Dock(DockStyle.Bottom, 8);
                leftVp = panel.Dock(DockStyle.Left, 32);
                fillVp = panel.Fill();
                topBar = new TextBar(topVp);
                statusBar = new TextBar(bottomVp);
                targetList = new ScrollableList<TargetListItem>(leftVp);
                detailWidget = new MarkdownWidget(detailVp);
                canvas = new Canvas<RgbaImage>(fillVp, canvasRenderer);
                panel.Add(topBar).Add(statusBar).Add(targetList).Add(detailWidget).Add(canvas);
                plannerState.NeedsRedraw = true;
            }

            if (!plannerState.NeedsRedraw)
            {
                continue;
            }
            plannerState.NeedsRedraw = false;

            // Update top bar
            var siteLabel = $"{plannerState.SiteLatitude:F1}°N {plannerState.SiteLongitude:F1}°E";
            var darkLocal = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
            var twLocal = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
            topBar.Text($" {siteLabel} | Dark: {darkLocal:HH:mm}-{twLocal:HH:mm} | Proposals: {plannerState.Proposals.Count}");
            topBar.RightText($"{plannerState.ActiveProfile?.DisplayName ?? "No profile"} ");

            // Update target list using shared content model
            var filteredTargets = PlannerActions.GetFilteredTargets(plannerState);
            var targetRows = PlannerTargetList.GetItems(plannerState, filteredTargets);
            var items = new TargetListItem[targetRows.Count];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = new TargetListItem(targetRows[i]);
            }
            targetList.Items(items).Header("Tonight's Best").ScrollTo(
                Math.Max(0, plannerState.SelectedTargetIndex - targetList.VisibleRows / 2));

            // Update detail panel using shared content model
            var detailLines = PlannerDetails.GetLines(plannerState, filteredTargets);
            if (detailLines.Count > 0)
            {
                var md = $"## {detailLines[0]}\n\n";
                for (var i = 1; i < detailLines.Count; i++)
                {
                    md += detailLines[i] + "\n\n";
                }
                md += "*Enter* to add/remove | *P* priority | *S* schedule | *Q* quit";
                detailWidget.Markdown(md);
            }

            // Render altitude chart
            canvasRenderer.FillRectangle(
                new RectInt(new PointInt((int)canvasPixelSize.Width, (int)canvasPixelSize.Height), new PointInt(0, 0)),
                new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));
            AltitudeChartRenderer.Render(canvasRenderer, plannerState, fontPath,
                highlightTargetIndex: plannerState.SelectedTargetIndex);

            // Update status bar
            var scheduleStatus = plannerState.Schedule is { Count: > 0 } s
                ? $"Schedule: {s.Count} obs | R:start session"
                : "No schedule (press S)";
            var statusText = plannerState.StatusMessage is { } msg
                ? $" {msg}"
                : " ↑↓:nav Enter:toggle P:priority S:schedule R:session Q:quit";
            statusBar.Text(statusText);
            statusBar.RightText($"{scheduleStatus} ");

            panel.RenderAll();
        }
    }

    private bool HandleInput(DIR.Lib.InputEvent evt, TianWen.Lib.Astrometry.SOFA.Transform transform,
        ScrollableList<TargetListItem> targetList)
    {
        switch (evt)
        {
            // Mouse click — select target from list
            case DIR.Lib.InputEvent.MouseUp(var x, var y, DIR.Lib.MouseButton.Left):
            {
                var cell = targetList.HitTest((int)x, (int)y);
                if (cell is { Row: var row } && row >= 0)
                {
                    var scrollOffset = Math.Max(0, plannerState.SelectedTargetIndex - targetList.VisibleRows / 2);
                    var itemIndex = row - 1 + scrollOffset; // row 0 = header
                    if (itemIndex >= 0 && itemIndex < plannerState.TonightsBest.Count)
                    {
                        plannerState.SelectedTargetIndex = itemIndex;
                        plannerState.NeedsRedraw = true;
                    }
                }
                return false;
            }

            // Scroll wheel — navigate target list
            case DIR.Lib.InputEvent.Scroll(var delta, _, _, _):
            {
                var step = delta > 0 ? -3 : 3;
                plannerState.SelectedTargetIndex = Math.Clamp(
                    plannerState.SelectedTargetIndex + step, 0, plannerState.TonightsBest.Count - 1);
                plannerState.NeedsRedraw = true;
                return false;
            }

            // Keyboard
            case DIR.Lib.InputEvent.KeyDown(var key, _):
            {
                // Clear transient status message on any keypress
                if (plannerState.StatusMessage is not null)
                {
                    plannerState.StatusMessage = null;
                    plannerState.NeedsRedraw = true;
                }

                switch (key)
                {
                    case DIR.Lib.InputKey.Q or DIR.Lib.InputKey.Escape:
                        return true;

                    case DIR.Lib.InputKey.Up:
                        if (plannerState.SelectedTargetIndex > 0)
                        {
                            plannerState.SelectedTargetIndex--;
                            plannerState.NeedsRedraw = true;
                        }
                        break;

                    case DIR.Lib.InputKey.Down:
                        if (plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count - 1)
                        {
                            plannerState.SelectedTargetIndex++;
                            plannerState.NeedsRedraw = true;
                        }
                        break;

                    case DIR.Lib.InputKey.Enter:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                        {
                            var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                            PlannerActions.ToggleProposal(plannerState, target);
                        }
                        break;

                    case DIR.Lib.InputKey.P:
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

                    case DIR.Lib.InputKey.S:
                        PlannerActions.BuildSchedule(plannerState, transform,
                            defaultGain: 120, defaultOffset: 10,
                            defaultSubExposure: TimeSpan.FromSeconds(120),
                            defaultObservationTime: TimeSpan.FromMinutes(60));
                        break;

                    case DIR.Lib.InputKey.R:
                        if (plannerState.Schedule is { Count: > 0 })
                        {
                            plannerState.StatusMessage = "Session start not yet implemented. Schedule is ready.";
                            plannerState.NeedsRedraw = true;
                        }
                        else
                        {
                            plannerState.StatusMessage = "No schedule. Press S to build one first.";
                            plannerState.NeedsRedraw = true;
                        }
                        break;
                }
                break;
            }
        }

        return false;
    }

    private static string ResolveFontPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\cour.ttf"]
            : OperatingSystem.IsMacOS()
                ? ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Monaco.dfont"]
                : ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf"];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return "";
    }
}
