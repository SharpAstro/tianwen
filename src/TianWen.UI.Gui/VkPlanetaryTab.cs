using System;
using System.Collections.Immutable;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned 🪐 planetary capture view. It <b>is</b> the shared FITS image viewer
/// (<see cref="VkImageRenderer"/>, the same concretion <c>tianwen-fits</c> uses) embedded in the Live
/// Session planetary mode, with a <b>left control panel</b> carved out of the content rect. By reusing the
/// real viewer it inherits the full stretch pipeline, the RAW/STACK toggle, the 6 wavelet-sharpen sliders,
/// white balance, histogram and zoom/pan for free -- no stripped-down mini viewer, no drift between the two
/// surfaces.
/// <para>
/// The viewer is told to arrange within a content rect (<see cref="ImageRendererBase{TSurface}.SetContentRegion"/>)
/// = the content area minus the left panel; the GPU image quad still projects over the full surface. Per
/// frame: <see cref="PlanetaryCaptureController.Tick"/> publishes the latest live-stack master, then the base
/// viewer renders <see cref="PlanetaryCaptureController.Source"/>, then the panel draws on top. Capture
/// (Start/Stop, exposure, gain, ROI) and the focuser jog are posted as signals (the view only routes; the
/// capture wiring lives in <c>AppSignalHandler</c>, the focuser jog reuses <c>JogFocuserSignal</c>).
/// </para>
/// <para>
/// The panel is built with the <c>DIR.Lib.Layout</c> engine (a <c>Layout.Builder</c> tree of star-weighted
/// rows), not pixel arithmetic -- placement is weights + spacers, and draw == hit by construction.
/// </para>
/// </summary>
public sealed class VkPlanetaryTab : VkImageRenderer, IPlanetaryViewWidget
{
    // Panel text follows the GUI's standard metrics (design units; RenderLayout scales by DpiScale) so it
    // matches the other tabs' chrome. Named to avoid hiding the inherited ImageRendererBase.BaseFontSize.
    private static readonly float PanelFontSize = GuiTheme.Metrics.BaseFontSize;
    private const float BasePanelWidth = 280f;
    private const float BaseRowHeight = 26f;
    private const float BasePanelPad = 8f;
    private const float BaseGap = 5f;

    // Planetary captures a single OTA; its focuser jog targets OTA index 0 (mirrors the single-OTA focus path).
    private const int PlanetaryOtaIndex = 0;

