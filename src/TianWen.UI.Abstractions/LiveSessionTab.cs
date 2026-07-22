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
    /// <para>
    /// Split by concern across partials (the <c>ImageRendererBase</c> convention): this core file
    /// holds the shared state/colours and the <c>Render</c> orchestration, with one file each for
    /// <c>.Input</c> (mouse + keyboard), <c>.Strips</c> (top strip, timeline, bottom strip,
    /// abort-confirm), <c>.Panels</c> (per-OTA panels, cooling sparkline, exposure log),
    /// <c>.Charts</c> (guide graph, V-curve), <c>.Preview</c> (mini-viewer toolbar, preview
    /// timeline, capture controls, mount section), and <c>.Polar</c> (polar-alignment mode).
    /// Add a new concern as a new partial; don't grow this file back into a monolith.
    /// </para>
    /// </summary>
    public partial class LiveSessionTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        /// <summary>The live session state for keyboard handling. Set during Render.</summary>
        public LiveSessionState? State { get; set; }

        /// <summary>
        /// The shared full image viewer (same widget as the FITS viewer / planetary tab) used to show the
        /// last captured frame in Preview / PolarAlign modes. Set by the host. Configured chromeless
        /// (<see cref="ViewerState.HideChrome"/>) with a lightweight <see cref="LiveFramePreviewSource"/> feed,
        /// so it is a strict superset of the old mini viewer (stretch / WB / grid / zoom-pan / WCS overlays).
        /// </summary>
        public ImageRendererBase<TSurface>? PreviewView { get; set; }

        /// <summary>Lightweight live-frame source feeding <see cref="PreviewView"/> (subsampled stats, no per-frame document).</summary>
        private readonly LiveFramePreviewSource _previewSource = new();

        /// <summary>Per-instance viewer state for the embedded preview: chromeless, image-only (no info panel / file list / histogram).</summary>
        private readonly ViewerState _previewState = new()
        {
            HideChrome = true,
            ShowInfoPanel = false,
            ShowFileList = false,
            ShowHistogram = false,
        };

        /// <summary>Full planetary capture view (viewer + control strip), shown in <see cref="LiveSessionMode.Planetary"/>. Set by the host.</summary>
        public IPlanetaryViewWidget? PlanetaryView { get; set; }

        /// <summary>The live planetary capture controller driving <see cref="LiveSessionMode.Planetary"/>. Set by the host (DI singleton).</summary>
        public PlanetaryCaptureController? PlanetaryCapture { get; set; }

        /// <summary>Tracks which image reference is currently displayed to avoid redundant uploads.</summary>
        private Image? _displayedImage;

        /// <summary>
        /// Preview pan + cursor-anchored zoom (DIR.Lib): the controller owns the gesture and the zoom
        /// math (this used to be a byte-for-byte copy of the viewer's formula); the display transform
        /// stays on <see cref="_previewState"/>, seeded per gesture and written back. Clamps match the
        /// historical preview behaviour ([0.1, 16]).
        /// </summary>
        private readonly PanZoomController _previewPanZoom = new PanZoomController { MinZoom = 0.1f, MaxZoom = 16f };

        /// <summary>Cached mini viewer image rect for center-point zoom.</summary>
        private RectF32 _viewerImageRect;

        // Layout constants (at 1x scale)
        private static readonly float BaseFontSize = GuiTheme.Metrics.BaseFontSize;
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
        private static readonly RGBAColor32 ContentBg        = GuiTheme.Palette.ContentBg;
        private static readonly RGBAColor32 PanelBg          = GuiTheme.Palette.PanelBg;
        private static readonly RGBAColor32 HeaderBg         = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText       = GuiTheme.Palette.HeaderText;
        private static readonly RGBAColor32 BodyText         = GuiTheme.Palette.BodyText;
        private static readonly RGBAColor32 DimText          = GuiTheme.Palette.DimText;
        private static readonly RGBAColor32 BrightText       = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 SeparatorColor   = GuiTheme.Palette.Separator;
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

        // Shared stepper button styling for the preview capture controls (exposure / gain). Values are
        // design units -- the layout engine scales the [-]/[+] button width and glyph font by dpiScale.
        // The disabled colours are unused (the preview steppers are always enabled) but the style record
        // requires them. Button bg matches the former local stepBg.
        private static readonly FormRowLayout.StepperStyle PreviewStepperStyle = new(
            new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff), BodyText,
            new RGBAColor32(0x33, 0x33, 0x33, 0xff), DimText,
            BaseFontSize * 0.85f, 28f);

        /// <summary>Render the complete live session tab.</summary>
        public void Render(
            LiveSessionState state,
            RectF32 contentRect,
            string fontPath,
            ITimeProvider timeProvider)
        {
            State = state;
            BeginFrame();

            var dpiScale = DpiScale;
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
            RenderTopStrip(state, topRect, fontPath, fs, timeProvider);

            // Planetary mode: the full image viewer + capture strip own everything below the top strip and
            // reuse the focuser/mount controls via signals -- the session timeline / OTA / exposure-log panels
            // don't apply, so render the planetary view (which draws its own chrome) and return. The mode-pill
            // dropdown overlay is re-drawn here so mode switching still works.
            if (state.Mode == LiveSessionMode.Planetary)
            {
                var planetaryRect = new RectF32(contentRect.X, contentRect.Y + topH,
                    contentRect.Width, contentRect.Height - topH);
                if (PlanetaryView is { } planetaryView)
                {
                    // Planetary captures a single OTA (index 0); hand its focuser telemetry to the view so the
                    // control panel can show position + jog (the jog posts JogFocuserSignal(0, ...)).
                    var focuser = state.PreviewOTATelemetry.Length > 0
                        ? state.PreviewOTATelemetry[0]
                        : PreviewOTATelemetry.Unknown;
                    planetaryView.RenderPlanetary(PlanetaryCapture, focuser, planetaryRect, fontPath);
                }
                else
                {
                    FillRect(planetaryRect.X, planetaryRect.Y, planetaryRect.Width, planetaryRect.Height, GraphBg);
                    DrawText("Planetary view unavailable", fontPath,
                        planetaryRect.X, planetaryRect.Y, planetaryRect.Width, planetaryRect.Height,
                        fs, DimText, TextAlign.Center, TextAlign.Center);
                }

                if (state.ModeDropdown.IsOpen)
                {
                    RenderDropdownMenu(state.ModeDropdown, fontPath, fs,
                        bgColor: GuiTheme.Palette.HeaderBg,
                        highlightColor: new RGBAColor32(0x44, 0x66, 0x99, 0xff),
                        textColor: BodyText,
                        borderColor: new RGBAColor32(0x44, 0x44, 0x55, 0xff),
                        viewportWidth: Renderer.Width,
                        viewportHeight: Renderer.Height);
                }

                // Prompts can fire in any mode -- draw the overlay here too so the planetary early-return
                // path doesn't swallow it (future dark-frame flows).
                if (state.PendingPrompt is { } planetaryPrompt)
                {
                    RenderSessionPrompt(contentRect, planetaryPrompt, fontPath, fs);
                }
                return;
            }

            // Timeline: phase bars + observation segments + now needle
            var timelineRect = new RectF32(contentRect.X, contentRect.Y + topH, contentRect.Width, timelineH);
            RenderTimeline(state, timelineRect, fontPath, fs, timeProvider);

            // Bottom strip: compact guide graph + RMS + ABORT
            var botRect = new RectF32(contentRect.X, contentRect.Y + contentRect.Height - botH, contentRect.Width, botH);
            RenderBottomStrip(state, botRect, fontPath);

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
            if (viewerW > 100 && PreviewView is { } viewer)
            {
                var pst = _previewState;

                // Polar mode publishes a refreshed WcsAnnotation each frame from the latest live solve so the
                // viewer overlays the pole crosses, 5'/15'/30' rings, and axis CircledCross on the live frame.
                // Before the first Phase A solve completes (LastPolarSolve still null), fall back to the
                // lightweight "J2000 pole preview" so the user already sees the crosshair + 30' ring while the
                // rotation runs. Cleared when polar mode exits so the annotation doesn't leak. The annotation
                // is on the WIDGET; its projection WCS comes from OverrideWcs (set below) since a live frame
                // is document-less.
                if (state.Mode == LiveSessionMode.PolarAlign)
                {
                    if (state.LastPolarSolve is { Overlay: { } overlay })
                    {
                        viewer.Annotation = PolarAnnotationBuilder.Build(overlay);
                    }
                    else if (state.PreviewPlateSolveResult?.Solution is { } wcs)
                    {
                        viewer.Annotation = PolarAnnotationBuilder.BuildJ2000PolePreview(wcs.CenterDec);
                    }
                    else
                    {
                        viewer.Annotation = WcsAnnotation.Empty;
                    }
                    // Freeze stretch only while refining: probe rungs ramp exposure 50x (100ms -> 5000ms) and
                    // the stretch must track each rung. Once locked at one exposure for the refining loop the
                    // stretch is stable, so we freeze to skip the per-frame subsampled scan on the UI thread.
                    pst.FreezeStretchStats = state.PolarPhase
                        is PolarAlignmentPhase.Refining
                        or PolarAlignmentPhase.Aligned;
                }
                else
                {
                    viewer.Annotation = WcsAnnotation.Empty;
                    pst.FreezeStretchStats = false;
                }

                // Check if a new frame arrived for the selected camera
                var images = state.LastCapturedImages;
                var selectedIdx = pst.SelectedCameraIndex;
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
                    // Normalise + (unless frozen) re-scan stats into the live source, then flag a re-upload.
                    _previewSource.AcceptFrame(latestImage, pst.FreezeStretchStats);
                    pst.NeedsTextureUpdate = true;
                    // A fresh frame invalidates any prior solve until the user requests a new plate solve.
                    // Without this, the grid would be drawn over a frame the WCS doesn't actually describe.
                    viewer.OverrideWcs = null;
                }

                // Bind the live preview's plate-solve result whenever the displayed frame is the one that
                // produced it. The signal handler stores the solve into LiveSessionState; we hand it to the
                // viewer as the projection WCS (the live source is document-less, so OverrideWcs is how the
                // GLSL grid + WcsAnnotation paths get a WCS).
                if (state.PreviewPlateSolveResult?.Solution is { } solveWcs
                    && _displayedImage is not null
                    && ReferenceEquals(_displayedImage, latestImage))
                {
                    viewer.OverrideWcs = solveWcs;
                }

                // Image area (below where the preview toolbar goes). The viewer projects over the full surface
                // and arranges its (chromeless) layout within this content rect.
                var toolbarH = BaseRowHeight * dpiScale;
                var imageRect = new RectF32(viewerX, mainY + toolbarH, viewerW, mainH - toolbarH);
                _viewerImageRect = imageRect;

                viewer.SetSurfaceSize((uint)Renderer.Width, (uint)Renderer.Height);
                viewer.SetContentRegion(imageRect);
                if (pst.NeedsTextureUpdate && _previewSource.Width > 0)
                {
                    viewer.UploadDocumentTextures(_previewSource, pst);
                }
                viewer.Render(_previewSource, pst);
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
            RenderOTAPanels(state, otaRect, fontPath, fs, pad, rowH, timeProvider);

            // Right: exposure log (paints over viewer overflow on the right)
            var logX = contentRect.X + contentRect.Width - logW;
            if (logW > 0)
            {
                var rightRect = new RectF32(logX, mainY, logW, mainH);
                RenderExposureLog(state, rightRect, fontPath, fs, pad, rowH);
            }

            // Preview toolbar (on top of the image, after panels)
            if (viewerW > 100 && PreviewView is not null)
            {
                var toolbarH = BaseRowHeight * dpiScale;
                var toolbarRect = new RectF32(viewerX, mainY, viewerW, toolbarH);
                RenderMiniViewerToolbar(_previewState, toolbarRect, fontPath, fs);
            }

            // Abort confirmation overlay
            if (state.ShowAbortConfirm)
            {
                RenderAbortConfirm(contentRect, fontPath, fs * 1.1f);
            }

            // Mode dropdown overlay -- rendered LAST so its hit regions win paint-order
            // hit testing. Backdrop closes on click-outside. Width and anchor are set
            // by the trigger inside RenderTopStrip.
            if (state.ModeDropdown.IsOpen)
            {
                var dropdownBg = GuiTheme.Palette.HeaderBg;
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

            // Session-driven user prompt (e.g. "switch on the manual panel") -- topmost overlay so its
            // buttons win paint-order hit testing over everything below.
            if (state.PendingPrompt is { } pendingPrompt)
            {
                RenderSessionPrompt(contentRect, pendingPrompt, fontPath, fs);
            }
        }

        // -----------------------------------------------------------------------
        // Mini viewer toolbar: [Fit] [1:1] [T] [S] [B]
        // -----------------------------------------------------------------------

        /// <summary>
        /// Cycles the preview viewer's stretch mode None -&gt; Unlinked -&gt; Linked -&gt; Luma -&gt; None (the
        /// embedded preview's [T] button + key). Preserves the old mini viewer's 4-way cycle; the full viewer's
        /// own [T] is a 2-way toggle, but the preview keeps the cycle so all modes stay reachable chromeless.
        /// </summary>
    }
}
