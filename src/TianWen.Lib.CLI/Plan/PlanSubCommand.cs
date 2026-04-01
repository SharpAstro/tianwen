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
                        var cliSessionState = new SessionTabState();
                        PlannerActions.BuildSchedule(plannerState, cliSessionState, transform,
                            defaultGain: 120, defaultOffset: 10,
                            defaultSubExposure: TimeSpan.FromSeconds(120),
                            defaultObservationTime: TimeSpan.FromMinutes(60));
                        if (cliSessionState.Schedule is { Count: > 0 } schedule)
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

        var renderer = new SixelRgbaImageRenderer((uint)chartW, (uint)chartH);

        renderer.FillRectangle(
            new RectInt(new PointInt(chartW, chartH), new PointInt(0, 0)),
            new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));

        var fontPath = Tui.TuiFontPath.Resolve();
        AltitudeChartRenderer.Render(renderer, plannerState, fontPath);

        renderer.EncodeSixel(terminal.OutputStream);
        terminal.Flush();
    }

}