    private static readonly RGBAColor32 ContentBg = new RGBAColor32(0x06, 0x06, 0x08, 0xff);
    private static readonly RGBAColor32 PanelBg = GuiTheme.Palette.HeaderBg;
    private static readonly RGBAColor32 HeaderText = GuiTheme.Palette.HeaderText;
    private static readonly RGBAColor32 DimText = GuiTheme.Palette.DimText;
    private static readonly RGBAColor32 StartBg = new RGBAColor32(0x1e, 0x5e, 0x2e, 0xff);
    private static readonly RGBAColor32 StopBg = new RGBAColor32(0x6e, 0x24, 0x24, 0xff);
    private static readonly RGBAColor32 ButtonText = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
    private static readonly RGBAColor32 SectionText = new RGBAColor32(0x88, 0x99, 0xbb, 0xff);
    private static readonly RGBAColor32 Divider = new RGBAColor32(0x33, 0x33, 0x40, 0xff);
    private static readonly RGBAColor32 MovingText = new RGBAColor32(0xff, 0xcc, 0x66, 0xff);
    private static readonly RGBAColor32 StepBtnBg = new RGBAColor32(0x2c, 0x2c, 0x33, 0xff);
    private static readonly RGBAColor32 StepBtnDisabledBg = new RGBAColor32(0x1c, 0x1c, 0x20, 0xff);
    private static readonly RGBAColor32 JogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
    // ROI picture-in-picture + the red ROI rectangle (PiP sensor thumbnail + the on-stream overlay).
    private static readonly RGBAColor32 PipSensorBg = new RGBAColor32(0x10, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 PipSensorBorder = new RGBAColor32(0x44, 0x4a, 0x5a, 0xff);
    private static readonly RGBAColor32 RoiColor = new RGBAColor32(0xff, 0x40, 0x40, 0xff);
    private static readonly RGBAColor32 CheckOnBg = new RGBAColor32(0x1e, 0x5e, 0x2e, 0xff);

    // Capture settings, edited by the panel steppers and posted on Start. Mutated only on the render thread
    // (in click handlers dispatched synchronously from input), so plain fields are fine.
    private static readonly double[] ExposurePresetsMs = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
    private int _exposureIdx = 4;                 // 10 ms

    private const int GainStep = 10;
    private const int GainMax = 1000;
    private int _gain = 100;

    // ROI is a FREE rect within the camera's hardware constraints, not a fixed list -- these presets are just
    // snapped-to-constraint convenience sizes the size stepper cycles. The chosen rect (_roi) is snapped to the
    // connected camera's RoiConstraints (step / alignment / sensor bounds) every frame.
    private static readonly (int W, int H)[] RoiSizePresets =
        [(320, 240), (512, 512), (640, 360), (640, 480), (800, 600), (1024, 768), (1280, 960)];
    private int _roiSizeIdx = 2;                  // 640x360

    // Fallback sensor when no camera is connected yet, so the PiP still renders a plausible frame. A typical
    // small planetary CMOS (e.g. IMX585-class); replaced by the real sensor dims the moment a camera reports.
    private const int FallbackSensorW = 1936;
    private const int FallbackSensorH = 1096;

    // The current ROI selection (origin + snapped size), the sensor it was snapped against, and whether the
    // ROI rectangle is overlaid on the live stream. Render-thread only (mutated in click handlers).
    private RoiRect _roi;
    private RoiConstraints _roiConstraints = RoiConstraints.ForSensor(FallbackSensorW, FallbackSensorH);
    private int _sensorW = FallbackSensorW;
    private int _sensorH = FallbackSensorH;
    private bool _roiInitialised;
    private bool _roiOverlay = true;              // draw the ROI extent on the live stream

    // PiP drag: the sensor-thumbnail rect + sensor->pixel scale captured in DrawRoiPip, so HandleInput can
    // hit-test the PiP and map the cursor back to sensor coords to reposition the ROI. Drag is idle-only.
    private RectF32 _pipSensorRect;
    private float _pipScale;
    private bool _pipDragging;
    private bool _capturing;                      // mirrors controller.IsCapturing, for the HandleInput gate

    // The active OTA's focuser telemetry, refreshed each frame by RenderPlanetary (drives the focuser readout
    // + whether the jog row is shown). Render-thread only.
    private PreviewOTATelemetry _focuser = PreviewOTATelemetry.Unknown;

    // One-time, planetary-appropriate defaults pushed onto the shared ViewerState the first time the view
    // renders: show the live stack (RAW/STACK starts on STACK), expose the info panel (the wavelet-sharpen +
    // WB sliders live there), and never the file list. Set once so the user's later toggles stick.
    private bool _configured;

    // A planetary disk is featureless + bright: there are no stars to detect, no plate solve, no SPCC /
    // background-neutralisation, and no file to "Open" in a live capture. So the inherited viewer toolbar is
    // narrowed to the controls that actually apply -- stretch (STF / Link / Params), channel + debayer (for a
    // colour sensor), HDR, and zoom (Fit / 1:1). The wavelet-sharpen + white-balance sliders live in the info
    // panel, not the toolbar.
    private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> PlanetaryToolbarButtons =
    [
        ("STF", ToolbarAction.StretchToggle, 1),
        ("Link", ToolbarAction.StretchLink, 1),
        ("Params", ToolbarAction.StretchParams, 1),
        ("Channel", ToolbarAction.Channel, 2),
        ("Debayer", ToolbarAction.Debayer, 2),
        ("HDR", ToolbarAction.Hdr, 2),
        ("Fit", ToolbarAction.ZoomFit, 3),
        ("1:1", ToolbarAction.ZoomActual, 3),
    ];

    protected override ImmutableArray<(string Label, ToolbarAction Action, int Group)> ToolbarButtons => PlanetaryToolbarButtons;

    public VkPlanetaryTab(VkRenderer renderer, uint width, uint height) : base(renderer, width, height)
    {
    }

    /// <summary>
    /// <see cref="IPlanetaryViewWidget"/> entry point: renders the left control panel + the live
    /// rolling-window stack (via the shared viewer). Drives <paramref name="controller"/> off the shared
    /// <see cref="PlanetaryCaptureController.ViewerState"/> (the same DI-singleton the controller holds, so
    /// wavelet-slider changes in the info panel reach the capture loop's sharpen).
    /// </summary>
    public void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser,
        RectF32 contentRect, float dpiScale, string fontPath)
    {
        _focuser = focuser;
        DpiScale = dpiScale;

        // Keep the GPU projection full-surface: the placement rects are full-surface pixel coords, and the
        // base maps [0, Width] x [0, Height] -> NDC over the whole window. Set the dimensions directly (not
        // via Resize(), whose OnResize would re-resize the shared VkRenderer the GUI already owns).
        SyncSurfaceSize();

        // Dark letterbox behind everything (the viewer only paints its image quad + chrome, not the gaps).
        FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

        if (controller?.ViewerState is not { } state)
        {
            // No capture controller wired -- draw a placeholder so the mode isn't blank.
            DrawText("Planetary capture unavailable", fontPath,
                contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                PanelFontSize * dpiScale, DimText, TextAlign.Center, TextAlign.Center);
            return;
        }

        if (!_configured)
        {
            state.ShowStacked = true;     // RAW/STACK starts on STACK (the live lucky-imaging stack)
            state.ShowInfoPanel = true;   // wavelet-sharpen + WB sliders live in the info panel
            state.ShowFileList = false;   // a live capture has no file list
            // Planets are bright: a linear preview shows the disk + belts at their true relative brightness,
            // where an auto-stretch (the viewer's Unlinked default, tuned for faint deep-sky) blows the disk
            // out to flat white. The wavelet sharpen + WB still operate on the linear data.
            state.StretchMode = StretchMode.None;
            _configured = true;
        }

        // Resolve the ROI rules + sensor dims from the OTA telemetry snapshot (populated off-thread from the
        // connected camera) -- so the picker reflects the REAL camera's constraints, pre-capture, without ever
        // touching the driver on the render thread. Falls back to a plausible sensor before a camera reports.
        _capturing = controller.IsCapturing; // mirrored for the PiP-drag gate in HandleInput (idle-only)
        ResolveAndEnsureRoi();

        var panelW = MathF.Min(BasePanelWidth * dpiScale, contentRect.Width * 0.5f);
        var panelRect = new RectF32(contentRect.X, contentRect.Y, panelW, contentRect.Height);
        var viewerRect = new RectF32(contentRect.X + panelW, contentRect.Y,
            MathF.Max(0f, contentRect.Width - panelW), contentRect.Height);

        // The viewer arranges its whole chrome (toolbar / image / info panel / status bar) within this rect.
        SetContentRegion(viewerRect);

        // Advance the live stack (publish a finished master + follow the latest captured frame), then upload
        // + render through the shared viewer -- mirroring the FITS viewer's OnRender drive sequence.
        controller.Tick();
        var source = controller.Source;
        if (source is not null && state.NeedsTextureUpdate)
        {
            UploadDocumentTextures(source, state);
        }

        // base.Render calls BeginFrame() (which clears the clickable tracker) before drawing the viewer
        // chrome, so the panel's clickables MUST be registered AFTER this call to survive.
        Render(source, state);

        // The ROI overlay + panel both draw on top of the rendered viewer via FillRect (same as the panel),
        // so they survive Render. The overlay outlines the displayed frame: the live stream IS the ROI crop,
        // so the ROI rect maps to the whole displayed image (clamped to the viewer area when zoomed in).
        if (_roiOverlay && source is not null)
        {
            DrawRoiOnStream(dpiScale, fontPath);
        }

        RenderControlPanel(controller, panelRect, dpiScale, fontPath);
    }

