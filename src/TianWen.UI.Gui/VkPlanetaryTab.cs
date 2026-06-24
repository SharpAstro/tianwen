using System;
using System.Collections.Immutable;
using DIR.Lib;
using SdlVulkan.Renderer;
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

    // Capture settings, edited by the panel steppers and posted on Start. Mutated only on the render thread
    // (in click handlers dispatched synchronously from input), so plain fields are fine.
    private static readonly double[] ExposurePresetsMs = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
    private int _exposureIdx = 4;                 // 10 ms

    private const int GainStep = 10;
    private const int GainMax = 1000;
    private int _gain = 100;

    private static readonly (int W, int H)[] RoiPresets =
        [(320, 240), (512, 512), (640, 320), (640, 480), (800, 600), (1024, 768), (1280, 960)];
    private int _roiIdx = 2;                      // 640x320

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
            _configured = true;
        }

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
        var canEdit = !capturing;

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
                        RoiWidth: RoiPresets[_roiIdx].W,
                        RoiHeight: RoiPresets[_roiIdx].H));
                }
            });

        var exposureMs = ExposurePresetsMs[_exposureIdx];
        var roi = RoiPresets[_roiIdx];

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();
        rows.Add(SectionHeader("CAPTURE"));
        rows.Add(startStop);
        // Steppers are editable only while idle (changing them mid-run does nothing until the next Start, so
        // they're disabled during capture to make that obvious; Stop to reconfigure).
        rows.Add(Stepper("Exp", $"{exposureMs:0.##} ms", canEdit, "Exposure",
            () => _exposureIdx = Math.Max(0, _exposureIdx - 1),
            () => _exposureIdx = Math.Min(ExposurePresetsMs.Length - 1, _exposureIdx + 1)));
        rows.Add(Stepper("Gain", _gain.ToString(), canEdit, "Gain",
            () => _gain = Math.Max(0, _gain - GainStep),
            () => _gain = Math.Min(GainMax, _gain + GainStep)));
        rows.Add(Stepper("ROI", $"{roi.W}x{roi.H}", canEdit, "Roi",
            () => _roiIdx = Math.Max(0, _roiIdx - 1),
            () => _roiIdx = Math.Min(RoiPresets.Length - 1, _roiIdx + 1)));
        if (capturing)
        {
            var readout = $"{controller.MeasuredFps:F0} fps   {controller.FramesReceived} frm   {controller.DroppedFrames} drop";
            rows.Add(Layout.Builder.Text(readout, PanelFontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
        }

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
        RenderLayout(panelTree, panel, fontPath, dpiScale);
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
}
