using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI live session monitor tab. Shows session phase, per-OTA status,
/// mount state, guide RMS, focus history, and exposure log.
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
        liveState.PollSession();

        // Top bar: phase + activity
        var phaseLabel = LiveSessionActions.PhaseLabel(liveState.Phase);
        var statusText = LiveSessionActions.PhaseStatusText(liveState, TimeProvider.System);
        _topBar.Text($" [{phaseLabel}]  {statusText}");

        // Top bar right: obs/frame/exp counter
        var obsIdx = liveState.CurrentObservationIndex;
        var obsCount = liveState.ActiveSession?.Observations.Count ?? 0;
        var obsInfo = $"Obs:{(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}";
        if (liveState.ActiveObservation is { } obs)
        {
            var subSec = obs.SubExposure.TotalSeconds;
            var estimated = subSec > 0 ? (int)(obs.Duration.TotalSeconds / (subSec + 10)) : 0;
            obsInfo += $" F:{liveState.TotalFramesWritten}/~{estimated}";
        }
        obsInfo += $" Exp:{LiveSessionActions.FormatDuration(liveState.TotalExposureTime)}";
        _topBar.RightText(obsInfo);

        // Guide RMS bar
        _guideBar.Text($" {LiveSessionActions.FormatGuideRms(liveState.LastGuideStats)}");

        var sb = new StringBuilder();

        // Per-OTA status
        if (liveState.ActiveSession is { } session)
        {
            var cameraStates = liveState.CameraStates;
            for (var i = 0; i < session.Setup.Telescopes.Length; i++)
            {
                var ota = session.Setup.Telescopes[i];
                sb.AppendLine($"## {ota.Camera.Device.DisplayName}");

                // Cooling info
                var coolingSamples = liveState.CoolingSamples;
                for (var j = coolingSamples.Length - 1; j >= 0; j--)
                {
                    if (coolingSamples[j].CameraIndex == i)
                    {
                        var s = coolingSamples[j];
                        sb.AppendLine($"  {s.TemperatureC:F0}°C  {s.CoolerPowerPercent:F0}%  → {s.SetpointTempC:F0}°C");
                        break;
                    }
                }

                // Focuser + exposure state
                if (i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    var focLine = $"  Foc: {cs.FocusPosition}";
                    if (!double.IsNaN(cs.FocuserTemperature))
                    {
                        focLine += $"  {cs.FocuserTemperature:F1}°C";
                    }
                    if (cs.FocuserIsMoving)
                    {
                        focLine += "  ⇄Moving";
                    }
                    sb.AppendLine(focLine);

                    // Exposure state
                    if (cs.State == CameraState.Exposing)
                    {
                        var elapsed = TimeProvider.System.GetUtcNow() - cs.ExposureStart;
                        var total = cs.SubExposure.TotalSeconds;
                        var elapsedSec = Math.Min(elapsed.TotalSeconds, total);
                        sb.AppendLine($"  {cs.FilterName} #{cs.FrameNumber} ({elapsedSec:F0}/{total:F0}s)");
                    }
                    else if (cs.State is CameraState.Download or CameraState.Reading)
                    {
                        sb.AppendLine($"  Downloading #{cs.FrameNumber}...");
                    }
                    else
                    {
                        sb.AppendLine("  Idle");
                    }
                }

                sb.AppendLine();
            }

            // Mount status
            var ms = liveState.MountState;
            var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
            var pier = ms.PierSide is PointingState.Normal ? "E" : ms.PierSide is PointingState.ThroughThePole ? "W" : "";
            sb.AppendLine($"## {session.Setup.Mount.Device.DisplayName}  {mountStatus}  {pier}");
            var raStr = CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
            var decStr = CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);
            sb.AppendLine($"  RA {raStr}  HA {ms.HourAngle:+0.00;-0.00}h");
            sb.AppendLine($"  Dec {decStr}");
            if (liveState.ActiveObservation is { Target: var target })
            {
                sb.AppendLine($"  → {target.Name}");
            }
        }
        else
        {
            sb.AppendLine("No session running");
        }

        // Focus history (last 3)
        var focusHistory = liveState.FocusHistory;
        if (focusHistory.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Focus");
            var startIdx = Math.Max(0, focusHistory.Length - 3);
            for (var i = startIdx; i < focusHistory.Length; i++)
            {
                sb.AppendLine(LiveSessionActions.FormatFocusHistoryRow(focusHistory[i]));
            }
        }

        // Exposure log (last 8)
        sb.AppendLine();
        sb.AppendLine("## Exposures");
        var log = liveState.ExposureLog;
        if (log.Length == 0)
        {
            sb.AppendLine("No frames yet");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine("Time  Target       Filter  HFD   ★");
            var startIdx = Math.Max(0, log.Length - 8);
            for (var i = startIdx; i < log.Length; i++)
            {
                sb.AppendLine(LiveSessionActions.FormatExposureLogRow(log[i]));
            }
            sb.AppendLine("```");
        }

        _mainContent.Markdown(sb.ToString());

        // Status bar
        _statusBar.Text(liveState.ShowAbortConfirm
            ? " Press Enter to confirm ABORT, Escape to cancel"
            : " Escape:abort  Q:quit");
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