    private void SyncSurfaceSize()
    {
        var w = (uint)Renderer.Width;
        var h = (uint)Renderer.Height;
        if (w != Width || h != Height)
        {
            Width = w;
            Height = h;
        }
    }

    // Builds + paints the left control panel as one Layout.Builder tree (a VStack of fixed-height rows). The
    // base viewer already cleared + re-armed the clickable tracker in Render(); the layout's .Clickable leaves
    // register here, after that, so they survive. Design-unit sizes/fonts are scaled by RenderLayout(dpiScale).
    private void RenderControlPanel(PlanetaryCaptureController controller, RectF32 panel, float dpiScale, string fontPath)
    {
        var capturing = controller.IsCapturing;
        // Exposure / gain / ROI size + pan are all live-tunable WHILE capturing now (just like a real
        // planetary capture), so the steppers stay enabled; a change updates the local value and, when a
        // capture is running, is pushed to the controller to take effect on the next frame.
        var canEdit = true;

        // --- Capture section ---
        var startStop = Layout.Builder.Text(capturing ? "Stop" : "Start", PanelFontSize, ButtonText, TextAlign.Center, TextAlign.Center)
            .Bg(capturing ? StopBg : StartBg).RowH(BaseRowHeight)
            .Clickable(new HitResult.ButtonHit("PlanetaryCaptureToggle"), _ =>
            {
                if (capturing)
                {
                    Bus?.Post(new StopVideoCaptureSignal());
                }
                else
                {
                    Bus?.Post(new StartVideoCaptureSignal(
                        ExposureMs: ExposurePresetsMs[_exposureIdx],
                        Gain: (short)_gain,
                        RoiWidth: _roi.Width,
                        RoiHeight: _roi.Height));
                }
            });

        var exposureMs = ExposurePresetsMs[_exposureIdx];

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();
        rows.Add(SectionHeader("CAPTURE"));
        rows.Add(startStop);
        // Steppers stay editable during capture: a change updates the local value and, if a capture is
        // running, pushes it to the controller so it takes effect on the next frame (live exposure / gain).
        rows.Add(Stepper("Exp", $"{exposureMs:0.##} ms", canEdit, "Exposure",
            () => { _exposureIdx = Math.Max(0, _exposureIdx - 1); PushExposure(controller, capturing); },
            () => { _exposureIdx = Math.Min(ExposurePresetsMs.Length - 1, _exposureIdx + 1); PushExposure(controller, capturing); }));
        rows.Add(Stepper("Gain", _gain.ToString(), canEdit, "Gain",
            () => { _gain = Math.Max(0, _gain - GainStep); if (capturing) controller.SetGain(_gain); },
            () => { _gain = Math.Min(GainMax, _gain + GainStep); if (capturing) controller.SetGain(_gain); }));
        if (capturing)
        {
            var readout = $"{controller.MeasuredFps:F0} fps   {controller.FramesReceived} frm   {controller.DroppedFrames} drop";
            rows.Add(Layout.Builder.Text(readout, PanelFontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
        }

        // --- Region of Interest section: PiP sensor thumbnail + the ROI rect, size presets (snapped to the
        // camera's RoiConstraints), pan, and the on-stream overlay toggle. All live during capture: Size
        // rebuilds the stream at the new framing on the next frame; Pan jogs the readout window (the fast,
        // mount-free recenter actuator). While idle they position the SELECTION shown in the PiP + overlay,
        // applied on the next Start.
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Spacer().RowH(1f).Bg(Divider));
        rows.Add(SectionHeader("REGION (ROI)"));
        rows.Add(Layout.Builder.Fill(key: "RoiPip").RowH(BaseRowHeight * 3f));
        rows.Add(Stepper("Size", $"{_roi.Width}x{_roi.Height}", canEdit, "RoiSize",
            () => { SetRoiSize(_roiSizeIdx - 1); if (capturing) controller.SetRoiSize(_roi.Width, _roi.Height); },
            () => { SetRoiSize(_roiSizeIdx + 1); if (capturing) controller.SetRoiSize(_roi.Width, _roi.Height); }));
        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text("Pan", PanelFontSize, DimText, TextAlign.Near, TextAlign.Center).WFixed(40f).HStar(),
                RoiPanButton("◀", "RoiPanLeft", canEdit, -1, 0, controller, capturing),
                RoiPanButton("▶", "RoiPanRight", canEdit, 1, 0, controller, capturing),
                RoiPanButton("▲", "RoiPanUp", canEdit, 0, -1, controller, capturing),
                RoiPanButton("▼", "RoiPanDown", canEdit, 0, 1, controller, capturing))
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
        rows.Add(OverlayToggleRow());

        // --- Focuser section (reuses JogFocuserSignal -- the same path the Live Session OTA panel posts) ---
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Spacer().RowH(1f).Bg(Divider));
        rows.Add(SectionHeader("FOCUSER"));
        if (_focuser.FocuserConnected)
        {
            var focLabel = $"Pos: {_focuser.FocusPosition}";
            if (!double.IsNaN(_focuser.FocuserTempC))
            {
                focLabel += $"   {_focuser.FocuserTempC:F1}°C";
            }
            if (_focuser.FocuserIsMoving)
            {
                focLabel += "   ⇄";
            }
            rows.Add(Layout.Builder.Text(focLabel, PanelFontSize, _focuser.FocuserIsMoving ? MovingText : HeaderText, TextAlign.Near, TextAlign.Center)
                .RowH(BaseRowHeight));

            // Jog row: [<<] [<] [>] [>>] -- coarse +/-100, fine +/-10 (matches the Live Session OTA panel).
            rows.Add(Layout.Builder.HStack(
                    JogButton("«", "FocCoarseIn", -100),
                    JogButton("‹", "FocFineIn", -10),
                    JogButton("›", "FocFineOut", 10),
                    JogButton("»", "FocCoarseOut", 100))
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
            rows.Add(Layout.Builder.Text("fine 10  /  coarse 100", PanelFontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center)
                .RowH(BaseRowHeight * 0.7f));
        }
        else
        {
            rows.Add(Layout.Builder.Text("No focuser connected", PanelFontSize, DimText, TextAlign.Near, TextAlign.Center)
                .RowH(BaseRowHeight));
        }

        var panelTree = Layout.Builder.VStack(rows.ToArray())
            .Pad(BasePanelPad).WithGap(BaseGap).Bg(PanelBg);
        // The panel's only Fill leaf is the ROI PiP, so the draw callback unconditionally paints it.
        RenderLayout(panelTree, panel, fontPath, dpiScale,
            drawFill: (_, r) => DrawRoiPip(r, dpiScale, fontPath));
    }

