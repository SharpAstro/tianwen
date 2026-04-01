using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
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
///
/// Layout:
/// <code>
/// ┌─────────────────────────────────────────────────────────────┐
/// │ [Phase] Activity text                 Obs:1/2 F:3/~27 Exp:6m│ Top bar
/// │ Guiding: Total 0.3" Ra 0.2" Dec 0.2" Peak 0.5"             │ Guide bar
/// ├───────────────────────────────┬─────────────────────────────┤
/// │ ## Camera  /  Mount  /  Focus │      Preview                │
/// │ Exposure log                  │      (Sixel Canvas)         │
/// ├───────────────────────────────┴─────────────────────────────┤
/// │ Escape:abort  Q:quit                       Session started  │ Status bar
/// └─────────────────────────────────────────────────────────────┘
/// </code>
/// </summary>
internal sealed class TuiLiveSessionTab(
    GuiAppState appState,
    LiveSessionState liveState,
    IVirtualTerminal terminal,
    SignalBus bus) : TuiTabBase
{
    private const int LeftPanelCols = 40;
    private const int ProgressBarWidth = 20;
    private const int SparklineWidth = 20;
    private const string SparkChars = "▁▂▃▄▅▆▇█";

    private TextBar? _topBar;
    private TextBar? _guideBar;
    private MarkdownWidget? _infoPanel;
    private TextBar? _statusBar;
    private TextBar? _previewToolbar;

    // Preview area — Canvas for Sixel, MarkdownWidget placeholder for non-Sixel
    private Canvas? _previewCanvas;
    private SixelRgbaImageRenderer? _previewRenderer;
    private MarkdownWidget? _previewFallback;

    // Sixel preview state
    private Image? _displayedImage;
    private Task<AstroImageDocument?>? _pendingDoc;
    private AstroImageDocument? _lastDoc;

    private readonly ViewerState _viewerState = new ViewerState();

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_guideBar), nameof(_infoPanel), nameof(_statusBar), nameof(_previewToolbar))]
    protected override bool IsReady =>
        _topBar is not null && _guideBar is not null && _infoPanel is not null && _statusBar is not null && _previewToolbar is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var topVp = panel.Dock(DockStyle.Top, 1);
        var guideVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var leftVp = panel.Dock(DockStyle.Left, LeftPanelCols);
        var toolbarVp = panel.Dock(DockStyle.Top, 1);
        var fillVp = panel.Fill();

        _topBar = new TextBar(topVp);
        _guideBar = new TextBar(guideVp);
        _statusBar = new TextBar(bottomVp);
        _infoPanel = new MarkdownWidget(leftVp);
        _previewToolbar = new TextBar(toolbarVp);

        // Preview area: Sixel-capable terminals get a Canvas, others get a text fallback
        if (terminal.HasSixelSupport)
        {
            var canvasPixelSize = fillVp.PixelSize;
            _previewRenderer = new SixelRgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
            _previewCanvas = new Canvas(fillVp, _previewRenderer);
            _previewFallback = null;
            panel.Add(_topBar).Add(_guideBar).Add(_statusBar).Add(_infoPanel).Add(_previewToolbar).Add(_previewCanvas);
        }
        else
        {
            _previewCanvas = null;
            _previewRenderer = null;
            _previewFallback = new MarkdownWidget(fillVp);
            panel.Add(_topBar).Add(_guideBar).Add(_statusBar).Add(_infoPanel).Add(_previewToolbar).Add(_previewFallback);
        }
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        RenderTopBar();
        RenderGuideBar();
        RenderInfoPanel();
        RenderPreviewToolbar();
        RenderPreview();
        RenderStatusBar();
    }

    private void RenderTopBar()
    {
        // Phase + activity
        var phaseLabel = LiveSessionActions.PhaseLabel(liveState.Phase);
        var statusText = LiveSessionActions.PhaseStatusText(liveState, TimeProvider.System);
        _topBar!.Text($" [{phaseLabel}]  {statusText}");

        // Right side: obs/frame/exp counter
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
    }

    private void RenderGuideBar()
    {
        _guideBar!.Text($" {LiveSessionActions.FormatGuideRms(liveState.LastGuideStats)}");
    }

    private void RenderInfoPanel()
    {
        var sb = new StringBuilder();

        if (liveState.ActiveSession is { } session)
        {
            RenderCameraStatus(sb, session);
            RenderMountStatus(sb, session);
        }
        else
        {
            sb.AppendLine("No session running");
        }

        RenderFocusHistory(sb);
        RenderExposureLog(sb);

        _infoPanel!.Markdown(sb.ToString());
    }

    private void RenderCameraStatus(StringBuilder sb, ISession session)
    {
        var cameraStates = liveState.CameraStates;
        for (var i = 0; i < session.Setup.Telescopes.Length; i++)
        {
            var ota = session.Setup.Telescopes[i];
            sb.AppendLine($"## {ota.Camera.Device.DisplayName}");

            // Cooling info — find latest sample + sparklines for this camera
            var coolingSamples = liveState.CoolingSamples;
            CoolingSample? latestSample = null;
            for (var j = coolingSamples.Length - 1; j >= 0; j--)
            {
                if (coolingSamples[j].CameraIndex == i)
                {
                    latestSample = coolingSamples[j];
                    break;
                }
            }

            if (latestSample is { } s)
            {
                sb.AppendLine($"Sensor: {s.TemperatureC:F0}°C → {s.SetpointTempC:F0}°C  {s.CoolerPowerPercent:F0}%");
                sb.AppendLine();

                // Temperature sparkline (last N samples for this camera)
                var tempSpark = BuildSparkline(coolingSamples, i, static c => c.TemperatureC);
                var pwrSpark = BuildSparkline(coolingSamples, i, static c => c.CoolerPowerPercent, 0, 100);
                if (tempSpark.Length > 0)
                {
                    sb.AppendLine($"Temp {tempSpark}");
                    sb.AppendLine();
                    sb.AppendLine($"Pwr  {pwrSpark}");
                    sb.AppendLine();
                }
            }

            // Focuser + exposure state
            if (i < cameraStates.Length)
            {
                var cs = cameraStates[i];

                // Focuser line
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
                sb.AppendLine();

                // Exposure state with progress bar
                if (cs.State == CameraState.Exposing)
                {
                    var elapsed = TimeProvider.System.GetUtcNow() - cs.ExposureStart;
                    var total = cs.SubExposure.TotalSeconds;
                    var elapsedSec = Math.Min(elapsed.TotalSeconds, total);
                    var progress = total > 0 ? elapsedSec / total : 0;
                    var filled = (int)(progress * ProgressBarWidth);
                    var bar = new string('█', filled) + new string('░', ProgressBarWidth - filled);
                    sb.AppendLine($"{cs.FilterName} #{cs.FrameNumber}  {bar} {elapsedSec:F0}/{total:F0}s");
                }
                else if (cs.State is CameraState.Download or CameraState.Reading)
                {
                    sb.AppendLine($"Downloading: #{cs.FrameNumber}");
                }
                else
                {
                    sb.AppendLine("Idle");
                }
                sb.AppendLine();
            }

            sb.AppendLine();
        }
    }

    private void RenderMountStatus(StringBuilder sb, ISession session)
    {
        var ms = liveState.MountState;
        var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
        var pier = ms.PierSide is PointingState.Normal ? "E" : ms.PierSide is PointingState.ThroughThePole ? "W" : "";
        sb.AppendLine($"## {session.Setup.Mount.Device.DisplayName}  {mountStatus}  {pier}");
        var raStr = CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
        var decStr = CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);
        sb.AppendLine($"RA {raStr}  HA {ms.HourAngle:+0.00;-0.00}h");
        sb.AppendLine();
        sb.AppendLine($"Dec {decStr}");
        sb.AppendLine();
        if (liveState.ActiveObservation is { Target: var target })
        {
            sb.AppendLine($"→ {target.Name}");
            sb.AppendLine();
        }
    }

    private void RenderFocusHistory(StringBuilder sb)
    {
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
    }

    private void RenderExposureLog(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## Exposures");
        var log = liveState.ExposureLog;
        if (log.Length == 0)
        {
            sb.AppendLine("No frames yet");
        }
        else
        {
            sb.AppendLine("Time  Target       Filter  HFD   ★");
            sb.AppendLine();
            var startIdx = Math.Max(0, log.Length - 8);
            for (var i = startIdx; i < log.Length; i++)
            {
                sb.AppendLine(LiveSessionActions.FormatExposureLogRow(log[i]));
                sb.AppendLine();
            }
        }
    }

    private void RenderPreview()
    {
        // Check for new frame across all cameras
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

        // New frame arrived — kick off async document creation + stretch
        if (latestImage is not null && !ReferenceEquals(latestImage, _displayedImage) && _pendingDoc is null)
        {
            _displayedImage = latestImage;
            var capturedImage = latestImage;
            _pendingDoc = Task.Run(async () => (AstroImageDocument?)await AstroImageDocument.CreateFromImageAsync(capturedImage));
        }

        // Check if document creation completed
        if (_pendingDoc is { IsCompleted: true } task)
        {
            _pendingDoc = null;
            if (task.IsCompletedSuccessfully && task.Result is { } doc)
            {
                _lastDoc = doc;
            }
        }

        // Render preview to Canvas (Sixel) or text fallback
        if (_previewCanvas is not null && _previewRenderer is not null && _lastDoc is not null)
        {
            // Render directly into the canvas's surface — ConsoleImageRenderer wraps an RgbaImageRenderer
            var renderer = new ConsoleImageRenderer(_previewRenderer);
            renderer.RenderImage(_lastDoc, _viewerState, _viewerState.CurvesBoost);
        }
        else if (_previewFallback is not null)
        {
            // Non-Sixel: show frame metadata
            var metrics = liveState.LastFrameMetrics;
            if (metrics.Length > 0 && metrics[0] is var m && m.StarCount > 0)
            {
                _previewFallback.Markdown(
                    $"## Last Frame\n\n" +
                    $"Stars: {m.StarCount}  HFD: {m.MedianHfd:F1}\"  FWHM: {m.MedianFwhm:F1}\"\n\n" +
                    $"Gain: {m.Gain}  Exp: {m.Exposure.TotalSeconds:F0}s");
            }
            else
            {
                _previewFallback.Markdown("## Preview\n\nWaiting for first frame...");
            }
        }
    }

    private void RenderPreviewToolbar()
    {
        var stretchLabel = _viewerState.StretchMode switch
        {
            StretchMode.None => "Off",
            StretchMode.Unlinked => "Unl",
            StretchMode.Linked => "Lnk",
            StretchMode.Luma => "Lum",
            _ => "?"
        };
        var boostLabel = _viewerState.CurvesBoost > 0 ? $"B:{_viewerState.CurvesBoost:F0}%" : "B:Off";
        var zoomLabel = _viewerState.ZoomToFit ? "Fit" : "1:1";
        _previewToolbar!.Text($" [{zoomLabel}] [{stretchLabel}] [{boostLabel}]");

        var paramLabel = _viewerState.StretchMode is not StretchMode.None
            ? $"({_viewerState.StretchParameters.Factor:F1}, {Math.Abs(_viewerState.StretchParameters.ShadowsClipping):F0})"
            : "";
        _previewToolbar.RightText($"{paramLabel} T:stretch B:boost +/-:params F:fit R:1:1 ");
    }

    private void RenderStatusBar()
    {
        _statusBar!.Text(liveState.ShowAbortConfirm
            ? " Press Enter to confirm ABORT, Escape to cancel"
            : liveState.IsRunning ? " Escape:abort" : " Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    /// <summary>
    /// Builds a Unicode sparkline (▁▂▃▄▅▆▇█) from the last <see cref="SparklineWidth"/> cooling
    /// samples for the given camera, using <paramref name="selector"/> to pick the value.
    /// </summary>
    private static string BuildSparkline(
        ImmutableArray<CoolingSample> samples, int cameraIndex,
        Func<CoolingSample, double> selector,
        double? fixedMin = null, double? fixedMax = null)
    {
        // Collect last N samples for this camera
        Span<double> values = stackalloc double[SparklineWidth];
        var count = 0;
        for (var j = samples.Length - 1; j >= 0 && count < SparklineWidth; j--)
        {
            if (samples[j].CameraIndex == cameraIndex)
            {
                values[count++] = selector(samples[j]);
            }
        }

        if (count < 2)
        {
            return "";
        }

        // Reverse so oldest is on the left
        values[..count].Reverse();

        var min = fixedMin ?? double.MaxValue;
        var max = fixedMax ?? double.MinValue;
        if (!fixedMin.HasValue || !fixedMax.HasValue)
        {
            for (var j = 0; j < count; j++)
            {
                if (!fixedMin.HasValue && values[j] < min) min = values[j];
                if (!fixedMax.HasValue && values[j] > max) max = values[j];
            }
        }

        var range = max - min;
        Span<char> spark = stackalloc char[count];
        for (var j = 0; j < count; j++)
        {
            var norm = range > 0 ? (values[j] - min) / range : 0.5;
            var idx = (int)(norm * (SparkChars.Length - 1));
            spark[j] = SparkChars[Math.Clamp(idx, 0, SparkChars.Length - 1)];
        }

        return new string(spark);
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

            case InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl) when liveState.IsRunning:
            case InputEvent.KeyDown(InputKey.Escape or InputKey.Q, _) when liveState.IsRunning:
                liveState.ShowAbortConfirm = true;
                NeedsRedraw = true;
                return false; // consumed via NeedsRedraw — don't quit

            // Preview viewer controls — same shortcuts as FITS viewer
            case InputEvent.KeyDown(InputKey.T, _):
                _viewerState.StretchMode = _viewerState.StretchMode switch
                {
                    StretchMode.None => StretchMode.Unlinked,
                    StretchMode.Unlinked => StretchMode.Linked,
                    StretchMode.Linked => StretchMode.Luma,
                    _ => StretchMode.None,
                };
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.B, _):
                ViewerActions.CycleCurvesBoost(_viewerState);
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.Plus, _):
                ViewerActions.CycleStretchPreset(_viewerState);
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.Minus, _):
                ViewerActions.CycleStretchPreset(_viewerState, reverse: true);
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.F, _):
                _viewerState.ZoomToFit = true;
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(InputKey.R, _):
                _viewerState.ZoomToFit = false;
                NeedsRedraw = true;
                return false;

            default:
                return false;
        }
    }
}
