using System;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Top-level GPU renderer for the N.I.N.A.-style integrated GUI.
    /// Extends <see cref="PixelWidgetBase{TSurface}"/> so the sidebar, status bar, and chrome
    /// participate in the unified <see cref="PixelWidgetBase{TSurface}.RegisterClickable"/>
    /// / <see cref="PixelWidgetBase{TSurface}.HitTestAndDispatch"/> system.
    /// </summary>
    public sealed class VkGuiRenderer : PixelWidgetBase<VulkanContext>, IGuiChrome, IDisposable
    {
        private readonly VkRenderer _renderer;
        private readonly VkPlannerTab _plannerTab;
        private readonly VkEquipmentTab _equipmentTab;
        private readonly VkSessionTab _sessionTab;
        private readonly VkViewerTab _viewerTab;
        private readonly VkSkyMapTab _skyMapTab;
        private readonly VkLiveSessionTab _liveSessionTab;
        private readonly GuiderTab<VulkanContext> _guiderTab;
        private readonly VkMiniViewerWidget _guiderMiniViewer;
        private readonly VkMiniViewerWidget _miniViewer;
        private string? _fontPath;
        private string? _emojiFontPath;
        private uint _width;
        private uint _height;

        /// <summary>DPI scale factor. Set from framebuffer size / window size ratio.</summary>
        public float DpiScale { get; set; } = 1f;

        /// <summary>Exposes the planner tab for external scroll control.</summary>
        public VkPlannerTab PlannerTab => _plannerTab;

        /// <summary>Exposes the equipment tab for state access.</summary>
        public VkEquipmentTab EquipmentTab => _equipmentTab;

        /// <summary>Exposes the session tab for scroll control and state access.</summary>
        public VkSessionTab SessionTab => _sessionTab;

        /// <summary>Exposes the viewer tab for file loading and texture upload.</summary>
        public VkViewerTab ViewerTab => _viewerTab;

        /// <summary>The currently active tab as an <see cref="IPixelWidget"/> for tab-specific hit testing.</summary>
        public IPixelWidget? ActiveTab { get; private set; }

        /// <inheritdoc/>
        public EquipmentTabState EquipmentState => _equipmentTab.State;

        /// <inheritdoc/>
        public SessionTabState SessionState => _sessionTab.State;

        /// <inheritdoc/>
        public LiveSessionState LiveSessionState { get; } = new LiveSessionState();

        /// <inheritdoc/>
        public SkyMapState SkyMapState => _skyMapTab.State;

        /// <inheritdoc/>
        public RectF32 PlannerChartRect => _plannerTab.ChartRect;

        /// <inheritdoc/>
        public void PlannerEnsureVisible(int index) => _plannerTab.EnsureVisible(index);

        // Base layout constants (at 1x scale)
        private const float BaseSidebarWidth = 52f;
        private const float BaseStatusBarHeight = 28f;
        private const float BaseFontSize = 14f;

        // Scaled accessors
        private float SidebarWidth => BaseSidebarWidth * DpiScale;
        private float StatusBarHeight => BaseStatusBarHeight * DpiScale;
        private float FontSize => BaseFontSize * DpiScale;

        // Sidebar tab definitions
        private static readonly (string Icon, GuiTab Tab)[] SidebarTabs =
        [
            ("\U0001F52D", GuiTab.Equipment),    // 🔭 Equipment
            ("\U0001F4C5", GuiTab.Planner),      // 📅 Planner
            ("\U0001F30C", GuiTab.SkyMap),        // 🌌 Sky Map
            ("\U0001F4F7", GuiTab.Session),       // 📷 Session Config
            ("\U0001F4F8", GuiTab.LiveSession),   // 📸 Live Session
            ("\U0001F3AF", GuiTab.Guider),        // 🎯 Guider
        ];

        // Sidebar colors
        private static readonly RGBAColor32 SidebarBg     = new RGBAColor32(0x1a, 0x1a, 0x22, 0xff);
        private static readonly RGBAColor32 ActiveTabBg   = new RGBAColor32(0x20, 0x30, 0x50, 0xff);
        private static readonly RGBAColor32 HoverTabBg    = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
        private static readonly RGBAColor32 IconColor     = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 ActiveIcon    = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 LockedIcon    = new RGBAColor32(0x44, 0x44, 0x50, 0xff);

        // Status bar colors
        private static readonly RGBAColor32 StatusBarBg   = new RGBAColor32(0x22, 0x22, 0x28, 0xff);
        private static readonly RGBAColor32 StatusText    = new RGBAColor32(0xaa, 0xaa, 0xaa, 0xff);

        // Content area placeholder
        private static readonly RGBAColor32 ContentBg     = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x55, 0x55, 0x66, 0xff);

        public VkGuiRenderer(VkRenderer renderer, uint width, uint height, SignalBus? bus = null, ILogger? logger = null) : base(renderer)
        {
            Bus = bus;
            _renderer = renderer;
            _width = width;
            _height = height;
            _plannerTab = new VkPlannerTab(renderer) { Bus = bus };
            _equipmentTab = new VkEquipmentTab(renderer) { Bus = bus };
            _sessionTab = new VkSessionTab(renderer) { Bus = bus };
            _viewerTab = new VkViewerTab(renderer, width, height) { Bus = bus };
            _skyMapTab = new VkSkyMapTab(renderer) { Bus = bus, Logger = logger };
            _miniViewer = new VkMiniViewerWidget(renderer);
            _liveSessionTab = new VkLiveSessionTab(renderer) { Bus = bus, MiniViewer = _miniViewer };
            _guiderMiniViewer = new VkMiniViewerWidget(renderer);
            _guiderTab = new GuiderTab<VulkanContext>(renderer) { Bus = bus, GuideCameraViewer = _guiderMiniViewer };
            ResolveFontPath();
        }

        public void Resize(uint width, uint height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>
        /// Main render method. Call between BeginFrame and EndFrame.
        /// Registers sidebar tabs and status bar elements as clickable regions.
        /// </summary>
        public void Render(
            GuiAppState appState,
            PlannerState plannerState,
            ViewerState viewerState,
            ITimeProvider timeProvider)
        {
            // Force Equipment tab when no profile exists
            if (appState.ActiveProfile is null && appState.ActiveTab is not GuiTab.Equipment)
            {
                appState.ActiveTab = GuiTab.Equipment;
            }

            BeginFrame();
            _equipmentTab.FrameCount++;
            _plannerTab.FrameCount++;
            _sessionTab.FrameCount++;
            _viewerTab.FrameCount++;
            _skyMapTab.FrameCount++;
            _liveSessionTab.FrameCount++;
            _guiderTab.FrameCount++;

            ActiveTab = appState.ActiveTab switch
            {
                GuiTab.Planner => _plannerTab,
                GuiTab.Equipment => _equipmentTab,
                GuiTab.Session => _sessionTab,
                GuiTab.SkyMap => _skyMapTab,
                GuiTab.LiveSession => _liveSessionTab,
                GuiTab.Guider => _guiderTab,
                _ => null
            };

            var contentRect = GetContentArea();

            // Render the active tab content first (it may fill the full renderer surface)
            RenderContent(appState, plannerState, viewerState, timeProvider, contentRect);

            // Paint sidebar and status bar on top — these register clickable regions
            RenderSidebar(appState);
            RenderStatusBar(appState, plannerState, timeProvider);
        }

        /// <summary>
        /// Returns the content area rectangle in pixels (excluding sidebar and status bar).
        /// </summary>
        public RectF32 GetContentArea()
        {
            return new RectF32(SidebarWidth, StatusBarHeight, (float)_width - SidebarWidth, (float)_height - StatusBarHeight);
        }

        public void Dispose()
        {
            _miniViewer.Dispose();
            _viewerTab.Dispose();
            // VkRenderer is owned by the caller; do not dispose here.
        }

        // -----------------------------------------------------------------------
        // Sidebar — registers each tab as a clickable region
        // -----------------------------------------------------------------------

        private void RenderSidebar(GuiAppState appState)
        {
            var sw = SidebarWidth;
            var h = (float)_height;

            // Background
            FillRect(0, 0, sw, h, SidebarBg);

            var buttonSize = sw;
            var startY = StatusBarHeight;
            var mouseX = appState.MouseScreenPosition.X;
            var mouseY = appState.MouseScreenPosition.Y;

            var noProfile = appState.ActiveProfile is null;

            for (var i = 0; i < SidebarTabs.Length; i++)
            {
                var (icon, tab) = SidebarTabs[i];
                var btnY = startY + i * buttonSize;
                var isActive = appState.ActiveTab == tab;
                var isLocked = (noProfile && tab is not GuiTab.Equipment)
                            || (LiveSessionState.IsRunning && tab is GuiTab.Equipment);
                var isHover = !isLocked && mouseX >= 0 && mouseX < sw
                           && mouseY >= btnY && mouseY < btnY + buttonSize;

                var bgColor = isActive ? ActiveTabBg
                            : isHover  ? HoverTabBg
                                       : SidebarBg;

                FillRect(0, btnY, sw, buttonSize, bgColor);

                var textColor = isLocked ? LockedIcon
                              : isActive ? ActiveIcon
                                         : IconColor;
                var iconFont = _emojiFontPath ?? _fontPath;
                if (iconFont is not null)
                {
                    _renderer.DrawText(icon.AsSpan(), iconFont, FontSize * 1.3f,
                        textColor, new RectInt(new PointInt((int)sw, (int)(btnY + buttonSize)), new PointInt(0, (int)btnY)),
                        TextAlign.Center, TextAlign.Center);
                }

                // Register clickable region (only enabled tabs)
                if (!isLocked)
                {
                    var capturedTab = tab;
                    RegisterClickable(0, btnY, sw, buttonSize,
                        new HitResult.ButtonHit($"Tab:{tab}"),
                        _ => { appState.ActiveTab = capturedTab; appState.NeedsRedraw = true; });
                }
            }
        }

        // -----------------------------------------------------------------------
        // Status bar — registers clickable regions for interactive elements
        // -----------------------------------------------------------------------

        private void RenderStatusBar(GuiAppState appState, PlannerState plannerState, ITimeProvider timeProvider)
        {
            var w = (float)_width;
            var sbh = StatusBarHeight;

            FillRect(0, 0, w, sbh, StatusBarBg);

            if (_fontPath is null)
            {
                return;
            }

            var now = timeProvider.System.GetLocalNow().ToOffset(plannerState.SiteTimeZone);
            var clockText = now.ToString("ddd d MMM HH:mm:ss");

            // Profile name (left)
            var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
            DrawText(profileName.AsSpan(), _fontPath,
                SidebarWidth + 6f, 0, w * 0.35f, sbh,
                FontSize, StatusText, TextAlign.Near, TextAlign.Center);

            // Date navigation with night window: [<] date (HH:mm – HH:mm) [>]
            // Locked during a running session to prevent confusion
            {
                var centerX = w * 0.35f;
                var centerW = w * 0.35f;
                var arrowW = sbh; // square arrow buttons
                var arrowFontSize = FontSize * 0.9f;
                var arrowBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
                var sessionRunning = LiveSessionState.IsRunning;
                SessionState.IsSessionRunning = sessionRunning;

                // Measure the date label text to place arrows snugly around it
                var arrowInset = w * 0.06f; // tighten arrows toward center

                if (!sessionRunning)
                {
                    // [<] previous day — closer to the date text
                    RenderButton("\u25C0", centerX + arrowInset, 0, arrowW, sbh, _fontPath, arrowFontSize,
                        arrowBg, StatusText, "DatePrev",
                        _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, -1); });

                    // [>] next day — closer to the date text
                    RenderButton("\u25B6", centerX + centerW - arrowW - arrowInset, 0, arrowW, sbh, _fontPath, arrowFontSize,
                        arrowBg, StatusText, "DateNext",
                        _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, +1); });
                }

                // Date + night window label between arrows
                var labelX = centerX + arrowInset + arrowW;
                var labelW = centerW - (arrowInset + arrowW) * 2;
                var planDate = plannerState.PlanningDate ?? now;
                var isTonight = !plannerState.PlanningDate.HasValue || planDate.Date == now.Date;
                string dateStr;

                if (isTonight && plannerState.AstroDark != default)
                {
                    var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                    var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                    dateStr = $"Tonight {dark:HH:mm}\u2013{twilight:HH:mm}";
                }
                else if (plannerState.AstroDark != default)
                {
                    var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                    var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                    dateStr = $"{planDate:ddd d MMM} {dark:HH:mm}\u2013{twilight:HH:mm}";
                }
                else
                {
                    dateStr = isTonight ? "Tonight" : planDate.ToString("ddd d MMM");
                }

                var dateColor = plannerState.PlanningDate.HasValue ? new RGBAColor32(0x88, 0xcc, 0xff, 0xff) : StatusText;
                DrawText(dateStr.AsSpan(), _fontPath,
                    labelX, 0, labelW, sbh,
                    FontSize, dateColor, TextAlign.Center, TextAlign.Center);

                // Click on date label resets to tonight (disabled during session)
                if (plannerState.PlanningDate.HasValue && !sessionRunning)
                {
                    RegisterClickable(labelX, 0, labelW, sbh,
                        new HitResult.ButtonHit("DateTonight"),
                        _ => { PlannerActions.ResetPlanningDate(plannerState); });
                }
            }

            // Clock (right) — on tabs that benefit from live time display
            if (appState.ActiveTab is GuiTab.Planner or GuiTab.LiveSession or GuiTab.Guider)
            {
                DrawText(clockText.AsSpan(), _fontPath,
                    w * 0.7f, 0, w * 0.3f - 4f, sbh,
                    FontSize, StatusText, TextAlign.Far, TextAlign.Center);
            }

            // Status message — shown to the right of the profile name, left of the date area
            if (appState.StatusMessage is { Length: > 0 } msg)
            {
                var msgX = SidebarWidth + 6f + w * 0.06f;
                var msgW = w * 0.28f;
                DrawText(msg.AsSpan(), _fontPath,
                    msgX, 0, msgW, sbh,
                    FontSize * 0.85f, new RGBAColor32(0xff, 0xcc, 0x66, 0xff), TextAlign.Center, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Content area dispatch
        // -----------------------------------------------------------------------

        private void RenderContent(
            GuiAppState appState,
            PlannerState plannerState,
            ViewerState viewerState,
            ITimeProvider timeProvider,
            RectF32 contentRect)
        {
            // Poll session state for any tab that needs live data (LiveSession, Guider)
            if (appState.ActiveTab is GuiTab.LiveSession or GuiTab.Guider)
            {
                LiveSessionState.PollSession();
            }

            switch (appState.ActiveTab)
            {
                case GuiTab.Planner:
                    _plannerTab.Render(plannerState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider, appState.MouseScreenPosition,
                        _emojiFontPath);
                    break;

                case GuiTab.Equipment:
                    _equipmentTab.Render(appState, contentRect, DpiScale,
                        _fontPath ?? "monospace", _emojiFontPath);
                    break;

                case GuiTab.Session:
                    _sessionTab.Render(appState, plannerState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider);
                    break;

                case GuiTab.SkyMap:
                    // Feed the live mount snapshot into the sky map state so the
                    // reticle overlay tracks the mount without the tab needing its
                    // own poll path. Session-mode uses the session's own MountState
                    // (already populated at session's PollDeviceStatesAsync cadence);
                    // preview-mode uses PreviewMountState populated by PollPreviewTelemetry.
                    PopulateSkyMapMountOverlay(appState);
                    PopulateSkyMapMosaicPanels(appState, plannerState);
                    _skyMapTab.Render(plannerState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider);
                    break;

                case GuiTab.LiveSession:
                    // Copy twilight data from planner so preview timeline can render night window
                    if (!LiveSessionState.IsRunning)
                    {
                        LiveSessionState.AstroDark = plannerState.AstroDark;
                        LiveSessionState.AstroTwilight = plannerState.AstroTwilight;
                        LiveSessionState.CivilSet = plannerState.CivilSet;
                        LiveSessionState.CivilRise = plannerState.CivilRise;
                        LiveSessionState.NauticalSet = plannerState.NauticalSet;
                        LiveSessionState.NauticalRise = plannerState.NauticalRise;
                    }
                    _liveSessionTab.Render(LiveSessionState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider);
                    break;

                case GuiTab.Guider:
                    _guiderTab.Render(LiveSessionState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider);
                    break;

                default:
                    RenderComingSoonPlaceholder(contentRect, appState.ActiveTab);
                    break;
            }
        }

        /// <summary>
        /// Snapshots the current mount pointing into <see cref="SkyMapState.MountOverlay"/>
        /// just before the sky-map tab renders. This keeps the sky map free of any direct
        /// dependency on <see cref="LiveSessionState"/>; the tab itself only sees the tiny
        /// <see cref="SkyMapMountOverlay"/> snapshot. Picks the session's own MountState
        /// when a session is running (session drives the poll cadence itself), else the
        /// preview-mode snapshot (driven by <c>AppSignalHandler.PollPreviewTelemetry</c>).
        /// J2000 coords are preferred; native coords are used as a fallback for session
        /// mode which does not yet populate the J2000 fields.
        /// </summary>
        private void PopulateSkyMapMountOverlay(GuiAppState appState)
        {
            // Without an actual poll, default(MountState) has all-zero coords -- not
            // NaN -- so a NaN check alone would still draw a phantom reticle at (0h, 0)
            // before the first poll completes (or when no mount is configured at all).
            // Guard on the display name being set, which only happens after a successful
            // poll establishes that a mount is connected.
            var displayName = LiveSessionState.PreviewMountDisplayName;
            if (string.IsNullOrEmpty(displayName))
            {
                _skyMapTab.State.MountOverlay = null;
                return;
            }

            var ms = LiveSessionState.IsRunning
                ? LiveSessionState.MountState
                : LiveSessionState.PreviewMountState;

            if (double.IsNaN(ms.RightAscension) || double.IsNaN(ms.Declination))
            {
                _skyMapTab.State.MountOverlay = null;
                return;
            }

            var raJ2000 = !double.IsNaN(ms.RaJ2000) ? ms.RaJ2000 : ms.RightAscension;
            var decJ2000 = !double.IsNaN(ms.DecJ2000) ? ms.DecJ2000 : ms.Declination;

            // Compute sensor FOV from profile focal length + connected camera's pixel
            // size and sensor dimensions. Falls back to null (reticle only, no rectangle)
            // when any piece is unavailable.
            (double WidthDeg, double HeightDeg)? sensorFov = null;
            if (appState.ActiveProfile?.Data is { OTAs: { Length: > 0 } otas }
                && otas[0] is { FocalLength: > 0 } ota
                && appState.DeviceHub is { } hub
                && hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera)
                && camera is not null
                && camera.PixelSizeX > 0 && camera.CameraXSize > 0 && camera.CameraYSize > 0)
            {
                sensorFov = MosaicGenerator.ComputeFieldOfView(
                    ota.FocalLength, camera.PixelSizeX, camera.CameraXSize, camera.CameraYSize);
            }

            _skyMapTab.State.MountOverlay = new SkyMapMountOverlay(
                RaJ2000: raJ2000,
                DecJ2000: decJ2000,
                DisplayName: displayName,
                IsSlewing: ms.IsSlewing,
                IsTracking: ms.IsTracking,
                SensorFovDeg: sensorFov);
        }

        /// <summary>
        /// Generates mosaic panel grids for pinned targets whose catalog shape exceeds the
        /// sensor FOV. Each panel is a separate sensor-sized rectangle positioned so the full
        /// object is covered with the configured overlap. Only computes when a camera is
        /// connected (FOV available) and pinned targets exist. Panels with count == 1 are
        /// skipped (that's just the sensor FOV rectangle already drawn by the mount overlay).
        /// </summary>
        private void PopulateSkyMapMosaicPanels(GuiAppState appState, PlannerState plannerState)
        {
            _skyMapTab.State.MosaicPanels = [];

            // Need sensor FOV to compute panels
            if (_skyMapTab.State.MountOverlay is not { SensorFovDeg: { WidthDeg: > 0, HeightDeg: > 0 } fov })
            {
                return;
            }

            var proposals = plannerState.Proposals;
            if (proposals.Length == 0)
            {
                return;
            }

            // Need the catalog DB for shape lookups
            if (plannerState.ObjectDb is not { } db)
            {
                return;
            }

            var panels = new List<(double RA, double Dec, string Name, int Row, int Col)>();

            foreach (var proposal in proposals)
            {
                if (proposal.Target.CatalogIndex is not { } idx)
                {
                    continue;
                }

                var generated = MosaicGenerator.GeneratePanels(db, idx, fov.WidthDeg, fov.HeightDeg);

                // Single panel = object fits in one FOV, no mosaic needed (sensor
                // rectangle already covers it via the mount overlay)
                if (generated.Length <= 1)
                {
                    continue;
                }

                foreach (var panel in generated)
                {
                    panels.Add((panel.Target.RA, panel.Target.Dec, panel.Target.Name, panel.Row, panel.Column));
                }
            }

            if (panels.Count > 0)
            {
                _skyMapTab.State.MosaicPanels = [.. panels];
            }
        }

        private void RenderComingSoonPlaceholder(RectF32 rect, GuiTab tab)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);

            if (_fontPath is not null)
            {
                var msg = $"{tab} — Coming soon";
                DrawText(msg.AsSpan(), _fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    FontSize * 1.5f, PlaceholderText, TextAlign.Center, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Font resolution
        // -----------------------------------------------------------------------

        private void ResolveFontPath()
        {
            // Emoji font: bundled Noto COLRv1 (uses COLRv1 paint tree, rendered by DIR.Lib)
            var emojiPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "Noto-COLRv1.ttf");
            if (File.Exists(emojiPath))
            {
                _emojiFontPath = emojiPath;
            }
            else if (OperatingSystem.IsWindows() && File.Exists(@"C:\Windows\Fonts\seguiemj.ttf"))
            {
                _emojiFontPath = @"C:\Windows\Fonts\seguiemj.ttf";
            }

            // Prefer bundled DejaVu Sans for regular text
            var bundled = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");
            if (File.Exists(bundled))
            {
                _fontPath = bundled;
                return;
            }

            // Fall back to system fonts
            var resolved = FontResolver.ResolveSystemFont();
            if (resolved.Length > 0)
            {
                _fontPath = resolved;
            }
        }
    }
}