    private static Layout.Node SectionHeader(string text)
        => Layout.Builder.Text(text, PanelFontSize, SectionText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight);

    // "label [-] value [+]" as one fixed-height row. The -/+ leaves register clickables only when enabled; a
    // click runs the action and GuiEventHandlerBase sets NeedsRedraw, so the new value shows next frame.
    private Layout.Node Stepper(string label, string value, bool enabled, string idPrefix, Action onDec, Action onInc)
    {
        var btnBg = enabled ? StepBtnBg : StepBtnDisabledBg;
        var valueColor = enabled ? HeaderText : DimText;
        var dec = Layout.Builder.Text("-", PanelFontSize, enabled ? ButtonText : DimText, TextAlign.Center, TextAlign.Center)
            .WFixed(BaseRowHeight).HStar().Bg(btnBg)
            .Clickable(enabled ? new HitResult.ButtonHit(idPrefix + "Dec") : null,
                enabled ? (Action<InputModifier>)(_ => onDec()) : null);
        var inc = Layout.Builder.Text("+", PanelFontSize, enabled ? ButtonText : DimText, TextAlign.Center, TextAlign.Center)
            .WFixed(BaseRowHeight).HStar().Bg(btnBg)
            .Clickable(enabled ? new HitResult.ButtonHit(idPrefix + "Inc") : null,
                enabled ? (Action<InputModifier>)(_ => onInc()) : null);
        return Layout.Builder.HStack(
                Layout.Builder.Text(label, PanelFontSize, DimText, TextAlign.Near, TextAlign.Center).WFixed(52f).HStar(),
                dec,
                Layout.Builder.Text(value, PanelFontSize, valueColor, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                inc)
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f);
    }

