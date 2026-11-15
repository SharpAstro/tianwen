using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The GUI window's chrome: the top bar (status message, date group with arrows and verdict, Connect All, clock) and the
/// sidebar (the navigation strip over the <see cref="TabBar{TSurface}"/> rail), over any <typeparamref name="TSurface"/>.
/// </summary>
/// <remarks>
/// Hoisted out of the Vulkan window renderer (#2139) so a CPU renderer (DIR.Lib's WireframeRenderer, RgbaImageRenderer)
/// can draw the same chrome: a refactor, not a restyle. Everything it reads arrives as an explicit input of
/// <see cref="Render"/> (the app state, the planner, the clock, the local and the viewed session, the equipment and session
/// tab states, the sky map), so it reaches into nothing. The window composes it: the active tab paints under it, the night
/// calendar over it, and the calendar hangs from <see cref="DateCalendarAnchor"/>.
/// </remarks>
public sealed class GuiWindowChrome<TSurface> : CompositeWidget<TSurface>
{
    public GuiWindowChrome(Renderer<TSurface> renderer) : base(renderer)
    {
        // The sidebar is a configured DIR.Lib TabBar, not chrome drawn here: a vertical strip of
        // uniform icon cells that cannot be closed or reordered. See RenderSidebar.
        _sidebar = new TabBar<TSurface>(renderer)
        {
            Side = TabStripSide.Left,
            Sizing = TabSizing.Uniform,
            CanCloseTabs = false,
            CanReorderTabs = false,
        };
        _navigationStrip = new GuiNavigationStrip<TSurface>(renderer);
        _children = [_sidebar, _navigationStrip];
    }

    /// <summary>The rail, for the window to hand the shared UI context and interface scale to.</summary>
    public TabBar<TSurface> Sidebar => _sidebar;

    /// <summary>The strip of Back and Forward above the rail, for the same.</summary>
    public GuiNavigationStrip<TSurface> NavigationStrip => _navigationStrip;

    /// <summary>The window's navigation, which the strip's Back and Forward drive; null until the event handler has bound itself.</summary>
    public IGuiNavigation? Navigation
    {
        get => _navigationStrip.Navigation;
        set => _navigationStrip.Navigation = value;
    }

    private readonly IReadOnlyList<PixelWidgetBase<TSurface>> _children;

    /// <inheritdoc/>
    protected override IReadOnlyList<PixelWidgetBase<TSurface>> Children => _children;

    /// <inheritdoc/>
    /// <remarks>The rail and the strip are painted under this widget's own top bar, as they always were.</remarks>
    protected override int ChildrenUnderOwn => _children.Count;

    private float _width;
    private float _height;

    /// <summary>The sidebar's width in device pixels at this widget's scale.</summary>
    public float SidebarWidth => BaseSidebarWidth * DpiScale;

    /// <summary>The top bar's height in device pixels at this widget's scale.</summary>
    public float StatusBarHeight => BaseStatusBarHeight * DpiScale;

    /// <summary>The content area the window gives its active tab: right of the sidebar, under the top bar.</summary>
    public RectF32 ContentArea(float width, float height)
        => new(SidebarWidth, StatusBarHeight, width - SidebarWidth, height - StatusBarHeight);

    /// <summary>
    /// The date label's rectangle on the bar, as the last <see cref="Render"/> painted it; null while a session runs (the label
    /// is not a button then) and whenever the bar was not drawn. What the night calendar hangs from.
    /// </summary>
    public RectF32? DateCalendarAnchor { get; private set; }

    /// <summary>
    /// Paints the sidebar, then the top bar over it, and registers their regions. Call it after the active tab.
    /// </summary>
    public void Render(
        GuiAppState appState,
        PlannerState plannerState,
        ITimeProvider timeProvider,
        LiveSessionState localLiveSession,
        LiveSessionState viewedLiveSession,
        EquipmentTabState equipmentState,
        SessionTabState sessionState,
        SkyMapState skyMapState,
        float width,
        float height)
    {
        BeginFrame();
        _appState = appState;
        _width = width;
        _height = height;
        RenderSidebar(appState, localLiveSession, viewedLiveSession);
        RenderStatusBar(appState, plannerState, timeProvider, localLiveSession, equipmentState, sessionState, skyMapState);

        DateCalendarAnchor = null;
        foreach (var region in RegisteredRegions)
        {
            if (region.Result is HitResult.ButtonHit { Action: DateCalendarAction })
            {
                DateCalendarAnchor = new RectF32(region.X, region.Y, region.Width, region.Height);
            }
        }
    }

