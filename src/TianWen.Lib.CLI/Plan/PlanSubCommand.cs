using Console.Lib;
using DIR.Lib;
using System.CommandLine;
using System.Linq;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Plan;

internal class PlanSubCommand(
    IConsoleHost consoleHost,
    PlannerState plannerState,
    ICelestialObjectDB objectDb,
    ProfileSelector profileSelector
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
        // Profile is required — it provides the site location via mount URI
        var profile = await profileSelector.ResolveProfileAsync(parseResult, false, ct);
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

        // Compute tonight's best — show inline progress
        await PlannerActions.ComputeTonightsBestAsync(
            plannerState, objectDb, transform,
            plannerState.MinHeightAboveHorizon, ct,
            onProgress: msg => System.Console.Error.Write($"\r{msg.PadRight(60)}"));

        await RunInlineAsync(transform, ct);
    }

    private void PrintHeader()
    {
        var siteLabel = $"{plannerState.SiteLatitude:F1}\u00b0{(plannerState.SiteLatitude >= 0 ? "N" : "S")}, {plannerState.SiteLongitude:F1}\u00b0{(plannerState.SiteLongitude >= 0 ? "E" : "W")}";
        var darkLocal = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
        var twLocal = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
        var nightHours = (plannerState.AstroTwilight - plannerState.AstroDark).TotalHours;

        // MarkdownRenderer with ColorMode.None produces clean plain text (no VT codes)
        WriteMarkdown(
            $"## Tonight's Best Targets ({darkLocal:yyyy-MM-dd}, {siteLabel})\n\n" +
            $"**Astro dark:** {darkLocal:HH:mm} \u2014 **Astro twilight:** {twLocal:HH:mm} ({nightHours:F1}h)  \n" +
            $"**Profile:** {plannerState.ActiveProfile?.DisplayName ?? "none"}");
    }

    private void PrintTargetTable()
    {
        // Markdown table renders as aligned plain text when ColorMode is None
        WriteMarkdown(FormatTargetTableMarkdown());
    }

    private string FormatTargetTableMarkdown(int maxLines = 30)
    {
        var maxScore = plannerState.TonightsBest.Count > 0 ? plannerState.TonightsBest[0].CombinedScore : 1.0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| # | Target | Type | Alt | Window | Rating |");
        sb.AppendLine("|--:|--------|------|----:|--------|-------:|");

        for (var i = 0; i < Math.Min(plannerState.TonightsBest.Count, maxLines); i++)
        {
            var s = plannerState.TonightsBest[i];
            var pin = plannerState.Proposals.Any(p => p.Target == s.Target) ? "\u2605" : "";
            var objType = s.ObjectType.ToAbbreviation();
            var window = $"{s.OptimalStart.ToOffset(plannerState.SiteTimeZone):HH:mm}\u2013{(s.OptimalStart + s.OptimalDuration).ToOffset(plannerState.SiteTimeZone):HH:mm}";
            var rating = PlannerActions.ScoreToRating(s.CombinedScore, maxScore);

            sb.AppendLine($"| {pin}{i + 1} | {s.Target.Name} | {objType} | {s.OptimalAltitude:F0}\u00b0 | {window} | {rating:F1}\u2605 |");
        }

        return sb.ToString();
    }

    private void WriteMarkdown(string markdown)
    {
        var terminal = consoleHost.Terminal;
        var width = terminal.Size.Width;
        var lines = Console.Lib.MarkdownRenderer.RenderLines(markdown, width, terminal.ColorMode);
        foreach (var line in lines)
        {
            consoleHost.WriteScrollable(line);
        }
    }

    private void PrintChart(IVirtualTerminal terminal)
    {
        consoleHost.WriteScrollable("");

        if (terminal.HasSixelSupport)
        {
            RenderSixelChart(terminal);
        }
        else if (terminal.ColorMode is not Console.Lib.ColorMode.None)
        {
            foreach (var line in AsciiAltitudeChart.Render(plannerState))
            {
                consoleHost.WriteScrollable(line);
            }
        }
        else
        {
            // NO_COLOR or piped — show text summary of proposed observations
            PrintProposalSummary();
        }
    }

    private void PrintProposalSummary()
    {
        if (plannerState.Proposals.Count == 0)
        {
            consoleHost.WriteScrollable("No targets proposed yet.");
            return;
        }

        consoleHost.WriteScrollable("Proposed observations:");
        var pinnedCount = plannerState.PinnedCount;
        for (var i = 0; i < pinnedCount; i++)
        {
            var target = plannerState.Proposals[i].Target;
            var scored = plannerState.TonightsBest.FirstOrDefault(t => t.Target == target);
            if (scored is { Target: not null })
            {
                var start = i == 0 ? plannerState.AstroDark
                    : i - 1 < plannerState.HandoffSliders.Count ? plannerState.HandoffSliders[i - 1] : scored.OptimalStart;
                var end = i >= pinnedCount - 1 || i >= plannerState.HandoffSliders.Count
                    ? plannerState.AstroTwilight : plannerState.HandoffSliders[i];
                var duration = end - start;
                var durationStr = duration.TotalHours >= 1.0
                    ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}m"
                    : $"{(int)duration.TotalMinutes}m";
                var startStr = start.ToOffset(plannerState.SiteTimeZone).ToString("HH:mm");
                var endStr = end.ToOffset(plannerState.SiteTimeZone).ToString("HH:mm");

                consoleHost.WriteScrollable($"  {i + 1}. {startStr}-{endStr}  {target.Name,-22} ({durationStr}, peak {scored.OptimalAltitude:F0}\u00b0)");
            }
        }
    }

    private void PrintConflictWarnings()
    {
        var filtered = PlannerActions.GetFilteredTargets(plannerState);
        var pinnedCount = plannerState.PinnedCount;

        for (var i = 0; i < pinnedCount; i++)
        {
            var windowStart = i == 0 ? plannerState.AstroDark
                : i - 1 < plannerState.HandoffSliders.Count ? plannerState.HandoffSliders[i - 1] : plannerState.AstroDark;
            var windowEnd = i >= pinnedCount - 1 || i >= plannerState.HandoffSliders.Count
                ? plannerState.AstroTwilight : plannerState.HandoffSliders[i];
            var hours = (windowEnd - windowStart).TotalHours;

            if (hours < 1.5)
            {
                var target = filtered[i].Target;
                var mins = (int)((windowEnd - windowStart).TotalMinutes);
                consoleHost.WriteScrollable($"  \u26a0 {target.Name}: only {mins}m allocated");
            }
        }
    }

    /// <summary>
    /// Inline REPL mode — prints table + chart to scrollback, then accepts input
    /// at a prompt line. Works over SSH without alternate screen.
    /// When piped (stdin redirected), just prints and exits.
    /// </summary>
    private async Task RunInlineAsync(TianWen.Lib.Astrometry.SOFA.Transform transform, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        if (!consoleHost.Terminal.IsInputRedirected)
        {
            await terminal.InitAsync();
        }

        PrintHeader();
        PrintTargetTable();

        // If piped, also print chart and exit
        if (consoleHost.Terminal.IsInputRedirected)
        {
            PrintChart(terminal);
            return;
        }

        // Select first target
        plannerState.SelectedTargetIndex = 0;

        consoleHost.WriteScrollable("");
        consoleHost.WriteScrollable("\u2191\u2193:browse  Enter:pin/unpin  G:chart  S:schedule  Q:quit");

        WritePrompt(terminal);

        // Inline input loop
        while (!ct.IsCancellationRequested)
        {
            if (!terminal.HasInput())
            {
                await Task.Delay(16, ct);
                continue;
            }

            var rawEvt = terminal.TryReadInput();
            if (rawEvt.ToInputEvent is not { } evt)
            {
                continue;
            }

            if (HandleInlineInput(evt, transform, terminal))
            {
                consoleHost.WriteScrollable(""); // move past prompt line
                return;
            }
        }
    }

    private void WritePrompt(IVirtualTerminal terminal)
    {
        var targets = plannerState.TonightsBest;
        var idx = plannerState.SelectedTargetIndex;

        if (idx < 0 || idx >= targets.Count)
        {
            terminal.WriteInPlace("> (no targets)");
            return;
        }

        var scored = targets[idx];
        var isPinned = plannerState.Proposals.Any(p => p.Target == scored.Target);
        var pin = isPinned ? "\u2605" : " ";
        var details = PlannerDetails.GetLines(plannerState, targets);
        var coordLine = details.Count >= 2 ? $"  {details[1]}" : "";

        terminal.WriteInPlace($"> [{idx + 1}] {pin} {scored.Target.Name}{coordLine}");
    }

    private bool HandleInlineInput(DIR.Lib.InputEvent evt, TianWen.Lib.Astrometry.SOFA.Transform transform,
        IVirtualTerminal terminal)
    {
        switch (evt)
        {
            case DIR.Lib.InputEvent.Scroll(var delta, _, _, _):
            {
                var step = delta > 0 ? -3 : 3;
                plannerState.SelectedTargetIndex = Math.Clamp(
                    plannerState.SelectedTargetIndex + step, 0, plannerState.TonightsBest.Count - 1);
                WritePrompt(terminal);
                return false;
            }

            case DIR.Lib.InputEvent.KeyDown(var key, _):
            {
                switch (key)
                {
                    case DIR.Lib.InputKey.Q or DIR.Lib.InputKey.Escape:
                        return true;

                    case DIR.Lib.InputKey.Up:
                        if (plannerState.SelectedTargetIndex > 0)
                        {
                            plannerState.SelectedTargetIndex--;
                            WritePrompt(terminal);
                        }
                        return false;

                    case DIR.Lib.InputKey.Down:
                        if (plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count - 1)
                        {
                            plannerState.SelectedTargetIndex++;
                            WritePrompt(terminal);
                        }
                        return false;

                    case DIR.Lib.InputKey.Enter:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                        {
                            var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                            var wasPinned = plannerState.Proposals.Any(p => p.Target == target);
                            PlannerActions.ToggleProposal(plannerState, target);
                            var action = wasPinned ? "-" : "+";
                            consoleHost.WriteScrollable($"  {action} {target.Name} ({plannerState.Proposals.Count} proposed)");
                            PrintConflictWarnings();
                            WritePrompt(terminal);
                        }
                        return false;

                    case DIR.Lib.InputKey.G:
                        consoleHost.WriteScrollable(""); // newline past prompt
                        PrintChart(terminal);
                        WritePrompt(terminal);
                        return false;

                    case DIR.Lib.InputKey.S:
                        consoleHost.WriteScrollable(""); // newline past prompt
                        PlannerActions.BuildSchedule(plannerState, transform,
                            defaultGain: 120, defaultOffset: 10,
                            defaultSubExposure: TimeSpan.FromSeconds(120),
                            defaultObservationTime: TimeSpan.FromMinutes(60));
                        if (plannerState.Schedule is { Count: > 0 } schedule)
                        {
                            consoleHost.WriteScrollable($"Schedule: {schedule.Count} observations built");
                            foreach (var obs in schedule)
                            {
                                var start = obs.Start.ToOffset(plannerState.SiteTimeZone).ToString("HH:mm");
                                var end = (obs.Start + obs.Duration).ToOffset(plannerState.SiteTimeZone).ToString("HH:mm");
                                var flipStr = "";
                                if (obs.AcrossMeridian
                                    && plannerState.AltitudeProfiles.TryGetValue(obs.Target, out var prof)
                                    && prof.Count > 0)
                                {
                                    var peakTime = prof.MaxBy(p => p.Alt).Time.ToOffset(plannerState.SiteTimeZone);
                                    flipStr = $"  [flip {peakTime:HH:mm}]";
                                }
                                consoleHost.WriteScrollable($"  {start}\u2013{end}  {obs.Target.Name}{flipStr}");
                            }
                        }
                        else
                        {
                            consoleHost.WriteScrollable("No targets proposed. Pin some targets first.");
                        }
                        WritePrompt(terminal);
                        return false;

                    case DIR.Lib.InputKey.P:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                        {
                            var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                            var propIdx = plannerState.Proposals.FindIndex(p => p.Target == target);
                            if (propIdx >= 0)
                            {
                                PlannerActions.CyclePriority(plannerState, propIdx);
                                consoleHost.WriteScrollable($"  \u2605 {target.Name} priority: {plannerState.Proposals[propIdx].Priority}");
                                WritePrompt(terminal);
                            }
                        }
                        return false;
                }
                break;
            }
        }

        return false;
    }

    private void RenderSixelChart(IVirtualTerminal terminal)
    {
        var pixelSize = terminal.PixelSize;
        var chartW = (int)pixelSize.Width;
        var chartH = Math.Min((int)(pixelSize.Height * 2 / 3), 600);

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

    internal async Task RunTuiAsync(TianWen.Lib.Astrometry.SOFA.Transform transform, CancellationToken ct)
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
