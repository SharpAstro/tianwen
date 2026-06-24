using System;
using System.Collections.Immutable;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned 🪐 planetary capture tab. It <b>is</b> the shared FITS image viewer
/// (<see cref="VkImageRenderer"/>, the same concretion <c>tianwen-fits</c> uses) embedded in the GUI
/// content area, with a thin <b>capture control strip</b> across the top. By reusing the real viewer it
/// inherits the full stretch pipeline, the RAW/STACK toggle, the 6 wavelet-sharpen sliders, white balance,
/// histogram and zoom/pan for free -- no stripped-down mini viewer, no drift between the two surfaces.
/// <para>
/// The viewer is told to arrange within a content rect (<see cref="ImageRendererBase{TSurface}.SetContentRegion"/>)
/// = the tab content area minus the capture strip; the GPU image quad still projects over the full surface.
/// Per frame: <see cref="PlanetaryCaptureController.Tick"/> publishes the latest live-stack master, then the
/// base viewer renders <see cref="PlanetaryCaptureController.Source"/>. Start/Stop are posted as signals
/// (the tab only routes; the capture wiring lives in <c>AppSignalHandler</c>).
/// </para>
/// </summary>
public sealed class VkPlanetaryTab : VkImageRenderer, IPlanetaryViewWidget
{
    // Named to avoid hiding the inherited ImageRendererBase.BaseFontSize (the viewer's chrome size); the
    // capture strip follows the GUI's standard metrics so it matches the other tabs' chrome.
    private static readonly float StripFontSize = GuiTheme.Metrics.BaseFontSize;
    private static readonly float StripPadding = GuiTheme.Metrics.Padding;
    private const float BaseStripHeight = 38f;

    private static readonly RGBAColor32 ContentBg = new RGBAColor32(0x06, 0x06, 0x08, 0xff);
    private static readonly RGBAColor32 StripBg = GuiTheme.Palette.HeaderBg;
    private static readonly RGBAColor32 HeaderText = GuiTheme.Palette.HeaderText;
    private static readonly RGBAColor32 DimText = GuiTheme.Palette.DimText;
    private static readonly RGBAColor32 StartBg = new RGBAColor32(0x1e, 0x5e, 0x2e, 0xff);
    private static readonly RGBAColor32 StopBg = new RGBAColor32(0x6e, 0x24, 0x24, 0xff);
    private static readonly RGBAColor32 ButtonText = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
    private static readonly RGBAColor32 LiveDot = new RGBAColor32(0x44, 0xdd, 0x55, 0xff);
    private static readonly RGBAColor32 IdleDot = new RGBAColor32(0x66, 0x66, 0x66, 0xff);
    private static readonly RGBAColor32 StepBtnBg = new RGBAColor32(0x2c, 0x2c, 0x33, 0xff);
    private static readonly RGBAColor32 StepBtnDisabledBg = new RGBAColor32(0x1c, 0x1c, 0x20, 0xff);

    // Capture settings, edited by the strip steppers and posted on Start. Mutated only on the render thread
    // (in click handlers dispatched synchronously from input), so plain fields are fine.
    private static readonly double[] ExposurePresetsMs = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
    private int _exposureIdx = 4;                 // 10 ms

    private const int GainStep = 10;
    private const int GainMax = 1000;
    private int _gain = 100;

    private static readonly (int W, int H)[] RoiPresets =
        [(320, 240), (512, 512), (640, 320), (640, 480), (800, 600), (1024, 768), (1280, 960)];
    private int _roiIdx = 2;                      // 640x320