    /// <summary>The status-bar date label's action: it opens the night calendar.</summary>
    private const string DateCalendarAction = "DateCalendar";

    // The navigation sidebar. A DIR.Lib TabBar configured as a nav rail rather than ~70 lines of
    // hand-placed rects here, which is what makes its click regions the rects it drew instead of a
    // second piece of geometry derived from an index.
    private readonly TabBar<TSurface> _sidebar;
    private readonly GuiNavigationStrip<TSurface> _navigationStrip;

    // Reused per frame so building the strip allocates nothing: the tab set is fixed (GuiAppState
    // .TabOrder) and only the per-item icon / enabled state varies.
    private readonly List<TabItem<GuiTab>> _sidebarItems = [];

    // Stashed by Render, for the same reason _activeTab is: a press arrives BETWEEN frames, and the
    // rail's dispatch has to act on the state the frame was painted from. The hand-drawn sidebar
    // reached the same state through a closure captured per region per frame; one field is the same
    // reference held once. A window has exactly one app state, so this can never be a different one.
    private GuiAppState? _appState;

    /// <summary>
    /// What the navigation rail's cell at <paramref name="hit"/> MEANS: the tab it selects, or null
    /// when the hit is not an enabled rail cell.
    /// </summary>
    /// <remarks>
    /// The rail is a generic <see cref="TabBar{TSurface}"/>, so it reports a cell as an indexed
    /// <see cref="HitResult.ListItemHit"/> and cannot know what an index stands for. Turning the index
    /// into a <see cref="GuiTab"/> is this chrome's, and it is said HERE and nowhere else -- the region
    /// walk, the node walk and the dispatch below all ask this one question. A LOCKED cell arrives as
    /// <see cref="TabBarRegions.DisabledTabs"/> and answers null, which is what keeps it inert.
    /// </remarks>
    private GuiTab? RailTab(HitResult? hit)
        => hit is HitResult.ListItemHit { ListId: TabBarRegions.Tabs, Index: var i } && i < _sidebarItems.Count
            ? _sidebarItems[i].Value
            : null;

    /// <summary>The action id a rail cell reports, which is what click-by-label drives a tab by.</summary>
    private static HitResult.ButtonHit TabButton(GuiTab tab) => new HitResult.ButtonHit($"Tab:{tab}");

    /// <summary>
    /// <see cref="SelectTab"/> as a delegate, made ONCE. The item list is rebuilt every frame, so a
    /// method group written at the item site would allocate one delegate per tab per frame for a
    /// handler that never varies.
    /// </summary>
    private Action<GuiTab>? _selectTab;

    /// <summary>Puts <paramref name="tab"/> on screen. What a rail press and a rail cell's chord both do.</summary>
    private void SelectTab(GuiTab tab)
    {
        if (_appState is not { } appState || appState.ActiveTab == tab)
        {
            return;
        }

        appState.ActiveTab = tab;
        appState.NeedsRedraw = true;
    }

    /// <summary>
    /// Every clickable region painted this frame, with each rail cell re-stated as the tab button it
    /// is: the <c>Tab:&lt;name&gt;</c> label click-by-label drives, and the handler that switches to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The COMPOSITION is <see cref="CompositeWidget{TSurface}.CollectPaintedRegions"/>'s; only the
    /// re-statement is TianWen's. The rect is the bar's own and the label comes from the same item list
    /// the bar was handed, so this adds no second geometry.
    /// </para>
    /// <para>
    /// Only the LABEL now. The handler used to be here too, because the bar had no seam to hand one
    /// to; <see cref="TabItem{T}.OnSelect"/> is that seam, so the cell arrives already knowing what it
    /// does and this walk leaves <see cref="ClickableRegion.OnClick"/> alone.
    /// </para>
    /// </remarks>
    public override void CollectPaintedRegions(List<ClickableRegion> into)
    {
        var first = into.Count;
        base.CollectPaintedRegions(into);

        for (var i = first; i < into.Count; i++)
        {
            if (RailTab(into[i].Result) is { } tab)
            {
                into[i] = into[i] with { Result = TabButton(tab) };
            }
        }
    }

