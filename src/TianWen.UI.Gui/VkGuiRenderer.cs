using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Top-level GPU renderer for the N.I.N.A.-style integrated GUI.
    /// Draws the left sidebar, top status bar, and delegates to the active tab renderer.
    /// </summary>
    public sealed class VkGuiRenderer : IDisposable
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

        /// <summary>Exposes the planner tab for external hit testing and scroll control.</summary>
        public VkPlannerTab PlannerTab => _plannerTab;

        /// <summary>Exposes the equipment tab for external hit testing and input routing.</summary>
        public VkEquipmentTab EquipmentTab => _equipmentTab;

        // Base layout constants (at 1x scale)
        private const float BaseSidebarWidth = 52f;
        private const float BaseStatusBarHeight = 28f;
        private const float BaseFontSize = 14f;

        // Scaled accessors
        private float SidebarWidth => BaseSidebarWidth * DpiScale;
        private float StatusBarHeight => BaseStatusBarHeight * DpiScale;
        private float FontSize => BaseFontSize * DpiScale;

        // Sidebar tab definitions
        // DejaVu Sans Unicode symbols (color emoji needs DIR.Lib CBDT/COLR investigation)
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

        // Status bar colors
        private static readonly RGBAColor32 StatusBarBg   = new RGBAColor32(0x22, 0x22, 0x28, 0xff);
        private static readonly RGBAColor32 StatusText    = new RGBAColor32(0xaa, 0xaa, 0xaa, 0xff);

        // Content area placeholder
        private static readonly RGBAColor32 ContentBg     = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x55, 0x55, 0x66, 0xff);

        public VkGuiRenderer(VkRenderer renderer, uint width, uint height)
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

            _equipmentTab.FrameCount++;

            var (contentLeft, contentTop, contentWidth, contentHeight) = GetContentArea();

            // Render the active tab content first (it may fill the full renderer surface)
            RenderContent(appState, plannerState, viewerState, timeProvider,
                contentLeft, contentTop, contentWidth, contentHeight);

            // Paint sidebar and status bar on top
            RenderSidebar(appState);
            RenderStatusBar(appState, plannerState, timeProvider);
        }

        /// <summary>
        /// Returns (Left, Top, Width, Height) of the content area in pixels.
        /// </summary>
        public (float Left, float Top, float Width, float Height) GetContentArea()
        {
            var left = SidebarWidth;
            var top = StatusBarHeight;
            var width = (float)_width - SidebarWidth;
            var height = (float)_height - StatusBarHeight;
            return (left, top, width, height);
        }

        /// <summary>
        /// Hit-tests the sidebar for a tab click.
        /// Returns the tab if (x, y) is within a tab button, otherwise null.
        /// </summary>
        public GuiTab? HitTestSidebar(float x, float y, GuiAppState appState)
        {
            if (x < 0 || x >= SidebarWidth)
            {
                return null;
            }

            var buttonSize = SidebarWidth; // square buttons
            var startY = StatusBarHeight;

            for (var i = 0; i < SidebarTabs.Length; i++)
            {
                var btnY = startY + i * buttonSize;
                if (y >= btnY && y < btnY + buttonSize)
                {
                    var tab = SidebarTabs[i].Tab;
                    // When no profile, only Equipment tab is clickable
                    if (appState.ActiveProfile is null && tab is not GuiTab.Equipment)
                    {
                        return null;
                    }
                    return tab;
                }
            }

            return null;
        }

        public void Dispose()
        {
            // VkRenderer is owned by the caller; do not dispose here.
        }

        // -----------------------------------------------------------------------
        // Sidebar
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

                var textColor = isLocked ? new RGBAColor32(0x44, 0x44, 0x50, 0xff)
                              : isActive ? ActiveIcon
                                         : IconColor;
                var iconFont = _emojiFontPath ?? _fontPath;
                if (iconFont is not null)
                {
                    var ix = (int)0;
                    var iy = (int)btnY;
                    var iw = (int)sw;
                    var ih = (int)buttonSize;
                    _renderer.DrawText(icon.AsSpan(), iconFont, FontSize * 1.3f,
                        textColor, new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy)),
                        TextAlign.Center, TextAlign.Center);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Status bar
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
            DrawText(profileName.AsSpan(), SidebarWidth + 6f, 0, w * 0.35f, sbh, FontSize, StatusText,
                TextAlign.Near, TextAlign.Center);

            // Night window (center)
            if (plannerState.AstroDark != default)
            {
                var dark = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
                var twilight = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
                var nightText = $"{dark:HH:mm} – {twilight:HH:mm}";
                DrawText(nightText.AsSpan(), w * 0.35f, 0, w * 0.35f, sbh, FontSize, StatusText,
                    TextAlign.Center, TextAlign.Center);
            }

            // Clock (right)
            DrawText(clockText.AsSpan(), w * 0.7f, 0, w * 0.3f - 4f, sbh, FontSize, StatusText,
                TextAlign.Far, TextAlign.Center);

            // Status message overlay (replaces night window if set)
            if (appState.StatusMessage is { Length: > 0 } msg)
            {
                DrawText(msg.AsSpan(), SidebarWidth + 6f, 0, w - SidebarWidth - 6f, sbh, FontSize, StatusText,
                    TextAlign.Center, TextAlign.Center);
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
            float left, float top, float width, float height)
        {
            switch (appState.ActiveTab)
            {
                case GuiTab.Planner:
                    _plannerTab.Render(plannerState, left, top, width, height, DpiScale,
                        _fontPath ?? "monospace", timeProvider);
                    break;

                case GuiTab.Equipment:
                    _equipmentTab.Render(appState, left, top, width, height, DpiScale,
                        _fontPath ?? "monospace");
                    break;

                default:
                    RenderComingSoonPlaceholder(left, top, width, height, appState.ActiveTab);
                    break;
            }
        }

        private void RenderComingSoonPlaceholder(float left, float top, float width, float height, GuiTab tab)
        {
            FillRect(left, top, width, height, ContentBg);

            var msg = $"{tab} — Coming soon";
            DrawText(msg.AsSpan(), left, top, width, height, FontSize * 1.5f, PlaceholderText,
                TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Drawing helpers
        // -----------------------------------------------------------------------

        private void FillRect(float x, float y, float w, float h, RGBAColor32 color)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }

            var ix = (int)x;
            var iy = (int)y;
            var iw = (int)w;
            var ih = (int)h;
            _renderer.FillRectangle(
                new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy)),
                color);
        }

        private void DrawText(
            ReadOnlySpan<char> text,
            float x, float y, float w, float h,
            float fontSize,
            RGBAColor32 color,
            TextAlign horizAlign = TextAlign.Near,
            TextAlign vertAlign = TextAlign.Near)
        {
            if (_fontPath is null || text.IsEmpty)
            {
                return;
            }

            var ix = (int)x;
            var iy = (int)y;
            var iw = (int)w;
            var ih = Math.Max(1, (int)h);
            var layout = new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy));
            _renderer.DrawText(text, _fontPath, fontSize, color, layout, horizAlign, vertAlign);
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