    // One-time, planetary-appropriate defaults pushed onto the shared ViewerState the first time the tab
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
    /// Renders the tab: the capture strip across the top of <paramref name="contentRect"/> and the live
    /// rolling-window stack (via the shared viewer) below it. Drives <paramref name="controller"/> off the
    /// shared <paramref name="state"/> (the same DI-singleton <see cref="ViewerState"/> the controller holds,
    /// so wavelet-slider changes in the info panel reach the capture loop's sharpen).
    /// </summary>
    public void RenderTab(PlanetaryCaptureController? controller, ViewerState state,
        RectF32 contentRect, float dpiScale, string fontPath)
    {
        DpiScale = dpiScale;

        // Keep the GPU projection full-surface: the placement rects are full-surface pixel coords, and the
        // base maps [0, Width] x [0, Height] -> NDC over the whole window. Set the dimensions directly (not
        // via Resize(), whose OnResize would re-resize the shared VkRenderer the GUI already owns).
        SyncSurfaceSize();

        if (!_configured)
        {
            state.ShowStacked = true;     // RAW/STACK starts on STACK (the live lucky-imaging stack)
            state.ShowInfoPanel = true;   // wavelet-sharpen + WB sliders live in the info panel
            state.ShowFileList = false;   // a live capture has no file list
            _configured = true;
        }

        var stripH = BaseStripHeight * dpiScale;
        var stripRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, stripH);
        var viewerRect = new RectF32(contentRect.X, contentRect.Y + stripH,
            contentRect.Width, MathF.Max(0f, contentRect.Height - stripH));

        // The viewer arranges its whole chrome (toolbar / image / info panel / status bar) within this rect.
        SetContentRegion(viewerRect);

        // Dark letterbox behind everything (the viewer only paints its image quad + chrome, not the gaps).
        FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

        // Advance the live stack (publish a finished master + follow the latest captured frame), then upload
        // + render through the shared viewer -- mirroring the FITS viewer's OnRender drive sequence.
        controller?.Tick();
        var source = controller?.Source;
        if (source is not null && state.NeedsTextureUpdate)
        {
            UploadDocumentTextures(source, state);
        }

        // base.Render calls BeginFrame() (which clears the clickable tracker) before drawing the viewer
        // chrome, so the capture strip's clickable MUST be registered AFTER this call to survive.
        Render(source, state);