    // Base layout constants (at 1x scale)
    private const float BaseSidebarWidth = 52f;
    private const float BaseStatusBarHeight = 28f;
    private static readonly float BaseFontSize = GuiTheme.Metrics.BaseFontSize;

    private float FontSize => BaseFontSize * DpiScale;

    // Live Session sidebar icon reflects session state (overridden per-frame in GuiWindowChrome.RenderSidebar):
    // idle = camera, running = camera with flash. The clapper board marks the Session Setup tab
    // as the "set up and launch here" entry point. That tab is BOTH the night's configuration and
    // the Start button, which is why neither obvious glyph worked: a cog implied only the setup
    // half and a rocket only the launch half. A shoot you stage and then call action on spans both.
    private const string LiveSessionIdleIcon      = "\U0001F4F7"; // camera (Preview)
    private const string LiveSessionRunningIcon   = "\U0001F4F8"; // camera with flash (running session)
    private const string LiveSessionPolarIcon     = "\U0001F9ED"; // compass (Polar Align mode)
    private const string LiveSessionPlanetaryIcon = "\U0001FA90"; // ringed planet (Planetary mode)
    private const string LiveSessionFlatsIcon     = "\U0001F4A1"; // light bulb (Flats mode)

    // Per-tab sidebar chrome (icon + display name + the chord that reaches it; the hover tooltip is
    // "Label (Shortcut)"). The sidebar ORDER comes from GuiAppState.TabOrder (shared with
    // Ctrl+Tab cycling) so the visual order and the cycle order can't drift apart. The label is
    // a separate field because the window title reuses it (via TabTitleChrome) without the
    // shortcut suffix.
    //
    // The chord is a KeyChord rather than a printed string, and the tooltip is printed FROM it
    // (ChordLabel): the binding used to be written twice, once as text here and once as an arm in the
    // host's key router, which is two places a new tab can be added to and only one of them shows.
    private static readonly Dictionary<GuiTab, (string Icon, string Label, KeyChord Shortcut)> TabChrome = new()
    {
        // House, not satellite/antenna/globe: the icon has to stay neutral between local and remote,
        // or a single-scope user's one-card board looks like a remote-monitoring feature they do not
        // use -- and it names the SCREEN, which is due to hold multi-night progress beside the cards.
        [GuiTab.Home]          = ("\U0001F3E0",        "Home",          new KeyChord(InputKey.H, InputModifier.Ctrl)),
        [GuiTab.Equipment]     = ("\U0001F52D",        "Equipment",     new KeyChord(InputKey.E, InputModifier.Ctrl)),
        [GuiTab.Planner]       = ("\U0001F4C5",        "Planner",       new KeyChord(InputKey.P, InputModifier.Ctrl)),
        [GuiTab.SkyMap]        = ("\U0001F30C",        "Sky Map",       new KeyChord(InputKey.M, InputModifier.Ctrl)),
        [GuiTab.Session]       = ("\U0001F3AC",        "Session Setup", new KeyChord(InputKey.S, InputModifier.Ctrl)),
        [GuiTab.LiveSession]   = (LiveSessionIdleIcon, "Live Session",  new KeyChord(InputKey.L, InputModifier.Ctrl)),
        [GuiTab.Guider]        = ("\U0001F3AF",        "Guider",        new KeyChord(InputKey.G, InputModifier.Ctrl)),
        [GuiTab.Notifications] = ("\U0001F514",        "Notifications", new KeyChord(InputKey.N, InputModifier.Ctrl)),
    };

    // The Live Session glyph follows the VIEWED context's state: a running session flips to
    // "camera with flash"; otherwise the mode picks the glyph -- compass for Polar Align, ringed
    // planet for Planetary, light bulb for Flats, camera for Preview. (Mirrors the mode pill so
    // the sidebar reads at a glance.) A flat run deliberately leaves IsRunning false, so the
    // running-session glyph never masks the Flats one for the duration of the run.
    private static string LiveSessionIcon(LiveSessionState viewed) => viewed.IsRunning
        ? LiveSessionRunningIcon
        : viewed.Mode switch
        {
            LiveSessionMode.PolarAlign => LiveSessionPolarIcon,
            LiveSessionMode.Planetary => LiveSessionPlanetaryIcon,
            LiveSessionMode.Flats => LiveSessionFlatsIcon,
            _ => LiveSessionIdleIcon,
        };

