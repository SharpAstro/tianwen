using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Notifications tab. Shows <see cref="GuiAppState.Notifications"/>
    /// as a newest-first scrollable list, with colour-coded severity and a timestamp.
    /// No per-tab state — scroll position lives on the tab instance (session-scoped).
    /// </summary>
    public class NotificationsTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Colours — muted, readable on the standard dark chrome background.
        private static readonly RGBAColor32 ContentBg    = GuiTheme.Palette.ContentBg;
        private static readonly RGBAColor32 HeaderBg     = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText   = GuiTheme.Palette.HeaderText;
        private static readonly RGBAColor32 RowAltBg     = new(0x1a, 0x1a, 0x24, 0xff);
        private static readonly RGBAColor32 BodyText     = new(0xdd, 0xdd, 0xdd, 0xff);
        private static readonly RGBAColor32 DimText      = new(0x80, 0x80, 0x90, 0xff);
        private static readonly RGBAColor32 EmptyText    = new(0x55, 0x55, 0x66, 0xff);
        private static readonly RGBAColor32 InfoStripe   = new(0x55, 0x88, 0xcc, 0xff);
        private static readonly RGBAColor32 WarnStripe   = new(0xcc, 0x99, 0x33, 0xff);
        private static readonly RGBAColor32 ErrorStripe  = new(0xcc, 0x44, 0x44, 0xff);

        private const float BaseRowHeight = 32f;
        private const float BaseFontSize  = 13f;
        private const float BasePadding   = 10f;

        // Atom-model scroll controller (DIR.Lib): one notification row is one atom. Smooth (pixel-precise)
        // sub-row scrolling with an interactive (grabbable-thumb) scrollbar + body drag-to-scroll; owns
        // the offset + wheel accumulator + clamp, and VisibleRows() owns row placement.
        private readonly ListScrollController _scroll = new ListScrollController { Mode = ScrollBarMode.Interactive };

        private RectF32 _listRect;

        public void Render(
            GuiAppState appState,
            RectF32 contentRect)
        {
            // DPI + font come from the inherited DpiScale / FontPath (host-set); local aliases keep the
            // px math + draw calls unchanged.
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var rowH = BaseRowHeight * dpiScale;
            var fontSize = BaseFontSize * dpiScale;
            var pad = BasePadding * dpiScale;
            var headerH = 28f * dpiScale;

            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);

            // Header
            RenderLayout(Layout.Builder.Spacer().Bg(HeaderBg), new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH));
            var entries = appState.Notifications;
            var headerLabel = $"Notifications ({entries.Length})";
            DrawText(headerLabel.AsSpan(), fontPath,
                contentRect.X + pad, contentRect.Y, contentRect.Width - pad * 2f, headerH,
                fontSize * 1.05f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Clear button (right edge of header)
            if (entries.Length > 0)
            {
                var btnW = 70f * dpiScale;
                var btnH = headerH - 6f * dpiScale;
                var btnX = contentRect.X + contentRect.Width - btnW - pad;
                var btnY = contentRect.Y + 3f * dpiScale;

                // Clear button as one draw==hit Layout.Node leaf instead of separate FillRect +
                // DrawText + RegisterClickable (which can drift). Font is a raw design unit --
                // PaintLayout re-applies dpiScale.
                var clearBtn = Layout.Builder.Text("Clear", BaseFontSize * 0.95f, BodyText, TextAlign.Center, TextAlign.Center)
                    .Stretch()
                    .Bg(new RGBAColor32(0x3a, 0x3a, 0x46, 0xff))
                    .Clickable(new HitResult.ButtonHit("NotificationsClear"), _ =>
                    {
                        appState.ClearNotifications();
                        appState.NeedsRedraw = true;
                    });
                RenderLayout(clearBtn, new RectF32(btnX, btnY, btnW, btnH));
            }

            // List area
            _listRect = new RectF32(
                contentRect.X,
                contentRect.Y + headerH,
                contentRect.Width,
                contentRect.Height - headerH);

            if (entries.IsEmpty)
            {
                DrawText("No notifications yet.".AsSpan(), fontPath,
                    _listRect.X, _listRect.Y, _listRect.Width, _listRect.Height,
                    fontSize, EmptyText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Hand the controller this frame's geometry (viewport = list rect, one atom = one row). It owns
            // the offset + wheel accumulator (a trackpad delta no longer truncates to zero) + clamp;
            // VisibleRows() places each row with the smooth sub-row shift and reserves the scrollbar column.
            _scroll.SetExtent(_listRect, rowH, entries.Length, dpiScale);

            foreach (var (i, rowRect) in _scroll.VisibleRows())
            {
                var entry = entries[i];

                // One row tree: [severity stripe | gap | timestamp | message] with an alternating
                // background. Was a FillRect bg + FillRect stripe + two hand-positioned DrawTexts.
                var stripeColor = entry.Severity switch
                {
                    NotificationSeverity.Error   => ErrorStripe,
                    NotificationSeverity.Warning => WarnStripe,
                    _                            => InfoStripe
                };
                // Timestamp (HH:mm:ss) in the single app-wide site timezone -- never the machine TZ.
                var tsText = entry.When.ToOffset(appState.SiteTimeZone).ToString("HH:mm:ss");

                var row = Layout.Builder.HStack(
                        Layout.Builder.Box(3f, 0f, stripeColor).WFixed(3f).HStar(),
                        Layout.Builder.Spacer().WFixed(7f).HStar(),
                        Layout.Builder.Text(tsText, BaseFontSize * 0.9f, DimText).WFixed(70f).HStar(),
                        Layout.Builder.Spacer().WFixed(8f).HStar(),
                        Layout.Builder.Text(entry.Message, BaseFontSize, BodyText).WStar().HStar(),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar());
                if ((i & 1) == 1) row = row.Bg(RowAltBg);
                RenderLayout(row, rowRect);
            }

            // Interactive scrollbar at the right edge (hidden when the list fits).
            _scroll.DrawScrollBar(FillRect);
        }

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(_, var mx, var my, _) when _listRect.Contains(mx, my) => _scroll.HandleInput(evt),
            // Unclaimed presses (the host dispatches registered clickables first) arm body drag-to-scroll
            // or grab the thumb; move/up continue the gesture (false when idle, so they fall through).
            InputEvent.MouseDown or InputEvent.MouseMove or InputEvent.MouseUp => HandleListMouse(evt),
            _ => false
        };

        private bool HandleListMouse(InputEvent evt)
        {
            if (!_scroll.HandleInput(evt))
            {
                return false;
            }
            _scroll.TakeAtomTap(); // rows have no tap action -- discard so a tap never lingers
            return true;
        }
    }
}
