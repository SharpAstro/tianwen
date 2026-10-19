using System;
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
/// viewer renders <see cref="PlanetaryCaptureController.Source"/>, then the panel draws on top.
/// </para>
/// <para>
/// The panel is a declaration, <see cref="PlanetaryControlPanel"/> (#1612): its settings, its rows and what each control
/// posts or stages live there, so a test arranges the same tree. This view paints it and keeps what only it can draw: the
/// sensor's picture-in-picture (the panel's <see cref="PlanetaryControlPanel.PipKey"/> fill), its drag, and the ROI
/// outlined on the stream.
/// </para>
/// </summary>
public sealed class VkPlanetaryTab : VkImageRenderer, IPlanetaryViewWidget<VulkanContext>
{
    // Named to avoid hiding the inherited ImageRendererBase.BaseFontSize; design units, as the panel's text.
    private static readonly float PanelFontSize = GuiTheme.Metrics.BaseFontSize;
    private const float BaseRowHeight = 26f;

    private static RGBAColor32 ContentBg => TianWen.UI.Abstractions.GuiTheme.Palette.ContentBg;
    private static RGBAColor32 DimText => GuiTheme.Palette.DimText;
    // ROI picture-in-picture + the red ROI rectangle (PiP sensor thumbnail + the on-stream overlay).
    private static RGBAColor32 PipSensorBg => TianWen.UI.Abstractions.GuiTheme.Palette.ContentBg;
    private static RGBAColor32 PipSensorBorder => TianWen.UI.Abstractions.GuiTheme.Palette.SeparatorStrong;
    private static RGBAColor32 RoiColor => TianWen.UI.Abstractions.GuiTheme.Palette.Error;

    // The panel: the capture settings, the ROI selection, the recording and the recenter. Render-thread only.
    private readonly PlanetaryControlPanel _panel = new PlanetaryControlPanel();

    // PiP drag: the sensor-thumbnail rect + sensor->pixel scale captured in DrawRoiPip, so the PiP's press can tell a press
    // on the sensor from one beside it and map the cursor back to sensor coords to reposition the ROI.
    private RectF32 _pipSensorRect;
    private float _pipScale;

    // The active OTA's focuser telemetry, refreshed each frame by RenderPlanetary (drives the focuser readout
    // + whether the jog row is shown). Render-thread only.
    private PreviewOTATelemetry _focuser = PreviewOTATelemetry.Unknown;

    // One-time, planetary-appropriate defaults pushed onto the shared ViewerState the first time the view
    // renders: show the live stack (RAW/STACK starts on STACK), expose the info panel (the wavelet-sharpen +
    // WB sliders live there), and never the file list. Set once so the user's later toggles stick.
    private bool _configured;

    public VkPlanetaryTab(VkRenderer renderer, uint width, uint height) : base(renderer, width, height)
    {
        // Only the viewer's controls that apply to a planetary disk: ToolbarOffer.Planetary says which and why.
        Offer = ToolbarOffer.Planetary;
    }

    /// <inheritdoc/>
    public PixelWidgetBase<VulkanContext> Widget => this;