    /// <summary>How a chord is written in a tooltip: "Ctrl+M", modifiers in the order a keyboard
    /// reads them.</summary>
    private static string ChordLabel(KeyChord chord)
    {
        var mods = chord.Modifiers;
        var prefix = ((mods & InputModifier.Ctrl) != 0 ? "Ctrl+" : string.Empty)
            + ((mods & InputModifier.Alt) != 0 ? "Alt+" : string.Empty)
            + ((mods & InputModifier.Shift) != 0 ? "Shift+" : string.Empty);
        return prefix + chord.Key;
    }

    /// <summary>
    /// Icon + display name for <paramref name="tab"/> exactly as the sidebar draws it this frame
    /// (the Live Session glyph follows the viewed context's mode/run state). The window title is
    /// built from this, so the title and the sidebar can never disagree about the active tab.
    /// </summary>
    public static (string Icon, string Label) TabTitleChrome(GuiTab tab, LiveSessionState viewedLiveSession)
    {
        var (icon, label, _) = TabChrome[tab];
        if (tab is GuiTab.LiveSession)
        {
            icon = LiveSessionIcon(viewedLiveSession);
        }
        return (icon, label);
    }

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
    // A locked tab's icon is deliberately the heaviest RULE weight rather than a text role: it marks an
    // affordance that is unavailable, and WCAG exempts inactive components from the contrast minimums
    // (it measures 1.45-1.75:1, close to the 1.95 the hand-picked 0x444450 gave). Do NOT "fix" this to
    // DimText, which would make a locked tab look merely de-emphasised.
    private static RGBAColor32 LockedIcon      => Palette.SeparatorStrong;

    // Status bar
    private static RGBAColor32 StatusBarBg     => Palette.HeaderBg;
    private static RGBAColor32 StatusText      => Palette.DimText;


    // Local alias so the chrome above reads as roles rather than as a namespace walk. UiPalette is
    // DIR.Lib's (already in scope via the using); only GuiTheme is TianWen's.
    private static UiPalette Palette => GuiTheme.Palette;

    /// <summary>
    /// The rail's palette, mapped from the same roles the hand-drawn sidebar used so the adoption is
    /// a refactor and not a restyle. A PROPERTY, like every colour above, because a theme can change
    /// while the window is alive and a field initialiser would snapshot at type-init.
    /// </summary>
    /// <remarks>
    /// Two of these deliberately blend into the surface behind them, because a nav rail is not a
    /// document strip and TianWen's has never drawn either:
    /// <list type="bullet">
    /// <item><b>Separator</b> takes the rail's own background, so no rule is drawn between cells or
    /// down the rail's inner edge. Give it <c>Palette.Separator</c> to turn them on.</item>
    /// <item><b>ActiveAccent</b> takes the active plate, so the accent bar is invisible. Give it
    /// <c>Palette.Accent</c> for the VS Code-style marker on the outer edge.</item>
    /// </list>
    /// <see cref="TabBarColors.HoverBackground"/> is the reason the rail needed it: with no accent,
    /// hover and active would otherwise render identically and the rail could not say which cell a
    /// click would take you to.
    /// </remarks>
    private static TabBarColors SidebarColors => new()
    {
        BarBackground = SidebarBg,
        InactiveBackground = SidebarBg,
        HoverBackground = HoverTabBg,
        ActiveBackground = ActiveTabBg,
        ActiveText = ActiveIcon,
        InactiveText = IconColor,
        DisabledText = LockedIcon,
        Separator = SidebarBg,
        ActiveAccent = ActiveTabBg,
    };