    private Layout.Node JogButton(string glyph, string id, int steps)
        => Layout.Builder.Text(glyph, PanelFontSize, ButtonText, TextAlign.Center, TextAlign.Center)
            .WStar().HStar().Bg(JogBg)
            .Clickable(new HitResult.ButtonHit(id), _ => Bus?.Post(new JogFocuserSignal(PlanetaryOtaIndex, steps)));

    // --- ROI selection (4c) -------------------------------------------------------------------------------

    // Resolve the sensor dims + ROI constraints from the OTA telemetry snapshot (off-thread populated, so the
    // render thread never touches the driver), falling back to a plausible sensor until a camera reports. The
    // ROI is (re)centred on first use or a sensor change, and otherwise re-snapped so it stays legal if the
    // constraints refresh. Render-thread only.
    private void ResolveAndEnsureRoi()
    {
        int sw, sh;
        RoiConstraints c;
        if (_focuser.CameraConnected && _focuser.SensorWidth > 0 && _focuser.SensorHeight > 0)
        {
            sw = _focuser.SensorWidth;
            sh = _focuser.SensorHeight;
            // A connected camera always reports valid constraints (the DIM default is a free rect over the
            // sensor); guard against an all-zero default just in case the snapshot predates the camera read.
            c = _focuser.RoiConstraints.MaxWidth > 0 ? _focuser.RoiConstraints : RoiConstraints.ForSensor(sw, sh);
        }
        else
        {
            sw = FallbackSensorW;
            sh = FallbackSensorH;
            c = RoiConstraints.ForSensor(sw, sh);
        }

        _roiConstraints = c;
        var sensorChanged = sw != _sensorW || sh != _sensorH;
        _sensorW = sw;
        _sensorH = sh;

        if (!_roiInitialised || sensorChanged)
        {
            var p = RoiSizePresets[_roiSizeIdx];
            _roi = c.Snap(RoiRect.Centered(sw, sh, p.W, p.H));
            _roiInitialised = true;
        }
        else
        {
            _roi = c.Snap(_roi); // keep it legal against the (possibly refreshed) constraints
        }
    }

