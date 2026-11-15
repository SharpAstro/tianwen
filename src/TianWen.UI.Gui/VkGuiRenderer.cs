using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using TianWen.Lib;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Top-level GPU renderer for the N.I.N.A.-style integrated GUI.
    /// Extends <see cref="PixelWidgetBase{TSurface}"/> so the sidebar, status bar, and chrome
    /// participate in the unified <see cref="PixelWidgetBase{TSurface}.RegisterClickable"/>
    /// / <see cref="PixelWidgetBase{TSurface}.HitTestAndDispatch"/> system.
    /// </summary>
    public sealed class VkGuiRenderer : CompositeWidget<VulkanContext>, IGuiChrome, IDisposable
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
        private readonly NightCalendarPopover<VulkanContext> _nightCalendar;

        private ScheduledObservationTree? _cachedSchedule;
        private Target? _cachedActiveTarget;
        private uint _width;
        private uint _height;

        // No DpiScale / FontPath / EmojiFontPath / FontFallback overrides here any more. Every widget this
        // chrome composes -- the eight tabs AND the three embedded viewers -- reads this chrome's
        // PixelWidgetBase.Ui, so the host's one assignment is theirs too. The four propagation blocks that
        // used to sit here each named all eight tabs, and the viewers were then hand-fed the DPI they could
        // not be excluded from: display scale is a property of the WINDOW, and these all draw into one. What
        // is NOT shared is each widget's own scale over it (DIR.Lib's InterfaceScale): the chrome and the tabs
        // carry GuiTheme.InterfaceScale and the viewers keep 1, set once in the constructor below.




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

        /// <summary>The Preview mode's live view (#1111). Set by the host (resolved from DI); forwarded to the Live Session tab.</summary>
        public LiveViewController? LiveView
        {
            get => _liveSessionTab.LiveView;
            set => _liveSessionTab.LiveView = value;
        }

        /// <summary>The Guider tab's loop of the guide camera (#1915). Set by the host (resolved from DI).</summary>
        public GuideLoopController? GuideLoop
        {
            get => _guiderTab.GuideLoop;
            set => _guiderTab.GuideLoop = value;
        }

        // Held as the concrete widget base, not as the IPixelWidget the interface exposes, because the
        // cursor query lives on the base: DIR.Lib's IPixelWidget carries HitTest, click dispatch and
        // text-input discovery, but not HitTestCursor. Every tab derives from PixelWidgetBase, so this
        // costs nothing beyond the extra field.
        private PixelWidgetBase<VulkanContext>? _activeTab;

        // The top bar and the sidebar, one widget over any surface (GuiWindowChrome, #2139): the rail, the strip above it,
        // the status bar, the tab chrome table, the clock's rectangle and the calendar's anchor all live there.
        private readonly GuiWindowChrome<VulkanContext> _chrome;

        // What this chrome paints, in PAINT order (back to front), rebuilt by Render. Every aggregate
        // query -- hit test, dispatch, cursor, text inputs, region enumeration -- derives from this one
        // list via CompositeWidget, so they cannot disagree about what is on screen or in what order.
        //
        // They did disagree. Before this was a composite the same list was restated per query, and the
        // five copies had drifted into three different orderings plus one omission: the cursor asked the
        // active tab first (inverted -- the status bar paints OVER the tab), dispatch asked the chrome
        // first, and the text-input scan had never heard of the rail. None of it was visible, because
        // every site is individually plausible and the frame looks correct either way.
        private readonly List<PixelWidgetBase<VulkanContext>> _children = [];

        /// <inheritdoc/>
        protected override IReadOnlyList<PixelWidgetBase<VulkanContext>> Children => _children;

        /// <inheritdoc/>
        /// <remarks>
        /// All but the last: the active tab and the rail are painted under this chrome's own status bar, and the night
        /// calendar, always listed last, hangs OVER the bar it opens from (the composition stated beside the painting).
        /// Nothing before the first frame.
        /// </remarks>
        protected override int ChildrenUnderOwn => Math.Max(0, _children.Count - 1);

        /// <summary>The currently active tab as an <see cref="IPixelWidget"/> for tab-specific hit testing.</summary>
        public IPixelWidget? ActiveTab => _activeTab;

        /// <summary>
        /// What the pointer should look like at this point, asked of the regions painted last frame, and
        /// null when nothing under the pointer had a view (which is NOT the same as "the arrow" -- that
        /// is the host's default to choose).
        /// <para>
        /// A thin name over <see cref="CompositeWidget{TSurface}.HitTestCursor"/>, kept because a host
        /// reads better asking the chrome what the cursor is than asking it to hit-test. The ORDER is no
        /// longer restated here -- it comes from <see cref="Children"/>, like every other aggregate
        /// query. It used to ask the active tab first and this chrome last, which was inverted (the
        /// status bar paints OVER the tab) and disagreed with the dispatch order below.
        /// </para>
        /// </summary>
        public CursorKind? CursorAt(float x, float y) => HitTestCursor(x, y);

        /// <summary>The regions the debug inspector enumerates: exactly what the router dispatches over.</summary>
        public IReadOnlyList<ClickableRegion> InspectorRegions()
        {
            var regions = new List<ClickableRegion>();
            CollectPaintedRegions(regions);
            return regions;
        }

        /// <summary>
        /// Every text field painted in the frame just drawn, across this chrome and everything it hosts.
        /// Feeding it to <see cref="TextInputFocus.BlurIfUnpainted"/> is what stops a field keeping the
        /// keyboard after it leaves the screen -- scrolled out of a culled list, or on a tab the user
        /// switched away from.
        /// </summary>
        /// <remarks>
        /// The composition is <see cref="CompositeWidget{TSurface}.GetRegisteredTextInputs"/>'s now; this
        /// survives only to DE-DUPLICATE, since chrome and tab can register the same field instance in
        /// one frame and the blur check compares by identity.
        /// </remarks>
        public IReadOnlyCollection<TextInputState> PaintedTextInputs()
            => new HashSet<TextInputState>(GetRegisteredTextInputs());

        /// <summary>
        /// Where the focused field's caret was painted this frame, or <c>default</c> if no active field
        /// was drawn. The host hands this to <c>SdlVulkanWindow.SetTextInputArea</c> so an input method
        /// places its candidate window beside the caret rather than over the text being typed.
        /// </summary>
        /// <remarks>
        /// No longer composed across chrome and tab, because there is nothing to compose: a caret is a
        /// per-WINDOW fact and has ridden on the shared <see cref="WindowUiSettings"/> since DIR.Lib 7.30,
        /// so every widget sharing this context returns the same rect. Asking two of them was a leftover
        /// from before that moved.
        /// </remarks>
        public RectInt FocusedCaretRect => CaretRect;

        /// <inheritdoc/>
        public EquipmentTabState EquipmentState => _equipmentTab.State;

        /// <inheritdoc/>
        public SessionTabState SessionState => _sessionTab.State;

        /// <inheritdoc/>
        public IGuiNavigation? Navigation
        {
            get => _chrome.Navigation;
            set => _chrome.Navigation = value;
        }

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

        /// <summary>The atlas hover's counts so far (resolves, and resolves that asked for a frame), for the frame log.</summary>
        public (int Resolves, int FrameRequests) SkyMapHoverCounts => (_skyMapTab.HoverResolves, _skyMapTab.HoverFrameRequests);

        /// <summary>Where the atlas info panel's pictures come from; see <see cref="VkSkyMapTab.PictureStore"/>.</summary>
        public IObjectPictureStore? ObjectPictureStore
        {
            get => _skyMapTab.PictureStore;
            set
            {
                _skyMapTab.PictureStore = value;
                _plannerTab.PictureStore = value;
            }
        }

        /// <summary>Where the atlas's planet discs get OPAL's maps; see <see cref="VkSkyMapTab.PlanetMapStore"/>.</summary>
        public IPlanetMapStore? PlanetMapStore
        {
            get => _skyMapTab.PlanetMapStore;
            set => _skyMapTab.PlanetMapStore = value;
        }

        /// <summary>
        /// The object catalogue the Live Session preview's object overlay reads, set once by the host from its
        /// services, as tianwen-fits gives its viewer one. Lazy, so setting it starts nothing: the viewer asks for
        /// it only once the overlay reaches the objects.
        /// </summary>
        public DotNext.Threading.AsyncLazy<ICelestialObjectDB>? CelestialObjectDB
        {
            get => _previewViewer.CelestialObjectDB;
            set => _previewViewer.CelestialObjectDB = value;
        }

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

        private static readonly float BaseFontSize = TianWen.UI.Abstractions.GuiTheme.Metrics.BaseFontSize;

        private float FontSize => BaseFontSize * DpiScale;

        // Content area placeholder
        private static RGBAColor32 ContentBg       => Palette.ContentBg;
        // Text that must be READ, so a text role and not a rule weight. SeparatorStrong measured
        // 1.5-1.9:1 here in every state, which is invisible rather than subdued.
        private static RGBAColor32 PlaceholderText => Palette.DimText;

        // Local alias so the chrome above reads as roles rather than as a namespace walk. UiPalette is
        // DIR.Lib's (already in scope via the using); only GuiTheme is TianWen's.
        private static UiPalette Palette => TianWen.UI.Abstractions.GuiTheme.Palette;


        public VkGuiRenderer(VkRenderer renderer, uint width, uint height, SignalBus? bus = null, ILogger? logger = null) : base(renderer)
        {
            Bus = bus;
            _renderer = renderer;
            _width = width;
            _height = height;
            _plannerTab = new VkPlannerTab(renderer) { Bus = bus, Logger = logger };
            _equipmentTab = new VkEquipmentTab(renderer) { Bus = bus };
            _sessionTab = new VkSessionTab(renderer) { Bus = bus };
            _skyMapTab = new VkSkyMapTab(renderer)
            {
                HostRepaintsStatedMoves = true,
                Bus = bus,
                Logger = logger,
            };
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
            // The night calendar under the status-bar date; painted last, over every tab.
            _nightCalendar = new NightCalendarPopover<VulkanContext>(renderer) { Bus = bus };
            // One assignment, after every child exists and before the first per-window value is resolved
            // below: from here on they READ this chrome's settings instead of holding copies, so DPI, font,
            // emoji face and fallback chain all reach them without being pushed -- and so will anything
            // added later. The embedded viewers are included: they self-resolve a font only when nothing
            // gave them one (the standalone tianwen-fits case), and they share this window's display scale
            // because that is what a window HAS. Adopting these settings discards the face they resolved
            // during their own construction, which is the intent -- a viewer inside this chrome should
            // label itself in the chrome's face.
            // The sidebar is a configured DIR.Lib TabBar, not chrome drawn here: a vertical strip of
            // uniform icon cells that cannot be closed or reordered. See RenderSidebar.
            _chrome = new GuiWindowChrome<VulkanContext>(renderer) { Bus = bus };
            ShareUiContext(_plannerTab, _equipmentTab, _sessionTab, _skyMapTab,
                           _liveSessionTab, _guiderTab, _notificationsTab, _homeTab,
                           _guiderViewer, _previewViewer, _planetaryTab, _chrome, _chrome.Sidebar, _chrome.NavigationStrip, _nightCalendar);

            // The GUI's own chrome a tad larger than the viewers it embeds (GuiTheme.InterfaceScale): this chrome, every tab,
            // the sidebar and the calendar. Not the three viewers (the preview, the guide camera and the planetary view, which
            // IS a viewer with a strip), whose toolbar keeps the size it has in tianwen-fits.
            foreach (var widget in (ReadOnlySpan<PixelWidgetBase<VulkanContext>>)[this, _plannerTab, _equipmentTab, _sessionTab,
                _skyMapTab, _liveSessionTab, _guiderTab, _notificationsTab, _homeTab, _chrome, _chrome.Sidebar, _chrome.NavigationStrip, _nightCalendar])
            {
                widget.InterfaceScale = GuiTheme.InterfaceScale;
            }
            ResolveFontPath();
        }

        /// <summary>
        /// The ground the embedded image viewers paint where a picture does not reach. The host sets it to
        /// the colour it clears the window to; see <c>ImageRendererBase.CanvasBackground</c> for why the
        /// two have to be the same value.
        /// </summary>
        /// <remarks>
        /// Fanned out to the three hosted viewers rather than shared through <see cref="ShareUiContext"/>,
        /// which carries the DIR.Lib per-window settings (scale, faces) that every widget reads. This is
        /// not one of those: only a widget with an image pane has anywhere to put it.
        /// </remarks>
        public RGBAColor32 CanvasBackground
        {
            get => _canvasBackground;
            set
            {
                _canvasBackground = value;
                _previewViewer.CanvasBackground = value;
                _guiderViewer.CanvasBackground = value;
                _planetaryTab.CanvasBackground = value;
            }
        }

        private RGBAColor32 _canvasBackground = new RGBAColor32(0x12, 0x12, 0x18, 0xff);

        /// <summary>The caret blink phase the last frame was painted in; see <see cref="CaretBlink"/>.</summary>
        public long PaintedCaretPhase { get; private set; }

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
            // Force Equipment tab when no profile exists, by the node's word: before it has answered the window stays where
            // it opened, and the Equipment tab says it is connecting.
            if (appState.NeedsAProfile && appState.ActiveTab is not GuiTab.Equipment)
            {
                appState.SetActiveTabWithoutHistory(GuiTab.Equipment);
            }

            BeginFrame();
            // The caret blinks on the clock (DIR.Lib's CaretBlink); PaintedCaretPhase is what the host's
            // redraw check compares against, so a focused field costs a frame per blink, not every frame.
            var caretPhase = CaretBlink.PhaseAt(timeProvider.GetTimestamp(), timeProvider.TimestampFrequency);
            PaintedCaretPhase = caretPhase;
            _equipmentTab.CaretPhase = caretPhase;
            _plannerTab.CaretPhase = caretPhase;
            _sessionTab.CaretPhase = caretPhase;
            _skyMapTab.CaretPhase = caretPhase;
            // A frame that repaints part of the window draws the sky of the frame before it (SkyMapTab.HoldViewingTime).
            _skyMapTab.HoldViewingTime = _renderer.LastFrameWasPartial;
            _liveSessionTab.CaretPhase = caretPhase;
            _guiderTab.CaretPhase = caretPhase;
            _planetaryTab.CaretPhase = caretPhase;
            _notificationsTab.CaretPhase = caretPhase;
            _homeTab.CaretPhase = caretPhase;

            _activeTab = appState.ActiveTab switch
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

            // Paint sidebar and status bar on top: these register clickable regions
            _chrome.Render(appState, plannerState, timeProvider, LocalLiveSession, ViewedLiveSession,
                _equipmentTab.State, _sessionTab.State, _skyMapTab.State, _width, _height);
            RenderNightCalendar(plannerState, timeProvider);

            // Declare the composition in the order it was just painted, back to front. Stated HERE, next
            // to the painting, because that is the only place the order is actually decided -- a list
            // built anywhere else is a second opinion about it. The calendar is listed every frame, open or
            // not, so the pointer reaches it before it opens; closed, it has no regions to answer with.
            _children.Clear();
            if (_activeTab is { } painted)
            {
                _children.Add(painted);
            }
            _children.Add(_chrome);
            _children.Add(_nightCalendar);
        }

        /// <summary>
        /// The night calendar, hung from the date label the status bar just painted. Closed while a session runs,
        /// like the arrows beside the date, and whenever the label is not on the bar to hang from.
        /// </summary>
        private void RenderNightCalendar(PlannerState plannerState, ITimeProvider timeProvider)
        {
            var anchor = _chrome.DateCalendarAnchor;

            if (anchor is null)
            {
                plannerState.Calendar.Popover.Close();
            }

            _nightCalendar.Render(plannerState, anchor ?? default, new RectF32(0f, 0f, _width, _height),
                timeProvider, Bus, _skyMapTab.State);
        }

        /// <summary>
        /// Returns the content area rectangle in pixels (excluding sidebar and status bar).
        /// </summary>
        public RectF32 GetContentArea()
        {
            return _chrome.ContentArea(_width, _height);
        }

        /// <summary>
        /// Icon + display name for <paramref name="tab"/> exactly as the sidebar draws it this frame
        /// (the Live Session glyph follows the viewed context's mode/run state). The window title is
        /// built from this, so the title and the sidebar can never disagree about the active tab.
        /// </summary>
        public (string Icon, string Label) TabTitleChrome(GuiTab tab)
            => GuiWindowChrome<VulkanContext>.TabTitleChrome(tab, ViewedLiveSession);

        /// <summary>
        /// The clock's rectangle as the last frame painted it, in the surface's pixels; null when the bar was not drawn.
        /// What a frame owed to the clock's tick alone repaints (<see cref="GuiFrameReasons"/>).
        /// </summary>
        internal RectF32? ClockRect => _chrome.ClockRect;

        public void Dispose()
        {
            _previewViewer.Dispose();
            _guiderViewer.Dispose();
            _planetaryTab.Dispose();
            _plannerTab.Dispose();
            _skyMapTab.Dispose();
            // VkRenderer is owned by the caller; do not dispose here.
        }

        /// <summary>
        /// Paints the tooltip the router has due over the whole window, through DIR.Lib's one painter
        /// (<c>Layout.Builder.Tooltip</c>, #1446): any region's, the rail's, a tab's or an embedded viewer's.
        /// Called by the host after <see cref="Render"/>, so it is the last thing drawn.
        /// </summary>
        /// <remarks>
        /// Before it, the rail painted its own tooltip from <c>TabBar.HoveredIndex</c>, measured and placed by
        /// hand (assuming square cells), and nothing painted the router's, so every <c>.WithTooltip</c> in the
        /// app was dead. The fill keeps its near-opaque alpha so a tooltip still reads as floating over the
        /// content rather than cut into it; only the hue comes from the palette.
        /// </remarks>
        public void RenderTooltip(TooltipRequest? request)
        {
            if (request is not { } due || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var style = new Layout.TooltipStyle(
                Background: Palette.HeaderBg.WithAlpha(0xF0),
                Text: Palette.BodyText,
                Border: Palette.SeparatorStrong,
                FontSize: BaseFontSize);
            RenderLayout(Layout.Builder.Tooltip(due, style), new RectF32(0f, 0f, _width, _height));
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
                    _notificationsTab.Render(appState, ViewContexts.Active, contentRect);
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
            // when any piece is unavailable. A rig's reticle carries the rig's own sensor, from its profile's captured
            // geometry (P5b part 8): this computer's camera is not the one at that pointing.
            // ONE rule for every view (P6): the profile's captured sensor, which a connect records into its OTA. The
            // local view used to read its live camera driver here, on the render thread, every frame.
            var sensorFov = ViewContexts.ProfileOnShow(appState.ActiveProfile)?.Data?.PrimarySensorFovDeg;

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
        /// Surfaces the schedule of the view on show to the sky map so the user can see where tonight's targets sit
        /// (P5b part 8): this computer's committed plan (<see cref="SessionTabState.Schedule"/>), or a rig's own. The run's
        /// <see cref="LiveSessionState.ActiveObservation"/> is flagged so the renderer can highlight the target currently
        /// being imaged / slewed to (<see cref="SkyMapScheduleMarkers"/>).
        /// </summary>
        private void PopulateSkyMapScheduleTargets()
        {
            var view = ViewContexts.Active;
            var schedule = view.IsLocal ? SessionState.Schedule : view.LiveSession.ActiveSession?.Observations;
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
            // and active target identity avoids an allocation every render frame
            // (~60 FPS); a rig's mirror maps its schedule once per polled state, so
            // its identity holds between polls too.
            // The view's own run, to match its schedule source: the run executing that plan.
            var active = view.LiveSession.ActiveObservation?.Target;
            if (ReferenceEquals(_cachedSchedule, schedule) && _cachedActiveTarget == active)
            {
                return;
            }
            _cachedSchedule = schedule;
            _cachedActiveTarget = active;

            _skyMapTab.State.ScheduleTargets = SkyMapScheduleMarkers.Build(schedule, active);
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
                var msg = $"{tab}. Coming soon";
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
            //
            // One resolve covering every role, shared with the FITS viewer. This used to be two probes here
            // plus a chain built below them, while the viewer ran its own copy of the two probes and none of
            // the chain -- so the two hosts could disagree about which marks were drawable, and did.
            var fonts = BundledFonts.Resolve();

            EmojiFontPath = fonts.Emoji;
            if (fonts.Text.Length > 0)
            {
                FontPath = fonts.Text;
            }

            FontFallback = fonts.Fallback;
        }
    }
}
