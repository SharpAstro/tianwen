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
    Option<string?> selectedProfileOption,
    Option<bool> interactiveOption
)
{
    private readonly Option<double?> latOption = new("--lat") { Description = "Site latitude in degrees (overrides profile)" };
    private readonly Option<double?> lonOption = new("--lon") { Description = "Site longitude in degrees (overrides profile)" };

    public Command Build()
    {
        var planCommand = new Command("plan", "Observation planner — show tonight's best targets and build a schedule")
        {
            Options = { latOption, lonOption }
        };
        planCommand.SetAction(PlanActionAsync);

        return planCommand;
    }

    internal async Task PlanActionAsync(ParseResult parseResult, CancellationToken ct)
    {
        var interactive = parseResult.GetValue(interactiveOption);

        // Resolve profile (needed for location)
        var allProfiles = await consoleHost.ListDevicesAsync<Profile>(DeviceType.Profile, DeviceDiscoveryOption.Force, ct);
        var profile = parseResult.GetSelected(allProfiles, selectedProfileOption);

        var lat = parseResult.GetValue(latOption);
        var lon = parseResult.GetValue(lonOption);

        // Resolve location
        var transform = LocationResolver.Resolve(consoleHost, profile, lat, lon, consoleHost.External.TimeProvider);
        if (transform is null)
        {
            return;
        }

        plannerState.SiteLatitude = transform.SiteLatitude;
        plannerState.SiteLongitude = transform.SiteLongitude;
        plannerState.ActiveProfile = profile;

        // Compute tonight's best
        await PlannerActions.ComputeTonightsBestAsync(
            plannerState, objectDb, transform,
            plannerState.MinHeightAboveHorizon, ct);

        if (interactive)
        {
            await RunInteractiveAsync(transform, ct);
        }
        else
        {
            RunNonInteractive(transform);
        }
    }

    private void RunNonInteractive(TianWen.Lib.Astrometry.SOFA.Transform transform)
    {
        var terminal = consoleHost.Terminal;

        // Header
        var siteLabel = $"{plannerState.SiteLatitude:F1}°{(plannerState.SiteLatitude >= 0 ? "N" : "S")}, {plannerState.SiteLongitude:F1}°{(plannerState.SiteLongitude >= 0 ? "E" : "W")}";
        var darkLocal = plannerState.AstroDark.ToLocalTime();
        var twLocal = plannerState.AstroTwilight.ToLocalTime();
        var nightHours = (plannerState.AstroTwilight - plannerState.AstroDark).TotalHours;

        consoleHost.WriteScrollable($"\nTonight's Best Targets ({darkLocal:yyyy-MM-dd}, {siteLabel})");
        consoleHost.WriteScrollable($"Astro dark: {darkLocal:HH:mm} — Astro twilight: {twLocal:HH:mm} ({nightHours:F1}h)\n");

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
        var chartH = Math.Min((int)(pixelSize.Height / 2), 400); // Use at most half the terminal height

        if (chartW < 100 || chartH < 50)
        {
            // Terminal too small for Sixel chart
            foreach (var line in AsciiAltitudeChart.Render(plannerState))
            {
                consoleHost.WriteScrollable(line);
            }
            return;
        }

        var renderer = new RgbaImageRenderer((uint)chartW, (uint)chartH);

        // Clear to dark background
        renderer.FillRectangle(
            new RectInt(new PointInt(chartW, chartH), new PointInt(0, 0)),
            new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));

        // Resolve font
        var fontPath = ResolveFontPath();

        AltitudeChartRenderer.Render(renderer, plannerState, fontPath);

        // Encode and output
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

        // Build canvas for altitude chart
        var canvasPixelSize = fillVp.PixelSize;
        var canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
        var canvas = new Canvas<RgbaImage>(fillVp, canvasRenderer);

        panel.Add(topBar).Add(statusBar).Add(targetList).Add(detailWidget).Add(canvas);

        var fontPath = ResolveFontPath();

        while (!ct.IsCancellationRequested)
        {
            // Handle input
            if (terminal.HasInput())
            {
                var evt = terminal.TryReadInput();
                if (HandleInput(evt, transform))
                {
                    return; // quit
                }
            }
            else
            {
                await Task.Delay(16, ct);
            }

            // Handle resize
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
            var darkLocal = plannerState.AstroDark.ToLocalTime();
            var twLocal = plannerState.AstroTwilight.ToLocalTime();
            topBar.Text($" {siteLabel} | Dark: {darkLocal:HH:mm}-{twLocal:HH:mm} | Proposals: {plannerState.Proposals.Count}");
            topBar.RightText($"{plannerState.ActiveProfile?.DisplayName ?? "No profile"} ");

            // Update target list
            var items = new TargetListItem[plannerState.TonightsBest.Count];
            for (var i = 0; i < items.Length; i++)
            {
                var scored = plannerState.TonightsBest[i];
                var isProposed = plannerState.Proposals.Any(p => p.Target == scored.Target);
                items[i] = new TargetListItem(scored, isProposed, i == plannerState.SelectedTargetIndex);
            }
            targetList.Items(items).Header("Tonight's Best").ScrollTo(
                Math.Max(0, plannerState.SelectedTargetIndex - targetList.VisibleRows / 2));

            // Update detail panel
            if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
            {
                var selected = plannerState.TonightsBest[plannerState.SelectedTargetIndex];
                var isProposed = plannerState.Proposals.Any(p => p.Target == selected.Target);
                var proposedMark = isProposed ? " **[PROPOSED]**" : "";
                detailWidget.Markdown(
                    $"## {selected.Target.Name}{proposedMark}\n\n" +
                    $"**RA**: {selected.Target.RA:F3}h  **Dec**: {selected.Target.Dec:F2}°\n\n" +
                    $"**Peak altitude**: {selected.OptimalAltitude:F0}°  " +
                    $"**Window**: {selected.OptimalStart.ToLocalTime():HH:mm}–{(selected.OptimalStart + selected.OptimalDuration).ToLocalTime():HH:mm}  " +
                    $"**Score**: {selected.CombinedScore:F0}\n\n" +
                    $"*Enter* to add/remove | *P* priority | *S* schedule | *Q* quit");
            }

            // Render altitude chart
            canvasRenderer.FillRectangle(
                new RectInt(new PointInt((int)canvasPixelSize.Width, (int)canvasPixelSize.Height), new PointInt(0, 0)),
                new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));
            AltitudeChartRenderer.Render(canvasRenderer, plannerState, fontPath,
                highlightTargetIndex: plannerState.SelectedTargetIndex);

            // Update status bar
            var scheduleStatus = plannerState.Schedule is { Count: > 0 } s
                ? $"Schedule: {s.Count} obs"
                : "No schedule (press S)";
            statusBar.Text($" ↑↓:nav Enter:toggle P:priority S:schedule ?:help Q:quit");
            statusBar.RightText($"{scheduleStatus} ");

            panel.RenderAll();
        }
    }

    /// <summary>
    /// Handles keyboard input. Returns true if the user wants to quit.
    /// </summary>
    private bool HandleInput(ConsoleInputEvent evt, TianWen.Lib.Astrometry.SOFA.Transform transform)
    {
        switch (evt.Key)
        {
            case ConsoleKey.Q or ConsoleKey.Escape:
                return true;

            case ConsoleKey.UpArrow:
                if (plannerState.SelectedTargetIndex > 0)
                {
                    plannerState.SelectedTargetIndex--;
                    plannerState.NeedsRedraw = true;
                }
                break;

            case ConsoleKey.DownArrow:
                if (plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count - 1)
                {
                    plannerState.SelectedTargetIndex++;
                    plannerState.NeedsRedraw = true;
                }
                break;

            case ConsoleKey.Enter:
                if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                {
                    var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                    PlannerActions.ToggleProposal(plannerState, target);
                }
                break;

            case ConsoleKey.P:
                // Find proposal index for selected target
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

            case ConsoleKey.S:
                PlannerActions.BuildSchedule(plannerState, transform,
                    defaultGain: 120, defaultOffset: 10,
                    defaultSubExposure: TimeSpan.FromSeconds(120),
                    defaultObservationTime: TimeSpan.FromMinutes(60));
                break;
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