    /// <summary>
    /// Paints the navigation rail by handing <see cref="GuiAppState.TabOrder"/> to a configured
    /// <see cref="TabBar{TSurface}"/>, and draws the hovered tab's tooltip beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be ~70 lines of hand-placed rects: a button rect derived from an index
    /// (<c>startY + i * size</c>), hover re-derived from the mouse against that same arithmetic, and
    /// a click region registered separately from the drawing. Three expressions of one geometry, any
    /// of which could drift from the others. The bar registers the rects it painted, so the click
    /// region IS the drawn cell by construction.
    /// </para>
    /// <para>
    /// The tooltip stays here on purpose. It is drawn OUTSIDE the rail, over whatever content is
    /// beside it, and the bar clips to its own bounds — so it reports
    /// <see cref="TabBar{TSurface}.HoveredIndex"/> and the host paints.
    /// </para>
    /// </remarks>
    private void RenderSidebar(GuiAppState appState, LiveSessionState localLiveSession, LiveSessionState viewedLiveSession)
    {
        var sw = SidebarWidth;
        var noProfile = appState.ActiveProfile is null;

        _sidebarItems.Clear();
        foreach (var tab in GuiAppState.TabOrder)
        {
            // One path with the window title: the mode-following Live Session glyph is resolved
            // inside TabTitleChrome (see LiveSessionIcon), never re-derived here.
            var (icon, label) = TabTitleChrome(tab, viewedLiveSession);
            // Home, where the window opens, stays open while this computer's node is still answering which profile it runs.
            var locked = (noProfile && tab is not GuiTab.Equipment && (appState.NeedsAProfile || tab is not GuiTab.Home))
                      || (localLiveSession.IsRunning && tab is GuiTab.Equipment);

            // The tooltip carries the shortcut, and for a LOCKED tab it is the only place the reason
            // can be said at all -- the rail has room for a glyph and nothing else.
            var shortcut = ChordLabel(TabChrome[tab].Shortcut);
            var tooltip = locked
                ? $"{label} — {(!noProfile ? "not while a session is running" : appState.NeedsAProfile ? "create a profile first" : appState.WaitingForLocalNode)}"
                : $"{label} ({shortcut})";

            _sidebarItems.Add(new TabItem<GuiTab>(label, tab)
            {
                Icon = icon,
                IsEnabled = !locked,
                Tooltip = tooltip,
                // The binding and the handler are the ITEM's (DIR.Lib 9.5), so neither has to be put
                // back on after the paint. The strip drops both for a locked tab, which is what makes
                // its chord inert without a guard beside the key.
                Shortcut = TabChrome[tab].Shortcut,
                OnSelect = _selectTab ??= SelectTab,
            });
        }

        // The pointer, so the bar resolves its own hover rather than being told an index derived from
        // last frame's geometry.
        _sidebar.Pointer = appState.MouseScreenPosition;
        // Set per frame rather than once at construction: both follow the palette and the display
        // scale, either of which can change while the window is alive (F12, a monitor move).
        _sidebar.Colors = SidebarColors;
        _sidebar.IconSize = FontSize * 1.3f;
        // Each rail cell declares its tooltip on its own region now (DIR.Lib's tab strip, opening to the
        // right of the rail), so it reaches the router like any other and RenderTooltip draws it.
        // Back and Forward sit at the top of the rail, in its own width; the tab cells start below the height the strip itself reports.
        var stripH = _navigationStrip.Height;
        _navigationStrip.Render(new RectF32(0f, StatusBarHeight, sw, stripH));
        _sidebar.Render(new RectF32(0f, StatusBarHeight + stripH, sw, _height - StatusBarHeight - stripH),
            _sidebarItems, appState.ActiveTab);
    }

    // -----------------------------------------------------------------------
    // Status bar: registers clickable regions for interactive elements
    // -----------------------------------------------------------------------