    // Cycle the size preset, snapping to the current constraints; keep the current origin (Snap re-clamps so
    // the new size still fits the sensor).
    private void SetRoiSize(int idx)
    {
        _roiSizeIdx = Math.Clamp(idx, 0, RoiSizePresets.Length - 1);
        var p = RoiSizePresets[_roiSizeIdx];
        _roi = _roiConstraints.Snap(new RoiRect(_roi.X, _roi.Y, p.W, p.H));
    }

    // One pan step in sensor pixels: ~5% of the sensor per click (at least one origin step).
    private (int Dx, int Dy) RoiPanDelta(int dirX, int dirY)
        => (dirX * Math.Max(_roiConstraints.OriginStepX, _sensorW / 20),
            dirY * Math.Max(_roiConstraints.OriginStepY, _sensorH / 20));

    // Pan the ROI selection origin (the PiP / overlay), then snap to alignment + bounds.
    private void PanRoi(int dirX, int dirY)
    {
        var (dx, dy) = RoiPanDelta(dirX, dirY);
        _roi = _roiConstraints.Snap(new RoiRect(_roi.X + dx, _roi.Y + dy, _roi.Width, _roi.Height));
    }

    private Layout.Node RoiPanButton(string glyph, string id, bool enabled, int dirX, int dirY,
        PlanetaryCaptureController controller, bool capturing)
        => Layout.Builder.Text(glyph, PanelFontSize, enabled ? ButtonText : DimText, TextAlign.Center, TextAlign.Center)
            .WStar().HStar().Bg(enabled ? JogBg : StepBtnDisabledBg)
            .Clickable(enabled ? new HitResult.ButtonHit(id) : null,
                enabled ? (Action<InputModifier>)(_ =>
                {
                    // Move the SELECTION (PiP / overlay); while capturing also jog the live readout window by
                    // the same pixel delta -- the fast, mount-free recenter actuator.
                    if (capturing)
                    {
                        var (dx, dy) = RoiPanDelta(dirX, dirY);
                        controller.JogRoi(dx, dy);
                    }
                    PanRoi(dirX, dirY);
                }) : null);

