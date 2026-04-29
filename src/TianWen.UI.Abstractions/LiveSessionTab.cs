using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic live session monitor tab. Shows session phase, timeline,
    /// per-OTA panels with exposure countdown, guide graph, and exposure log.
    /// </summary>
    public class LiveSessionTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        /// <summary>The live session state for keyboard handling. Set during Render.</summary>
        public LiveSessionState? State { get; set; }

        /// <summary>Optional mini viewer widget for showing the last captured frame. Set by the host.</summary>
        public IMiniViewerWidget? MiniViewer { get; set; }

        /// <summary>Tracks which image reference is currently displayed to avoid redundant uploads.</summary>
        private Image? _displayedImage;

        /// <summary>Last mouse position for drag panning.</summary>
        private (float X, float Y)? _dragStart;

        /// <summary>Cached mini viewer image rect for center-point zoom.</summary>
        private RectF32 _viewerImageRect;

        // Layout constants (at 1x scale)
        private const float BaseFontSize       = 14f;
        private const float BaseTopStripHeight = 28f;
        private const float BaseTimelineHeight = 48f;
        private const float BaseBotStripHeight = 100f;
        private const float BaseOtaPanelW       = 240f;
        private const float BaseRightPanelW    = 325f;
        private const float BaseGuideRmsH      = 20f;
        private const float BasePadding        = 6f;
        private const float BaseRowHeight      = 20f;
        private const float BaseProgressBarH   = 14f;

        // Colors
        private static readonly RGBAColor32 ContentBg        = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg          = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg         = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText       = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText         = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText          = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 BrightText       = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 SeparatorColor   = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 GraphBg          = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
        private static readonly RGBAColor32 RaColor          = new RGBAColor32(0x44, 0x88, 0xff, 0xff); // blue
        private static readonly RGBAColor32 DecColor         = new RGBAColor32(0xff, 0x88, 0x44, 0xff); // orange
        private static readonly RGBAColor32 AbortBg          = new RGBAColor32(0xcc, 0x33, 0x33, 0xff);
        private static readonly RGBAColor32 AbortText        = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 ConfirmStripBg   = new RGBAColor32(0x88, 0x22, 0x22, 0xff);
        private static readonly RGBAColor32 RowAltBg         = new RGBAColor32(0x1a, 0x1a, 0x24, 0xff);
        private static readonly RGBAColor32 ProgressBg       = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
        private static readonly RGBAColor32 ProgressFill     = new RGBAColor32(0x30, 0x88, 0x30, 0xff);
        private static readonly RGBAColor32 NowNeedleColor   = new RGBAColor32(0xff, 0xff, 0xff, 0xcc);
        private static readonly RGBAColor32 TimelineBg       = new RGBAColor32(0x18, 0x18, 0x22, 0xff);
        private static readonly RGBAColor32 TimelineTickColor = new RGBAColor32(0x55, 0x55, 0x66, 0xff);
        private static readonly RGBAColor32 StatusSlewing    = new RGBAColor32(0xcc, 0xcc, 0x44, 0xff); // yellow
        private static readonly RGBAColor32 StatusSolving    = new RGBAColor32(0x44, 0xaa, 0xcc, 0xff); // cyan
        private static readonly RGBAColor32 StatusTracking   = new RGBAColor32(0x44, 0xcc, 0x44, 0xff); // green
        private static readonly RGBAColor32 VCurveDotColor   = new RGBAColor32(0x44, 0xcc, 0xff, 0xff); // cyan dots
        private static readonly RGBAColor32 VCurveFitColor   = new RGBAColor32(0xff, 0x66, 0x33, 0xcc); // orange-red fit curve
        private static readonly RGBAColor32 VCurveBestColor  = new RGBAColor32(0x44, 0xff, 0x44, 0xaa); // green best-focus line
        private static readonly RGBAColor32 VCurveAxisColor  = new RGBAColor32(0x44, 0x44, 0x55, 0xff); // dim axis lines

        // Per-camera color palette (temp = solid, power = same hue lighter)
        private static readonly RGBAColor32[] CameraTempColors =
        [
            new RGBAColor32(0x44, 0x88, 0xff, 0xff), // blue
            new RGBAColor32(0x44, 0xcc, 0x44, 0xff), // green
            new RGBAColor32(0xcc, 0x44, 0xcc, 0xff), // magenta
            new RGBAColor32(0xff, 0xcc, 0x44, 0xff), // yellow
        ];
        private static readonly RGBAColor32[] CameraPowerColors =
        [
            new RGBAColor32(0xff, 0x66, 0x44, 0xff), // orange-red
            new RGBAColor32(0xff, 0x88, 0x66, 0xff), // light orange
            new RGBAColor32(0xff, 0x66, 0x88, 0xff), // pink
            new RGBAColor32(0xff, 0xaa, 0x66, 0xff), // peach
        ];

        /// <summary>Render the complete live session tab.</summary>
        public void Render(
            LiveSessionState state,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            ITimeProvider timeProvider)
        {
            State = state;
            BeginFrame();

            var fs = BaseFontSize * dpiScale;
            var topH = BaseTopStripHeight * dpiScale;
            var timelineH = BaseTimelineHeight * dpiScale;
            var botH = BaseBotStripHeight * dpiScale;
            var rightW = BaseRightPanelW * dpiScale;
            var pad = BasePadding * dpiScale;
            var rowH = BaseRowHeight * dpiScale;

            // Background
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Top strip: phase pill + activity + clock
            var topRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, topH);
            RenderTopStrip(state, topRect, fontPath, fs, dpiScale, timeProvider);

            // Timeline: phase bars + observation segments + now needle
            var timelineRect = new RectF32(contentRect.X, contentRect.Y + topH, contentRect.Width, timelineH);
            RenderTimeline(state, timelineRect, fontPath, fs, dpiScale, timeProvider);

            // Bottom strip: compact guide graph + RMS + ABORT
            var botRect = new RectF32(contentRect.X, contentRect.Y + contentRect.Height - botH, contentRect.Width, botH);
            RenderBottomStrip(state, botRect, fontPath, fs, dpiScale, pad, timeProvider);

            // Main area between timeline and bottom strip
            var mainY = contentRect.Y + topH + timelineH;
            var mainH = contentRect.Height - topH - timelineH - botH;

            // Layout dimensions
            var otaCount = state.OtaCount;
            var otaTotalW = BaseOtaPanelW * dpiScale * otaCount;
            var logW = BaseRightPanelW * dpiScale;
            var viewerX = contentRect.X + otaTotalW;
            var viewerW = contentRect.Width - otaTotalW - logW;

            // Center: mini viewer — rendered FIRST so panels paint over any overflow
            if (viewerW > 100 && MiniViewer is { } viewer)
            {
                // Polar mode publishes a refreshed WcsAnnotation each frame from the
                // latest live solve so the renderer can overlay the pole crosses,
                // 5'/15'/30' rings, and axis CircledCross on the live frame. Before
                // the first Phase A solve completes (LastPolarSolve still null),
                // fall back to the lightweight "J2000 pole preview" so the user
                // already sees the crosshair + 30' ring while the rotation runs.
                // Cleared when polar mode exits so the annotation doesn't leak.
                if (state.Mode == LiveSessionMode.PolarAlign)
                {
                    if (state.LastPolarSolve is { Overlay: { } overlay })
                    {
                        viewer.State.Annotation = PolarAnnotationBuilder.Build(overlay);
                    }
                    else if (state.PreviewPlateSolveResult?.Solution is { } wcs)
                    {
                        viewer.State.Annotation = PolarAnnotationBuilder.BuildJ2000PolePreview(wcs.CenterDec);
                    }
                    else
                    {
                        viewer.State.Annotation = WcsAnnotation.Empty;
                    }
                    // Probe rungs ramp 100ms -> 5000ms; the 50x exposure swing
                    // would whip the auto-stretch through wildly different
                    // mid-tones each frame and pulse the histogram (300ms)
                    // on the UI thread for every push. Pin stretch on first
                    // frame and reuse for the whole polar-align session.
                    viewer.State.FreezeStretchStats = true;
                }
                else
                {
                    viewer.State.Annotation = WcsAnnotation.Empty;
                    viewer.State.FreezeStretchStats = false;
                }

                // Check if a new frame arrived for the selected camera
                var images = state.LastCapturedImages;
                var selectedIdx = viewer.State.SelectedCameraIndex;
                Image? latestImage = null;
                if (selectedIdx >= 0 && selectedIdx < images.Length)
                {
                    latestImage = images[selectedIdx];
                }
                else
                {
                    // Auto: show first available
                    for (var i = 0; i < images.Length; i++)
                    {
                        if (images[i] is { } img)
                        {
                            latestImage = img;
                            break;
                        }
                    }
                }

                if (latestImage is not null && !ReferenceEquals(latestImage, _displayedImage))
                {
                    _displayedImage = latestImage;
                    viewer.QueueImage(latestImage);
                    // A fresh frame invalidates any prior solve until the user
                    // requests a new plate solve. Without this, the grid would
                    // be drawn over a frame the WCS doesn't actually describe.
                    viewer.Wcs = null;
                }

                // Bind the live preview's plate-solve result whenever the displayed
                // frame is the one that produced it. The signal handler stores the
                // solve into LiveSessionState; we just hand it down to the renderer
                // so the GLSL grid path can switch on without a separate plumbing
                // pass each frame.
                if (state.PreviewPlateSolveResult?.Solution is { } solveWcs
                    && _displayedImage is not null
                    && ReferenceEquals(_displayedImage, latestImage))
                {
                    viewer.Wcs = solveWcs;
                }

                // Image area (below where toolbar will go)
                var toolbarH = BaseRowHeight * dpiScale;
                var imageRect = new RectF32(viewerX, mainY + toolbarH, viewerW, mainH - toolbarH);
                _viewerImageRect = imageRect;
                viewer.Render(imageRect, Renderer.Width, Renderer.Height);
            }
            else if (viewerW > 0)
            {
                FillRect(viewerX, mainY, viewerW, mainH, GraphBg);
                var placeholderText = state.IsRunning && state.Phase is SessionPhase.Observing
                    ? "Waiting for first frame\u2026"
                    : !state.IsRunning
                        ? "Take a preview exposure \u2192"
                        : null;
                if (placeholderText is not null)
                {
                    DrawText(placeholderText, fontPath,
                        viewerX, mainY, viewerW, mainH,
                        fs, DimText, TextAlign.Center, TextAlign.Center);
                }
            }

            // Left: per-OTA panels (paints over viewer overflow on the left)
            var otaRect = new RectF32(contentRect.X, mainY, otaTotalW, mainH);
            RenderOTAPanels(state, otaRect, fontPath, fs, dpiScale, pad, rowH, timeProvider);

            // Right: exposure log (paints over viewer overflow on the right)
            var logX = contentRect.X + contentRect.Width - logW;
            if (logW > 0)
            {
                var rightRect = new RectF32(logX, mainY, logW, mainH);
                RenderExposureLog(state, rightRect, fontPath, fs, pad, rowH);
            }

            // Mini viewer toolbar (on top of the image, after panels)
            if (viewerW > 100 && MiniViewer is { } viewer2)
            {
                var toolbarH = BaseRowHeight * dpiScale;
                var toolbarRect = new RectF32(viewerX, mainY, viewerW, toolbarH);
                RenderMiniViewerToolbar(viewer2.State, toolbarRect, fontPath, fs, dpiScale);
            }

            // Abort confirmation overlay
            if (state.ShowAbortConfirm)
            {
                RenderAbortConfirm(contentRect, fontPath, fs * 1.1f, dpiScale);
            }

            // Mode dropdown overlay -- rendered LAST so its hit regions win paint-order
            // hit testing. Backdrop closes on click-outside. Width and anchor are set
            // by the trigger inside RenderTopStrip.
            if (state.ModeDropdown.IsOpen)
            {
                var dropdownBg = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
                var highlight  = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
                var border     = new RGBAColor32(0x44, 0x44, 0x55, 0xff);
                RenderDropdownMenu(state.ModeDropdown, fontPath, fs,
                    bgColor: dropdownBg,
                    highlightColor: highlight,
                    textColor: BodyText,
                    borderColor: border,
                    viewportWidth: Renderer.Width,
                    viewportHeight: Renderer.Height);
            }
        }

        // -----------------------------------------------------------------------
        // Mini viewer toolbar: [Fit] [1:1] [T] [S] [B]
        // -----------------------------------------------------------------------

        private void RenderMiniViewerToolbar(MiniViewerState vs, RectF32 rect, string fontPath, float fontSize, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var btnW = 36f * dpiScale;
            var btnH = rect.Height - 4 * dpiScale;
            var btnY = rect.Y + 2 * dpiScale;
            var btnFs = fontSize * 0.8f;
            var x = rect.X + pad;

            var activeBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);

            // [Fit] — zoom to fit
            RenderButton("Fit", x, btnY, btnW, btnH, fontPath, btnFs,
                vs.ZoomToFit ? activeBg : inactiveBg, BodyText, "ViewerFit",
                _ => { vs.ZoomToFit = true; });
            x += btnW + pad;

            // [1:1] — actual pixels
            RenderButton("1:1", x, btnY, btnW, btnH, fontPath, btnFs,
                !vs.ZoomToFit && MathF.Abs(vs.Zoom - 1f) < 0.01f ? activeBg : inactiveBg, BodyText, "Viewer1to1",
                _ => { vs.ZoomToFit = false; vs.Zoom = 1f; vs.PanOffset = (0, 0); });
            x += btnW + pad;

            // [T] — cycle stretch mode
            var stretchLabel = vs.StretchMode switch
            {
                StretchMode.None => "Raw",
                StretchMode.Linked => "Lnk",
                StretchMode.Unlinked => "Unl",
                StretchMode.Luma => "Lum",
                _ => "T"
            };
            RenderButton(stretchLabel, x, btnY, btnW, btnH, fontPath, btnFs,
                vs.StretchMode is not StretchMode.None ? activeBg : inactiveBg, BodyText, "ViewerStretch",
                _ => { vs.CycleStretch(); });
            x += btnW + pad;

            // [S] — cycle stretch preset
            var presetLabel = $"{vs.StretchParameters}";
            RenderButton("S", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                inactiveBg, BodyText, "ViewerPreset",
                _ => { vs.CycleStretchPreset(); });
            x += btnW * 0.8f + pad;

            // [B] — cycle boost
            var boostActive = vs.CurvesBoost > 0;
            RenderButton("B", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                boostActive ? activeBg : inactiveBg, BodyText, "ViewerBoost",
                _ => { vs.CycleBoost(); });
            x += btnW * 0.8f + pad;

            // [G] -- WCS coordinate grid overlay. Enabled only once the preview frame
            // has been plate-solved (we need a WCS to project RA/Dec lines). Lit when
            // active. Polar-alignment mode switching now lives on the top-strip mode
            // pill dropdown -- the toolbar stays focused on viewer chrome.
            if (State is { } liveState && MiniViewer is { State: { } miniState })
            {
                var hasWcs = liveState.PreviewPlateSolveResult?.Solution is not null;
                var gridActive = miniState.ShowGrid;
                var gridBg = gridActive ? activeBg : inactiveBg;
                var gridFg = hasWcs || gridActive ? BodyText : DimText;
                RenderButton("G", x, btnY, btnW * 0.6f, btnH, fontPath, btnFs,
                    gridBg, gridFg, "ViewerGrid",
                    _ =>
                    {
                        if (hasWcs)
                        {
                            miniState.ShowGrid = !miniState.ShowGrid;
                            liveState.NeedsRedraw = true;
                        }
                    });
                x += btnW * 0.6f + pad;
            }

            // OTA selector buttons (right-aligned) — works in both session and preview mode
            var otaButtonCount = State?.OtaCount ?? 0;
            if (otaButtonCount > 1)
            {
                var otaBtnX = rect.X + rect.Width - (btnW * 0.8f + pad) * otaButtonCount - pad;
                for (var oi = 0; oi < otaButtonCount; oi++)
                {
                    var idx = oi; // capture
                    var isSelected = vs.SelectedCameraIndex == idx;
                    RenderButton($"#{idx + 1}", otaBtnX, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                        isSelected ? activeBg : inactiveBg, BodyText, $"ViewerOTA{idx}",
                        _ => { vs.SelectedCameraIndex = vs.SelectedCameraIndex == idx ? -1 : idx; });
                    otaBtnX += btnW * 0.8f + pad;
                }
            }

            // Status text: stretch info
            var infoText = $"{vs.StretchMode} {vs.StretchParameters}";
            if (vs.CurvesBoost > 0)
            {
                infoText += $" Boost:{vs.CurvesBoost:F2}";
            }
            var infoW = rect.X + rect.Width - x - pad;
            if (otaButtonCount > 1)
            {
                infoW -= (btnW * 0.8f + pad) * otaButtonCount;
            }
            DrawText(infoText, fontPath,
                x, rect.Y, infoW, rect.Height,
                fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center);
        }

        /// <inheritdoc/>
        public override bool HandleInput(InputEvent evt)
        {
            if (State is not { } state)
            {
                return false;
            }

            switch (evt)
            {
                case InputEvent.KeyDown(InputKey.Escape, _) when state.ShowAbortConfirm:
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Enter, _) when state.ShowAbortConfirm:
                    PostSignal(new ConfirmAbortSessionSignal());
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.IsRunning:
                    state.ShowAbortConfirm = true;
                    state.NeedsRedraw = true;
                    return true;

                // Polar-align fake-mount jog: arrow keys nudge simulated (az, alt)
                // misalignment by 1' (5' with Shift). Az on Left/Right, Alt on
                // Up/Down. Only active in PolarAlign mode so the keys are free
                // for other purposes elsewhere. The signal is a no-op when the
                // connected mount isn't a FakeSkywatcherMountDriver, so this
                // is safe to leave wired up unconditionally.
                case InputEvent.KeyDown(InputKey.Left, var ml) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (ml & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(-step, 0));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Right, var mr) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (mr & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(+step, 0));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Up, var mu) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (mu & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(0, +step));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Down, var md) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (md & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(0, -step));
                    return true;
                }

                case InputEvent.Scroll(var scrollY, var mx, var my, _) when MiniViewer is { State: { ZoomToFit: false } vs }:
                {
                    // Center-point zoom toward cursor position
                    var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
                    var oldZoom = vs.Zoom;
                    var newZoom = MathF.Max(0.1f, MathF.Min(oldZoom * zoomFactor, 16f));

                    // Cursor position relative to viewer center + pan offset
                    var cx = mx - _viewerImageRect.X - _viewerImageRect.Width * 0.5f - vs.PanOffset.X;
                    var cy = my - _viewerImageRect.Y - _viewerImageRect.Height * 0.5f - vs.PanOffset.Y;

                    // Adjust pan so the image point under the cursor stays fixed
                    vs.PanOffset = (
                        vs.PanOffset.X - cx * (newZoom / oldZoom - 1f),
                        vs.PanOffset.Y - cy * (newZoom / oldZoom - 1f)
                    );
                    vs.Zoom = newZoom;
                    state.NeedsRedraw = true;
                    return true;
                }

                case InputEvent.Scroll(var scrollY2, _, _, _):
                    state.ExposureLogScrollOffset = Math.Max(0, state.ExposureLogScrollOffset + (scrollY2 > 0 ? -1 : 1));
                    state.NeedsRedraw = true;
                    return true;

                // Mini viewer mouse drag for panning
                case InputEvent.MouseDown(var mx, var my, _, _, _) when MiniViewer is { State: { ZoomToFit: false } }:
                    _dragStart = (mx, my);
                    return true;

                case InputEvent.MouseMove(var mx, var my) when _dragStart is { } drag && MiniViewer is { State: { } vs }:
                    var dx = mx - drag.X;
                    var dy = my - drag.Y;
                    vs.PanOffset = (vs.PanOffset.X + dx, vs.PanOffset.Y + dy);
                    _dragStart = (mx, my);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.MouseUp(_, _, _):
                    if (_dragStart is not null)
                    {
                        _dragStart = null;
                        return true;
                    }
                    return false;

                // Mini viewer keyboard shortcuts
                case InputEvent.KeyDown(InputKey.F, _) when MiniViewer is { State: { } vs }:
                    vs.ZoomToFit = true;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.R, _) when MiniViewer is { State: { } vs }:
                    vs.ZoomToFit = false;
                    vs.Zoom = 1f;
                    vs.PanOffset = (0, 0);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.T, _) when MiniViewer is { State: { } vs }:
                    vs.CycleStretch();
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.B, _) when MiniViewer is { State: { } vs }:
                    vs.CycleBoost();
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.S, _) when MiniViewer is { State: { } vs }:
                    vs.CycleStretchPreset();
                    state.NeedsRedraw = true;
                    return true;

                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Top strip: phase pill + activity + progress + clock
        // -----------------------------------------------------------------------

        private void RenderTopStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var pillW = 140f * dpiScale;
            var pillH = rect.Height - pad * 2;

            if (!state.IsRunning)
            {
                // Mode pill -- doubles as a dropdown trigger so the user can switch
                // between Preview and Polar Align without hunting for a separate
                // toolbar button. The polar-align entry posts the standard signal
                // (re-validated by AppSignalHandler); selecting Preview while polar
                // is active posts a Cancel. Caret hint indicates the click affordance.
                var inPolar = state.Mode == LiveSessionMode.PolarAlign;
                var modePillColor = inPolar
                    ? StatusSolving                                    // cyan while PA running
                    : new RGBAColor32(0x55, 0x33, 0x88, 0xff);         // purple for plain Preview
                var pillLabel = inPolar ? "POLAR \u25BE" : "PREVIEW \u25BE";
                var pillX = rect.X + pad;
                var pillY = rect.Y + pad;
                FillRect(pillX, pillY, pillW, pillH, modePillColor);
                DrawText(pillLabel, fontPath,
                    pillX, rect.Y, pillW, rect.Height,
                    fontSize * 0.9f, AbortText, TextAlign.Center, TextAlign.Center);

                // Click-to-open: anchor the dropdown directly under the pill.
                var dropdown = state.ModeDropdown;
                RegisterClickable(pillX, pillY, pillW, pillH, new HitResult.ButtonHit("ModePill"),
                    _ =>
                    {
                        if (dropdown.IsOpen)
                        {
                            dropdown.Close();
                            return;
                        }
                        dropdown.Open(
                            pillX, pillY + pillH, pillW,
                            ImmutableArray.Create("Preview", "Polar Align"),
                            (idx, _) =>
                            {
                                if (idx == 0 && inPolar)
                                {
                                    if (state.PolarAlignmentCts is null && state.PolarPhase == PolarAlignmentPhase.Idle)
                                    {
                                        // Setup phase: no routine running, just flip back
                                        // to Preview without going through the cancel
                                        // signal (which would try to abort a non-existent
                                        // CTS and leave the user in an awkward "cancelling
                                        // forever" state).
                                        state.Mode = LiveSessionMode.Preview;
                                        state.PolarStatusMessage = "";
                                        state.NeedsRedraw = true;
                                    }
                                    else
                                    {
                                        PostSignal(new CancelPolarAlignmentSignal());
                                    }
                                }
                                else if (idx == 1 && !inPolar)
                                {
                                    var (polarEnabled, polarReason) = EvaluatePolarPreconditions(state);
                                    if (polarEnabled)
                                    {
                                        // Switch into PolarAlign mode but DON'T fire
                                        // StartPolarAlignmentSignal yet -- the setup panel
                                        // (rendered in PolarPhase.Idle) lets the user review
                                        // / edit the configuration before committing. The
                                        // panel's Start button posts the signal with
                                        // state.PolarSetupConfig as the captured config.
                                        if (MiniViewer?.State is { } ms)
                                        {
                                            // Auto-enable the WCS grid -- once the first probe
                                            // frame plate-solves, the grid will appear and the
                                            // user sees meridians converging on the celestial
                                            // pole alongside the configured-axis ring.
                                            ms.ShowGrid = true;
                                        }
                                        state.Mode = LiveSessionMode.PolarAlign;
                                        state.PolarPhase = PolarAlignmentPhase.Idle;
                                        state.PolarStatusMessage = "Configure and click Start";
                                        state.NeedsRedraw = true;
                                    }
                                    else
                                    {
                                        state.PolarStatusMessage = polarReason;
                                        state.NeedsRedraw = true;
                                    }
                                }
                            });
                        state.NeedsRedraw = true;
                    });

                // Current time (right side)
                var timeText = timeProvider.GetUtcNow().ToOffset(state.SiteTimeZone).ToString("HH:mm:ss");
                DrawText(timeText, fontPath,
                    rect.X + rect.Width - 120f * dpiScale, rect.Y, 116f * dpiScale, rect.Height,
                    fontSize, DimText, TextAlign.Far, TextAlign.Center);
                return;
            }

            var pillColor = LiveSessionActions.PhaseColor(state.Phase);
            var label = LiveSessionActions.PhaseLabel(state.Phase);

            // Phase pill
            FillRect(rect.X + pad, rect.Y + pad, pillW, pillH, pillColor);
            DrawText(label, fontPath,
                rect.X + pad, rect.Y, pillW, rect.Height,
                fontSize * 0.9f, AbortText, TextAlign.Center, TextAlign.Center);

            // Activity text
            var targetLabel = LiveSessionActions.PhaseStatusText(state, timeProvider);
            DrawText(targetLabel, fontPath,
                rect.X + pillW + pad * 2, rect.Y, rect.Width * 0.45f, rect.Height,
                fontSize, BodyText, TextAlign.Near, TextAlign.Center);

            // Obs / frame count / exposure time (top right)
            var obsIdx = state.CurrentObservationIndex;
            var obsCount = state.ActiveSession?.Observations.Count ?? 0;
            var obsDisplay = obsCount > 0 ? Math.Clamp(obsIdx + 1, 0, obsCount) : 0;
            var progressParts = $"Obs: {obsDisplay}/{obsCount}";
            if (state.ActiveObservation is { } topObs)
            {
                var subSec = topObs.SubExposure.TotalSeconds;
                var estimatedFrames = subSec > 0 ? (int)(topObs.Duration.TotalSeconds / (subSec + 10)) : 0;
                progressParts += $"  Frames: {state.TotalFramesWritten}/~{estimatedFrames}";
            }
            progressParts += $"  Exp: {LiveSessionActions.FormatDuration(state.TotalExposureTime)}";
            DrawText(progressParts, fontPath,
                rect.X + rect.Width * 0.5f, rect.Y, rect.Width * 0.45f, rect.Height,
                fontSize, DimText, TextAlign.Far, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Timeline: phase bars + now needle + time axis
        // -----------------------------------------------------------------------

        private void RenderTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

            if (!state.IsRunning)
            {
                RenderPreviewTimeline(state, rect, fontPath, fontSize, dpiScale, timeProvider);
                return;
            }

            var timeline = state.PhaseTimeline;
            if (timeline.Length == 0)
            {
                DrawText("No timeline data", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var pad = BasePadding * dpiScale;
            var barH = 24f * dpiScale;
            var barY = rect.Y + pad;
            var axisY = barY + barH + 2 * dpiScale;
            var axisH = rect.Height - barH - pad * 2 - 2 * dpiScale;

            // Time range: session start to now + 30min lookahead
            var timeStart = timeline[0].StartTime;
            var now = timeProvider.GetUtcNow();
            var sessionEnd = now + TimeSpan.FromMinutes(30);

            // Don't let range be too narrow (10 minutes minimum)
            var totalSeconds = Math.Max((sessionEnd - timeStart).TotalSeconds, 600);

            float TimeToX(DateTimeOffset t)
            {
                var frac = (float)((t - timeStart).TotalSeconds / totalSeconds);
                return rect.X + pad + frac * (rect.Width - pad * 2);
            }

            // Draw phase bars
            for (var i = 0; i < timeline.Length; i++)
            {
                var phaseStart = timeline[i].StartTime;
                var phaseEnd = i + 1 < timeline.Length ? timeline[i + 1].StartTime : now;
                var color = LiveSessionActions.PhaseColor(timeline[i].Phase);

                var x1 = Math.Max(TimeToX(phaseStart), rect.X + pad);
                var x2 = Math.Min(TimeToX(phaseEnd), rect.X + rect.Width - pad);
                var w = x2 - x1;
                if (w > 0)
                {
                    FillRect(x1, barY, w, barH, color);

                    // Label if wide enough
                    if (w > 40 * dpiScale)
                    {
                        var phaseLabel = LiveSessionActions.PhaseLabel(timeline[i].Phase);
                        // Shorten long labels
                        if (phaseLabel.Length > 8 && w < 80 * dpiScale)
                        {
                            phaseLabel = phaseLabel[..7] + "\u2026";
                        }
                        DrawText(phaseLabel, fontPath,
                            x1 + 2, barY, w - 4, barH,
                            fontSize * 0.8f, BrightText, TextAlign.Center, TextAlign.Center);
                    }
                }
            }

            // Now needle
            if (now >= timeStart && now <= sessionEnd)
            {
                var nowX = TimeToX(now);
                FillRect(nowX, barY - 2 * dpiScale, 2 * dpiScale, barH + axisH + 4 * dpiScale, NowNeedleColor);
            }

            // Time axis ticks (every 30 min)
            if (axisH > 4)
            {
                // Adaptive tick interval: 5min if range < 30min, 10min if < 2h, 30min otherwise
                var rangeMins = totalSeconds / 60.0;
                var tickMins = rangeMins < 30 ? 5 : rangeMins < 120 ? 10 : 30;
                var tickStart = new DateTimeOffset(timeStart.Year, timeStart.Month, timeStart.Day,
                    timeStart.Hour, (int)(timeStart.Minute / tickMins) * (int)tickMins, 0, timeStart.Offset);
                for (var t = tickStart; t <= sessionEnd; t = t.AddMinutes(tickMins))
                {
                    if (t < timeStart) continue;
                    var tx = TimeToX(t);
                    if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                    FillRect(tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                    DrawText(t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
                        tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                        fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
                }
            }
        }

        /// <summary>
        /// Preview mode timeline: twilight bands + now needle.
        /// Shows civil/nautical/astronomical twilight zones so the user knows when dark arrives.
        /// </summary>
        private void RenderPreviewTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            if (state.AstroDark == default)
            {
                DrawText("Twilight data loading\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var pad = BasePadding * dpiScale;
            var barH = 24f * dpiScale;
            var now = timeProvider.GetUtcNow();

            // Time range: 15 min before civil set → 15 min after civil rise
            var tStart = (state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1)) - TimeSpan.FromMinutes(15);
            var tEnd = (state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1)) + TimeSpan.FromMinutes(15);
            var totalSeconds = Math.Max((tEnd - tStart).TotalSeconds, 600);
            var barY = rect.Y + pad;

            float TimeToX(DateTimeOffset t) =>
                rect.X + pad + (float)((t - tStart).TotalSeconds / totalSeconds) * (rect.Width - pad * 2);

            // Twilight zone colors
            var civilColor = new RGBAColor32(0x44, 0x44, 0x22, 0x88);
            var nautColor = new RGBAColor32(0x22, 0x33, 0x55, 0x88);
            var astroColor = new RGBAColor32(0x11, 0x22, 0x44, 0x88);
            var nightColor = new RGBAColor32(0x00, 0x00, 0x22, 0xcc);

            // Fill the twilight bands
            if (state.CivilSet is { } cs)
            {
                FillRect(TimeToX(tStart), barY, TimeToX(cs) - TimeToX(tStart), barH, civilColor);
            }
            if (state.NauticalSet is { } ns)
            {
                var nsX = TimeToX(ns);
                var fromX = state.CivilSet is { } cs2 ? TimeToX(cs2) : TimeToX(tStart);
                FillRect(fromX, barY, nsX - fromX, barH, nautColor);
            }
            {
                var astroStartX = state.NauticalSet is { } ns2 ? TimeToX(ns2) : (state.CivilSet is { } cs3 ? TimeToX(cs3) : TimeToX(tStart));
                var darkX = TimeToX(state.AstroDark);
                FillRect(astroStartX, barY, darkX - astroStartX, barH, astroColor);
            }
            // Night (dark)
            {
                var darkX = TimeToX(state.AstroDark);
                var dawnX = TimeToX(state.AstroTwilight);
                FillRect(darkX, barY, dawnX - darkX, barH, nightColor);
            }
            // Dawn side: astro → nautical → civil (mirror)
            {
                var dawnX = TimeToX(state.AstroTwilight);
                var astroEndX = state.NauticalRise is { } nr ? TimeToX(nr) : (state.CivilRise is { } cr ? TimeToX(cr) : TimeToX(tEnd));
                FillRect(dawnX, barY, astroEndX - dawnX, barH, astroColor);
            }
            if (state.NauticalRise is { } nRise)
            {
                var nrX = TimeToX(nRise);
                var toX = state.CivilRise is { } cr2 ? TimeToX(cr2) : TimeToX(tEnd);
                FillRect(nrX, barY, toX - nrX, barH, nautColor);
            }
            if (state.CivilRise is { } cRise)
            {
                FillRect(TimeToX(cRise), barY, TimeToX(tEnd) - TimeToX(cRise), barH, civilColor);
            }

            // Now needle
            if (now >= tStart && now <= tEnd)
            {
                var nowX = TimeToX(now);
                FillRect(nowX, barY - 2, 2 * dpiScale, barH + 4, NowNeedleColor);
            }

            // Time axis ticks
            var axisY = barY + barH + 2;
            var axisH = rect.Height - barH - pad * 2 - 2;
            if (axisH > 4)
            {
                var rangeMins = totalSeconds / 60.0;
                var tickMins = rangeMins < 120 ? 10 : 30;
                var tickStart = new DateTimeOffset(tStart.Year, tStart.Month, tStart.Day,
                    tStart.Hour, (int)(tStart.Minute / tickMins) * (int)tickMins, 0, tStart.Offset);
                for (var t = tickStart; t <= tEnd; t = t.AddMinutes(tickMins))
                {
                    if (t < tStart) continue;
                    var tx = TimeToX(t);
                    if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                    FillRect(tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                    DrawText(t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
                        tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                        fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Bottom strip: compact guide graph + RMS + ABORT
        // -----------------------------------------------------------------------

        private void RenderBottomStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, float pad, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);
            FillRect(rect.X, rect.Y, rect.Width, 1, SeparatorColor);

            var abortW = state.IsRunning ? 80f * dpiScale : 0;
            var rmsW = 220f * dpiScale;
            var guideW = rect.Width - rmsW - abortW - pad * (state.IsRunning ? 5 : 3);

            // Mini guide graph (left portion)
            if (guideW > 40)
            {
                var guideRect = new RectF32(rect.X + pad, rect.Y + 2, guideW, rect.Height - 4);
                RenderCompactGuideGraph(state, guideRect, dpiScale);
            }

            // RMS stats (between graph and abort)
            var rmsX = rect.X + guideW + pad * 2;
            var rmsText = LiveSessionActions.FormatGuideRms(state.LastGuideStats);
            DrawText(rmsText, fontPath,
                rmsX, rect.Y, rmsW, rect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Near, TextAlign.Center);

            // ABORT button (right, after RMS)
            if (state.IsRunning)
            {
                var abortX = rmsX + rmsW + pad;
                RenderButton("ABORT", abortX, rect.Y + 4 * dpiScale, abortW, rect.Height - 8 * dpiScale,
                    fontPath, fontSize, AbortBg, AbortText, "AbortSession",
                    _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; });
            }
        }

        /// <summary>
        /// PHD2-style guide graph using shared <see cref="GuideGraphRenderer"/> helpers.
        /// </summary>
        private void RenderCompactGuideGraph(LiveSessionState state, RectF32 rect, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuideGraphRenderer.GraphBg);

            var samples = state.GuideSamples;
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            if (samples.Length < 2) return;

            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var yScale = GuideGraphRenderer.ComputeYScale(state.LastGuideStats, samples, startIdx, visibleCount);

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);
            var lineW = Math.Max(dpiScale, 1f);

            // Settling shading + correction bars + dither markers (behind lines)
            var barW = Math.Max(spacing * 0.3f, 1f);
            for (var i = 0; i < visibleCount; i++)
            {
                var sample = samples[startIdx + i];
                var sx = rect.X + i * spacing;
                if (sample.IsSettling)
                {
                    FillRect(sx, rect.Y, spacing + 1, rect.Height, GuideGraphRenderer.SettlingShadeColor);
                }
                if (sample.RaCorrectionMs != 0)
                {
                    var cbH = GuideGraphRenderer.CorrectionBarFraction(sample.RaCorrectionMs) * halfH;
                    if (sample.RaCorrectionMs > 0)
                        FillRect(sx, zeroY - cbH, barW, cbH, GuideGraphRenderer.RaCorrectionColor);
                    else
                        FillRect(sx, zeroY, barW, cbH, GuideGraphRenderer.RaCorrectionColor);
                }
                if (sample.DecCorrectionMs != 0)
                {
                    var cbH = GuideGraphRenderer.CorrectionBarFraction(sample.DecCorrectionMs) * halfH;
                    var dbx = sx + barW + 1;
                    if (sample.DecCorrectionMs > 0)
                        FillRect(dbx, zeroY - cbH, barW, cbH, GuideGraphRenderer.DecCorrectionColor);
                    else
                        FillRect(dbx, zeroY, barW, cbH, GuideGraphRenderer.DecCorrectionColor);
                }
                if (sample.IsDither)
                {
                    for (var dy = rect.Y; dy < rect.Y + rect.Height; dy += 6 * dpiScale)
                    {
                        FillRect(sx, dy, Math.Max(1, lineW), 3 * dpiScale, GuideGraphRenderer.DitherMarkerColor);
                    }
                }
            }

            for (var i = 1; i < visibleCount; i++)
            {
                var x1 = rect.X + (i - 1) * spacing;
                var x2 = rect.X + i * spacing;

                var raY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].RaError, yScale, zeroY, halfH);
                var raY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].RaError, yScale, zeroY, halfH);
                FillRect(x1, raY1, x2 - x1, lineW, GuideGraphRenderer.RaColor);
                FillRect(x2, Math.Min(raY1, raY2), lineW, Math.Abs(raY2 - raY1) + lineW, GuideGraphRenderer.RaColor);

                var decY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].DecError, yScale, zeroY, halfH);
                var decY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].DecError, yScale, zeroY, halfH);
                FillRect(x1, decY1, x2 - x1, lineW, GuideGraphRenderer.DecColor);
                FillRect(x2, Math.Min(decY1, decY2), lineW, Math.Abs(decY2 - decY1) + lineW, GuideGraphRenderer.DecColor);
            }
        }

        // -----------------------------------------------------------------------
        // Main: per-OTA panels with live exposure state
        // -----------------------------------------------------------------------

        private void RenderOTAPanels(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            if (!state.IsRunning)
            {
                RenderPreviewOTAPanels(state, rect, fontPath, fontSize, dpiScale, pad, rowH, timeProvider);
                return;
            }

            if (state.ActiveSession is not { } session)
            {
                DrawText("Starting\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var telescopes = session.Setup.Telescopes;
            var cameraStates = state.CameraStates;
            var otaCount = telescopes.Length;

            if (otaCount == 0)
            {
                return;
            }

            // Split horizontally for multiple OTAs
            var panelW = rect.Width / otaCount;
            var progressH = BaseProgressBarH * dpiScale;
            var smallFs = fontSize * 0.85f;

            for (var i = 0; i < otaCount; i++)
            {
                var ota = telescopes[i];
                var px = rect.X + i * panelW;

                // Separator between OTAs
                if (i > 0)
                {
                    FillRect(px, rect.Y, 1, rect.Height, SeparatorColor);
                }

                var y = rect.Y + pad;
                var textW = panelW - pad * 2;
                // Mount status is pinned to the bottom; stop rendering OTA items before they overlap
                var maxY = rect.Y + rect.Height - rowH * 6;

                // OTA header (camera name)
                DrawText(ota.Camera.Device.DisplayName, fontPath,
                    px + pad, y, textW, rowH,
                    fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                // Temperature + power from latest cooling sample for this camera
                var lastTemp = double.NaN;
                var lastPower = double.NaN;
                var lastSetpoint = double.NaN;
                var coolingSamples = state.CoolingSamples;
                for (var j = coolingSamples.Length - 1; j >= 0; j--)
                {
                    if (coolingSamples[j].CameraIndex == i)
                    {
                        lastTemp = coolingSamples[j].TemperatureC;
                        lastPower = coolingSamples[j].CoolerPowerPercent;
                        lastSetpoint = coolingSamples[j].SetpointTempC;
                        break;
                    }
                }

                if (!double.IsNaN(lastTemp))
                {
                    var tempColor = CameraTempColors[i % CameraTempColors.Length];
                    var tempText = $"{lastTemp:F0}\u00B0C  {lastPower:F0}%";
                    if (!double.IsNaN(lastSetpoint))
                    {
                        tempText += $"  \u2192 {lastSetpoint:F0}\u00B0C";
                    }
                    DrawText(tempText, fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, tempColor, TextAlign.Near, TextAlign.Center);
                    y += rowH;

                    // Mini cooling sparkline (last 20 samples for this camera)
                    var sparkH = 60f * dpiScale;
                    RenderMiniSparkline(coolingSamples, i, new RectF32(px + pad, y, textW, sparkH), tempColor, dpiScale);
                    y += sparkH + pad;
                }
                else
                {
                    y += pad;
                }

                // Focuser position + temperature + moving state
                if (y < maxY && ota.Focuser is not null && i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    var focLabel = $"Foc: {cs.FocusPosition}";
                    if (!double.IsNaN(cs.FocuserTemperature))
                    {
                        focLabel += $"  {cs.FocuserTemperature:F1}\u00B0C";
                    }
                    if (cs.FocuserIsMoving)
                    {
                        focLabel += "  \u21C4 Moving";
                    }
                    var focColor = cs.FocuserIsMoving ? StatusSlewing : BodyText;
                    DrawText(focLabel, fontPath,
                        px + pad, y, textW, rowH,
                        fontSize, focColor, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Filter
                if (y < maxY && ota.FilterWheel is not null)
                {
                    var filterName = (i < cameraStates.Length && cameraStates[i].FilterName is { Length: > 0 } fn) ? fn : "--";
                    DrawText($"FW: {filterName}", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Exposure state + progress bar
                y += pad;
                if (y < maxY && i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    RenderExposureState(cs, px + pad, y, textW, progressH, rowH, fontPath, fontSize, smallFs, dpiScale, timeProvider);
                    y += rowH + progressH + pad;
                }
                else if (y < maxY)
                {
                    DrawText("Idle", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // V-curve chart for this OTA (below its exposure state)
                var activeSamples = state.ActiveFocusSamples;
                var lastFocusRun = state.FocusHistory is { Length: > 0 } fh ? fh[^1] : default(FocusRunRecord?);
                var showVCurve = y < maxY
                    && (activeSamples.Length >= 2
                        || (lastFocusRun?.Curve.Length >= 2
                            && state.Phase is SessionPhase.AutoFocus or SessionPhase.CalibratingGuider or SessionPhase.RoughFocus));
                if (showVCurve)
                {
                    var chartSamples = activeSamples.Length >= 2 ? activeSamples : lastFocusRun!.Value.Curve;
                    var chartH = maxY - y - pad;
                    if (chartH > 40)
                    {
                        RenderVCurveChart(chartSamples, lastFocusRun, new RectF32(px + pad, y, textW, chartH), fontPath, smallFs, dpiScale);
                    }
                }
            }

            // Mount status section (below OTAs, full width)
            var mountY = rect.Y + rect.Height - rowH * 6 - pad;
            if (mountY > rect.Y + rect.Height * 0.35f) // only show if there's room
            {
                FillRect(rect.X, mountY, rect.Width, 1, SeparatorColor);
                mountY += pad;

                // Mount name row
                var mountName = session.Setup.Mount.Device.DisplayName;
                var ms = state.MountState;
                var dotColor = ms.IsSlewing ? StatusSlewing
                    : ms.IsTracking ? StatusTracking
                    : DimText;
                var dotSize = rowH * 0.4f;
                FillRect(rect.X + pad, mountY + (rowH - dotSize) / 2, dotSize, dotSize, dotColor);
                DrawText(mountName, fontPath,
                    rect.X + pad + dotSize + pad, mountY, rect.Width - pad * 3 - dotSize, rowH,
                    smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // Status + pier side row
                var pierLabel = ms.PierSide is Lib.Devices.PointingState.Normal ? "E" : ms.PierSide is Lib.Devices.PointingState.ThroughThePole ? "W" : "";
                var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
                var statusColor = ms.IsSlewing ? StatusSlewing : ms.IsTracking ? StatusTracking : DimText;
                DrawText($"{mountStatus}  {pierLabel}", fontPath,
                    rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                    smallFs, statusColor, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // RA + HA on one row
                var raStr = Lib.Astrometry.CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
                var haStr = $"HA {ms.HourAngle:+0.00;-0.00}h";
                DrawText($"RA {raStr}  {haStr}", fontPath,
                    rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                    smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // Dec on separate row
                var decStr = Lib.Astrometry.CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);
                DrawText($"Dec {decStr}", fontPath,
                    rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                    smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // Target name (if observing)
                if (state.ActiveObservation is { Target: var target })
                {
                    DrawText($"\u2609 {target.Name}", fontPath,
                        rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private void RenderExposureState(CameraExposureState cs, float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, ITimeProvider timeProvider)
        {
            if (cs.State == CameraState.Idle)
            {
                DrawText("Idle", fontPath,
                    x, y, w, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            if (cs.State == CameraState.Download || cs.State == CameraState.Reading)
            {
                DrawText($"Downloading #{cs.FrameNumber}\u2026", fontPath,
                    x, y, w, rowH, smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                return;
            }

            // Exposing — show countdown + progress bar
            var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
            var totalSec = cs.SubExposure.TotalSeconds;
            var elapsedSec = Math.Min(elapsed.TotalSeconds, totalSec);
            var fraction = totalSec > 0 ? (float)(elapsedSec / totalSec) : 0f;

            // Filter + frame label
            var filterLabel = cs.FilterName is { Length: > 0 } fn ? fn : "L";
            var expLabel = $"{filterLabel} #{cs.FrameNumber} ({elapsedSec:F0}/{totalSec:F0}s)";
            DrawText(expLabel, fontPath,
                x, y, w, rowH, smallFs, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // Progress bar
            FillRect(x, y, w, progressH, ProgressBg);
            var fillW = w * Math.Clamp(fraction, 0f, 1f);
            if (fillW > 0)
            {
                FillRect(x, y, fillW, progressH, ProgressFill);
            }

            // Remaining time overlay on bar
            var remaining = cs.SubExposure - elapsed;
            if (remaining.TotalSeconds > 0)
            {
                var remText = $"{remaining.TotalSeconds:F0}s";
                DrawText(remText, fontPath,
                    x, y, w, progressH,
                    fontSize * 0.65f, BrightText, TextAlign.Center, TextAlign.Center);
            }
        }

        /// <summary>Renders V-curve chart: scatter dots for measured HFD + fitted hyperbola curve.</summary>
        private void RenderVCurveChart(
            ImmutableArray<(int Position, float Hfd)> samples,
            FocusRunRecord? completedRun,
            RectF32 rect, string fontPath, float fontSize, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            if (samples.Length < 2) return;

            // Compute data bounds
            var minPos = int.MaxValue;
            var maxPos = int.MinValue;
            var minHfd = float.MaxValue;
            var maxHfd = float.MinValue;
            foreach (var (pos, hfd) in samples)
            {
                if (pos < minPos) minPos = pos;
                if (pos > maxPos) maxPos = pos;
                if (hfd < minHfd) minHfd = hfd;
                if (hfd > maxHfd) maxHfd = hfd;
            }

            if (maxPos <= minPos || maxHfd <= minHfd) return;

            var margin = 4f * dpiScale;
            var chartX = rect.X + margin;
            var chartY = rect.Y + margin;
            var chartW = rect.Width - margin * 2;
            var chartH = rect.Height - margin * 2 - fontSize; // leave room for axis label

            // Add padding so dots don't sit on the axis edges
            var hfdRange = maxHfd - minHfd;
            if (hfdRange < 0.5f) hfdRange = 0.5f; // minimum range to avoid noise magnification
            minHfd -= hfdRange * 0.15f;
            maxHfd += hfdRange * 0.15f;
            if (minHfd < 0) minHfd = 0;
            hfdRange = maxHfd - minHfd;

            var posRange = maxPos - minPos;
            var posPad = Math.Max(posRange * 0.05, 1);
            minPos -= (int)posPad;
            maxPos += (int)posPad;
            posRange = maxPos - minPos;

            float PosToX(double pos) => chartX + (float)((pos - minPos) / posRange) * chartW;
            float HfdToY(double hfd) => chartY + chartH - (float)((hfd - minHfd) / hfdRange) * chartH;

            // Axis lines
            FillRect(chartX, chartY + chartH, chartW, 1, VCurveAxisColor); // X axis
            FillRect(chartX, chartY, 1, chartH, VCurveAxisColor);          // Y axis

            // Scatter dots
            var dotR = 3f * dpiScale;
            foreach (var (pos, hfd) in samples)
            {
                var dx = PosToX(pos);
                var dy = HfdToY(hfd);
                FillRect(dx - dotR, dy - dotR, dotR * 2, dotR * 2, VCurveDotColor);
            }

            // Fitted hyperbola curve (if we have fit parameters)
            if (completedRun is { FitA: var a, FitB: var b, BestPosition: var bestPos }
                && !double.IsNaN(a) && !double.IsNaN(b) && b > 0)
            {
                // Draw smooth curve
                var steps = (int)chartW;
                var lineW = Math.Max(1f, dpiScale);
                for (var i = 0; i < steps; i++)
                {
                    var xPos = minPos + (double)i / steps * posRange;
                    var yVal = TianWen.Lib.Astrometry.Focus.Hyperbola.CalculateValueAtPosition(xPos, bestPos, a, b);
                    var px = PosToX(xPos);
                    var py = HfdToY(yVal);
                    if (py >= chartY && py <= chartY + chartH)
                    {
                        FillRect(px, py, lineW, lineW, VCurveFitColor);
                    }
                }

                // Best focus vertical line
                var bestX = PosToX(bestPos);
                FillRect(bestX, chartY, 1, chartH, VCurveBestColor);

                // Best focus label
                DrawText($"\u2193 {bestPos} (HFD {a:F2})", fontPath,
                    bestX + 2 * dpiScale, chartY, chartW * 0.4f, fontSize,
                    fontSize * 0.8f, VCurveBestColor, TextAlign.Near, TextAlign.Near);
            }

            // X-axis label
            DrawText($"Focuser position ({minPos}\u2013{maxPos})", fontPath,
                chartX, chartY + chartH + 1, chartW, fontSize,
                fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Near);
        }

        /// <summary>Tiny sparkline of temperature + power for a single camera.</summary>
        private void RenderMiniSparkline(ImmutableArray<CoolingSample> allSamples, int cameraIndex, RectF32 rect, RGBAColor32 tempColor, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            var powerColor = CameraPowerColors[cameraIndex % CameraPowerColors.Length];

            // Collect last N samples for this camera
            const int maxPoints = 20;
            Span<float> temps = stackalloc float[maxPoints];
            Span<float> powers = stackalloc float[maxPoints];
            var count = 0;
            for (var i = allSamples.Length - 1; i >= 0 && count < maxPoints; i--)
            {
                if (allSamples[i].CameraIndex == cameraIndex)
                {
                    temps[maxPoints - 1 - count] = (float)allSamples[i].TemperatureC;
                    powers[maxPoints - 1 - count] = (float)allSamples[i].CoolerPowerPercent;
                    count++;
                }
            }

            if (count < 2)
            {
                return;
            }

            var start = maxPoints - count;
            var tempSlice = temps.Slice(start, count);
            var powerSlice = powers.Slice(start, count);

            // Find temp range
            var minT = float.MaxValue;
            var maxT = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                if (tempSlice[i] < minT) minT = tempSlice[i];
                if (tempSlice[i] > maxT) maxT = tempSlice[i];
            }
            var range = Math.Max(maxT - minT, 2f);
            minT -= 1;
            maxT = minT + range + 2;

            var stepX = rect.Width / Math.Max(count - 1, 1);

            // Draw power line first (behind temp)
            for (var i = 1; i < count; i++)
            {
                var x1 = rect.X + (i - 1) * stepX;
                var x2 = rect.X + i * stepX;
                var y1 = rect.Y + rect.Height - (powerSlice[i - 1] / 100f) * rect.Height;
                var y2 = rect.Y + rect.Height - (powerSlice[i] / 100f) * rect.Height;

                FillRect(x1, y1, x2 - x1, Math.Max(1, dpiScale), powerColor);
                FillRect(x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, powerColor);
            }

            // Draw temp line on top
            for (var i = 1; i < count; i++)
            {
                var x1 = rect.X + (i - 1) * stepX;
                var x2 = rect.X + i * stepX;
                var y1 = rect.Y + rect.Height - ((tempSlice[i - 1] - minT) / (maxT - minT)) * rect.Height;
                var y2 = rect.Y + rect.Height - ((tempSlice[i] - minT) / (maxT - minT)) * rect.Height;

                FillRect(x1, y1, x2 - x1, Math.Max(1, dpiScale), tempColor);
                FillRect(x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, tempColor);
            }
        }

        // -----------------------------------------------------------------------
        // Preview mode: OTA panels from profile + hub telemetry
        // -----------------------------------------------------------------------

        private void RenderPreviewOTAPanels(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH, ITimeProvider timeProvider)
        {
            var preview = state.PreviewOTATelemetry;
            var otaCount = preview.Length;
            if (otaCount == 0)
            {
                DrawText("No OTAs configured in profile", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var panelW = rect.Width / otaCount;
            var progressH = BaseProgressBarH * dpiScale;
            var smallFs = fontSize * 0.85f;

            for (var i = 0; i < otaCount; i++)
            {
                var tel = preview[i];
                var px = rect.X + i * panelW;
                if (i > 0)
                {
                    FillRect(px, rect.Y, 1, rect.Height, SeparatorColor);
                }

                var y = rect.Y + pad;
                var textW = panelW - pad * 2;
                // Reserve space for mount section at the bottom
                var maxY = rect.Y + rect.Height - rowH * 5;

                // Camera name (dim if not connected)
                var nameColor = tel.CameraConnected ? HeaderText : DimText;
                DrawText(tel.CameraDisplayName, fontPath,
                    px + pad, y, textW, rowH,
                    fontSize, nameColor, TextAlign.Near, TextAlign.Center);
                y += rowH;

                // Temperature from PreviewOTATelemetry
                if (!double.IsNaN(tel.CcdTempC))
                {
                    var tempColor = CameraTempColors[i % CameraTempColors.Length];
                    var tempText = $"{tel.CcdTempC:F0}\u00B0C  {tel.CoolerPowerPct:F0}%";
                    if (!double.IsNaN(tel.SetpointC))
                    {
                        tempText += $"  \u2192 {tel.SetpointC:F0}\u00B0C";
                    }
                    DrawText(tempText, fontPath,
                        px + pad, y, textW, rowH, smallFs, tempColor, TextAlign.Near, TextAlign.Center);
                    y += rowH + pad;
                }
                else
                {
                    y += pad;
                }

                // Focuser readout + jog controls (fine ±10, coarse ±100)
                if (y < maxY)
                {
                    if (tel.FocuserConnected)
                    {
                        var focLabel = $"Foc: {tel.FocusPosition}";
                        if (!double.IsNaN(tel.FocuserTempC))
                        {
                            focLabel += $"  {tel.FocuserTempC:F1}\u00B0C";
                        }
                        if (tel.FocuserIsMoving)
                        {
                            focLabel += "  \u21C4";
                        }
                        DrawText(focLabel, fontPath,
                            px + pad, y, textW, rowH,
                            fontSize, tel.FocuserIsMoving ? StatusSlewing : BodyText, TextAlign.Near, TextAlign.Center);
                        y += rowH;

                        // Capture index once for both the jog button closures
                        // (5 buttons) and the goto-position row's OnCommit /
                        // Go button below.
                        var capturedI = i;

                        // Jog buttons row: [<<] [<] position [>] [>>]
                        if (y < maxY)
                        {
                            var jogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
                            var jogBtnW = 32f * dpiScale;
                            var jogBtnH = rowH * 0.85f;
                            var jogBtnY2 = y + (rowH - jogBtnH) / 2;
                            var jogX = px + pad;

                            // Coarse in (<<)
                            RenderButton("\u00AB", jogX, jogBtnY2, jogBtnW, jogBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocCoarseIn{capturedI}",
                                _ => PostSignal(new JogFocuserSignal(capturedI, -100)));
                            jogX += jogBtnW + 2;

                            // Fine in (<)
                            RenderButton("\u2039", jogX, jogBtnY2, jogBtnW, jogBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocFineIn{capturedI}",
                                _ => PostSignal(new JogFocuserSignal(capturedI, -10)));
                            jogX += jogBtnW + 2;

                            // Step size labels
                            var labelW = textW - (jogBtnW * 4 + 6);
                            DrawText("10 | 100", fontPath,
                                jogX, y, labelW, rowH,
                                smallFs * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                            jogX += labelW + 2;

                            // Fine out (>)
                            RenderButton("\u203A", jogX, jogBtnY2, jogBtnW, jogBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocFineOut{capturedI}",
                                _ => PostSignal(new JogFocuserSignal(capturedI, 10)));
                            jogX += jogBtnW + 2;

                            // Coarse out (>>)
                            RenderButton("\u00BB", jogX, jogBtnY2, jogBtnW, jogBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocCoarseOut{capturedI}",
                                _ => PostSignal(new JogFocuserSignal(capturedI, 100)));

                            y += rowH;
                        }

                        // Goto-position row: numeric input pre-filled with the
                        // current focuser step + a "Go" button. Posts
                        // GotoFocuserSignal which routes to focuser.BeginMoveAsync.
                        // The input is parsed as int on commit; non-numeric
                        // values are ignored silently.
                        if (y < maxY && capturedI < state.FocuserGotoInputs.Length)
                        {
                            var input = state.FocuserGotoInputs[capturedI];
                            // Refresh placeholder/text to current position when
                            // not actively being edited so the user always sees
                            // a sensible starting value to tweak.
                            if (!input.IsActive && string.IsNullOrEmpty(input.Text))
                            {
                                input.Text = tel.FocusPosition.ToString();
                                input.CursorPos = input.Text.Length;
                            }
                            input.OnCommit = text =>
                            {
                                if (int.TryParse(text, out var pos))
                                {
                                    PostSignal(new GotoFocuserSignal(capturedI, pos));
                                }
                                return Task.CompletedTask;
                            };

                            var jogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
                            var rowBtnH = rowH * 0.85f;
                            var rowBtnY = y + (rowH - rowBtnH) / 2;
                            var goBtnW = 32f * dpiScale;
                            var inputW = textW - goBtnW - 4f;
                            RenderTextInput(input, (int)(px + pad), (int)rowBtnY,
                                (int)inputW, (int)rowBtnH, fontPath, smallFs);
                            RenderButton("Go", px + pad + inputW + 4f, rowBtnY, goBtnW, rowBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocGoto{capturedI}",
                                _ =>
                                {
                                    if (int.TryParse(input.Text, out var pos))
                                    {
                                        PostSignal(new GotoFocuserSignal(capturedI, pos));
                                    }
                                });
                            y += rowH;
                        }
                    }
                    else
                    {
                        DrawText("Foc: \u2014", fontPath,
                            px + pad, y, textW, rowH,
                            fontSize, DimText, TextAlign.Near, TextAlign.Center);
                        y += rowH;
                    }
                }

                // Filter
                if (y < maxY)
                {
                    if (tel.FilterWheelConnected)
                    {
                        DrawText($"FW: {tel.FilterName}", fontPath,
                            px + pad, y, textW, rowH, smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    }
                    else
                    {
                        DrawText("FW: \u2014", fontPath,
                            px + pad, y, textW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
                    }
                    y += rowH;
                }

                // Capture controls
                y += pad;
                if (y < maxY)
                {
                    RenderPreviewCaptureControls(state, i, px + pad, y, textW, progressH, rowH,
                        fontPath, fontSize, smallFs, dpiScale, timeProvider);
                }
            }

            // Mount section (pinned to bottom, full width)
            RenderPreviewMountSection(state, rect, fontPath, fontSize, dpiScale, pad, rowH);
        }

        private void RenderPreviewCaptureControls(LiveSessionState state, int otaIndex,
            float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, ITimeProvider timeProvider)
        {
            var isCapturing = otaIndex < state.PreviewCapturing.Length && state.PreviewCapturing[otaIndex];
            var capBtnW = 72f * dpiScale;

            if (isCapturing)
            {
                // Show progress bar + elapsed/total
                var start = state.PreviewCaptureStart[otaIndex];
                var dur = state.PreviewExposureDuration[otaIndex];
                var elapsed = timeProvider.GetUtcNow() - start;
                var fraction = dur.TotalSeconds > 0
                    ? (float)Math.Min(elapsed.TotalSeconds / dur.TotalSeconds, 1.0)
                    : 0f;
                DrawText($"Capturing {elapsed.TotalSeconds:F0}/{dur.TotalSeconds:F0}s",
                    fontPath, x, y, w, rowH, smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;
                FillRect(x, y, w, progressH, ProgressBg);
                FillRect(x, y, w * fraction, progressH, ProgressFill);
                return;
            }

            var stepBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
            var stepBtnW = 28f * dpiScale;
            var stepBtnH = rowH * 0.85f;
            var stepBtnY = y + (rowH - stepBtnH) / 2;

            // Exposure row: [-] value [+]   [Capture]
            var expSec = otaIndex < state.PreviewExposureSeconds.Length
                ? state.PreviewExposureSeconds[otaIndex] : 5.0;

            var expX = x;
            RenderButton("-", expX, stepBtnY, stepBtnW, stepBtnH,
                fontPath, smallFs, stepBg, BodyText, $"ExpDec{otaIndex}",
                _ =>
                {
                    if (otaIndex >= state.PreviewExposureSeconds.Length) return;
                    state.PreviewExposureSeconds[otaIndex] = LiveSessionActions.StepExposure(
                        state.PreviewExposureSeconds[otaIndex], direction: -1);
                });
            expX += stepBtnW + 2;

            var labelW = w - stepBtnW * 2 - 4 - capBtnW - 4 * dpiScale;
            DrawText($"Exp: {LiveSessionActions.FormatExposureLabel(expSec)}", fontPath,
                expX, y, labelW, rowH,
                smallFs, BodyText, TextAlign.Center, TextAlign.Center);
            expX += labelW + 2;

            RenderButton("+", expX, stepBtnY, stepBtnW, stepBtnH,
                fontPath, smallFs, stepBg, BodyText, $"ExpInc{otaIndex}",
                _ =>
                {
                    if (otaIndex >= state.PreviewExposureSeconds.Length) return;
                    state.PreviewExposureSeconds[otaIndex] = LiveSessionActions.StepExposure(
                        state.PreviewExposureSeconds[otaIndex], direction: +1);
                });

            // [Capture] button -- disabled while polar alignment is running so the
            // user can't fire a manual exposure that would interleave with the
            // PolarAlignmentSession's own captures (different frame settings, breaks
            // the two-frame solve / refine cadence).
            var polarActive = state.Mode == LiveSessionMode.PolarAlign;
            var captureBtnColor = polarActive
                ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                : new RGBAColor32(0x33, 0x66, 0x33, 0xff);
            var captureBtnText = polarActive ? DimText : BrightText;
            RenderButton("Capture", x + w - capBtnW, y, capBtnW, rowH * 0.9f,
                fontPath, smallFs, captureBtnColor, captureBtnText,
                $"PreviewCapture{otaIndex}",
                polarActive ? null : _ =>
                {
                    var exp = otaIndex < state.PreviewExposureSeconds.Length
                        ? state.PreviewExposureSeconds[otaIndex] : 5.0;
                    PostSignal(new TakePreviewSignal(otaIndex, exp,
                        otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null,
                        otaIndex < state.PreviewBinning.Length ? state.PreviewBinning[otaIndex] : (short)1));
                });
            y += rowH;

            // Gain row: [-] value [+]  (only if camera supports gain value or gain mode)
            var tel = otaIndex < state.PreviewOTATelemetry.Length
                ? state.PreviewOTATelemetry[otaIndex]
                : PreviewOTATelemetry.Unknown;

            // Gain row — numeric (ZWO/ASCOM) and mode (DSLR ISO) share the same layout;
            // LiveSessionActions picks the right step semantics based on the telemetry.
            // Bracketed label = camera default; plain = user override (rendered brighter).
            var hasGainControl = (tel.UsesGainValue && tel.GainMax > tel.GainMin)
                || (tel.UsesGainMode && tel.GainModes.Length > 0);
            if (hasGainControl)
            {
                stepBtnY = y + (rowH - stepBtnH) / 2;
                var gainVal = otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null;

                var gx = x;
                RenderButton("-", gx, stepBtnY, stepBtnW, stepBtnH,
                    fontPath, smallFs, stepBg, BodyText, $"GainDec{otaIndex}",
                    _ =>
                    {
                        if (otaIndex >= state.PreviewGain.Length) return;
                        state.PreviewGain[otaIndex] = LiveSessionActions.StepGain(
                            state.PreviewGain[otaIndex], tel, direction: -1);
                    });
                gx += stepBtnW + 2;

                var gainLabel = LiveSessionActions.FormatGainLabel(gainVal, tel);
                var gainLabelW = w - stepBtnW * 2 - 4;
                DrawText(gainLabel, fontPath,
                    gx, y, gainLabelW, rowH,
                    smallFs, gainVal.HasValue ? BodyText : DimText, TextAlign.Center, TextAlign.Center);
                gx += gainLabelW + 2;

                RenderButton("+", gx, stepBtnY, stepBtnW, stepBtnH,
                    fontPath, smallFs, stepBg, BodyText, $"GainInc{otaIndex}",
                    _ =>
                    {
                        if (otaIndex >= state.PreviewGain.Length) return;
                        state.PreviewGain[otaIndex] = LiveSessionActions.StepGain(
                            state.PreviewGain[otaIndex], tel, direction: +1);
                    });
                y += rowH;
            }

            // [Save] and [Solve] only appear if a preview image exists for this OTA
            var hasImage = otaIndex < state.LastCapturedImages.Length
                && state.LastCapturedImages[otaIndex] is not null;
            if (hasImage)
            {
                var halfW = (w - 4 * dpiScale) / 2;
                var saveBtnColor = new RGBAColor32(0x22, 0x55, 0x44, 0xff);
                var solveBtnColor = new RGBAColor32(0x22, 0x44, 0x66, 0xff);
                RenderButton("Save", x, y, halfW, rowH * 0.9f,
                    fontPath, smallFs, saveBtnColor, BrightText,
                    $"PreviewSave{otaIndex}",
                    _ => PostSignal(new SaveSnapshotSignal(otaIndex)));
                var solving = otaIndex < state.PreviewPlateSolving.Length
                    && state.PreviewPlateSolving[otaIndex];
                var solveLabel = solving ? "Solving\u2026" : "Solve";
                var solveBg = solving
                    ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                    : solveBtnColor;
                var solveText = solving ? DimText : BrightText;
                RenderButton(solveLabel, x + halfW + 4 * dpiScale, y, halfW, rowH * 0.9f,
                    fontPath, smallFs, solveBg, solveText,
                    $"PreviewSolve{otaIndex}",
                    solving ? null : _ => PostSignal(new PlateSolvePreviewSignal(otaIndex)));
            }
        }

        private void RenderPreviewMountSection(LiveSessionState state, RectF32 rect,
            string fontPath, float fontSize, float dpiScale, float pad, float rowH)
        {
            var smallFs = fontSize * 0.85f;
            var mountY = rect.Y + rect.Height - rowH * 4 - pad;
            if (mountY <= rect.Y + rect.Height * 0.35f)
            {
                return;
            }

            FillRect(rect.X, mountY, rect.Width, 1, SeparatorColor);
            mountY += pad;

            var ms = state.PreviewMountState;
            var dotColor = ms.IsSlewing ? StatusSlewing
                : ms.IsTracking ? StatusTracking
                : DimText;
            var dotSize = rowH * 0.4f;
            FillRect(rect.X + pad, mountY + (rowH - dotSize) / 2, dotSize, dotSize, dotColor);

            var mountName = state.PreviewMountDisplayName ?? "Mount";
            DrawText(mountName, fontPath,
                rect.X + pad + dotSize + pad, mountY, rect.Width - pad * 3 - dotSize, rowH,
                smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
            mountY += rowH;

            // RA / Dec
            var raHms = $"RA {ms.RightAscension:F4}h";
            var decDms = $"Dec {ms.Declination:F3}\u00B0";
            DrawText(raHms, fontPath,
                rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                smallFs, BodyText, TextAlign.Near, TextAlign.Center);
            mountY += rowH;
            DrawText(decDms, fontPath,
                rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                smallFs, BodyText, TextAlign.Near, TextAlign.Center);
            mountY += rowH;

            // Status line
            var statusText = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
            var pierLabel = ms.PierSide switch
            {
                TianWen.Lib.Devices.PointingState.Normal => "Normal",
                TianWen.Lib.Devices.PointingState.ThroughThePole => "Through Pole",
                _ => "?"
            };
            statusText += $"  Pier: {pierLabel}  HA: {ms.HourAngle:F2}h";
            DrawText(statusText, fontPath,
                rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                smallFs, ms.IsSlewing ? StatusSlewing : DimText, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Right panel: exposure log
        // -----------------------------------------------------------------------

        private void RenderExposureLog(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Separator on left edge
            FillRect(rect.X, rect.Y, 1, rect.Height, SeparatorColor);

            if (!state.IsRunning && state.Mode == LiveSessionMode.PolarAlign)
            {
                RenderPolarSidePanel(state, rect, fontPath, fontSize, pad, rowH);
                return;
            }

            if (!state.IsRunning)
            {
                // Preview mode: show header + plate solve result if available
                DrawText("Preview Mode", fontPath,
                    rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                    fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

                if (state.PreviewPlateSolveResult is { } solveResult)
                {
                    var solveY = rect.Y + rowH + pad;
                    if (solveResult.Solution is { } wcs)
                    {
                        DrawText($"RA  {wcs.CenterRA:F4}h", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Dec {wcs.CenterDec:F3}\u00B0", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Scale {wcs.PixelScaleArcsec:F2}\"/px", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Solved in {solveResult.Elapsed.TotalSeconds:F1}s  {solveResult.MatchedStars} matched", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
                    }
                    else
                    {
                        DrawText("Solve: no match", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
                    }
                }
                return;
            }

            // Header
            DrawText("Exposure Log", fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Column layout — fixed pixel positions for alignment with proportional fonts
            var colY = rect.Y + rowH;
            var x0 = rect.X + pad;
            var w = rect.Width;
            var colTime = x0;
            var colTarget = x0 + w * 0.14f;
            var colFilter = x0 + w * 0.55f;
            var colHfd = x0 + w * 0.73f;
            var colStars = x0 + w * 0.88f;
            var smallFs = fontSize * 0.75f;
            var rowFs = fontSize * 0.8f;

            FillRect(rect.X, colY, rect.Width, rowH, HeaderBg);
            DrawText("Time", fontPath, colTime, colY, colTarget - colTime, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Target", fontPath, colTarget, colY, colFilter - colTarget, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Filter", fontPath, colFilter, colY, colHfd - colFilter, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("HFD", fontPath, colHfd, colY, colStars - colHfd, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("\u2605", fontPath, colStars, colY, rect.X + rect.Width - colStars - pad, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);

            var log = state.ExposureLog;
            if (log.Length == 0)
            {
                DrawText("No frames yet", fontPath,
                    rect.X, colY + rowH, rect.Width, rowH * 2,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var y = colY + rowH + pad;
            var visibleRows = (int)((rect.Height - rowH * 2 - pad * 2) / rowH);

            if (state.ExposureLogScrollOffset < 0)
            {
                state.ExposureLogScrollOffset = 0;
            }

            var startIdx = Math.Max(0, log.Length - visibleRows - state.ExposureLogScrollOffset);
            if (startIdx < 0)
            {
                startIdx = 0;
            }

            for (var i = startIdx; i < log.Length && y < rect.Y + rect.Height - rowH; i++)
            {
                var entry = log[i];
                var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                FillRect(rect.X, y, rect.Width, rowH, bg);

                var target = entry.TargetName.Length > 10 ? entry.TargetName[..10] : entry.TargetName;
                var filter = entry.FilterName.Length > 6 ? entry.FilterName[..6] : entry.FilterName;
                var hfd = entry.MedianHfd > 0 ? $"{entry.MedianHfd:F1}\"" : "--";
                var stars = entry.StarCount > 0 ? $"{entry.StarCount}" : "--";

                DrawText(entry.Timestamp.ToString("HH:mm"), fontPath, colTime, y, colTarget - colTime, rowH, rowFs, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(target, fontPath, colTarget, y, colFilter - colTarget, rowH, rowFs, BodyText, TextAlign.Near, TextAlign.Center);
                DrawText(filter, fontPath, colFilter, y, colHfd - colFilter, rowH, rowFs, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(hfd, fontPath, colHfd, y, colStars - colHfd, rowH, rowFs, BodyText, TextAlign.Far, TextAlign.Center);
                DrawText(stars, fontPath, colStars, y, rect.X + rect.Width - colStars - pad, rowH, rowFs, BodyText, TextAlign.Far, TextAlign.Center);
                y += rowH;
            }

            // Focus history below exposure log if space allows
            var remainH = rect.Y + rect.Height - y;
            if (remainH > rowH * 3 && state.FocusHistory.Length > 0)
            {
                FillRect(rect.X, y, rect.Width, 1, SeparatorColor);
                y += pad;

                DrawText("Focus History", fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                var history = state.FocusHistory;
                var focusStartIdx = Math.Max(0, history.Length - (int)((remainH - rowH * 2) / rowH));
                for (var i = focusStartIdx; i < history.Length && y < rect.Y + rect.Height - rowH; i++)
                {
                    var row = LiveSessionActions.FormatFocusHistoryRow(history[i], state.SiteTimeZone);
                    var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                    FillRect(rect.X, y, rect.Width, rowH, bg);
                    DrawText(row, fontPath,
                        rect.X + pad, y, rect.Width - pad * 2, rowH,
                        fontSize * 0.75f, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Abort confirmation overlay
        // -----------------------------------------------------------------------

        private void RenderAbortConfirm(RectF32 contentRect, string fontPath, float fontSize, float dpiScale)
        {
            var stripH = 40f * dpiScale;
            var stripY = contentRect.Y + (contentRect.Height - stripH) / 2;

            // Semi-transparent backdrop (darken)
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x00, 0x00, 0x00, 0x88));

            // Confirm strip
            FillRect(contentRect.X, stripY, contentRect.Width, stripH, ConfirmStripBg);
            DrawText("Abort session? Press Enter to confirm, Escape to cancel", fontPath,
                contentRect.X, stripY, contentRect.Width, stripH,
                fontSize, AbortText, TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Polar alignment: precondition gating + side panel
        // -----------------------------------------------------------------------

        /// <summary>
        /// Surface-level precondition check for the toolbar PA button. Mirrors the
        /// authoritative check in <c>AppSignalHandler.StartPolarAlignmentSignal</c> —
        /// the handler re-validates and returns its own notifications so this is
        /// purely a UX hint about whether the click will succeed.
        /// </summary>
        private static (bool Enabled, string Reason) EvaluatePolarPreconditions(LiveSessionState state)
        {
            // Routine already running -> the PA button becomes a Cancel; the caller
            // handles that via the "active" branch. Treat as enabled here.
            if (state.Mode == LiveSessionMode.PolarAlign)
            {
                return (true, "Cancel polar alignment");
            }
            if (state.OtaCount == 0)
            {
                return (false, "Polar align: no OTA configured");
            }
            if (string.IsNullOrEmpty(state.PreviewMountDisplayName))
            {
                return (false, "Polar align: connect a mount first");
            }
            return (true, "Start polar alignment");
        }

        /// <summary>
        /// Side panel for polar-align mode: status pill, error needles, exposure
        /// indicator, IsSettled / IsAligned LEDs, direction-hint badges, Cancel /
        /// Done buttons. Replaces the right-hand exposure-log panel while
        /// <see cref="LiveSessionMode.PolarAlign"/> is active.
        /// </summary>
        private void RenderPolarSidePanel(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            var x0 = rect.X + pad;
            var w = rect.Width - pad * 2;

            // Header
            DrawText("Polar Alignment", fontPath,
                x0, rect.Y, w, rowH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // Source toggle: [Main | Guider] -- moved here from the toolbar G button
            // so the choice lives next to its consumers (the polar side panel).
            // Switching mid-run would invalidate the Phase A v1 anchor frame, so the
            // buttons are inert (DimText) while polar is actually running; users can
            // still preview which source is active.
            var sourceY = rect.Y + rowH;
            var sourceLabelW = w * 0.30f;
            var srcBtnW = (w - sourceLabelW) / 2f;
            var canSwitchSource = state.PolarPhase == PolarAlignmentPhase.Idle
                || state.PolarPhase == PolarAlignmentPhase.Failed;
            DrawText("Source", fontPath,
                x0, sourceY, sourceLabelW, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            var activeSrcBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveSrcBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            var srcFg = canSwitchSource ? BodyText : DimText;

            RenderButton("Main",
                x0 + sourceLabelW, sourceY + 2, srcBtnW - 2, rowH - 4,
                fontPath, fontSize * 0.85f,
                state.PolarAlignUseGuider ? inactiveSrcBg : activeSrcBg,
                srcFg, "PolarSrcMain",
                _ =>
                {
                    if (canSwitchSource && state.PolarAlignUseGuider)
                    {
                        state.PolarAlignUseGuider = false;
                        state.NeedsRedraw = true;
                    }
                });
            RenderButton("Guider",
                x0 + sourceLabelW + srcBtnW, sourceY + 2, srcBtnW - 2, rowH - 4,
                fontPath, fontSize * 0.85f,
                state.PolarAlignUseGuider ? activeSrcBg : inactiveSrcBg,
                srcFg, "PolarSrcGuider",
                _ =>
                {
                    if (canSwitchSource && !state.PolarAlignUseGuider)
                    {
                        state.PolarAlignUseGuider = true;
                        state.NeedsRedraw = true;
                    }
                });

            // Setup phase: routine not yet started -> render the configuration
            // form + Start button instead of the running-phase status / gauges.
            // The Start button posts StartPolarAlignmentSignal with a snapshot
            // of state.PolarSetupConfig as the captured Configuration.
            if (state.PolarPhase == PolarAlignmentPhase.Idle && state.PolarAlignmentCts is null)
            {
                RenderPolarSetupRows(state, rect, x0, sourceY + rowH + pad, w, rowH, fontPath, fontSize, pad);
                return;
            }

            // Phase pill
            var phaseY = sourceY + rowH + pad;
            var (phaseLabel, phaseColor) = state.PolarPhase switch
            {
                PolarAlignmentPhase.Idle => ("IDLE", DimText),
                PolarAlignmentPhase.ProbingExposure => ("PROBING", StatusSolving),
                PolarAlignmentPhase.Rotating => ("ROTATING", StatusSlewing),
                PolarAlignmentPhase.Frame2 => ("FRAME 2", StatusSolving),
                PolarAlignmentPhase.Refining => ("REFINING", StatusTracking),
                PolarAlignmentPhase.Aligned => ("ALIGNED", new RGBAColor32(0x44, 0xff, 0x44, 0xff)),
                PolarAlignmentPhase.RestoringMount => ("RESTORING", StatusSlewing),
                PolarAlignmentPhase.Failed => ("FAILED", AbortBg),
                _ => ("?", DimText)
            };
            FillRect(x0, phaseY, w, rowH, phaseColor);
            DrawText(phaseLabel, fontPath,
                x0, phaseY, w, rowH,
                fontSize * 0.95f, BrightText, TextAlign.Center, TextAlign.Center);

            var y = phaseY + rowH + pad;

            // Status message
            if (state.PolarStatusMessage is { Length: > 0 } status)
            {
                var statusH = rowH * 2;
                DrawText(status, fontPath,
                    x0, y, w, statusH,
                    fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Near);
                y += statusH + pad;
            }

            // Phase A info: locked exposure + chord-angle sanity readout
            if (state.PolarPhaseAResult is { Success: true } phaseA)
            {
                var lockedMs = phaseA.LockedExposure.TotalMilliseconds;
                DrawText(
                    $"Locked: {lockedMs:F0}ms  ({phaseA.StarsMatchedFrame1}/{phaseA.StarsMatchedFrame2} stars)",
                    fontPath,
                    x0, y, w, rowH,
                    fontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                var chordObsArcsec = phaseA.ChordAngleObservedRad * 180.0 / Math.PI * 3600.0;
                var chordPredArcsec = phaseA.ChordAnglePredictedRad * 180.0 / Math.PI * 3600.0;
                var chordDiff = Math.Abs(chordObsArcsec - chordPredArcsec);
                var chordColor = chordDiff < 5 ? StatusTracking : chordDiff < 30 ? StatusSlewing : AbortBg;
                DrawText($"Chord \u0394: {chordDiff:F1}\u2033", fontPath,
                    x0, y, w, rowH,
                    fontSize * 0.78f, chordColor, TextAlign.Near, TextAlign.Center);
                y += rowH + pad;
            }

            // Live solve gauges (Az / Alt error needles)
            if (state.LastPolarSolve is { } solve)
            {
                y = RenderPolarErrorGauges(state, solve, x0, y, w, rowH, fontPath, fontSize, pad);
            }

            // Cancel / Done buttons (bottom of panel). The Cancel button gates on
            // three states: idle (greyed), active (red, clickable), and
            // cancellation-in-flight (amber, disabled, "Cancelling..." label).
            // The intermediate state covers the gap between the user's click and
            // the session's RestoringMount cleanup completing -- otherwise a
            // mash of repeated clicks could fire multiple Cancel signals or the
            // user wouldn't know the request was actually picked up.
            var buttonY = rect.Y + rect.Height - rowH * 2 - pad * 2;
            var halfW = (w - pad) / 2;
            var cancelInFlight = state.PolarAlignmentCts is { IsCancellationRequested: true };
            var canCancel = state.Mode == LiveSessionMode.PolarAlign
                && state.PolarPhase != PolarAlignmentPhase.Idle
                && !cancelInFlight;
            var canDone = state.PolarPhase == PolarAlignmentPhase.Aligned
                || (state.PolarPhase == PolarAlignmentPhase.Refining && state.LastPolarSolve is { IsSettled: true, IsAligned: true });

            // Amber for "in progress" -- distinct from the active red and the
            // disabled grey so the state is unambiguous at a glance.
            var cancellingBg = new RGBAColor32(0xc4, 0x8a, 0x2c, 0xff);
            var (cancelLabel, cancelBg, cancelFg) = cancelInFlight
                ? ("Cancelling\u2026", cancellingBg, BrightText)
                : canCancel
                    ? ("Cancel", AbortBg, AbortText)
                    : ("Cancel", new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText);

            RenderButton(cancelLabel, x0, buttonY, halfW, rowH * 1.5f,
                fontPath, fontSize * 0.9f,
                cancelBg, cancelFg,
                "PolarCancel",
                _ => { if (canCancel) PostSignal(new CancelPolarAlignmentSignal()); });

            RenderButton("Done", x0 + halfW + pad, buttonY, halfW, rowH * 1.5f,
                fontPath, fontSize * 0.9f,
                canDone ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff),
                canDone ? BrightText : DimText,
                "PolarDone",
                _ => { if (canDone) PostSignal(new DonePolarAlignmentSignal()); });
        }

        /// <summary>
        /// Setup-phase rendering: numeric +/- rows for the headline polar-align
        /// tunables, an "On done" cycle button, a "Save frames" toggle, and a
        /// prominent Start button. Posts <c>StartPolarAlignmentSignal</c> with
        /// the captured <c>state.PolarSetupConfig</c> when the user clicks Start.
        /// </summary>
        private void RenderPolarSetupRows(
            LiveSessionState state, RectF32 rect,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float pad)
        {
            var labelW = w * 0.42f;
            var btnW = (w - labelW) / 4f;
            var valW = (w - labelW) / 2f;
            var smallFs = fontSize * 0.78f;

            // ---- Rotation (DeltaRaDeg) ---------------------------------------
            y = RenderConfigRow(
                "Rotation", $"{state.PolarSetupConfig.RotationDeg:F0}\u00B0",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, pad,
                "PolarSetupRotMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.RotationDeg - 15.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Max(15.0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupRotPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.RotationDeg + 15.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Min(180.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Settle (SettleSeconds) --------------------------------------
            y = RenderConfigRow(
                "Settle", $"{state.PolarSetupConfig.SettleSeconds:F0}s",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, pad,
                "PolarSetupSettleMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.SettleSeconds - 1.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Max(0.0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupSettlePlus",
                () =>
                {
                    var v = state.PolarSetupConfig.SettleSeconds + 1.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Min(30.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Target accuracy (TargetAccuracyArcmin) ----------------------
            y = RenderConfigRow(
                "Target acc", $"{state.PolarSetupConfig.TargetAccuracyArcmin:F1}\u2032",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, pad,
                "PolarSetupAccMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.TargetAccuracyArcmin - 0.5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Max(0.5, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupAccPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.TargetAccuracyArcmin + 0.5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Min(10.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Min stars for solve -----------------------------------------
            y = RenderConfigRow(
                "Min stars", $"{state.PolarSetupConfig.MinStarsForSolve}",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, pad,
                "PolarSetupMinStarsMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.MinStarsForSolve - 5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Max(5, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupMinStarsPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.MinStarsForSolve + 5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Min(100, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Re-seed interval (RefineFullSolveInterval) ------------------
            // 0 reads as "off" -- the orchestrator skips the periodic full-solve
            // re-seed and relies entirely on the residual-spike fallback.
            var reseedText = state.PolarSetupConfig.RefineFullSolveInterval <= 0
                ? "off"
                : $"{state.PolarSetupConfig.RefineFullSolveInterval}";
            y = RenderConfigRow(
                "Re-seed every", reseedText,
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, pad,
                "PolarSetupReseedMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.RefineFullSolveInterval - 10;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Max(0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupReseedPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.RefineFullSolveInterval + 10;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Min(200, v) };
                    state.NeedsRedraw = true;
                });

            y += pad;

            // ---- On-done cycle (ReverseAxisBack / Park / LeaveInPlace) -------
            var onDoneLabel = state.PolarSetupConfig.OnDone switch
            {
                PolarAlignmentOnDone.ReverseAxisBack => "Reverse",
                PolarAlignmentOnDone.Park => "Park",
                PolarAlignmentOnDone.LeaveInPlace => "Leave",
                _ => "?"
            };
            DrawText("On done", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            RenderButton(onDoneLabel, x0 + labelW, y + 2, w - labelW, rowH - 4,
                fontPath, smallFs,
                new RGBAColor32(0x44, 0x66, 0x99, 0xff), BodyText,
                "PolarSetupOnDone",
                _ =>
                {
                    var next = state.PolarSetupConfig.OnDone switch
                    {
                        PolarAlignmentOnDone.ReverseAxisBack => PolarAlignmentOnDone.Park,
                        PolarAlignmentOnDone.Park => PolarAlignmentOnDone.LeaveInPlace,
                        _ => PolarAlignmentOnDone.ReverseAxisBack,
                    };
                    state.PolarSetupConfig = state.PolarSetupConfig with { OnDone = next };
                    state.NeedsRedraw = true;
                });
            y += rowH;

            // ---- Save frames toggle ------------------------------------------
            var saveLabel = state.PolarSetupConfig.SaveFrames ? "On" : "Off";
            var saveBg = state.PolarSetupConfig.SaveFrames
                ? new RGBAColor32(0x44, 0x66, 0x99, 0xff)
                : new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            DrawText("Save frames", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            RenderButton(saveLabel, x0 + labelW, y + 2, w - labelW, rowH - 4,
                fontPath, smallFs, saveBg, BodyText,
                "PolarSetupSaveFrames",
                _ =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { SaveFrames = !state.PolarSetupConfig.SaveFrames };
                    state.NeedsRedraw = true;
                });
            y += rowH;

            // ---- Incremental-solver toggle (diagnostic / safe fallback) ------
            var incLabel = state.PolarSetupConfig.UseIncrementalSolver ? "On" : "Off";
            var incBg = state.PolarSetupConfig.UseIncrementalSolver
                ? new RGBAColor32(0x44, 0x66, 0x99, 0xff)
                : new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            DrawText("Incremental", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            RenderButton(incLabel, x0 + labelW, y + 2, w - labelW, rowH - 4,
                fontPath, smallFs, incBg, BodyText,
                "PolarSetupUseIncremental",
                _ =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { UseIncrementalSolver = !state.PolarSetupConfig.UseIncrementalSolver };
                    state.NeedsRedraw = true;
                });

            // ---- Start button (anchored at panel bottom, full width) ---------
            // Cancel-back-to-Preview lives above Start so the Start button sits
            // in the muscle-memory location for "primary action" (bottom).
            var buttonY = rect.Y + rect.Height - rowH * 3 - pad * 3;
            RenderButton("Cancel", x0, buttonY, w, rowH * 1.2f,
                fontPath, fontSize * 0.85f,
                new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText,
                "PolarSetupBack",
                _ =>
                {
                    state.Mode = LiveSessionMode.Preview;
                    state.PolarStatusMessage = "";
                    state.NeedsRedraw = true;
                });
            buttonY += rowH * 1.2f + pad;

            // Authoritative pre-flight check mirrors EvaluatePolarPreconditions;
            // disabled-grey button gives the user a hint without burying them
            // in the side panel's status line.
            var (canStart, _) = EvaluatePolarPreconditions(state);
            var startBg = canStart ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff);
            var startFg = canStart ? BrightText : DimText;
            RenderButton("Start", x0, buttonY, w, rowH * 1.6f,
                fontPath, fontSize,
                startBg, startFg,
                "PolarSetupStart",
                _ =>
                {
                    if (!canStart) return;
                    var miniIdx = MiniViewer?.State.SelectedCameraIndex ?? -1;
                    var otaIdx = miniIdx >= 0 ? miniIdx : 0;
                    PostSignal(new StartPolarAlignmentSignal(
                        OtaIndex: otaIdx,
                        DeltaRaDeg: state.PolarSetupConfig.RotationDeg,
                        UseGuider: state.PolarAlignUseGuider,
                        Configuration: state.PolarSetupConfig));
                });
        }

        /// <summary>
        /// One row of the polar-align setup form: dim label + [-] + value display +
        /// [+]. Returns the y-cursor advanced past the row.
        /// </summary>
        private float RenderConfigRow(
            string label, string valueText,
            float x0, float y, float labelW, float btnW, float valW, float rowH,
            string fontPath, float fontSize, float pad,
            string minusAction, Action onMinus,
            string plusAction, Action onPlus)
        {
            DrawText(label, fontPath, x0, y, labelW, rowH, fontSize, DimText, TextAlign.Near, TextAlign.Center);
            var btnBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            RenderButton("-", x0 + labelW, y + 2, btnW - 2, rowH - 4,
                fontPath, fontSize, btnBg, BodyText, minusAction, _ => onMinus());
            DrawText(valueText, fontPath, x0 + labelW + btnW, y, valW, rowH, fontSize, BrightText, TextAlign.Center, TextAlign.Center);
            RenderButton("+", x0 + labelW + btnW + valW, y + 2, btnW - 2, rowH - 4,
                fontPath, fontSize, btnBg, BodyText, plusAction, _ => onPlus());
            return y + rowH;
        }

        private float RenderPolarErrorGauges(
            LiveSessionState state,
            LiveSolveResult solve,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float pad)
        {
            const double radToArcmin = 60.0 * 180.0 / Math.PI;
            var azArcmin = solve.SmoothedAzErrorRad * radToArcmin;
            var altArcmin = solve.SmoothedAltErrorRad * radToArcmin;

            DrawText("Az error", fontPath,
                x0, y, w, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            y = RenderErrorBar(azArcmin, x0, y + rowH, w, rowH * 0.6f, fontPath, fontSize);
            y += pad;

            DrawText("Alt error", fontPath,
                x0, y, w, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            y = RenderErrorBar(altArcmin, x0, y + rowH, w, rowH * 0.6f, fontPath, fontSize);
            y += pad;

            // Direction hint badges (where to push the knobs).
            var azHint = azArcmin >= 0 ? "\u2192 East" : "\u2190 West";
            var altHint = altArcmin >= 0 ? "\u2191 Up" : "\u2193 Down";
            DrawText($"Az: {azHint} {Math.Abs(azArcmin):F1}\u2032", fontPath,
                x0, y, w, rowH,
                fontSize * 0.85f, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH;
            DrawText($"Alt: {altHint} {Math.Abs(altArcmin):F1}\u2032", fontPath,
                x0, y, w, rowH,
                fontSize * 0.85f, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH + pad;

            // Exposure / star indicator
            DrawText(
                $"{solve.ExposureUsed.TotalMilliseconds:F0}ms  {solve.StarsMatched} stars"
                + (solve.ConsecutiveFailedSolves > 0 ? $"  ({solve.ConsecutiveFailedSolves} fail)" : ""),
                fontPath,
                x0, y, w, rowH,
                fontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // IsSettled / IsAligned LEDs
            var ledY = y;
            var ledSize = rowH * 0.6f;
            var settledColor = solve.IsSettled ? StatusTracking : DimText;
            var alignedColor = solve.IsAligned ? new RGBAColor32(0x44, 0xff, 0x44, 0xff) : DimText;
            FillRect(x0, ledY + (rowH - ledSize) / 2, ledSize, ledSize, settledColor);
            DrawText("Settled", fontPath,
                x0 + ledSize + pad, ledY, w - ledSize - pad, rowH,
                fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
            ledY += rowH;
            FillRect(x0, ledY + (rowH - ledSize) / 2, ledSize, ledSize, alignedColor);
            DrawText("Aligned", fontPath,
                x0 + ledSize + pad, ledY, w - ledSize - pad, rowH,
                fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
            return ledY + rowH;
        }

        private float RenderErrorBar(double arcmin, float x, float y, float w, float h, string fontPath, float fontSize)
        {
            // Centred zero-line bar; needle position scaled to a +/- 30' span (clamped).
            FillRect(x, y, w, h, GraphBg);
            var midX = x + w / 2;
            FillRect(midX, y, 1, h, SeparatorColor);

            const double FullScaleArcmin = 30.0;
            var clamped = Math.Clamp(arcmin, -FullScaleArcmin, FullScaleArcmin);
            var fraction = (float)(clamped / FullScaleArcmin);

            var absArcmin = Math.Abs(arcmin);
            var color = absArcmin < 1.0 ? StatusTracking
                : absArcmin < 5.0 ? StatusSlewing
                : AbortBg;
            var needleX = midX + fraction * (w / 2);
            var needleW = Math.Max(2f, h * 0.25f);
            FillRect(needleX - needleW / 2, y, needleW, h, color);

            DrawText($"{arcmin:+0.0;-0.0}\u2032", fontPath,
                x, y, w, h,
                fontSize * 0.8f, BrightText, TextAlign.Center, TextAlign.Center);
            return y + h;
        }
    }
}
