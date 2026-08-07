using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;

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
        private readonly VkSkyMapTab _skyMapTab;
        private readonly VkLiveSessionTab _liveSessionTab;
        private readonly GuiderTab<VulkanContext> _guiderTab;
        private readonly VkNotificationsTab _notificationsTab;
        private readonly VkHomeTab _homeTab;
        private readonly VkImageRenderer _guiderViewer;
        private readonly VkImageRenderer _previewViewer;
        private readonly VkPlanetaryTab _planetaryTab;
        private ScheduledObservationTree? _cachedSchedule;
        private Target? _cachedActiveTarget;
        private uint _width;
        private uint _height;

        /// <summary>
        /// DPI scale factor, set by the host from the SDL window's DisplayScale (startup + resize).
        /// Overrides the <see cref="PixelWidgetBase{T}.DpiScale"/> setter to propagate the new scale to
        /// every child widget this chrome hosts, so tab layout + input math all read one owner -- no
        /// per-Render parameter threading.
        /// </summary>
        public override float DpiScale
        {
            get => base.DpiScale;
            set
            {
                base.DpiScale = value;
                _plannerTab.DpiScale = value;
                _equipmentTab.DpiScale = value;
                _sessionTab.DpiScale = value;
                _skyMapTab.DpiScale = value;
                _liveSessionTab.DpiScale = value;
                _guiderTab.DpiScale = value;
                _notificationsTab.DpiScale = value;
                _homeTab.DpiScale = value;
                _guiderViewer.DpiScale = value;
                _previewViewer.DpiScale = value;
                _planetaryTab.DpiScale = value;
            }
        }

        /// <summary>
        /// The window's primary text font, resolved once by this chrome (<see cref="ResolveFontPath"/>).
        /// Overrides the <see cref="PixelWidgetBase{T}.FontPath"/> setter to push the font to the seven plain
        /// child tabs, so their layout + text math all read one owner -- no per-Render fontPath argument.
        /// The two embedded image viewers and the planetary tab are <see cref="VkImageRenderer"/>s that
        /// self-resolve their own font in their ctor, so they are deliberately not pushed here (unchanged).
        /// </summary>
        public override string FontPath
        {
            get => base.FontPath;
            set
            {
                base.FontPath = value;
                _plannerTab.FontPath = value;
                _equipmentTab.FontPath = value;
                _sessionTab.FontPath = value;
                _skyMapTab.FontPath = value;
                _liveSessionTab.FontPath = value;
                _guiderTab.FontPath = value;
                _notificationsTab.FontPath = value;
                _homeTab.FontPath = value;
            }
        }

        /// <summary>
        /// Optional emoji/symbol fallback font, propagated to the same seven tabs as <see cref="FontPath"/>.
        /// Only the Planner + Equipment tabs consume it today (planet/weather glyphs on the altitude chart);
        /// the others ignore it harmlessly.
        /// </summary>
        public override string? EmojiFontPath
        {
            get => base.EmojiFontPath;
            set
            {
                base.EmojiFontPath = value;
                _plannerTab.EmojiFontPath = value;
                _equipmentTab.EmojiFontPath = value;
                _sessionTab.EmojiFontPath = value;
                _skyMapTab.EmojiFontPath = value;
                _liveSessionTab.EmojiFontPath = value;
                _guiderTab.EmojiFontPath = value;
                _notificationsTab.EmojiFontPath = value;
                _homeTab.EmojiFontPath = value;
            }
        }

        /// <summary>Exposes the planner tab for external scroll control.</summary>
        public VkPlannerTab PlannerTab => _plannerTab;

        /// <summary>Exposes the equipment tab for state access.</summary>
        public VkEquipmentTab EquipmentTab => _equipmentTab;

        /// <summary>Exposes the session tab for scroll control and state access.</summary>
        public VkSessionTab SessionTab => _sessionTab;

        /// <summary>The live planetary capture controller. Set by the host (resolved from DI); forwarded to the
        /// Live Session tab so it can drive the planetary mode (and to the standalone tab during migration).</summary>
        private PlanetaryCaptureController? _planetaryCapture;
        public PlanetaryCaptureController? PlanetaryCapture
        {
            get => _planetaryCapture;
            set
            {
                _planetaryCapture = value;
                _liveSessionTab.PlanetaryCapture = value;
            }
        }

        /// <summary>The currently active tab as an <see cref="IPixelWidget"/> for tab-specific hit testing.</summary>
        public IPixelWidget? ActiveTab { get; private set; }

        /// <inheritdoc/>
        public EquipmentTabState EquipmentState => _equipmentTab.State;

        /// <inheritdoc/>
        public SessionTabState SessionState => _sessionTab.State;

        /// <inheritdoc/>
        public ViewContexts ViewContexts { get; } = new ViewContexts();

        /// <summary>The on-screen context's session state -- what the chrome and tabs draw.</summary>
        private LiveSessionState ViewedLiveSession => ViewContexts.Active.LiveSession;

        /// <summary>
        /// This node's own session state, regardless of what is on screen. Used where the chrome
        /// guards LOCAL resources: the Equipment tab lock and <see cref="SessionTabState.IsSessionRunning"/>
        /// (which freezes the local schedule + planner date nav) protect this node's equipment and plan,
        /// so a session on a rig you happen to be watching must not lock them.
        /// </summary>
        private LiveSessionState LocalLiveSession => ViewContexts.Local.LiveSession;

        /// <inheritdoc/>
        public SkyMapState SkyMapState => _skyMapTab.State;

        /// <inheritdoc/>
        public RectF32 PlannerChartRect => _plannerTab.ChartRect;

        /// <inheritdoc/>
        public void PlannerEnsureVisible(int index) => _plannerTab.EnsureVisible(index);

        /// <summary>
        /// True when the planner's deferred chart-texture upload has produced a texture
        /// that hasn't been drawn yet, so the event loop should schedule one more frame.
        /// Gated to the Planner tab by the caller. See <see cref="VkPlannerTab.ChartTexturePendingDraw"/>.
        /// </summary>
        public bool PlannerChartPendingDraw => _plannerTab.ChartTexturePendingDraw;

        // Base layout constants (at 1x scale)
        private const float BaseSidebarWidth = 52f;
        private const float BaseStatusBarHeight = 28f;
        private static readonly float BaseFontSize = TianWen.UI.Abstractions.GuiTheme.Metrics.BaseFontSize;

        // Scaled accessors
        private float SidebarWidth => BaseSidebarWidth * DpiScale;
        private float StatusBarHeight => BaseStatusBarHeight * DpiScale;
        private float FontSize => BaseFontSize * DpiScale;

        // Live Session sidebar icon reflects session state (overridden per-frame in RenderSidebar):
        // idle = camera, running = camera with flash. The clapper board marks the Session Setup tab
        // as the "set up and launch here" entry point. That tab is BOTH the night's configuration and
        // the Start button, which is why neither obvious glyph worked: a cog implied only the setup
        // half and a rocket only the launch half. A shoot you stage and then call action on spans both.
        private const string LiveSessionIdleIcon      = "\U0001F4F7"; // camera (Preview)
        private const string LiveSessionRunningIcon   = "\U0001F4F8"; // camera with flash (running session)
        private const string LiveSessionPolarIcon     = "\U0001F9ED"; // compass (Polar Align mode)
        private const string LiveSessionPlanetaryIcon = "\U0001FA90"; // ringed planet (Planetary mode)
        private const string LiveSessionFlatsIcon     = "\U0001F4A1"; // light bulb (Flats mode)

        // Per-tab sidebar chrome (icon + hover tooltip with the Ctrl+letter shortcut). The sidebar
        // ORDER comes from GuiAppState.TabOrder (shared with Ctrl+Tab cycling) so the visual order
        // and the cycle order can't drift apart.
        private static readonly Dictionary<GuiTab, (string Icon, string Tooltip)> TabChrome = new()
        {
            // House, not satellite/antenna/globe: the icon has to stay neutral between local and remote,
            // or a single-scope user's one-card board looks like a remote-monitoring feature they do not
            // use -- and it names the SCREEN, which is due to hold multi-night progress beside the cards.
            [GuiTab.Home]          = ("\U0001F3E0",        "Home (Ctrl+H)"),
            [GuiTab.Equipment]     = ("\U0001F52D",        "Equipment (Ctrl+E)"),
            [GuiTab.Planner]       = ("\U0001F4C5",        "Planner (Ctrl+P)"),
            [GuiTab.SkyMap]        = ("\U0001F30C",        "Sky Map (Ctrl+M)"),
            [GuiTab.Session]       = ("\U0001F3AC",        "Session Setup (Ctrl+S)"),
            [GuiTab.LiveSession]   = (LiveSessionIdleIcon, "Live Session (Ctrl+L)"),
            [GuiTab.Guider]        = ("\U0001F3AF",        "Guider (Ctrl+G)"),
            [GuiTab.Notifications] = ("\U0001F514",        "Notifications (Ctrl+N)"),
        };

        // Window chrome, entirely from the shared palette. PROPERTIES, not static readonly fields: a
        // field initialiser snapshots at type-init, which is how the one palette-derived colour that was
        // already here (ContentBg) still failed to follow a theme switch.
        //
        // The sidebar is a panel that the active tab lifts off, so it takes PanelBg and the active tab
        // takes Selection: the selected-surface role IS what "the tab you are on" means, and it already
        // carries the cool lift the hand-picked 0x203050 was reaching for. Hover sits between the two on
        // HeaderBg. An inactive icon is de-emphasised text and an active one is body text, so they are
        // DimText and BodyText rather than two greys; a locked tab is not text at all, so it takes the
        // heavier rule weight instead of a third one.
        private static RGBAColor32 SidebarBg       => Palette.PanelBg;
        private static RGBAColor32 ActiveTabBg     => Palette.Selection;
        private static RGBAColor32 HoverTabBg      => Palette.HeaderBg;
        private static RGBAColor32 IconColor       => Palette.DimText;
        private static RGBAColor32 ActiveIcon      => Palette.BodyText;
        private static RGBAColor32 LockedIcon      => Palette.SeparatorStrong;

        // Status bar
        private static RGBAColor32 StatusBarBg     => Palette.HeaderBg;
        private static RGBAColor32 StatusText      => Palette.DimText;

        // Content area placeholder
        private static RGBAColor32 ContentBg       => Palette.ContentBg;
        private static RGBAColor32 PlaceholderText => Palette.SeparatorStrong;

        // Local alias so the chrome above reads as roles rather than as a namespace walk. UiPalette is
        // DIR.Lib's (already in scope via the using); only GuiTheme is TianWen's.
        private static UiPalette Palette => TianWen.UI.Abstractions.GuiTheme.Palette;

        public VkGuiRenderer(VkRenderer renderer, uint width, uint height, SignalBus? bus = null, ILogger? logger = null) : base(renderer)
        {
            Bus = bus;
            _renderer = renderer;
            _width = width;
            _height = height;
            _plannerTab = new VkPlannerTab(renderer) { Bus = bus };
            _equipmentTab = new VkEquipmentTab(renderer) { Bus = bus };
            _sessionTab = new VkSessionTab(renderer) { Bus = bus };
            _skyMapTab = new VkSkyMapTab(renderer) { Bus = bus, Logger = logger };
            // Preview + guide-cam now use the SAME full image viewer as the FITS viewer + planetary tab
            // (configured chromeless via ViewerState.HideChrome), not a separate mini widget.
            _previewViewer = new VkImageRenderer(renderer, width, height);
            _liveSessionTab = new VkLiveSessionTab(renderer) { Bus = bus, PreviewView = _previewViewer };
            _guiderViewer = new VkImageRenderer(renderer, width, height);
            _guiderTab = new GuiderTab<VulkanContext>(renderer) { Bus = bus, GuideCameraViewer = _guiderViewer };
            _notificationsTab = new VkNotificationsTab(renderer) { Bus = bus };
            // Bus, because a card click changes the view context through the same signals the profile
            // picker posts -- the board itself neither connects nor commands anything.
            _homeTab = new VkHomeTab(renderer) { Bus = bus };
            // The 🪐 tab IS a full image viewer (shares VkImageRenderer with tianwen-fits) + a capture strip,
            // so it gets the same stretch pipeline / RAW-STACK toggle / wavelet sliders as the FITS viewer.
            _planetaryTab = new VkPlanetaryTab(renderer, width, height) { Bus = bus };
            // The planetary tab IS also the Live Session planetary-mode view (one instance, one ViewerState).
            _liveSessionTab.PlanetaryView = _planetaryTab;
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
            _skyMapTab.FrameCount++;
            _liveSessionTab.FrameCount++;
            _guiderTab.FrameCount++;
            _planetaryTab.FrameCount++;
            _notificationsTab.FrameCount++;
            _homeTab.FrameCount++;

            ActiveTab = appState.ActiveTab switch
            {
                GuiTab.Planner => _plannerTab,
                GuiTab.Equipment => _equipmentTab,
                GuiTab.Session => _sessionTab,
                GuiTab.SkyMap => _skyMapTab,
                GuiTab.LiveSession => _liveSessionTab,
                GuiTab.Guider => _guiderTab,
                GuiTab.Notifications => _notificationsTab,
                GuiTab.Home => _homeTab,
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
            _previewViewer.Dispose();
            _guiderViewer.Dispose();
            _planetaryTab.Dispose();
            _plannerTab.Dispose();
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

            // Track hovered tooltip to draw last (over sidebar + any adjacent content).
            string? hoverTooltip = null;
            float hoverY = 0f;

            var tabs = GuiAppState.TabOrder;
            for (var i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                var (icon, tooltip) = TabChrome[tab];
                // Live Session icon reflects the active mode: a running session flips to "camera with
                // flash"; otherwise the mode picks the glyph -- compass for Polar Align, ringed planet for
                // Planetary, light bulb for Flats, camera for Preview. (Mirrors the mode pill so the
                // sidebar reads at a glance.) A flat run deliberately leaves IsRunning false, so the
                // running-session glyph never masks the Flats one for the duration of the run.
                if (tab is GuiTab.LiveSession)
                {
                    icon = ViewedLiveSession.IsRunning
                        ? LiveSessionRunningIcon
                        : ViewedLiveSession.Mode switch
                        {
                            LiveSessionMode.PolarAlign => LiveSessionPolarIcon,
                            LiveSessionMode.Planetary => LiveSessionPlanetaryIcon,
                            LiveSessionMode.Flats => LiveSessionFlatsIcon,
                            _ => LiveSessionIdleIcon,
                        };
                }
                var btnY = startY + i * buttonSize;
                var isActive = appState.ActiveTab == tab;
                var isLocked = (noProfile && tab is not GuiTab.Equipment)
                            || (LocalLiveSession.IsRunning && tab is GuiTab.Equipment);
                var isHover = !isLocked && mouseX >= 0 && mouseX < sw
                           && mouseY >= btnY && mouseY < btnY + buttonSize;

                var bgColor = isActive ? ActiveTabBg
                            : isHover  ? HoverTabBg
                                       : SidebarBg;

                FillRect(0, btnY, sw, buttonSize, bgColor);

                var textColor = isLocked ? LockedIcon
                              : isActive ? ActiveIcon
                                         : IconColor;
                var iconFont = EmojiFontPath ?? FontPath;
                if (!string.IsNullOrEmpty(iconFont))
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

                if (isHover)
                {
                    hoverTooltip = tooltip;
                    hoverY = btnY + buttonSize * 0.5f;
                }
            }

            if (hoverTooltip is not null && !string.IsNullOrEmpty(FontPath))
            {
                DrawTooltip(hoverTooltip, sw + 6f, hoverY);
            }
        }

        // Tooltip rendered to the right of the sidebar at the hovered tab's vertical
        // centre. Dark rounded rect + one-line label; z-order is guaranteed by being
        // called at the end of RenderSidebar (paint-last = top).
        private void DrawTooltip(string text, float anchorX, float anchorY)
        {
            if (string.IsNullOrEmpty(FontPath)) return;
            var pad = 6f * DpiScale;
            var fontSize = FontSize;
            var (tw, th) = _renderer.MeasureText(text.AsSpan(), FontPath, fontSize);
            var w = tw + pad * 2f;
            var h = th + pad;
            var x = anchorX;
            var y = anchorY - h * 0.5f;

            // Border + fill. The fill keeps its near-opaque alpha so the tooltip still reads as floating
            // over the content rather than cut into it; only the hue comes from the palette.
            FillRect(x - 1, y - 1, w + 2, h + 2, Palette.SeparatorStrong);
            FillRect(x, y, w, h, Palette.HeaderBg.WithAlpha(0xF0));
            _renderer.DrawText(text.AsSpan(), FontPath, fontSize,
                Palette.BodyText,
                new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)(x + pad), (int)y)),
                TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Status bar — registers clickable regions for interactive elements
        // -----------------------------------------------------------------------

        private void RenderStatusBar(GuiAppState appState, PlannerState plannerState, ITimeProvider timeProvider)
        {
            var w = (float)_width;
            var sbh = StatusBarHeight;

            FillRect(0, 0, w, sbh, StatusBarBg);

            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var now = timeProvider.GetUtcNow().ToOffset(plannerState.SiteTimeZone);
            var clockText = now.ToString("ddd d MMM HH:mm:ss");
            var sessionRunning = LocalLiveSession.IsRunning;
            SessionState.IsSessionRunning = sessionRunning;

            // Date + night-window label string (data only): [<] date (HH:mm-HH:mm) [>].
            var planDate = plannerState.PlanningDate ?? now;
            var isTonight = !plannerState.PlanningDate.HasValue || planDate.Date == now.Date;
            string dateStr;
            if (isTonight && plannerState.AstroDark != default)
            {
                var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                dateStr = $"Tonight {dark:HH:mm}-{twilight:HH:mm}";
            }
            else if (plannerState.AstroDark != default)
            {
                var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                dateStr = $"{planDate:ddd d MMM} {dark:HH:mm}-{twilight:HH:mm}";
            }
            else
            {
                dateStr = isTonight ? "Tonight" : planDate.ToString("ddd d MMM");
            }
            // A pinned planning date is the one thing in this bar that differs from "now", so it takes the
            // accent; an unpinned date is ordinary de-emphasised chrome.
            var dateColor = plannerState.PlanningDate.HasValue ? Palette.Accent : StatusText;

            // The whole bar is one layout tree: three star-weighted zones (left | centre | right), so
            // placement is "weights + spacers", not pixel arithmetic. Sizes/fonts are design units;
            // RenderLayout scales them by DpiScale. Every leaf is .HStar() so it fills the bar height and
            // VAlign=Center centres the glyph (a horizontal stack top-aligns Auto-height children).
            var arrowBg = Palette.HeaderBg;
            const float gapDu = 6f; // design-unit inter-element gap

            // LEFT: the (truncated) status message.
            //
            // The profile name used to lead this zone and no longer does: the window title names it now,
            // which is one place instead of several. It was also the copy that said the least -- the
            // Equipment tab has a Profile picker, and the Home board labels every rig with the profile it
            // runs, so a third grey copy beside the notification text was noise competing with the message.
            Layout.Node statusNode;
            if (appState.StatusMessage is { Length: > 0 } msg)
            {
                // Truncation needs a target width (intrinsic to ellipsising); the status cell is the left
                // third, since each of the three zones takes a third of the content area.
                var contentW = w - (SidebarWidth + 6f) - 4f;
                var msgCellW = Math.Max(contentW / 3f, 40f);
                var displayMsg = TruncateToFit(msg, msgCellW, FontSize * 0.85f);
                statusNode = Layout.Builder.Text(displayMsg, BaseFontSize * 0.85f, Palette.Warn, TextAlign.Near, TextAlign.Center)
                    .WStar().HStar();
            }
            else
            {
                statusNode = Layout.Builder.Spacer().WStar();
            }
            var leftZone = Layout.Builder.HStack(statusNode).WStar().HStar();

            // CENTRE: [<] date [>], centred via flanking star spacers. Arrows hidden during a session.
            var dateLabel = Layout.Builder.Text(dateStr, BaseFontSize, dateColor, TextAlign.Center, TextAlign.Center)
                .WAuto().HStar();
            if (plannerState.PlanningDate.HasValue && !sessionRunning)
            {
                dateLabel = dateLabel.Clickable(new HitResult.ButtonHit("DateTonight"),
                    _ => { PlannerActions.ResetPlanningDate(plannerState); });
            }
            Layout.Node dateGroup;
            if (!sessionRunning)
            {
                var prev = Layout.Builder.Text("◀", BaseFontSize * 0.9f, StatusText, TextAlign.Center, TextAlign.Center)
                    .WFixed(BaseStatusBarHeight).HStar().Bg(arrowBg)
                    .Clickable(new HitResult.ButtonHit("DatePrev"),
                        _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, -1, _skyMapTab.State); });
                var next = Layout.Builder.Text("▶", BaseFontSize * 0.9f, StatusText, TextAlign.Center, TextAlign.Center)
                    .WFixed(BaseStatusBarHeight).HStar().Bg(arrowBg)
                    .Clickable(new HitResult.ButtonHit("DateNext"),
                        _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, +1, _skyMapTab.State); });
                dateGroup = Layout.Builder.HStack(prev, dateLabel, next).WAuto().HStar().WithGap(gapDu);
            }
            else
            {
                dateGroup = dateLabel;
            }
            var centreZone = Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), dateGroup, Layout.Builder.Spacer().WStar())
                .WStar().HStar();

            // RIGHT: [Connect All] + wall clock, right-aligned via a leading star spacer. Connect All is
            // globally reachable on every tab; visible whenever the active profile has assigned devices and
            // only actionable once discovery has finished (gate computed by EquipmentActions, shared with
            // the equipment panel). The trailing gap rides with the button so the clock stays flush right
            // when the button is absent.
            Layout.Node connectAll = Layout.Builder.Spacer().WFixed(0f);
            if (appState.ActiveProfile?.Data is { } connectAllProfile)
            {
                var ca = EquipmentActions.ComputeConnectAllStatus(
                    connectAllProfile, appState.DeviceHub,
                    EquipmentState.DiscoveredDevices, EquipmentState.PendingTransitions,
                    EquipmentState.IsDiscovering);
                if (ca.Visible)
                {
                    // Connect All was the loudest literal left in the chrome: a fixed green that ignored
                    // the theme entirely and, in a dark-adaptation palette, became the brightest thing on
                    // screen. Success is the role for it, and in a palette with no green to spend it
                    // resolves to the accent instead of insisting on one. Disabled is a plain rule fill.
                    // Enabled is a FILLED chip, so its label is ink chosen from the fill, not a text role:
                    // DimText on the green Success fill measured about 1.4:1 and was effectively unreadable.
                    // Disabled is a plain rule fill, where the ordinary de-emphasised role is right.
                    var caBg = ca.Enabled ? Palette.Success : Palette.Separator;
                    var caLabelColor = ca.Enabled
                        ? TianWen.UI.Abstractions.GuiTheme.InkOn(caBg)
                        : Palette.DimText;
                    var caButton = Layout.Builder.HStack(
                            Layout.Builder.Spacer().WFixed(gapDu * 2f),
                            Layout.Builder.Text(ca.Label, BaseFontSize * 0.9f, caLabelColor, TextAlign.Center, TextAlign.Center).WAuto().HStar(),
                            Layout.Builder.Spacer().WFixed(gapDu * 2f))
                        .WAuto().HStar().Bg(caBg);
                    // Only the enabled state registers a click region (mirrors the prior behaviour).
                    if (ca.Enabled)
                    {
                        caButton = caButton.Clickable(new HitResult.ButtonHit("ConnectAll"),
                            _ => { PostSignal(new ConnectAllDevicesSignal()); });
                    }
                    connectAll = Layout.Builder.HStack(caButton, Layout.Builder.Spacer().WFixed(gapDu * 2f)).WAuto().HStar();
                }
            }
            var clockNode = Layout.Builder.Text(clockText, BaseFontSize, StatusText, TextAlign.Far, TextAlign.Center)
                .WAuto().HStar();
            var rightZone = Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), connectAll, clockNode)
                .WStar().HStar();

            // Arrange + paint the bar over the content area (right of the sidebar gutter, small right margin).
            var bar = Layout.Builder.HStack(leftZone, centreZone, rightZone);
            RenderLayout(bar, new RectF32(SidebarWidth + 6f, 0f, w - (SidebarWidth + 6f) - 4f, sbh));
        }

        // Truncate with ellipsis so the string fits within maxWidth at the given font size.
        // Binary-search the longest prefix that, with the ellipsis appended, still fits.
        private string TruncateToFit(string text, float maxWidth, float fontSize)
        {
            if (string.IsNullOrEmpty(FontPath)) return text;
            var (fullWidth, _) = _renderer.MeasureText(text.AsSpan(), FontPath, fontSize);
            if (fullWidth <= maxWidth) return text;

            const string Ellipsis = "\u2026";
            var lo = 0;
            var hi = text.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var candidate = string.Concat(text.AsSpan(0, mid), Ellipsis);
                var (cw, _) = _renderer.MeasureText(candidate.AsSpan(), FontPath, fontSize);
                if (cw <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return lo == 0 ? Ellipsis : string.Concat(text.AsSpan(0, lo), Ellipsis);
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
            // Refresh the cached session snapshot every frame, regardless of tab. PollSession
            // early-returns when no session is active (cheap volatile-field copies otherwise), so
            // this is essentially free when idle. Running it unconditionally keeps the single
            // canonical LiveSessionState.MountState current on EVERY tab while a session runs --
            // previously this was gated to LiveSession/Guider, so the Sky Map reticle read a stale
            // default(MountState) (RA0/Dec0) the whole time a session was active and the user was
            // watching the map. The mount was never wrong; the copy that fed the reticle was never
            // refreshed.
            //
            // Every context, not just the visible one: a local session hidden under a remote overlay
            // still has to keep its phase and mount pointing current (its notifications bubble up, and
            // the chrome will grow a local-session indicator), and an off-screen context that stopped
            // polling would resume with a stale snapshot the moment you switched back.
            ViewContexts.PollAll();

            switch (appState.ActiveTab)
            {
                case GuiTab.Planner:
                    // Tabs read the DpiScale + FontPath + EmojiFontPath properties (pushed by this chrome's
                    // setter overrides at startup/resize) -- no per-Render dpi/font arguments any more.
                    _plannerTab.Render(plannerState, contentRect,
                        timeProvider, appState.MouseScreenPosition);
                    break;

                case GuiTab.Equipment:
                    // Local: this tab lists and actuates THIS node's hub-connected devices, so it reads
                    // the local session even while a rig is on screen. A remote Equipment view (the rig's
                    // own devices, over the API) is P5.
                    _equipmentTab.Render(appState, contentRect, LocalLiveSession);
                    break;

                case GuiTab.Session:
                    _sessionTab.Render(appState, plannerState, contentRect, timeProvider);
                    break;

                case GuiTab.SkyMap:
                    // Feed the live mount snapshot into the sky map state so the reticle overlay
                    // tracks the mount without the tab needing its own poll path. Reads the single
                    // canonical LiveSessionState.MountState (kept current every frame by the
                    // unconditional PollSession above when a session runs, or by PollPreviewTelemetry
                    // when idle) -- no session-vs-preview branch here any more.
                    PopulateSkyMapMountOverlay(appState, timeProvider);
                    PopulateSkyMapMosaicPanels(appState, plannerState);
                    PopulateSkyMapScheduleTargets();
                    _skyMapTab.Render(plannerState, contentRect, timeProvider);
                    break;

                case GuiTab.LiveSession:
                    // Copy twilight data from planner so preview timeline can render night window
                    {
                        var viewed = ViewedLiveSession;
                        if (!viewed.IsRunning)
                        {
                            viewed.AstroDark = plannerState.AstroDark;
                            viewed.AstroTwilight = plannerState.AstroTwilight;
                            viewed.CivilSet = plannerState.CivilSet;
                            viewed.CivilRise = plannerState.CivilRise;
                            viewed.NauticalSet = plannerState.NauticalSet;
                            viewed.NauticalRise = plannerState.NauticalRise;
                        }
                        // Null for the local context. Only the prompt overlay reads it, to name where
                        // somebody would have to physically be for a presence-gated prompt.
                        _liveSessionTab.RemoteRigName = ViewContexts.Active.IsLocal ? null : ViewContexts.Active.DisplayName;
                        _liveSessionTab.Render(viewed, contentRect, timeProvider);
                    }
                    break;

                case GuiTab.Guider:
                    _guiderTab.Render(ViewedLiveSession, contentRect, timeProvider);
                    break;

                case GuiTab.Notifications:
                    _notificationsTab.Render(appState, contentRect);
                    break;

                case GuiTab.Home:
                    // Renders the board snapshot the telemetry poll published this frame. No context or rig
                    // registry is threaded in on purpose: the board must not be able to reach live session
                    // state, or a card could be painted from a session being updated underneath it.
                    // The clock is the app's, not the tab's -- it only resolves the flip countdown.
                    _homeTab.Render(appState, contentRect, timeProvider.GetUtcNow());
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
        /// <see cref="SkyMapMountOverlay"/> snapshot. Reads the single canonical
        /// <see cref="LiveSessionState.MountState"/> -- fed by the session poll while running and
        /// the preview poll while idle -- so there is no session-vs-preview branch to keep in sync.
        /// J2000 coords are preferred; native coords are the fallback when the active source does
        /// not populate the J2000 fields (the session poll currently does not, so session-mode is
        /// accurate to within precession until the believed/true split lands).
        /// </summary>
        private void PopulateSkyMapMountOverlay(GuiAppState appState, ITimeProvider timeProvider)
        {
            // The single canonical snapshot. NaN RA/Dec (or an empty display name) means "no
            // current pointing" -- either no mount is configured or no poll has succeeded yet --
            // and suppresses the reticle. A genuine, freshly-polled RA0/Dec0 would still draw,
            // which is correct: that is a real (if unusual) pointing, not the old phantom that
            // came from reading a never-refreshed default(MountState).
            var viewed = ViewedLiveSession;
            var ms = viewed.MountState;
            var displayName = viewed.MountDisplayName;

            if (string.IsNullOrEmpty(displayName))
            {
                _skyMapTab.State.MountOverlay = null;
                return;
            }

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

            // Refine the slew ETA from the just-read believed position + wall clock. Uses
            // the position the telemetry poll already produced (raJ2000/decJ2000) rather
            // than reading the mount again, so it never races PollPreviewTelemetry on the port.
            UpdateSlewEta(raJ2000, decJ2000, timeProvider);
        }

        // Render-thread-only ETA tracking for the active slew destination. Observes how
        // far the reticle has moved toward the target since the slew began and divides the
        // remaining arc by that rate. NaN until enough motion is seen to be meaningful.
        private SlewTargetInfo? _etaTrackedTarget;
        private DateTimeOffset _etaStartUtc;
        private double _etaStartRemainingDeg;

        private void UpdateSlewEta(double curRaJ2000, double curDecJ2000, ITimeProvider timeProvider)
        {
            var target = _skyMapTab.State.ActiveSlewTarget;
            if (target is null || double.IsNaN(curRaJ2000) || double.IsNaN(curDecJ2000))
            {
                _etaTrackedTarget = null;
                return;
            }

            var now = timeProvider.GetUtcNow();
            var remainingDeg = CoordinateUtils.AngularSeparationDeg(
                curRaJ2000, curDecJ2000, target.RaJ2000, target.DecJ2000);

            // (Re)start the observation window when a new goto sets a fresh target instance.
            if (!ReferenceEquals(_etaTrackedTarget, target))
            {
                _etaTrackedTarget = target;
                _etaStartUtc = now;
                _etaStartRemainingDeg = remainingDeg;
                _skyMapTab.State.SlewEtaSeconds = double.NaN;
                return;
            }

            var elapsed = (now - _etaStartUtc).TotalSeconds;
            var covered = _etaStartRemainingDeg - remainingDeg;
            // Need a little time + observed motion before a rate estimate is trustworthy.
            if (elapsed >= 0.75 && covered >= 0.05)
            {
                var rateDegPerSec = covered / elapsed;
                _skyMapTab.State.SlewEtaSeconds = rateDegPerSec > 1e-6
                    ? Math.Max(0.0, remainingDeg / rateDegPerSec)
                    : double.NaN;
            }
        }

        /// <summary>
        /// Surfaces the committed observing plan's target(s) to the sky map so the user can
        /// see where tonight's targets sit. Sourced from the built schedule
        /// (<see cref="SessionTabState.Schedule"/>); the running session's
        /// <see cref="LiveSessionState.ActiveObservation"/> is flagged so the renderer can
        /// highlight the target currently being imaged / slewed to.
        /// </summary>
        private void PopulateSkyMapScheduleTargets()
        {
            var schedule = SessionState.Schedule;
            if (schedule is not { Count: > 0 })
            {
                _skyMapTab.State.ScheduleTargets = [];
                _cachedSchedule = null;
                _cachedActiveTarget = null;
                return;
            }

            // Rebuild only when the schedule or active observation changes.
            // The schedule is static during a session; only the active target
            // changes as observations advance. Comparing the schedule identity
            // and active target identity avoids a List+ImmutableArray allocation
            // every render frame (~60 FPS).
            // Local, to match its schedule source: the targets come from SessionState.Schedule (this
            // node's committed plan), so the "currently imaging" highlight has to come from the run
            // that is executing that plan.
            var active = LocalLiveSession.ActiveObservation?.Target;
            if (_cachedSchedule == schedule && _cachedActiveTarget == active)
            {
                return;
            }
            _cachedSchedule = schedule;
            _cachedActiveTarget = active;

            var targets = new List<(double RA, double Dec, string Name, bool IsActive)>(schedule.Count);
            foreach (var obs in schedule)
            {
                var t = obs.Target;
                if (double.IsNaN(t.RA) || double.IsNaN(t.Dec))
                {
                    continue;
                }
                targets.Add((t.RA, t.Dec, t.Name, active is { } a && a == t));
            }
            _skyMapTab.State.ScheduleTargets = [.. targets];
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

            if (!string.IsNullOrEmpty(FontPath))
            {
                var msg = $"{tab} — Coming soon";
                DrawText(msg.AsSpan(), FontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    FontSize * 1.5f, PlaceholderText, TextAlign.Center, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Font resolution
        // -----------------------------------------------------------------------

        private void ResolveFontPath()
        {
            // Assigning FontPath / EmojiFontPath goes through the overridden setters above, which push the
            // resolved fonts to the child tabs (one set-point). The preview + guide-cam viewers and the
            // planetary tab self-resolve their font in their VkImageRenderer ctor, so they are not pushed.

            // Emoji font: bundled Noto COLRv1 (uses COLRv1 paint tree, rendered by DIR.Lib)
            var emojiPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "Noto-COLRv1.ttf");
            if (File.Exists(emojiPath))
            {
                EmojiFontPath = emojiPath;
            }
            else if (OperatingSystem.IsWindows() && File.Exists(@"C:\Windows\Fonts\seguiemj.ttf"))
            {
                EmojiFontPath = @"C:\Windows\Fonts\seguiemj.ttf";
            }

            // Prefer bundled DejaVu Sans for regular text
            var bundled = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");
            if (File.Exists(bundled))
            {
                FontPath = bundled;
            }
            else
            {
                // Fall back to system fonts
                var resolved = FontResolver.ResolveSystemFont();
                if (resolved.Length > 0)
                {
                    FontPath = resolved;
                }
            }
        }
    }
}