    // Pushes the currently-selected exposure preset to a running capture (no-op while idle; applied on Start).
    private void PushExposure(PlanetaryCaptureController controller, bool capturing)
    {
        if (capturing)
        {
            controller.SetExposure(TimeSpan.FromMilliseconds(ExposurePresetsMs[_exposureIdx]));
        }
    }

    // "[x] Show ROI on image" -- toggles the on-stream overlay. Allowed any time (a display toggle, not a
    // capture setting), so it is not gated on the idle state like the steppers.
    private Layout.Node OverlayToggleRow()
    {
        var label = (_roiOverlay ? "[x] " : "[ ] ") + "Show ROI on image";
        return Layout.Builder.Text(label, PanelFontSize * 0.9f, _roiOverlay ? HeaderText : DimText, TextAlign.Near, TextAlign.Center)
            .RowH(BaseRowHeight).Bg(_roiOverlay ? CheckOnBg : StepBtnBg)
            .Clickable(new HitResult.ButtonHit("RoiOverlayToggle"), _ => _roiOverlay = !_roiOverlay);
    }

    // The PiP: a sensor-proportioned thumbnail (dark box) with the red ROI rectangle drawn at its mapped
    // position -- the primary "where on the sensor does my high-speed crop sit" visualisation.
    private void DrawRoiPip(RectF32 area, float dpiScale, string fontPath)
    {
        var inset = 4f * dpiScale;
        var availW = area.Width - 2f * inset;
        var availH = area.Height - 2f * inset;
        if (_sensorW <= 0 || _sensorH <= 0 || availW <= 1f || availH <= 1f)
        {
            return;
        }

        var s = MathF.Min(availW / _sensorW, availH / _sensorH);
        var boxW = _sensorW * s;
        var boxH = _sensorH * s;
        var boxX = area.X + (area.Width - boxW) * 0.5f;
        var boxY = area.Y + (area.Height - boxH) * 0.5f;
        FillRect(boxX, boxY, boxW, boxH, PipSensorBg);
        StrokeRect(boxX, boxY, boxW, boxH, MathF.Max(1f, dpiScale), PipSensorBorder);

        // Remember the sensor box + scale so HandleInput can map a drag in the PiP back to sensor coords.
        _pipSensorRect = new RectF32(boxX, boxY, boxW, boxH);
        _pipScale = s;

        var rx = boxX + _roi.X * s;
        var ry = boxY + _roi.Y * s;
        var rw = MathF.Max(1f, _roi.Width * s);
        var rh = MathF.Max(1f, _roi.Height * s);
        StrokeRect(rx, ry, rw, rh, MathF.Max(1.5f, 1.5f * dpiScale), RoiColor);
    }

