using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Top-level GPU renderer for the N.I.N.A.-style integrated GUI.
    /// Extends <see cref="VkTabBase"/> so the sidebar, status bar, and chrome
    /// participate in the unified <see cref="PixelWidgetBase{TSurface}.RegisterClickable"/>
    /// / <see cref="PixelWidgetBase{TSurface}.HitTestAndDispatch"/> system.
    /// </summary>
    public sealed class VkGuiRenderer : VkTabBase, IDisposable
    {
        private readonly VkRenderer _renderer;
        private readonly VkPlannerTab _plannerTab;
        private readonly VkEquipmentTab _equipmentTab;
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

        /// <summary>The currently active tab as an <see cref="IPixelWidget"/> for tab-specific hit testing.</summary>
        public IPixelWidget? ActiveTab { get; private set; }

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
            ("\U0001F52D", GuiTab.Equipment),   // 🔭 Telescope
            ("\U0001F4C5", GuiTab.Planner),     // 📅 Calendar
            ("\U0001F30C", GuiTab.Viewer),      // 🌌 Milky Way
            ("\U0001F3AF", GuiTab.Session),     // 🎯 Bullseye
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

        public VkGuiRenderer(VkRenderer renderer, uint width, uint height) : base(renderer)
        {
            _renderer = renderer;
            _width = width;
            _height = height;
            _plannerTab = new VkPlannerTab(renderer);
            _equipmentTab = new VkEquipmentTab(renderer);
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
            TimeProvider timeProvider)
        {
            // Force Equipment tab when no profile exists
            if (appState.ActiveProfile is null && appState.ActiveTab is not GuiTab.Equipment)
            {
                appState.ActiveTab = GuiTab.Equipment;
            }

            BeginFrame();
            _equipmentTab.FrameCount++;
            _plannerTab.FrameCount++;

            ActiveTab = appState.ActiveTab switch
            {
                GuiTab.Planner => _plannerTab,
                GuiTab.Equipment => _equipmentTab,
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
        public PixelRect GetContentArea()
        {
            return new PixelRect(SidebarWidth, StatusBarHeight, (float)_width - SidebarWidth, (float)_height - StatusBarHeight);
        }

        public void Dispose()
        {
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
                var isLocked = noProfile && tab is not GuiTab.Equipment;
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
                        () => { appState.ActiveTab = capturedTab; appState.NeedsRedraw = true; });
                }
            }
        }

        // -----------------------------------------------------------------------
        // Status bar — registers clickable regions for interactive elements
        // -----------------------------------------------------------------------

        private void RenderStatusBar(GuiAppState appState, PlannerState plannerState, TimeProvider timeProvider)
        {
            var w = (float)_width;
            var sbh = StatusBarHeight;

            FillRect(0, 0, w, sbh, StatusBarBg);

            if (_fontPath is null)
            {
                return;
            }

            var now = timeProvider.GetLocalNow().ToOffset(plannerState.SiteTimeZone);
            var clockText = now.ToString("HH:mm:ss");

            // Profile name (left)
            var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
            DrawText(profileName.AsSpan(), _fontPath,
                SidebarWidth + 6f, 0, w * 0.35f, sbh,
                FontSize, StatusText, TextAlign.Near, TextAlign.Center);

            // Night window (center) — will become clickable for date navigation
            if (plannerState.AstroDark != default)
            {
                var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                var nightText = $"{dark:HH:mm} – {twilight:HH:mm}";
                DrawText(nightText.AsSpan(), _fontPath,
                    w * 0.35f, 0, w * 0.35f, sbh,
                    FontSize, StatusText, TextAlign.Center, TextAlign.Center);
            }

            // Clock (right)
            DrawText(clockText.AsSpan(), _fontPath,
                w * 0.7f, 0, w * 0.3f - 4f, sbh,
                FontSize, StatusText, TextAlign.Far, TextAlign.Center);

            // Status message overlay (replaces night window if set)
            if (appState.StatusMessage is { Length: > 0 } msg)
            {
                DrawText(msg.AsSpan(), _fontPath,
                    SidebarWidth + 6f, 0, w - SidebarWidth - 6f, sbh,
                    FontSize, StatusText, TextAlign.Center, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Content area dispatch
        // -----------------------------------------------------------------------

        private void RenderContent(
            GuiAppState appState,
            PlannerState plannerState,
            ViewerState viewerState,
            TimeProvider timeProvider,
            PixelRect contentRect)
        {
            switch (appState.ActiveTab)
            {
                case GuiTab.Planner:
                    _plannerTab.Render(plannerState, contentRect, DpiScale,
                        _fontPath ?? "monospace", timeProvider, appState.MouseScreenPosition);
                    break;

                case GuiTab.Equipment:
                    _equipmentTab.Render(appState, contentRect, DpiScale,
                        _fontPath ?? "monospace");
                    break;

                default:
                    RenderComingSoonPlaceholder(contentRect, appState.ActiveTab);
                    break;
            }
        }

        private void RenderComingSoonPlaceholder(PixelRect rect, GuiTab tab)
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

            string[] candidates = OperatingSystem.IsWindows()
                ? [@"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\cour.ttf"]
                : OperatingSystem.IsMacOS()
                    ? ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Monaco.dfont"]
                    : ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf"];

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _fontPath = path;
                    return;
                }
            }
        }
    }
}
