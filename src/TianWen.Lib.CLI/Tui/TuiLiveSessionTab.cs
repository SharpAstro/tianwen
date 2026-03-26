using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.CLI.View;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI live session monitor tab. Shows session phase, per-OTA status,
/// mount state, guide RMS, focus history, exposure log, and Sixel preview.
/// </summary>
internal sealed class TuiLiveSessionTab(
    GuiAppState appState,
    LiveSessionState liveState,
    IVirtualTerminal terminal,
    SignalBus bus) : TuiTabBase
{
    private TextBar? _topBar;
    private TextBar? _guideBar;
    private MarkdownWidget? _mainContent;
    private TextBar? _statusBar;

    // Sixel preview state
    private Image? _displayedImage;
    private Task<byte[]?>? _pendingSixel;
    private byte[]? _sixelBytes;

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
        if (!IsReady) return;

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
                        sb.AppendLine($"Sensor: {s.TemperatureC:F0}°C → {s.SetpointTempC:F0}°C  Cooler: {s.CoolerPowerPercent:F0}%");
                        break;
                    }
                }

                // Focuser + exposure state
                if (i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    var focParts = $"Focus: {cs.FocusPosition}";
                    if (!double.IsNaN(cs.FocuserTemperature))
                    {
                        focParts += $"  ({cs.FocuserTemperature:F1}°C)";
                    }
                    if (cs.FocuserIsMoving)
                    {
                        focParts += "  Moving";
                    }
                    sb.AppendLine(focParts);

                    // Exposure state
                    if (cs.State == CameraState.Exposing)
                    {
                        var elapsed = TimeProvider.System.GetUtcNow() - cs.ExposureStart;
                        var total = cs.SubExposure.TotalSeconds;
                        var elapsedSec = Math.Min(elapsed.TotalSeconds, total);
                        sb.AppendLine($"Exposing: {cs.FilterName} #{cs.FrameNumber} ({elapsedSec:F0}/{total:F0}s)");
                    }
                    else if (cs.State is CameraState.Download or CameraState.Reading)
                    {
                        sb.AppendLine($"Downloading: #{cs.FrameNumber}");
                    }
                    else
                    {
                        sb.AppendLine("Idle");
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

        // Sixel preview: check for new frame, kick off encoding, render when ready
        if (terminal.HasSixelSupport)
        {
            var images = liveState.LastCapturedImages;
            Image? latestImage = null;
            for (var i = 0; i < images.Length; i++)
            {
                if (images[i] is { } img)
                {
                    latestImage = img;
                    break;
                }
            }

            // New frame arrived — kick off async Sixel encoding
            if (latestImage is not null && !ReferenceEquals(latestImage, _displayedImage) && _pendingSixel is null)
            {
                _displayedImage = latestImage;
                _pendingSixel = Task.Run<byte[]?>(async () =>
                {
                    var doc = await AstroImageDocument.CreateFromImageAsync(latestImage);
                    var pixW = Math.Min(320, (int)terminal.PixelSize.Width / 2);
                    var pixH = (int)(pixW * ((double)doc.UnstretchedImage.Height / doc.UnstretchedImage.Width));
                    var renderer = new ConsoleImageRenderer(pixW, pixH);
                    renderer.RenderImage(doc, new ViewerState());
                    using var ms = new MemoryStream();
                    renderer.EncodeSixel(ms);
                    return ms.ToArray();
                });
            }

            // Check if encoding completed
            if (_pendingSixel is { IsCompleted: true } task)
            {
                _pendingSixel = null;
                if (task.IsCompletedSuccessfully && task.Result is { } bytes)
                {
                    _sixelBytes = bytes;
                }
            }

            // Append Sixel to content if available
            if (_sixelBytes is not null)
            {
                sb.AppendLine();
                sb.AppendLine("## Preview");
            }
        }

        _mainContent.Markdown(sb.ToString());

        // Write Sixel directly to terminal after markdown render (Sixel bypasses the widget system)
        if (_sixelBytes is not null && terminal.HasSixelSupport)
        {
            terminal.OutputStream.Write(_sixelBytes);
            terminal.Flush();
        }

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

            case InputEvent.KeyDown(InputKey.Escape or InputKey.Q, _) when liveState.IsRunning:
                liveState.ShowAbortConfirm = true;
                NeedsRedraw = true;
                return false; // consumed via NeedsRedraw — don't quit

            default:
                return false;
        }
    }
}