        RenderCaptureStrip(controller, stripRect, dpiScale, fontPath);
    }

    /// <summary>
    /// <see cref="IPlanetaryViewWidget"/> entry point used when this widget is hosted as the Live Session
    /// planetary mode (not the standalone tab). Renders against the controller's shared <see cref="ViewerState"/>.
    /// </summary>
    public void RenderPlanetary(PlanetaryCaptureController? controller, RectF32 contentRect, float dpiScale, string fontPath)
    {
        if (controller?.ViewerState is { } state)
        {
            RenderTab(controller, state, contentRect, dpiScale, fontPath);
            return;
        }

        // No capture controller wired -- draw a placeholder so the mode isn't blank.
        DpiScale = dpiScale;
        SyncSurfaceSize();
        FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);
        DrawText("Planetary capture unavailable", fontPath,
            contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
            StripFontSize * dpiScale, DimText, TextAlign.Center, TextAlign.Center);
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

    private void RenderCaptureStrip(PlanetaryCaptureController? controller, RectF32 strip, float dpiScale, string fontPath)
    {
        var fontSize = StripFontSize * dpiScale;
        var padding = StripPadding * dpiScale;

        FillRect(strip.X, strip.Y, strip.Width, strip.Height, StripBg);

        var capturing = controller?.IsCapturing ?? false;

        // Status dot.
        var dotR = 5f * dpiScale;
        var dotCx = strip.X + padding + dotR;
        var dotCy = strip.Y + strip.Height / 2f;
        FillCircle(dotCx, dotCy, dotR, capturing ? LiveDot : IdleDot);
        var x = dotCx + dotR + padding;

        if (controller is null)
        {
            DrawText("Planetary capture unavailable", fontPath, x, strip.Y, strip.Width - x, strip.Height,
                fontSize, DimText, TextAlign.Near, TextAlign.Center);
            return;
        }

        // Start/Stop toggle. Posts the configured settings (exposure / gain / ROI); AppSignalHandler wires it.
        var btnLabel = capturing ? "Stop" : "Start";
        var btnW = MathF.Max(78f * dpiScale, MeasureStripText(btnLabel, fontPath, fontSize) + padding * 3f);
        var btnH = strip.Height - padding;
        var btnY = strip.Y + (strip.Height - btnH) / 2f;
        FillRect(x, btnY, btnW, btnH, capturing ? StopBg : StartBg);
        DrawText(btnLabel, fontPath, x, btnY, btnW, btnH, fontSize, ButtonText, TextAlign.Center, TextAlign.Center);
        var isCapturing = capturing;
        RegisterClickable(x, btnY, btnW, btnH, new HitResult.ButtonHit("PlanetaryCaptureToggle"),
            _ =>
            {
                if (isCapturing)
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
        x += btnW + padding * 1.5f;

        // Exposure / Gain / ROI steppers -- editable only while idle (changing them mid-run does nothing
        // until the next Start, so they're disabled during capture to make that obvious; Stop to reconfigure).
        var canEdit = !capturing;
        var exposureMs = ExposurePresetsMs[_exposureIdx];
        x = DrawStepper(x, strip.Y, strip.Height, fontSize, padding, fontPath,
            "Exp", $"{exposureMs:0.##} ms", "888 ms", canEdit, "Exposure",
            () => _exposureIdx = Math.Max(0, _exposureIdx - 1),
            () => _exposureIdx = Math.Min(ExposurePresetsMs.Length - 1, _exposureIdx + 1));

        x = DrawStepper(x, strip.Y, strip.Height, fontSize, padding, fontPath,
            "Gain", _gain.ToString(), "8888", canEdit, "Gain",
            () => _gain = Math.Max(0, _gain - GainStep),
            () => _gain = Math.Min(GainMax, _gain + GainStep));

        var roi = RoiPresets[_roiIdx];
        x = DrawStepper(x, strip.Y, strip.Height, fontSize, padding, fontPath,
            "ROI", $"{roi.W}x{roi.H}", "8888x8888", canEdit, "Roi",
            () => _roiIdx = Math.Max(0, _roiIdx - 1),
            () => _roiIdx = Math.Min(RoiPresets.Length - 1, _roiIdx + 1));

        // Live readout, right-aligned (frames / fps / dropped while capturing).
        if (capturing)
        {
            var readout = $"{controller.MeasuredFps:F0} fps  -  {controller.FramesReceived} frames  -  {controller.DroppedFrames} dropped";
            var readoutW = MeasureStripText(readout, fontPath, fontSize);
            DrawText(readout, fontPath, strip.X + strip.Width - readoutW - padding, strip.Y, readoutW, strip.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
        }
    }

    // Draws "Label [-] value [+]" starting at x; returns the x after it. The -/+ buttons register clickables
    // (with OnClick) only when enabled; a click runs the action, and GuiEventHandlerBase sets NeedsRedraw, so
    // the new value shows next frame. Value column is sized to widthSample so the +/- don't jump as it changes.
    private float DrawStepper(float x, float y, float h, float fontSize, float padding, string fontPath,
        string label, string value, string widthSample, bool enabled, string idPrefix, Action onDec, Action onInc)
    {
        var gap = padding * 0.4f;
        var btn = h - padding;
        var btnY = y + (h - btn) / 2f;

        var labelW = MeasureStripText(label, fontPath, fontSize);
        DrawText(label, fontPath, x, y, labelW, h, fontSize, DimText, TextAlign.Near, TextAlign.Center);
        x += labelW + gap;

        DrawStepButton("-", x, btnY, btn, fontSize, fontPath, enabled);
        if (enabled)
        {
            RegisterClickable(x, btnY, btn, btn, new HitResult.ButtonHit(idPrefix + "Dec"), _ => onDec());
        }
        x += btn + gap;

        var valueW = MeasureStripText(widthSample, fontPath, fontSize);
        DrawText(value, fontPath, x, y, valueW, h, fontSize, enabled ? HeaderText : DimText, TextAlign.Center, TextAlign.Center);
        x += valueW + gap;

        DrawStepButton("+", x, btnY, btn, fontSize, fontPath, enabled);
        if (enabled)
        {
            RegisterClickable(x, btnY, btn, btn, new HitResult.ButtonHit(idPrefix + "Inc"), _ => onInc());
        }
        return x + btn + padding;
    }

    private void DrawStepButton(string glyph, float x, float y, float size, float fontSize, string fontPath, bool enabled)
    {
        FillRect(x, y, size, size, enabled ? StepBtnBg : StepBtnDisabledBg);
        DrawText(glyph, fontPath, x, y, size, size, fontSize, enabled ? ButtonText : DimText, TextAlign.Center, TextAlign.Center);
    }

    private float MeasureStripText(string text, string fontPath, float fontSize)
        => Renderer.MeasureText(text.AsSpan(), fontPath, fontSize).Width;
}