    private void RenderStatusBar(GuiAppState appState, PlannerState plannerState, ITimeProvider timeProvider,
        LiveSessionState localLiveSession, EquipmentTabState equipmentState, SessionTabState sessionState, SkyMapState skyMapState)
    {
        var w = (float)_width;
        var sbh = StatusBarHeight;

        FillRect(0, 0, w, sbh, StatusBarBg);

        // Unknown until this frame's bar has arranged it: a frame that draws no clock leaves none to repaint alone.
        ClockRect = null;

        if (string.IsNullOrEmpty(FontPath))
        {
            return;
        }

        var now = timeProvider.GetUtcNow().ToOffset(plannerState.SiteTimeZone);
        // Invariant names: the clock's box is sized from an English sample (ClockWidthSample), and that box is what a
        // clock frame repaints, so a locale's longer day or month name would be cut or left stale outside it.
        var clockText = now.ToString("ddd d MMM HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var sessionRunning = localLiveSession.IsRunning;
        sessionState.IsSessionRunning = sessionRunning;

        // Date + night-window label string (data only): [<] date (HH:mm-HH:mm) [>].
        var planDate = plannerState.PlanningDate ?? now;
        var isTonight = !plannerState.PlanningDate.HasValue || planDate.Date == now.Date;
        string dateStr;
        // The same label without the night's hours: what the last rung of the fit below keeps when even the date
        // with its hours does not fit beside the clock and Connect All.
        var dateShort = isTonight ? "Tonight" : planDate.ToString("ddd d MMM");
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
        // Shown while the bar has room for it and hidden where it has not (see the fit below): the Notifications
        // tab keeps every message.
        var statusMessage = appState.StatusMessage is { Length: > 0 } msg ? msg : null;
        // The warning mark (#2025) leads the message, so a status message is told from an accented control by
        // form as well as colour. StatusMessage carries no severity, and the message has always been drawn in
        // Warn, so it is treated as a Warning here.
        var markDu = BaseFontSize * 0.85f;
        Layout.Node StatusZone(bool shown, bool measuring)
        {
            if (statusMessage is null || !shown)
            {
                return measuring ? Layout.Builder.Spacer().WFixed(0f) : Layout.Builder.Spacer().WStar();
            }

            var mark = SeverityMarks.Lead(NotificationSeverity.Warning, markDu, gapDu);

            // The message takes what its zone has left after the mark and ends in an ellipsis where that is short:
            // the engine trims the run to the width it arranged. The whole message is on hover. Measured, it is the
            // least a message is worth showing at, since a run trimmed to a letter or two says nothing.
            var text = measuring
                ? Layout.Builder.Text("", BaseFontSize * 0.85f, Palette.Warn, TextAlign.Near, TextAlign.Center, widthSample: StatusLeastSample)
                    .WAuto().HStar()
                : Layout.Builder.Text(statusMessage, BaseFontSize * 0.85f, Palette.Warn, TextAlign.Near, TextAlign.Center)
                    .WStar().HStar();
            var zone = Layout.Builder.HStack(mark, text).HStar();
            return measuring ? zone.WAuto() : zone.WStar().WithTooltip(statusMessage);
        }

        // CENTRE: [<] date [>] verdict, centred via flanking star spacers. Arrows hidden during a session.
        // The label opens the night calendar (whose Tonight button is what the label's old reset-on-click
        // became); the verdict is the planned night's, from the same summary the calendar cell reads.
        Layout.Node DateLabel(string text)
        {
            var label = Layout.Builder.Text(text, BaseFontSize, dateColor, TextAlign.Center, TextAlign.Center)
                .WAuto().HStar();
            if (sessionRunning)
            {
                return label;
            }
            var calendar = plannerState.Calendar;
            return label.PadX(gapDu)
                .BgHover(TianWen.UI.Abstractions.GuiTheme.Hover(StatusBarBg))
                .Opens(calendar.Popover)
                // Runs before the toggle: an opening calendar starts on the planned night's month.
                .Clickable(new HitResult.ButtonHit(DateCalendarAction),
                    _ => { if (!calendar.Popover.IsOpen) NightCalendarActions.PrepareToOpen(calendar); });
        }
        var dateLabelFull = DateLabel(dateStr);
        var dateLabelShort = DateLabel(dateShort);
        if (sessionRunning)
        {
            plannerState.Calendar.Popover.Close();
        }

        Layout.Node verdictNode = Layout.Builder.Spacer().WFixed(0f);
        var plannedNight = NightCalendarActions.PlanningEveningDate(plannerState, timeProvider);
        if (plannerState.Calendar.Data.Nights.TryGetValue(plannedNight, out var plannedSummary))
        {
            var verdict = NightVerdict.For(plannedSummary);
            verdictNode = Layout.Builder.Text(verdict.Label, BaseFontSize * 0.9f,
                    NightCalendarActions.VerdictColour(verdict.Outlook),
                    TextAlign.Center, TextAlign.Center)
                .WAuto().HStar();
        }
        Layout.Node? prev = null;
        Layout.Node? next = null;
        if (!sessionRunning)
        {
            prev = FormRowLayout.StepMark("◀", BaseFontSize * 0.9f, StatusText)
                .WFixed(BaseStatusBarHeight).HStar().Bg(arrowBg).BgHover(TianWen.UI.Abstractions.GuiTheme.Hover(arrowBg))
                .Clickable(new HitResult.ButtonHit("DatePrev"),
                    _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, -1, skyMapState); });
            next = FormRowLayout.StepMark("▶", BaseFontSize * 0.9f, StatusText)
                .WFixed(BaseStatusBarHeight).HStar().Bg(arrowBg).BgHover(TianWen.UI.Abstractions.GuiTheme.Hover(arrowBg))
                .Clickable(new HitResult.ButtonHit("DateNext"),
                    _ => { PlannerActions.ShiftPlanningDate(plannerState, timeProvider, +1, skyMapState); });
        }
        Layout.Node DateGroup(bool arrows, bool verdict, bool hours)
        {
            var dateLabel = hours ? dateLabelFull : dateLabelShort;
            var shownVerdict = verdict ? verdictNode : Layout.Builder.Spacer().WFixed(0f);
            return arrows && prev is not null && next is not null
                ? Layout.Builder.HStack(prev, dateLabel, next, shownVerdict).WAuto().HStar().WithGap(gapDu)
                : Layout.Builder.HStack(dateLabel, shownVerdict).WAuto().HStar().WithGap(gapDu);
        }

