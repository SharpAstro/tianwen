using System;
using System.Collections.Generic;
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
    ITimeProvider timeProvider,
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

    // Preview-mode clickable-cell cache. Populated while rendering the info panel,
    // consumed by RegisterClickableRegions. Each entry is a cell range within
    // _infoPanel's text (line + column span) plus a click handler; the register
    // step translates those to pixel coordinates using the widget viewport.
    private readonly List<(int Line, int ColStart, int ColEnd, Action<InputModifier> OnClick)> _previewButtons = [];

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
        var statusText = LiveSessionActions.PhaseStatusText(liveState, timeProvider);
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
        _previewButtons.Clear();

        // Preview mode: no session running + profile with OTAs. Render per-OTA
        // telemetry rows with clickable stepper cells + action buttons.
        if (!liveState.IsRunning
            && appState.ActiveProfile?.Data is { OTAs.Length: > 0 } profileData)
        {
            // Arrays are sized on telemetry poll; render can race ahead of the first
            // poll on the very first frame after the tab becomes active. Size them
            // here too so indexing is always safe.
            liveState.ResizePreviewArrays(profileData.OTAs.Length);
            RenderPreviewPanel(sb, profileData);
        }
        else if (liveState.ActiveSession is { } session)
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

    private void RenderPreviewPanel(StringBuilder sb, ProfileData profileData)
    {
        var otas = profileData.OTAs;
        var selectedIdx = Math.Clamp(liveState.SelectedPreviewOtaIndex, 0, otas.Length - 1);
        liveState.SelectedPreviewOtaIndex = selectedIdx; // snap stale value

        var lineNum = 0;
        for (var i = 0; i < otas.Length; i++)
        {
            var capturedIdx = i;
            var isSelected = i == selectedIdx;
            var tel = i < liveState.PreviewOTATelemetry.Length
                ? liveState.PreviewOTATelemetry[i]
                : PreviewOTATelemetry.Unknown;

            // Header — click anywhere on it to select this OTA.
            var marker = isSelected ? ">" : " ";
            var camName = tel.CameraConnected && !string.IsNullOrEmpty(tel.CameraDisplayName)
                ? tel.CameraDisplayName : otas[i].Name;
            sb.AppendLine($"{marker} {i + 1}: {camName}");
            _previewButtons.Add((lineNum, 0, LeftPanelCols,
                _ => { liveState.SelectedPreviewOtaIndex = capturedIdx; NeedsRedraw = true; }));
            lineNum++;

            // Temp / cooler
            if (tel.CameraConnected && !double.IsNaN(tel.CcdTempC))
            {
                var setpoint = !double.IsNaN(tel.SetpointC) ? $" / {tel.SetpointC:F0}\u00b0C" : "";
                var power = !double.IsNaN(tel.CoolerPowerPct) ? $"  Pwr: {tel.CoolerPowerPct:F0}%" : "";
                sb.AppendLine($"  T: {tel.CcdTempC:F1}\u00b0C{setpoint}{power}");
            }
            else if (!tel.CameraConnected)
            {
                sb.AppendLine("  Camera: disconnected");
            }
            else
            {
                sb.AppendLine("  T: --");
            }
            lineNum++;

            // Focus
            if (tel.FocuserConnected)
            {
                var tempStr = !double.IsNaN(tel.FocuserTempC) ? $" @ {tel.FocuserTempC:F1}\u00b0C" : "";
                var movingStr = tel.FocuserIsMoving ? " moving" : "";
                sb.AppendLine($"  Focus: {tel.FocusPosition}{tempStr}{movingStr}");
            }
            else
            {
                sb.AppendLine("  Focus: (no focuser)");
            }
            lineNum++;

            // Filter (only if filter wheel is connected)
            if (tel.FilterWheelConnected)
            {
                sb.AppendLine($"  Filter: {tel.FilterName}");
                lineNum++;
            }

            // Exposure / capture row — either a progress line or the stepper strip.
            var isCapturing = i < liveState.PreviewCapturing.Length && liveState.PreviewCapturing[i];
            if (isCapturing)
            {
                var start = liveState.PreviewCaptureStart[i];
                var dur = liveState.PreviewExposureDuration[i];
                var elapsed = timeProvider.GetUtcNow() - start;
                var elapsedSec = Math.Min(elapsed.TotalSeconds, dur.TotalSeconds);
                sb.AppendLine($"  Capturing: {elapsedSec:F0}/{dur.TotalSeconds:F0}s");
                lineNum++;
            }
            else
            {
                var expSec = i < liveState.PreviewExposureSeconds.Length
                    ? liveState.PreviewExposureSeconds[i] : 5.0;
                var expLabel = LiveSessionActions.FormatExposureLabel(expSec);
                // Fixed-width label so stepper cells have predictable column positions.
                // Layout: "  Exp: [-] LLLLLL [+]  [Capture]"
                //                       ^7  ^11          ^20
                var line = $"  Exp: [-] {expLabel,-6} [+]  [Capture]";
                sb.AppendLine(line);
                var decCol = 7;   // after "  Exp: "
                var incCol = 17;  // "[-] LLLLLL "
                var capCol = 22;  // "[+]  "
                _previewButtons.Add((lineNum, decCol, decCol + 3, _ =>
                {
                    if (capturedIdx < liveState.PreviewExposureSeconds.Length)
                    {
                        liveState.PreviewExposureSeconds[capturedIdx] = LiveSessionActions.StepExposure(
                            liveState.PreviewExposureSeconds[capturedIdx], direction: -1);
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        NeedsRedraw = true;
                    }
                }));
                _previewButtons.Add((lineNum, incCol, incCol + 3, _ =>
                {
                    if (capturedIdx < liveState.PreviewExposureSeconds.Length)
                    {
                        liveState.PreviewExposureSeconds[capturedIdx] = LiveSessionActions.StepExposure(
                            liveState.PreviewExposureSeconds[capturedIdx], direction: +1);
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        NeedsRedraw = true;
                    }
                }));
                _previewButtons.Add((lineNum, capCol, capCol + 9, _ =>
                {
                    liveState.SelectedPreviewOtaIndex = capturedIdx;
                    PostCapture(capturedIdx);
                }));
                lineNum++;
            }

            // Gain stepper (only when camera supports gain AND we're not mid-capture)
            var hasGain = (tel.UsesGainValue && tel.GainMax > tel.GainMin)
                || (tel.UsesGainMode && tel.GainModes.Length > 0);
            if (hasGain && !isCapturing)
            {
                var gainVal = i < liveState.PreviewGain.Length ? liveState.PreviewGain[i] : null;
                var gainLabel = LiveSessionActions.FormatGainLabel(gainVal, tel);
                // Layout: "  Gain: [-] LLLLLLLLLL [+]"
                //                         ^8   ^12
                var line = $"  Gain: [-] {gainLabel,-10} [+]";
                sb.AppendLine(line);
                var capturedTel = tel;
                var decCol = 8;
                var incCol = 22;
                _previewButtons.Add((lineNum, decCol, decCol + 3, _ =>
                {
                    if (capturedIdx < liveState.PreviewGain.Length)
                    {
                        liveState.PreviewGain[capturedIdx] = LiveSessionActions.StepGain(
                            liveState.PreviewGain[capturedIdx], capturedTel, direction: -1);
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        NeedsRedraw = true;
                    }
                }));
                _previewButtons.Add((lineNum, incCol, incCol + 3, _ =>
                {
                    if (capturedIdx < liveState.PreviewGain.Length)
                    {
                        liveState.PreviewGain[capturedIdx] = LiveSessionActions.StepGain(
                            liveState.PreviewGain[capturedIdx], capturedTel, direction: +1);
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        NeedsRedraw = true;
                    }
                }));
                lineNum++;
            }

            // Action row: jog (needs focuser) + save/solve (needs a captured image).
            var hasImage = i < liveState.LastCapturedImages.Length
                && liveState.LastCapturedImages[i] is not null;
            if (tel.FocuserConnected || hasImage)
            {
                var actionLine = new StringBuilder("  ");
                var col = 2;
                if (tel.FocuserConnected)
                {
                    actionLine.Append("[J-50] [J+50]  ");
                    _previewButtons.Add((lineNum, col, col + 6, _ =>
                    {
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        bus.Post(new JogFocuserSignal(capturedIdx, -50));
                    }));
                    _previewButtons.Add((lineNum, col + 7, col + 13, _ =>
                    {
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        bus.Post(new JogFocuserSignal(capturedIdx, +50));
                    }));
                    col += 15;
                }
                if (hasImage)
                {
                    actionLine.Append("[Save] [Solve]");
                    _previewButtons.Add((lineNum, col, col + 6, _ =>
                    {
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        bus.Post(new SaveSnapshotSignal(capturedIdx));
                    }));
                    _previewButtons.Add((lineNum, col + 7, col + 14, _ =>
                    {
                        liveState.SelectedPreviewOtaIndex = capturedIdx;
                        bus.Post(new PlateSolvePreviewSignal(capturedIdx));
                    }));
                }
                sb.AppendLine(actionLine.ToString());
                lineNum++;
            }

            sb.AppendLine();
            lineNum++;
        }
    }

    private void PostCapture(int otaIndex)
    {
        var exp = otaIndex < liveState.PreviewExposureSeconds.Length
            ? liveState.PreviewExposureSeconds[otaIndex] : 5.0;
        var gain = otaIndex < liveState.PreviewGain.Length ? liveState.PreviewGain[otaIndex] : null;
        var binning = otaIndex < liveState.PreviewBinning.Length
            ? liveState.PreviewBinning[otaIndex] : (short)1;
        bus.Post(new TakePreviewSignal(otaIndex, exp, gain, binning));
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
                    var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
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
                sb.AppendLine(LiveSessionActions.FormatFocusHistoryRow(focusHistory[i], liveState.SiteTimeZone));
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
                sb.AppendLine(LiveSessionActions.FormatExposureLogRow(log[i], liveState.SiteTimeZone));
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
        string hint;
        if (liveState.ShowAbortConfirm)
        {
            hint = " Press Enter to confirm ABORT, Escape to cancel";
        }
        else if (liveState.IsRunning)
        {
            hint = " Escape:abort";
        }
        else if (appState.ActiveProfile?.Data is { OTAs.Length: > 0 })
        {
            // Preview mode: short cheat sheet. Selected-OTA actions.
            hint = " Tab:OTA  [/]:exp  ,/.:gain  Enter:capture  S:save  P:solve  J/K:focus  Q:quit";
        }
        else
        {
            hint = " Q:quit";
        }
        _statusBar!.Text(hint);
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

    protected override void RegisterClickableRegions()
    {
        if (_infoPanel is null || _previewButtons.Count == 0)
        {
            return;
        }

        var cellSize = _infoPanel.Viewport.CellSize;
        var offset = _infoPanel.Viewport.Offset;
        var scrollOffset = _infoPanel.ScrollOffset;
        var baseX = (float)(offset.Column * cellSize.Width);
        var baseY = (float)(offset.Row * cellSize.Height);
        var visibleRows = _infoPanel.VisibleRows;

        foreach (var (line, colStart, colEnd, onClick) in _previewButtons)
        {
            var visibleLine = line - scrollOffset;
            if (visibleLine < 0 || visibleLine >= visibleRows)
            {
                continue;
            }
            var y = baseY + visibleLine * (float)cellSize.Height;
            var x = baseX + colStart * (float)cellSize.Width;
            var w = (colEnd - colStart) * (float)cellSize.Width;
            Tracker.Register(x, y, w, (float)cellSize.Height,
                new HitResult.ListItemHit("PreviewBtn", 0), onClick);
        }
    }

    public override bool HandleInput(InputEvent evt)
    {
        // Preview-mode shortcuts (only when no session is running). Handled first so
        // they don't get swallowed by the viewer keybindings below.
        if (!liveState.IsRunning && HandlePreviewInput(evt))
        {
            return false;
        }

        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (Tracker.HitTestAndDispatch(x, y) is not null)
                {
                    NeedsRedraw = true;
                }
                return false;

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

    /// <summary>
    /// Preview-mode keybindings. Returns true when the event was consumed so
    /// <see cref="HandleInput"/> can short-circuit and avoid the session/viewer paths.
    /// </summary>
    private bool HandlePreviewInput(InputEvent evt)
    {
        if (appState.ActiveProfile?.Data is not { OTAs.Length: > 0 } profileData)
        {
            return false;
        }

        var otaCount = profileData.OTAs.Length;
        var sel = Math.Clamp(liveState.SelectedPreviewOtaIndex, 0, otaCount - 1);

        switch (evt)
        {
            case InputEvent.KeyDown(InputKey.Tab, InputModifier.Shift):
                liveState.SelectedPreviewOtaIndex = (sel - 1 + otaCount) % otaCount;
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.Tab, _):
                liveState.SelectedPreviewOtaIndex = (sel + 1) % otaCount;
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.BracketLeft, _) when sel < liveState.PreviewExposureSeconds.Length:
                liveState.PreviewExposureSeconds[sel] = LiveSessionActions.StepExposure(
                    liveState.PreviewExposureSeconds[sel], direction: -1);
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.BracketRight, _) when sel < liveState.PreviewExposureSeconds.Length:
                liveState.PreviewExposureSeconds[sel] = LiveSessionActions.StepExposure(
                    liveState.PreviewExposureSeconds[sel], direction: +1);
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.Comma, _) when sel < liveState.PreviewGain.Length:
                {
                    var tel = sel < liveState.PreviewOTATelemetry.Length
                        ? liveState.PreviewOTATelemetry[sel] : PreviewOTATelemetry.Unknown;
                    liveState.PreviewGain[sel] = LiveSessionActions.StepGain(
                        liveState.PreviewGain[sel], tel, direction: -1);
                    NeedsRedraw = true;
                    return true;
                }

            case InputEvent.KeyDown(InputKey.Period, _) when sel < liveState.PreviewGain.Length:
                {
                    var tel = sel < liveState.PreviewOTATelemetry.Length
                        ? liveState.PreviewOTATelemetry[sel] : PreviewOTATelemetry.Unknown;
                    liveState.PreviewGain[sel] = LiveSessionActions.StepGain(
                        liveState.PreviewGain[sel], tel, direction: +1);
                    NeedsRedraw = true;
                    return true;
                }

            case InputEvent.KeyDown(InputKey.Enter, _):
                PostCapture(sel);
                return true;

            case InputEvent.KeyDown(InputKey.S, _) when sel < liveState.LastCapturedImages.Length
                && liveState.LastCapturedImages[sel] is not null:
                bus.Post(new SaveSnapshotSignal(sel));
                return true;

            case InputEvent.KeyDown(InputKey.P, _) when sel < liveState.LastCapturedImages.Length
                && liveState.LastCapturedImages[sel] is not null:
                bus.Post(new PlateSolvePreviewSignal(sel));
                return true;

            case InputEvent.KeyDown(InputKey.J, _):
                bus.Post(new JogFocuserSignal(sel, -50));
                return true;

            case InputEvent.KeyDown(InputKey.K, _):
                bus.Post(new JogFocuserSignal(sel, +50));
                return true;

            default:
                return false;
        }
    }
}