    /// <summary>
    /// <see cref="IPlanetaryViewWidget{TSurface}"/> entry point: renders the left control panel + the live
    /// rolling-window stack (via the shared viewer). Drives <paramref name="controller"/> off the shared
    /// <see cref="PlanetaryCaptureController.ViewerState"/> (the same DI-singleton the controller holds, so
    /// wavelet-slider changes in the info panel reach the capture loop's sharpen).
    /// </summary>
    public void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser,
        RectF32 contentRect)
    {
        _focuser = focuser;
        // DPI + font come from the inherited DpiScale / FontPath -- this tab is a VkImageRenderer that
        // self-resolved its font in its ctor (the hosting chrome does not push it here); the local aliases
        // keep the existing px math + draw calls below unchanged.
        var dpiScale = DpiScale;
        var fontPath = FontPath;

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
            state.ShowStacked = false;    // RAW/STACK starts on RAW: the camera now, which focusing needs; the stack trails it
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
        _panel.FollowCamera(_focuser);

        var panelW = MathF.Min(PlanetaryControlPanel.BaseWidth * dpiScale, contentRect.Width * 0.5f);
        var panelRect = new RectF32(contentRect.X, contentRect.Y, panelW, contentRect.Height);
        var viewerRect = new RectF32(contentRect.X + panelW, contentRect.Y,
            MathF.Max(0f, contentRect.Width - panelW), contentRect.Height);

        // The viewer arranges its whole chrome (toolbar / image / info panel / status bar) within this rect.
        SetContentRegion(viewerRect);

        // Advance the live stack (publish a finished master + follow the latest captured frame), then upload
        // + render through the shared viewer -- mirroring the FITS viewer's OnRender drive sequence.
        controller.Tick();
        var source = controller.Source;
        // The texture upload NeedsTextureUpdate asks for happens inside Render's PrepareFrame, before
        // anything in the frame samples the channel views; see the remarks there.

        // base.Render calls BeginFrame() (which clears the clickable tracker) before drawing the viewer
        // chrome, so the panel's clickables MUST be registered AFTER this call to survive.
        Render(source, state);

        // A reframe (#1033) draws the live window and the proposal on the full-sensor still, and takes a drag on the image to
        // move the proposal: a press region of its own over the image, painted after the viewer so its press is this one's.
        if (controller.Reframe is { } reframe && source is not null)
        {
            DrawReframe(reframe, dpiScale);
        }

        RenderControlPanel(controller, panelRect, dpiScale);
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

    // Paints the panel's tree. The base viewer already cleared + re-armed the clickable tracker in Render(); the layout's
    // .Clickable leaves register here, after that, so they survive. Design-unit sizes/fonts are scaled by RenderLayout.
    private void RenderControlPanel(PlanetaryCaptureController controller, RectF32 panel, float dpiScale)
    {
        // A PiP not drawn this frame (folded while capturing, or scrolled out of the panel) has no sensor to press on.
        _pipScale = 0f;
        var panelTree = _panel.Build(controller.Capture, _focuser, controller.RecordingTimeLeft, Bus, PressPip, controller.Reframe);
        // The panel's only Fill leaf is the ROI PiP.
        RenderLayout(panelTree, panel, scale: Scale,
            drawFill: (fill, r) =>
            {
                if (fill.Key == PlanetaryControlPanel.PipKey)
                {
                    DrawRoiPip(r, dpiScale);
                }
            });
    }

    // The PiP: a sensor-proportioned thumbnail (dark box) with the red ROI rectangle drawn at its mapped
    // position -- the primary "where on the sensor does my high-speed crop sit" visualisation.
    private void DrawRoiPip(RectF32 area, float dpiScale)
    {
        var sensorW = _panel.SensorWidth;
        var sensorH = _panel.SensorHeight;
        var inset = 4f * dpiScale;
        var availW = area.Width - 2f * inset;
        var availH = area.Height - 2f * inset;
        if (sensorW <= 0 || sensorH <= 0 || availW <= 1f || availH <= 1f)
        {
            return;
        }

        var s = MathF.Min(availW / sensorW, availH / sensorH);
        var boxW = sensorW * s;
        var boxH = sensorH * s;
        var boxX = area.X + (area.Width - boxW) * 0.5f;
        var boxY = area.Y + (area.Height - boxH) * 0.5f;
        FillRect(boxX, boxY, boxW, boxH, PipSensorBg);
        StrokeRect(boxX, boxY, boxW, boxH, MathF.Max(1f, dpiScale), PipSensorBorder);

        // Remember the sensor box + scale so the PiP's press (PressPip) can map a drag back to sensor coords.
        _pipSensorRect = new RectF32(boxX, boxY, boxW, boxH);
        _pipScale = s;

        var roi = _panel.Roi;
        var rx = boxX + roi.X * s;
        var ry = boxY + roi.Y * s;
        var rw = MathF.Max(1f, roi.Width * s);
        var rh = MathF.Max(1f, roi.Height * s);
        StrokeRect(rx, ry, rw, rh, MathF.Max(1.5f, 1.5f * dpiScale), RoiColor);
    }

    // A reframe's overlay on the full-sensor still: the window the stream runs at (dim) and the proposal (the ROI colour, tagged),
    // mapped from sensor pixels to the image as the viewer drew it and clipped to the image area, so a zoomed-in still never
    // paints over the chrome; and the image area's press, which moves the proposal under the cursor while the button is held.
    private void DrawReframe(PlanetaryReframe reframe, float dpiScale)
    {
        var ir = CurrentImageRect;
        var area = ImageAreaRect;
        if (ir.Width <= 1f || ir.Height <= 1f || area.Width <= 1f || area.Height <= 1f || reframe.SensorWidth <= 0)
        {
            return;
        }
        var scale = ir.Width / reframe.SensorWidth;

        PushClip(area.X, area.Y, area.Width, area.Height);
        try
        {
            void Outline(RoiRect window, RGBAColor32 color)
                => StrokeRect(ir.X + window.X * scale, ir.Y + window.Y * scale, MathF.Max(1f, window.Width * scale),
                    MathF.Max(1f, window.Height * scale), MathF.Max(2f, 2f * dpiScale), color);

            Outline(reframe.LiveWindow, PipSensorBorder);
            var p = reframe.Proposal;
            Outline(p, RoiColor);
            // The proposal's size, placed by the engine in the proposal's own rect on screen.
            var proposalOnScreen = new RectF32(ir.X + p.X * scale, ir.Y + p.Y * scale, MathF.Max(1f, p.Width * scale), MathF.Max(1f, p.Height * scale));
            RenderLayout(Layout.Builder.Text($"{p.Width}x{p.Height}", PanelFontSize * 0.8f, RoiColor, TextAlign.Near, TextAlign.Near)
                .PadX(4f).RowH(BaseRowHeight), proposalOnScreen, scale: Scale);
        }
        finally
        {
            PopClip();
        }

        void MoveTo(float x, float y) => reframe.CentreProposalOn((x - ir.X) / scale, (y - ir.Y) / scale);
        RenderLayout(Layout.Builder.Fill(key: ReframeKey).Pressable(new HitResult.ButtonHit(ReframeKey), press =>
            {
                MoveTo(press.X, press.Y);
                return new DragCapture(move => MoveTo(move.X, move.Y), static _ => { });
            }), area, scale: Scale, drawFill: static (_, _) => { });
    }

    /// <summary>The image area's press while a reframe is on show (#1033).</summary>
    internal const string ReframeKey = "PlanetaryReframe";

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
    /// The PiP's press, declared on the panel's PiP fill so the router hands it here: a press on the sensor thumbnail
    /// begins a drag that re-centres the ROI on the cursor (mapped to sensor coords + snapped) until the button comes up;
    /// a press beside the thumbnail is declined. Idle-only, since the panel declares the PiP only while idle: a drag
    /// moves the SELECTION the next Start takes, while a running capture's readout window moves only through the panel's
    /// Pan and Size steppers, which stage the jog on the capture as they move the selection.
    /// </summary>
    private DragCapture? PressPip(PointerPress press)
    {
        if (!InPipSensor(press.X, press.Y))
        {
            return null;
        }

        SetRoiCentreFromPip(press.X, press.Y);
        return new DragCapture(move => SetRoiCentreFromPip(move.X, move.Y), static _ => { });
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

        _panel.CentreRoiOn((px - _pipSensorRect.X) / _pipScale, (py - _pipSensorRect.Y) / _pipScale);
    }
}