        // RIGHT: [Connect All] + wall clock, right-aligned via a leading star spacer. Connect All is
        // globally reachable on every tab; visible whenever the active profile has assigned devices and
        // only actionable once discovery has finished (gate computed by EquipmentActions, shared with
        // the equipment panel). The trailing gap rides with the button so the clock stays flush right
        // when the button is absent.
        Layout.Node connectAll = Layout.Builder.Spacer().WFixed(0f);
        if (appState.ActiveProfile?.Data is { } connectAllProfile)
        {
            var ca = EquipmentActions.ComputeConnectAllStatus(
                connectAllProfile, appState.LocalNode,
                appState.LocalNode?.Listed ?? [], equipmentState.PendingTransitions,
                equipmentState.IsDiscovering);
            if (ca.Visible)
            {
                // Connect All was the loudest literal left in the chrome: a fixed green that ignored
                // the theme entirely and, in a dark-adaptation palette, became the brightest thing on
                // screen. It was the Success role until #2125 and is the go button's fill now, the accent:
                // connecting the rig is an action, and green is kept for a state. Disabled is a plain rule fill.
                // Enabled is a FILLED chip, so its label is ink chosen from the fill, not a text role:
                // DimText on the green Success fill measured about 1.4:1 and was effectively unreadable.
                // Disabled is a plain rule fill, where the ordinary de-emphasised role is right.
                var caBg = ca.Enabled ? TianWen.UI.Abstractions.GuiTheme.GoButtonBg : Palette.Separator;
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
                    caButton = caButton.BgHover(TianWen.UI.Abstractions.GuiTheme.Hover(caBg))
                        .Clickable(new HitResult.ButtonHit("ConnectAll"),
                            _ => { PostSignal(new ConnectAllDevicesSignal()); });
                }
                connectAll = Layout.Builder.HStack(caButton, Layout.Builder.Spacer().WFixed(gapDu * 2f)).WAuto().HStar();
            }
        }
        // Measured from the widest it can be, so its box does not change with the digits: the clock is repainted
        // ALONE on a tick (#2130), which is only sound while its rectangle holds still, and a box that followed the
        // digits also moved Connect All beside it by a pixel or two every second. The time alone where the bar has no
        // room for the day beside it.
        var clockWithDay = Layout.Builder.Text(clockText, BaseFontSize, StatusText, TextAlign.Far, TextAlign.Center, widthSample: ClockWidthSample)
            .WAuto().HStar();
        var clockTimeAlone = Layout.Builder.Text(now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                BaseFontSize, StatusText, TextAlign.Far, TextAlign.Center, widthSample: ClockTimeWidthSample)
            .WAuto().HStar();

        // The bar over the content area (right of the sidebar gutter, small right margin). Three equal zones while
        // each holds what sits in it, which centres the date on the content below. A narrower window (half a screen
        // and less) has no room for that, and the zones drew over each other. There the bar gives things up, least
        // needed first, until what is left fits: the status message (the owner, 2026-11-14: it can be hidden where
        // there is no room), the day beside the time, the day arrows (the calendar the label opens still changes the
        // night) and the night's verdict. Every step is decided on the engine's measurement of the row, never a width
        // written here.
        var barRect = new RectF32(SidebarWidth + 6f, 0f, w - (SidebarWidth + 6f) - 4f, sbh);
        var available = new Layout.Size<float>(barRect.Width, barRect.Height);
        var groupGap = Layout.Builder.Spacer().WFixed(gapDu * 2f);
        // The last rung also drops the night's hours from the date label: a pinned date's label is the widest thing
        // left, and with Connect All beside the clock at 1.5x on a half-screen window the rung before it still ran the
        // clock off the window (found by the window-chrome sweep, #2139). The order of the rungs above it is unchanged.
        ReadOnlySpan<(bool Status, bool Day, bool Arrows, bool Verdict, bool Hours)> fits =
        [
            (true, true, true, true, true),
            (false, true, true, true, true),
            (false, false, true, true, true),
            (false, false, false, true, true),
            (false, false, false, false, true),
            (false, false, false, false, false),
        ];
        var fit = fits[^1];
        foreach (var candidate in fits)
        {
            var row = Layout.Builder.HStack(
                    StatusZone(candidate.Status, measuring: true),
                    DateGroup(candidate.Arrows, candidate.Verdict, candidate.Hours),
                    groupGap,
                    connectAll,
                    candidate.Day ? clockWithDay : clockTimeAlone)
                .WAuto().HStar();
            if (MeasureLayout(row, available).Width <= barRect.Width)
            {
                fit = candidate;
                break;
            }
        }

        var clockNode = fit.Day ? clockWithDay : clockTimeAlone;
        var dateGroup = DateGroup(fit.Arrows, fit.Verdict, fit.Hours);
        var rightGroup = Layout.Builder.HStack(connectAll, clockNode).WAuto().HStar();
        var widestGroup = MathF.Max(MeasureLayout(dateGroup, available).Width, MeasureLayout(rightGroup, available).Width);
        var bar = fit.Status && widestGroup <= barRect.Width / 3f
            ? Layout.Builder.HStack(
                Layout.Builder.HStack(StatusZone(shown: true, measuring: false)).WStar().HStar(),
                Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), dateGroup, Layout.Builder.Spacer().WStar()).WStar().HStar(),
                Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), rightGroup).WStar().HStar())
            : Layout.Builder.HStack(
                Layout.Builder.HStack(StatusZone(fit.Status, measuring: false)).WStar().HStar(),
                dateGroup, groupGap, rightGroup);
        var arrangedBar = RenderLayout(bar, barRect, drawFill: (fill, rect) =>
        {
            // The status message's warning mark: a keyed baked-icon Fill (SeverityMarks), centred in its slot.
            if (SeverityMarks.TryPlace(fill.Key, rect, out var mask, out var sq, out var ink))
            {
                DrawCoverageMask(mask, sq.X, sq.Y, sq.Width, ink);
            }
        });

        // Where the engine put the clock, for the frame that repaints it alone: its own box, as arranged (the bar's
        // fill, painted first, is clipped to the same region, so it is repainted under it).
        foreach (var arranged in arrangedBar)
        {
            if (ReferenceEquals(arranged.Node, clockNode))
            {
                ClockRect = new RectF32(arranged.Bounds.X, arranged.Bounds.Y, arranged.Bounds.Width, arranged.Bounds.Height);
                break;
            }
        }
    }

    /// <summary>No clock text is wider: a capital W or M and two of the widest lower-case letters bound every day and month name.</summary>
    private const string ClockWidthSample = "Wmm 30 Mmm 00:00:00";

    /// <summary>The clock's box where the bar has room for the time alone.</summary>
    private const string ClockTimeWidthSample = "00:00:00";

    /// <summary>The least of a status message worth showing: with less room than this for it, the bar hides it.</summary>
    private const string StatusLeastSample = "Found 32 devices";

    /// <summary>
    /// The clock's rectangle as the last frame painted it, in the surface's pixels; null when the bar was not drawn.
    /// What a frame owed to the clock's tick alone repaints (<see cref="GuiFrameReasons"/>).
    /// </summary>
    public RectF32? ClockRect { get; private set; }
}