    // The on-stream overlay: outline the displayed frame (= the ROI crop) + a "ROI WxH" tag, clamped to the
    // IMAGE AREA (between the toolbar and the right info panel), so a zoomed-in image's overflow -- or a
    // letterboxed fit -- never paints over the WB / wavelet / controls panel or the chrome.
    private void DrawRoiOnStream(float dpiScale, string fontPath)
    {
        var ir = CurrentImageRect;
        if (ir.Width <= 1f || ir.Height <= 1f)
        {
            return;
        }

        var area = ImageAreaRect;
        var l = MathF.Max(ir.X, area.X);
        var t = MathF.Max(ir.Y, area.Y);
        var r = MathF.Min(ir.X + ir.Width, area.X + area.Width);
        var b = MathF.Min(ir.Y + ir.Height, area.Y + area.Height);
        if (r <= l || b <= t)
        {
            return;
        }

        StrokeRect(l, t, r - l, b - t, MathF.Max(2f, 2f * dpiScale), RoiColor);
        DrawText($"ROI {_roi.Width}x{_roi.Height}", fontPath,
            l + 4f * dpiScale, t + 3f * dpiScale, r - l, BaseRowHeight * dpiScale,
            PanelFontSize * 0.8f * dpiScale, RoiColor, TextAlign.Near, TextAlign.Near);
    }

    // A rectangle outline drawn as four thin FillRects (so it composites on top of the rendered viewer, like
    // the panel -- DrawLineOverlay is for the in-Render overlay layer, which is already closed by this point).
    private void StrokeRect(float x, float y, float w, float h, float t, RGBAColor32 color)
    {
        if (w <= 0f || h <= 0f)
        {
            return;
        }

        t = MathF.Min(t, MathF.Min(w, h));
        FillRect(x, y, w, t, color);             // top
        FillRect(x, y + h - t, w, t, color);     // bottom
        FillRect(x, y, t, h, color);             // left
        FillRect(x + w - t, y, t, h, color);     // right
    }

    /// <summary>
    /// Intercepts ROI drags inside the PiP before the base viewer. A press inside the PiP sensor thumbnail
    /// begins a drag that re-centres the ROI on the cursor (mapped to sensor coords + snapped); without this
    /// the press would fall through to the base and start an image pan (the PiP Fill leaf has no clickable).
    /// Idle-only -- the ROI is fixed during a running capture, matching the disabled pan/size steppers. All
    /// other events defer to the base (toolbar / WB + wavelet sliders / pan / zoom).
    /// </summary>
    public override bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseDown(var px, var py, _, _, _) when !_capturing && InPipSensor(px, py):
                _pipDragging = true;
                SetRoiCentreFromPip(px, py);
                return true;
            case InputEvent.MouseMove(var px, var py) when _pipDragging:
                SetRoiCentreFromPip(px, py);
                return true;
            case InputEvent.MouseUp(_, _, _) when _pipDragging:
                _pipDragging = false;
                return true;
        }

        return base.HandleInput(evt);
    }

    private bool InPipSensor(float px, float py)
        => _pipScale > 0f
           && px >= _pipSensorRect.X && px < _pipSensorRect.X + _pipSensorRect.Width
           && py >= _pipSensorRect.Y && py < _pipSensorRect.Y + _pipSensorRect.Height;

    // Map a cursor position in the PiP to sensor coords and centre the ROI on it (snapped + clamped).
    private void SetRoiCentreFromPip(float px, float py)
    {
        if (_pipScale <= 0f)
        {
            return;
        }

        var sx = (px - _pipSensorRect.X) / _pipScale;
        var sy = (py - _pipSensorRect.Y) / _pipScale;
        var x = (int)MathF.Round(sx - _roi.Width / 2f);
        var y = (int)MathF.Round(sy - _roi.Height / 2f);
        _roi = _roiConstraints.Snap(new RoiRect(x, y, _roi.Width, _roi.Height));
    }
}
