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
using TianWen.Cli.View;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

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
    ViewContexts contexts,
    IVirtualTerminal terminal,
    ITimeProvider timeProvider,
    SignalBus bus) : TuiTabBase
{
    /// <summary>The on-screen context's session state -- this tab is a pure view, so it follows
    /// whichever context is active. Resolved per use, never cached in a field.</summary>
    private LiveSessionState LiveState => contexts.Active.LiveSession;

    private const int LeftPanelCols = 40;
    private const int ProgressBarWidth = 20;
    private const int SparklineWidth = 20;
    private const string SparkChars = "▁▂▃▄▅▆▇█";

    private TextBar? _topBar;
    private TextBar? _guideBar;
    private ScrollableList<InfoRowItem>? _infoList;
    private TextBar? _statusBar;
    private TextBar? _previewToolbar;

    // Preview area — Canvas for Sixel, MarkdownWidget placeholder for non-Sixel. The Canvas and its
    // renderer are pixel-backed, so they are built on first placement and rebuilt on resize (PaintHost);
    // the viewport is interface-typed because PixelSize is a default interface member.
    private ITerminalViewport? _previewViewport;
    private Canvas? _previewCanvas;
    private SixelRgbaImageRenderer? _previewRenderer;
    private MarkdownWidget? _previewFallback;

    // Sixel preview state
    private Image? _displayedImage;
    private Task<AstroImageDocument?>? _pendingDoc;
    private AstroImageDocument? _lastDoc;

    private readonly ViewerState _viewerState = new ViewerState();

    // Canonical row list for the info panel. Each frame rebuilds into this list,
    // then ScrollableList renders. RegisterClickableRegions walks the list to
    // collect ButtonRegions and translate them into pixel hit-test zones.
    private readonly List<InfoRowItem> _rows = [];

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_guideBar), nameof(_infoList), nameof(_statusBar), nameof(_previewToolbar))]
    protected override bool IsReady =>
        _topBar is not null && _guideBar is not null && _infoList is not null && _statusBar is not null && _previewToolbar is not null;

    // Fill-leaf keys. The tree names these; PaintHost draws them.
    private const string TopKey = "top";
    private const string GuideKey = "guide";
    private const string InfoKey = "info";
    private const string ToolbarKey = "toolbar";
    private const string PreviewKey = "preview";
    private const string StatusKey = "status";

    private bool UseSixel => terminal.HasSixelSupport;

    protected override void CreateWidgets()
    {
        _topBar = new TextBar(Host(TopKey));
        _guideBar = new TextBar(Host(GuideKey));
        _statusBar = new TextBar(Host(StatusKey));
        _infoList = new ScrollableList<InfoRowItem>(Host(InfoKey));
        _previewToolbar = new TextBar(Host(ToolbarKey));

        // Preview area: Sixel-capable terminals get a Canvas, others a text fallback. The Canvas needs a
        // pixel size the arrange has not produced yet, so only its viewport is taken here -- PaintHost
        // builds the renderer and canvas on first placement and on resize.
        var previewViewport = Host(PreviewKey);
        if (UseSixel)
        {
            _previewViewport = previewViewport;
            _previewFallback = null;
        }
        else
        {
            _previewViewport = null;
            _previewFallback = new MarkdownWidget(previewViewport);
        }
    }

    /// <summary>
    /// Two header rows, then the info panel beside the preview (which carries its own toolbar row), then
    /// the status row -- the same arrangement the docked Panel produced. The Sixel and text preview are
    /// one leaf, not two branches: only what gets drawn into it differs.
    /// </summary>
    protected override Layout.Node BuildLayout() =>
        Layout.Builder.VStack(
            Layout.Builder.Fill(key: TopKey).RowH(1),
            Layout.Builder.Fill(key: GuideKey).RowH(1),
            // ColW / Stretch, not WFixed / WStar: in an HStack the cross axis is the height, and a child left
            // on Auto height gets its intrinsic one -- zero for a Fill leaf, and zero for the inner VStack's
            // whole subtree with it.
            Layout.Builder.HStack(
                Layout.Builder.Fill(key: InfoKey).ColW(LeftPanelCols),
                Layout.Builder.VStack(
                    Layout.Builder.Fill(key: ToolbarKey).RowH(1),
                    Layout.Builder.Fill(key: PreviewKey).Stretch()).Stretch()).Stretch(),
            Layout.Builder.Fill(key: StatusKey).RowH(1));

    /// <summary>
    /// Draws one hosted widget. The Sixel preview is why <paramref name="geometryChanged"/> matters: its
    /// renderer is a fixed-size pixel buffer, so it is allocated on first placement and reallocated on
    /// resize. The Panel-based version sized it once at construction and never again, so a terminal
    /// resize left the preview rendering at the old pixel size.
    /// </summary>
    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        switch (key)
        {
            case TopKey:
                _topBar?.Render();
                break;

            case GuideKey:
                _guideBar?.Render();
                break;

            case InfoKey:
                _infoList?.Render();
                break;

            case ToolbarKey:
                _previewToolbar?.Render();
                break;

            case StatusKey:
                _statusBar?.Render();
                break;

            case PreviewKey when _previewViewport is { } viewport:
                if (geometryChanged)
                {
                    var (pixelWidth, pixelHeight) = viewport.PixelSize;
                    _previewRenderer?.Dispose();
                    _previewRenderer = new SixelRgbaImageRenderer((uint)pixelWidth, (uint)pixelHeight);
                    _previewCanvas = new Canvas(viewport, _previewRenderer);
                }

                if (_previewCanvas is { } canvas && _previewRenderer is { } previewRenderer)
                {
                    if (_lastDoc is { } doc)
                    {
                        // Render directly into the canvas's surface -- ConsoleImageRenderer wraps an RgbaImageRenderer
                        var renderer = new ConsoleImageRenderer(previewRenderer);
                        renderer.RenderImage(doc, _viewerState, _viewerState.CurvesBoost);
                    }
                    canvas.Render();
                }
                break;

            case PreviewKey:
                _previewFallback?.Render();
                break;
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
        var phaseLabel = LiveSessionActions.PhaseLabel(LiveState.Phase);
        var statusText = LiveSessionActions.PhaseStatusText(LiveState, timeProvider);
        _topBar!.Text($" [{phaseLabel}]  {statusText}");

        // Right side: obs/frame/exp counter
        var obsIdx = LiveState.CurrentObservationIndex;
        var obsCount = LiveState.ActiveSession?.Observations.Count ?? 0;
        var obsInfo = $"Obs:{(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}";
        if (LiveState.ActiveObservation is { } obs)
        {
            var subSec = obs.SubExposure.TotalSeconds;
            var estimated = subSec > 0 ? (int)(obs.Duration.TotalSeconds / (subSec + 10)) : 0;
            obsInfo += $" F:{LiveState.TotalFramesWritten}/~{estimated}";
        }
        obsInfo += $" Exp:{LiveSessionActions.FormatDuration(LiveState.TotalExposureTime)}";
        _topBar.RightText(obsInfo);
    }

    private void RenderGuideBar()
    {
        _guideBar!.Text($" {LiveSessionActions.FormatGuideRms(LiveState.LastGuideStats)}");
    }

    private void RenderInfoPanel()
    {
        _rows.Clear();

        // Polar alignment mode: replaces the preview / session per-OTA panel
        // entirely with the polar-specific status + gauges. Reuses the same
        // LiveSessionState fields (PolarPhase, PolarPhaseAResult, LastPolarSolve)
        // that the GPU tab reads, so the underlying routine drives both UIs.
        if (!LiveState.IsRunning && LiveState.Mode == LiveSessionMode.PolarAlign)
        {
            BuildPolarRows();
        }
        // Preview mode: no session running + profile with OTAs. Render per-OTA
        // telemetry rows with clickable stepper cells + action buttons.
        else if (!LiveState.IsRunning
            && appState.ActiveProfile?.Data is { OTAs.Length: > 0 } profileData)
        {
            // Arrays are sized on telemetry poll; render can race ahead of the first
            // poll on the very first frame after the tab becomes active. Size them
            // here too so indexing is always safe.
            LiveState.ResizePreviewArrays(profileData.OTAs.Length);
            BuildPreviewRows(profileData);
        }
        else if (LiveState.ActiveSession is { } session)
        {
            BuildCameraRows(session);
            BuildMountRows(session);
        }
        else
        {
            _rows.Add(new TextRow("No session running"));
        }

        BuildFocusHistoryRows();
        BuildExposureLogRows();

        _infoList!.Items(_rows);
    }

    private void BuildPreviewRows(ProfileData profileData)
    {
        var otas = profileData.OTAs;
        var selectedIdx = Math.Clamp(LiveState.SelectedPreviewOtaIndex, 0, otas.Length - 1);
        LiveState.SelectedPreviewOtaIndex = selectedIdx; // snap stale value

        for (var i = 0; i < otas.Length; i++)
        {
            var capturedIdx = i;
            var isSelected = i == selectedIdx;
            var tel = i < LiveState.PreviewOTATelemetry.Length
                ? LiveState.PreviewOTATelemetry[i]
                : PreviewOTATelemetry.Unknown;

            // Header — click anywhere on it to select this OTA.
            var camName = tel.CameraConnected && !string.IsNullOrEmpty(tel.CameraDisplayName)
                ? tel.CameraDisplayName : otas[i].Name;
            _rows.Add(new HeadingRow($"{i + 1}: {camName}", isSelected,
                _ => { LiveState.SelectedPreviewOtaIndex = capturedIdx; NeedsRedraw = true; }));

            // Temp / cooler
            if (tel.CameraConnected && !double.IsNaN(tel.CcdTempC))
            {
                var setpoint = !double.IsNaN(tel.SetpointC) ? $" / {tel.SetpointC:F0}\u00b0C" : "";
                var power = !double.IsNaN(tel.CoolerPowerPct) ? $"  Pwr: {tel.CoolerPowerPct:F0}%" : "";
                _rows.Add(new TextRow($"  T: {tel.CcdTempC:F1}\u00b0C{setpoint}{power}"));
            }
            else if (!tel.CameraConnected)
            {
                _rows.Add(new TextRow("  Camera: disconnected",
                    new VtStyle(SgrColor.BrightRed, SgrColor.Black)));
            }
            else
            {
                _rows.Add(new TextRow("  T: --"));
            }

            // Focus
            if (tel.FocuserConnected)
            {
                var tempStr = !double.IsNaN(tel.FocuserTempC) ? $" @ {tel.FocuserTempC:F1}\u00b0C" : "";
                var movingStr = tel.FocuserIsMoving ? " moving" : "";
                _rows.Add(new TextRow($"  Focus: {tel.FocusPosition}{tempStr}{movingStr}"));
            }
            else
            {
                _rows.Add(new TextRow("  Focus: (no focuser)",
                    new VtStyle(SgrColor.BrightBlack, SgrColor.Black)));
            }

            // Filter (only if filter wheel is connected)
            if (tel.FilterWheelConnected)
            {
                _rows.Add(new TextRow($"  Filter: {tel.FilterName}"));
            }

            // Exposure / capture row — either a progress line or the stepper strip.
            var isCapturing = i < LiveState.PreviewCapturing.Length && LiveState.PreviewCapturing[i];
            if (isCapturing)
            {
                var start = LiveState.PreviewCaptureStart[i];
                var dur = LiveState.PreviewExposureDuration[i];
                var elapsed = timeProvider.GetUtcNow() - start;
                var elapsedSec = Math.Min(elapsed.TotalSeconds, dur.TotalSeconds);
                _rows.Add(new ProgressRow("Capturing", elapsedSec, dur.TotalSeconds));
            }
            else
            {
                var expSec = i < LiveState.PreviewExposureSeconds.Length
                    ? LiveState.PreviewExposureSeconds[i] : 5.0;
                _rows.Add(new StepperRow(
                    Label: "Exp",
                    Value: LiveSessionActions.FormatExposureLabel(expSec),
                    OnDec: _ =>
                    {
                        if (capturedIdx < LiveState.PreviewExposureSeconds.Length)
                        {
                            LiveState.PreviewExposureSeconds[capturedIdx] = LiveSessionActions.StepExposure(
                                LiveState.PreviewExposureSeconds[capturedIdx], direction: -1);
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            NeedsRedraw = true;
                        }
                    },
                    OnInc: _ =>
                    {
                        if (capturedIdx < LiveState.PreviewExposureSeconds.Length)
                        {
                            LiveState.PreviewExposureSeconds[capturedIdx] = LiveSessionActions.StepExposure(
                                LiveState.PreviewExposureSeconds[capturedIdx], direction: +1);
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            NeedsRedraw = true;
                        }
                    },
                    ActionLabel: "Capture",
                    OnAction: _ =>
                    {
                        LiveState.SelectedPreviewOtaIndex = capturedIdx;
                        PostCapture(capturedIdx);
                    },
                    ActionStyle: new VtStyle(SgrColor.BrightWhite, SgrColor.Green),
                    ValueIsOverride: true));
            }

            // Gain stepper (only when camera supports gain AND we're not mid-capture)
            var hasGain = (tel.UsesGainValue && tel.GainMax > tel.GainMin)
                || (tel.UsesGainMode && tel.GainModes.Length > 0);
            if (hasGain && !isCapturing)
            {
                var gainVal = i < LiveState.PreviewGain.Length ? LiveState.PreviewGain[i] : null;
                var gainLabel = LiveSessionActions.FormatGainLabel(gainVal, tel);
                var capturedTel = tel;
                _rows.Add(new StepperRow(
                    Label: "Gain",
                    Value: gainLabel,
                    OnDec: _ =>
                    {
                        if (capturedIdx < LiveState.PreviewGain.Length)
                        {
                            LiveState.PreviewGain[capturedIdx] = LiveSessionActions.StepGain(
                                LiveState.PreviewGain[capturedIdx], capturedTel, direction: -1);
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            NeedsRedraw = true;
                        }
                    },
                    OnInc: _ =>
                    {
                        if (capturedIdx < LiveState.PreviewGain.Length)
                        {
                            LiveState.PreviewGain[capturedIdx] = LiveSessionActions.StepGain(
                                LiveState.PreviewGain[capturedIdx], capturedTel, direction: +1);
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            NeedsRedraw = true;
                        }
                    },
                    ValueIsOverride: gainVal.HasValue));
            }

            // Action row: jog (needs focuser) + save/solve (needs a captured image).
            var hasImage = i < LiveState.LastCapturedImages.Length
                && LiveState.LastCapturedImages[i] is not null;
            if (tel.FocuserConnected || hasImage)
            {
                var buttons = new List<ActionRow.Button>();
                if (tel.FocuserConnected)
                {
                    buttons.Add(new("J-50", _ =>
                        {
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            bus.Post(new JogFocuserSignal(capturedIdx, -50));
                        },
                        new VtStyle(SgrColor.White, SgrColor.Blue)));
                    buttons.Add(new("J+50", _ =>
                        {
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            bus.Post(new JogFocuserSignal(capturedIdx, +50));
                        },
                        new VtStyle(SgrColor.White, SgrColor.Blue)));
                }
                if (hasImage)
                {
                    buttons.Add(new("Save", _ =>
                        {
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            bus.Post(new SaveSnapshotSignal(capturedIdx));
                        },
                        new VtStyle(SgrColor.BrightWhite, SgrColor.Magenta)));
                    buttons.Add(new("Solve", _ =>
                        {
                            LiveState.SelectedPreviewOtaIndex = capturedIdx;
                            bus.Post(new PlateSolvePreviewSignal(capturedIdx));
                        },
                        new VtStyle(SgrColor.BrightWhite, SgrColor.Cyan)));
                }
                _rows.Add(new ActionRow(buttons));
            }

            _rows.Add(new BlankRow());
        }
    }

    private void PostCapture(int otaIndex)
    {
        var exp = otaIndex < LiveState.PreviewExposureSeconds.Length
            ? LiveState.PreviewExposureSeconds[otaIndex] : 5.0;
        var gain = otaIndex < LiveState.PreviewGain.Length ? LiveState.PreviewGain[otaIndex] : null;
        var binning = otaIndex < LiveState.PreviewBinning.Length
            ? LiveState.PreviewBinning[otaIndex] : (short)1;
        bus.Post(new TakePreviewSignal(otaIndex, exp, gain, binning));
    }

    /// <summary>
    /// Polar-align mode side panel: phase header, status message, locked-exposure /
    /// chord-Δ readouts (Phase A complete), error gauges + ASCII direction arrows
    /// from the latest <see cref="LiveSolveResult"/>, Settled / Aligned LEDs, and
    /// Cancel / Done buttons. Mirrors the GPU side panel; no annotation overlay
    /// (TUI is text-only).
    /// </summary>
    private void BuildPolarRows()
    {
        var srcLabel = LiveState.PolarAlignUseGuider ? "Guider" : "Main";
        _rows.Add(new HeadingRow($"Polar Alignment ({srcLabel})", IsSelected: true));

        var (phaseLabel, phaseStyle) = LiveState.PolarPhase switch
        {
            PolarAlignmentPhase.Idle => ("IDLE", new VtStyle(SgrColor.BrightBlack, SgrColor.Black)),
            PolarAlignmentPhase.ProbingExposure => ("PROBING", new VtStyle(SgrColor.BrightWhite, SgrColor.Cyan)),
            PolarAlignmentPhase.Rotating => ("ROTATING", new VtStyle(SgrColor.Black, SgrColor.BrightYellow)),
            PolarAlignmentPhase.Frame2 => ("FRAME 2", new VtStyle(SgrColor.BrightWhite, SgrColor.Cyan)),
            PolarAlignmentPhase.Refining => ("REFINING", new VtStyle(SgrColor.BrightWhite, SgrColor.Green)),
            PolarAlignmentPhase.Aligned => ("ALIGNED \u2713", new VtStyle(SgrColor.Black, SgrColor.BrightGreen)),
            PolarAlignmentPhase.RestoringMount => ("RESTORING", new VtStyle(SgrColor.Black, SgrColor.BrightYellow)),
            PolarAlignmentPhase.Failed => ("FAILED", new VtStyle(SgrColor.BrightWhite, SgrColor.Red)),
            _ => ("?", new VtStyle(SgrColor.White, SgrColor.Black)),
        };
        _rows.Add(new TextRow($"  Phase: {phaseLabel}", phaseStyle));

        if (LiveState.PolarStatusMessage is { Length: > 0 } status)
        {
            _rows.Add(new TextRow($"  {status}"));
        }

        // Phase A locked exposure + chord-angle sanity readout once Phase A succeeded.
        if (LiveState.PolarPhaseAResult is { Success: true } phaseA)
        {
            _rows.Add(new BlankRow());
            _rows.Add(new TextRow(
                $"  Locked exp: {phaseA.LockedExposure.TotalMilliseconds:F0}ms  "
                + $"({phaseA.StarsMatchedFrame1}/{phaseA.StarsMatchedFrame2} \u2605)"));

            var chordObsArcsec = phaseA.ChordAngleObservedRad * 180.0 / Math.PI * 3600.0;
            var chordPredArcsec = phaseA.ChordAnglePredictedRad * 180.0 / Math.PI * 3600.0;
            var chordDiff = Math.Abs(chordObsArcsec - chordPredArcsec);
            var chordStyle = chordDiff < 5
                ? new VtStyle(SgrColor.BrightGreen, SgrColor.Black)
                : chordDiff < 30
                    ? new VtStyle(SgrColor.BrightYellow, SgrColor.Black)
                    : new VtStyle(SgrColor.BrightRed, SgrColor.Black);
            _rows.Add(new TextRow($"  Chord \u0394: {chordDiff:F1}\u2033", chordStyle));
        }

        // Live refine gauges + direction hints + LEDs.
        if (LiveState.LastPolarSolve is { } solve)
        {
            _rows.Add(new BlankRow());

            const double radToArcmin = 60.0 * 180.0 / Math.PI;
            var azArcmin = solve.SmoothedAzErrorRad * radToArcmin;
            var altArcmin = solve.SmoothedAltErrorRad * radToArcmin;

            // Az gauge: bracketed band centred on zero, character needle at the offset.
            _rows.Add(new TextRow($"  Az  {BuildErrorBar(azArcmin)} {azArcmin:+0.0;-0.0}\u2032",
                ErrorBarStyle(azArcmin)));
            _rows.Add(new TextRow($"  Alt {BuildErrorBar(altArcmin)} {altArcmin:+0.0;-0.0}\u2032",
                ErrorBarStyle(altArcmin)));

            // Direction hints — ASCII arrows so they render on every terminal,
            // including the ones that swallow the rich Unicode codepoints.
            var azHint = azArcmin >= 0 ? "-> East" : "<- West";
            var altHint = altArcmin >= 0 ? "^ Up" : "v Down";
            _rows.Add(new TextRow($"  Push: Az {azHint} {Math.Abs(azArcmin):F1}\u2032   "
                + $"Alt {altHint} {Math.Abs(altArcmin):F1}\u2032"));

            _rows.Add(new BlankRow());
            _rows.Add(new TextRow(
                $"  {solve.ExposureUsed.TotalMilliseconds:F0}ms  {solve.StarsMatched} \u2605"
                + (solve.ConsecutiveFailedSolves > 0 ? $"  ({solve.ConsecutiveFailedSolves} fail)" : "")));

            // Settled / Aligned LEDs.
            _rows.Add(new TextRow(
                $"  Settled: {(solve.IsSettled ? "[*]" : "[ ]")}    "
                + $"Aligned: {(solve.IsAligned ? "[*]" : "[ ]")}",
                solve.IsAligned && solve.IsSettled
                    ? new VtStyle(SgrColor.BrightGreen, SgrColor.Black)
                    : new VtStyle(SgrColor.White, SgrColor.Black)));
        }

        _rows.Add(new BlankRow());

        // Cancel / Done as an ActionRow so click handlers register naturally.
        var canCancel = LiveState.PolarPhase != PolarAlignmentPhase.Idle;
        var canDone = LiveState.PolarPhase == PolarAlignmentPhase.Aligned
            || (LiveState.PolarPhase == PolarAlignmentPhase.Refining
                && LiveState.LastPolarSolve is { IsSettled: true, IsAligned: true });

        var buttons = new List<ActionRow.Button>();
        if (canCancel)
        {
            buttons.Add(new("Cancel", _ => bus.Post(new CancelPolarAlignmentSignal()),
                new VtStyle(SgrColor.BrightWhite, SgrColor.Red)));
        }
        if (canDone)
        {
            buttons.Add(new("Done", _ => bus.Post(new DonePolarAlignmentSignal()),
                new VtStyle(SgrColor.BrightWhite, SgrColor.Green)));
        }
        if (buttons.Count > 0)
        {
            _rows.Add(new ActionRow(buttons));
        }
    }

    /// <summary>
    /// Build a fixed-width text gauge: 21 cells centred on zero, '#' at the
    /// scaled needle position. Full-scale is +/-30 arcmin (matches the GPU
    /// side panel so both UIs convey the same magnitude at a glance).
    /// </summary>
    private static string BuildErrorBar(double arcmin)
    {
        const int Half = 10;
        const double FullScaleArcmin = 30.0;
        var clamped = Math.Clamp(arcmin, -FullScaleArcmin, FullScaleArcmin);
        var pos = (int)Math.Round(clamped / FullScaleArcmin * Half);

        Span<char> cells = stackalloc char[Half * 2 + 1];
        cells.Fill('-');
        cells[Half] = '|';
        cells[Half + pos] = '#';
        return $"[{new string(cells)}]";
    }

    private static VtStyle ErrorBarStyle(double arcmin)
    {
        var abs = Math.Abs(arcmin);
        return abs < 1.0 ? new VtStyle(SgrColor.BrightGreen, SgrColor.Black)
            : abs < 5.0 ? new VtStyle(SgrColor.BrightYellow, SgrColor.Black)
            : new VtStyle(SgrColor.BrightRed, SgrColor.Black);
    }

    private void BuildCameraRows(ISessionTelemetry session)
    {
        var cameraStates = LiveState.CameraStates;
        for (var i = 0; i < session.TelescopeDisplays.Length; i++)
        {
            var display = session.TelescopeDisplays[i];
            _rows.Add(new HeadingRow(display.CameraName));

            // Cooling info — find latest sample + sparklines for this camera
            var coolingSamples = LiveState.CoolingSamples;
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
                _rows.Add(new TextRow(
                    $"Sensor: {s.TemperatureC:F0}\u00b0C \u2192 {s.SetpointTempC:F0}\u00b0C  {s.CoolerPowerPercent:F0}%"));

                // Temperature sparkline (last N samples for this camera)
                var tempSpark = BuildSparkline(coolingSamples, i, static c => c.TemperatureC);
                var pwrSpark = BuildSparkline(coolingSamples, i, static c => c.CoolerPowerPercent, 0, 100);
                if (tempSpark.Length > 0)
                {
                    _rows.Add(new TextRow($"Temp {tempSpark}"));
                    _rows.Add(new TextRow($"Pwr  {pwrSpark}"));
                }
            }

            // Focuser + exposure state
            if (i < cameraStates.Length)
            {
                var cs = cameraStates[i];

                var focParts = $"Focus: {cs.FocusPosition}";
                if (!double.IsNaN(cs.FocuserTemperature))
                {
                    focParts += $"  ({cs.FocuserTemperature:F1}\u00b0C)";
                }
                if (cs.FocuserIsMoving)
                {
                    focParts += "  Moving";
                }
                _rows.Add(new TextRow(focParts));

                if (cs.State == CameraState.Exposing)
                {
                    var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
                    var total = cs.SubExposure.TotalSeconds;
                    var elapsedSec = Math.Min(elapsed.TotalSeconds, total);
                    _rows.Add(new ProgressRow($"{cs.FilterName} #{cs.FrameNumber}", elapsedSec, total));
                }
                else if (cs.State is CameraState.Download or CameraState.Reading)
                {
                    _rows.Add(new TextRow($"Downloading: #{cs.FrameNumber}"));
                }
                else
                {
                    _rows.Add(new TextRow("Idle"));
                }
            }

            _rows.Add(new BlankRow());
        }
    }

    private void BuildMountRows(ISessionTelemetry session)
    {
        var ms = LiveState.MountState;
        var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
        var pier = ms.PierSide is PointingState.Normal ? "E" : ms.PierSide is PointingState.ThroughThePole ? "W" : "";
        _rows.Add(new HeadingRow($"{session.MountDisplayName}  {mountStatus}  {pier}"));

        var raStr = CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
        var decStr = CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);
        _rows.Add(new TextRow($"RA {raStr}  HA {ms.HourAngle:+0.00;-0.00}h"));
        _rows.Add(new TextRow($"Dec {decStr}"));

        if (LiveState.ActiveObservation is { Target: var target })
        {
            _rows.Add(new TextRow($"\u2192 {target.Name}",
                new VtStyle(SgrColor.BrightYellow, SgrColor.Black)));
        }
        _rows.Add(new BlankRow());
    }

    private void BuildFocusHistoryRows()
    {
        var focusHistory = LiveState.FocusHistory;
        if (focusHistory.Length == 0) return;

        _rows.Add(new HeadingRow("Focus"));
        var startIdx = Math.Max(0, focusHistory.Length - 3);
        for (var i = startIdx; i < focusHistory.Length; i++)
        {
            _rows.Add(new TextRow(
                LiveSessionActions.FormatFocusHistoryRow(focusHistory[i], LiveState.SiteTimeZone)));
        }
        _rows.Add(new BlankRow());
    }

    private void BuildExposureLogRows()
    {
        _rows.Add(new HeadingRow("Exposures"));
        var log = LiveState.ExposureLog;
        if (log.Length == 0)
        {
            _rows.Add(new TextRow("No frames yet",
                new VtStyle(SgrColor.BrightBlack, SgrColor.Black)));
            return;
        }
        _rows.Add(new TextRow("Time  Target       Filter  HFD   \u2605",
            new VtStyle(SgrColor.BrightBlack, SgrColor.Black)));
        var startIdx = Math.Max(0, log.Length - 8);
        for (var i = startIdx; i < log.Length; i++)
        {
            _rows.Add(new TextRow(
                LiveSessionActions.FormatExposureLogRow(log[i], LiveState.SiteTimeZone)));
        }
    }

    private void RenderPreview()
    {
        // Check for new frame across all cameras
        var images = LiveState.LastCapturedImages;
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
            _pendingDoc = Task.Run(async () => (AstroImageDocument?)await AstroImageDocument.AdoptImageAsync(capturedImage));
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

        // The Sixel raster is drawn from PaintHost, not here -- it needs the arranged pixel size.
        // The text fallback is a cell widget, so its content can be filled now like any other.
        if (_previewFallback is not null)
        {
            // Non-Sixel: show frame metadata
            var metrics = LiveState.LastFrameMetrics;
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
        if (LiveState.ShowAbortConfirm)
        {
            hint = " Press Enter to confirm ABORT, Escape to cancel";
        }
        else if (LiveState.IsRunning)
        {
            hint = " Escape:abort";
        }
        else if (LiveState.Mode == LiveSessionMode.PolarAlign)
        {
            var src = LiveState.PolarAlignUseGuider ? "Guider" : "Main";
            hint = $" Escape:cancel polar align ({src})  (Done button above)";
        }
        else if (appState.ActiveProfile?.Data is { OTAs.Length: > 0 })
        {
            // Preview mode: short cheat sheet. Selected-OTA actions.
            var srcHint = LiveState.PolarAlignUseGuider ? "Guider" : "Main";
            hint = $" Tab:OTA  ,/.:exp  Shift\u2190/\u2192:gain  Enter:capture  S:save  P:solve  Shift+P:polar({srcHint})  Shift+G:source  J/K:focus  Q:quit";
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

    /// <summary>
    /// The info panel's rows carry inline buttons (steppers, actions), so each row contributes column
    /// SPANS rather than one whole-row region. The list owns all the geometry.
    /// <para>
    /// This previously hardcoded a zero scroll offset, with a comment that the widget had no public
    /// <c>ScrollOffset</c>. It does, and now the list applies it -- so the panel becoming scrollable
    /// stops being a latent mis-click bug waiting for the first list long enough to need it.
    /// </para>
    /// </summary>
    protected override void RegisterClickableRegions() =>
        _infoList?.RegisterRowSpanHits(Tracker, (index, row) =>
        {
            var buttons = row.Buttons;
            if (buttons.Count == 0)
            {
                return [];
            }

            var spans = new RowSpan[buttons.Count];
            for (var i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                spans[i] = new RowSpan(button.ColStart, button.ColEnd,
                    new HitResult.ListItemHit("InfoRow", index), button.OnClick);
            }
            return spans;
        });

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (_infoList is { } list && list.HandleMouse(mouse))
        {
            NeedsRedraw = true;
            return true;
        }
        return false;
    }

    protected override void HandleTabInput(InputEvent evt){
        // Polar-align mode: Esc cancels the routine instead of falling through to
        // the abort-confirmation strip (which is a session-only concept). Done is
        // surfaced as a button on the side panel rather than a keybind because
        // accidentally pressing Enter while gauges show "aligned" should not
        // commit-and-restore — the click is a deliberate confirmation.
        if (!LiveState.IsRunning && LiveState.Mode == LiveSessionMode.PolarAlign)
        {
            switch (evt)
            {
                case InputEvent.KeyDown(InputKey.Escape, _):
                    bus.Post(new CancelPolarAlignmentSignal());
                    NeedsRedraw = true;
                    return;
            }
        }

        // Preview-mode shortcuts (only when no session is running). Handled first so
        // they don't get swallowed by the viewer keybindings below.
        if (!LiveState.IsRunning && HandlePreviewInput(evt))
        {
            return;
        }

        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (Tracker.HitTestAndDispatch(x, y) is not null)
                {
                    NeedsRedraw = true;
                }
                return;

            case InputEvent.KeyDown(InputKey.Escape, _) when LiveState.ShowAbortConfirm:
                LiveState.ShowAbortConfirm = false;
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.Enter, _) when LiveState.ShowAbortConfirm:
                bus.Post(new ConfirmAbortSessionSignal());
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl) when LiveState.IsRunning:
            case InputEvent.KeyDown(InputKey.Escape or InputKey.Q, _) when LiveState.IsRunning:
                LiveState.ShowAbortConfirm = true;
                NeedsRedraw = true;
                return; // consumed via NeedsRedraw — don't quit

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
                return;

            case InputEvent.KeyDown(InputKey.B, _):
                ViewerActions.CycleCurvesBoost(_viewerState);
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.Plus, _):
                ViewerActions.CycleStretchPreset(_viewerState);
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.Minus, _):
                ViewerActions.CycleStretchPreset(_viewerState, reverse: true);
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.F, _):
                _viewerState.ZoomToFit = true;
                NeedsRedraw = true;
                return;

            case InputEvent.KeyDown(InputKey.R, _):
                _viewerState.ZoomToFit = false;
                NeedsRedraw = true;
                return;

            default:
                return;
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
        var sel = Math.Clamp(LiveState.SelectedPreviewOtaIndex, 0, otaCount - 1);

        switch (evt)
        {
            case InputEvent.KeyDown(InputKey.Tab, InputModifier.Shift):
                LiveState.SelectedPreviewOtaIndex = (sel - 1 + otaCount) % otaCount;
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.Tab, _):
                LiveState.SelectedPreviewOtaIndex = (sel + 1) % otaCount;
                NeedsRedraw = true;
                return true;

            // Exposure: , / . (no modifier)
            case InputEvent.KeyDown(InputKey.Comma, var em1) when (em1 & InputModifier.Shift) == 0
                && sel < LiveState.PreviewExposureSeconds.Length:
                LiveState.PreviewExposureSeconds[sel] = LiveSessionActions.StepExposure(
                    LiveState.PreviewExposureSeconds[sel], direction: -1);
                NeedsRedraw = true;
                return true;

            case InputEvent.KeyDown(InputKey.Period, var em2) when (em2 & InputModifier.Shift) == 0
                && sel < LiveState.PreviewExposureSeconds.Length:
                LiveState.PreviewExposureSeconds[sel] = LiveSessionActions.StepExposure(
                    LiveState.PreviewExposureSeconds[sel], direction: +1);
                NeedsRedraw = true;
                return true;

            // Gain: Shift+Left / Shift+Right (shift distinguishes from any future
            // plain arrow-key navigation we might add later).
            case InputEvent.KeyDown(InputKey.Left, var gm1) when (gm1 & InputModifier.Shift) != 0
                && sel < LiveState.PreviewGain.Length:
                {
                    var tel = sel < LiveState.PreviewOTATelemetry.Length
                        ? LiveState.PreviewOTATelemetry[sel] : PreviewOTATelemetry.Unknown;
                    LiveState.PreviewGain[sel] = LiveSessionActions.StepGain(
                        LiveState.PreviewGain[sel], tel, direction: -1);
                    NeedsRedraw = true;
                    return true;
                }

            case InputEvent.KeyDown(InputKey.Right, var gm2) when (gm2 & InputModifier.Shift) != 0
                && sel < LiveState.PreviewGain.Length:
                {
                    var tel = sel < LiveState.PreviewOTATelemetry.Length
                        ? LiveState.PreviewOTATelemetry[sel] : PreviewOTATelemetry.Unknown;
                    LiveState.PreviewGain[sel] = LiveSessionActions.StepGain(
                        LiveState.PreviewGain[sel], tel, direction: +1);
                    NeedsRedraw = true;
                    return true;
                }

            case InputEvent.KeyDown(InputKey.Enter, _):
                PostCapture(sel);
                return true;

            case InputEvent.KeyDown(InputKey.S, _) when sel < LiveState.LastCapturedImages.Length
                && LiveState.LastCapturedImages[sel] is not null:
                bus.Post(new SaveSnapshotSignal(sel));
                return true;

            // Shift+P: toggle polar-align mode (start a routine on the selected
            // OTA, or cancel one already running). Plain P plate-solves the
            // current preview frame, so the modifier disambiguates. The polar
            // source (main camera vs guider) is read from
            // <see cref="LiveSessionState.PolarAlignUseGuider"/>, toggled via
            // Shift+G below.
            case InputEvent.KeyDown(InputKey.P, var pmod) when (pmod & InputModifier.Shift) != 0:
                if (LiveState.Mode == LiveSessionMode.PolarAlign)
                {
                    bus.Post(new CancelPolarAlignmentSignal());
                }
                else
                {
                    bus.Post(new StartPolarAlignmentSignal(
                        OtaIndex: sel,
                        DeltaRaDeg: 45.0,
                        UseGuider: LiveState.PolarAlignUseGuider));
                }
                NeedsRedraw = true;
                return true;

            // Shift+G: toggle polar-align capture source between main camera and
            // guider. No effect while the routine is running (mid-run swap would
            // invalidate the v1 anchor frame).
            case InputEvent.KeyDown(InputKey.G, var gmod) when (gmod & InputModifier.Shift) != 0:
                if (LiveState.Mode != LiveSessionMode.PolarAlign)
                {
                    LiveState.PolarAlignUseGuider = !LiveState.PolarAlignUseGuider;
                    NeedsRedraw = true;
                }
                return true;

            case InputEvent.KeyDown(InputKey.P, _) when sel < LiveState.LastCapturedImages.Length
                && LiveState.LastCapturedImages[sel] is not null:
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
