using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI live session monitor tab. Shows session phase, guide RMS, device status,
/// focus history, and exposure log in a simplified text layout.
/// </summary>
internal sealed class TuiLiveSessionTab(
    GuiAppState appState,
    LiveSessionState liveState,
    SignalBus bus) : TuiTabBase
{
    private TextBar? _topBar;
    private TextBar? _guideBar;
    private MarkdownWidget? _mainContent;
    private TextBar? _statusBar;

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_guideBar), nameof(_mainContent), nameof(_statusBar))]
    protected override bool IsReady => _topBar is not null && _guideBar is not null && _mainContent is not null && _statusBar is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var topVp = panel.Dock(DockStyle.Top, 1);
        var guideVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var fillVp = panel.Fill();

        _topBar = new TextBar(topVp);
        _guideBar = new TextBar(guideVp);
        _statusBar = new TextBar(bottomVp);
        _mainContent = new MarkdownWidget(fillVp);

        panel.Add(_topBar).Add(_guideBar).Add(_statusBar).Add(_mainContent);
    }

    protected override void RenderContent()
    {
        // Poll session to update cached fields
        liveState.PollSession();

        // Top bar: phase + target
        var phaseLabel = LiveSessionActions.PhaseLabel(liveState.Phase);
        // TUI doesn't have TimeProvider access — use system time
        var statusText = LiveSessionActions.PhaseStatusText(liveState, TimeProvider.System);
        _topBar.Text($" [{phaseLabel}]  {statusText}");
        _topBar.RightText($"Frames: {liveState.TotalFramesWritten}  Exp: {LiveSessionActions.FormatDuration(liveState.TotalExposureTime)}");

        // Guide RMS bar
        _guideBar.Text($" {LiveSessionActions.FormatGuideRms(liveState.LastGuideStats)}");

        // Main content: device status + focus history + exposure log
        var sb = new StringBuilder();

        // Device status
        sb.AppendLine("## Devices");
        if (liveState.ActiveSession is { } session)
        {
            var setup = session.Setup;
            sb.AppendLine($"- Mount: {setup.Mount.Device.DisplayName}");
            for (var i = 0; i < setup.Telescopes.Length; i++)
            {
                var ota = setup.Telescopes[i];
                sb.AppendLine($"- {ota.Name}: {ota.Camera.Device.DisplayName}");
            }
            sb.AppendLine($"- Guider: {setup.Guider.Device.DisplayName}");
        }
        else
        {
            sb.AppendLine("No session running");
        }

        // Focus history
        sb.AppendLine();
        sb.AppendLine("## Focus History");
        var focusHistory = liveState.FocusHistory;
        if (focusHistory.Count == 0)
        {
            sb.AppendLine("No focus runs");
        }
        else
        {
            var startIdx = Math.Max(0, focusHistory.Count - 5);
            for (var i = startIdx; i < focusHistory.Count; i++)
            {
                sb.AppendLine(LiveSessionActions.FormatFocusHistoryRow(focusHistory[i]));
            }
        }

        // Exposure log (last 10)
        sb.AppendLine();
        sb.AppendLine("## Exposure Log");
        var log = liveState.ExposureLog;
        if (log.Count == 0)
        {
            sb.AppendLine("No frames yet");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine("Time  Target       Filter  HFD");
            var startIdx = Math.Max(0, log.Count - 10);
            for (var i = startIdx; i < log.Count; i++)
            {
                sb.AppendLine(LiveSessionActions.FormatExposureLogRow(log[i]));
            }
            sb.AppendLine("```");
        }

        _mainContent.Markdown(sb.ToString());

        // Status bar
        var obsIdx = liveState.CurrentObservationIndex;
        var obsCount = liveState.ActiveSession?.Observations.Count ?? 0;
        _statusBar.Text($" Obs: {(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}  {(liveState.ShowAbortConfirm ? "Press Enter to confirm ABORT, Escape to cancel" : "Escape:abort  Q:quit")}");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    public override bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.KeyDown(InputKey.Escape, _) when liveState.ShowAbortConfirm:
                liveState.ShowAbortConfirm = false;
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.Enter, _) when liveState.ShowAbortConfirm:
                bus.Post(new ConfirmAbortSessionSignal());
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.Escape, _) when liveState.IsRunning:
                liveState.ShowAbortConfirm = true;
                NeedsRedraw = true;
                return false;

            default:
                return false;
        }
    }
}
