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

        /// <summary>Scroll offset in pixels (top of list).</summary>
        public int ScrollOffset { get; set; }

        private RectF32 _listRect;
        private float _totalContentHeight;

        public void Render(
            GuiAppState appState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath)
        {
            var rowH = BaseRowHeight * dpiScale;
            var fontSize = BaseFontSize * dpiScale;
            var pad = BasePadding * dpiScale;
            var headerH = 28f * dpiScale;

            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Header
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, headerH, HeaderBg);
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

                // Clear button as one draw==hit LayoutNode leaf instead of separate FillRect +
                // DrawText + RegisterClickable (which can drift). Font is a raw design unit --
                // PaintLayout re-applies dpiScale.
                var clearBtn = new LayoutNode.Leaf(
                    new LayoutContent.Text("Clear", BaseFontSize * 0.95f) { Color = BodyText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
                {
                    Width = Sizing.Star(),
                    Height = Sizing.Star(),
                    Background = new RGBAColor32(0x3a, 0x3a, 0x46, 0xff),
                    Hit = new HitResult.ButtonHit("NotificationsClear"),
                    OnClick = _ =>
                    {
                        appState.ClearNotifications();
                        appState.NeedsRedraw = true;
                    },
                };
                RenderLayout(clearBtn, new RectF32(btnX, btnY, btnW, btnH), fontPath, dpiScale);
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
                _totalContentHeight = 0;
                return;
            }

            _totalContentHeight = entries.Length * rowH;
            var maxScroll = Math.Max(0, (int)(_totalContentHeight - _listRect.Height));
            ScrollOffset = Math.Clamp(ScrollOffset, 0, maxScroll);

            // Render visible rows only.
            var firstVisible = Math.Max(0, (int)(ScrollOffset / rowH));
            var lastVisible = Math.Min(entries.Length - 1,
                (int)Math.Ceiling((ScrollOffset + _listRect.Height) / rowH));

            for (var i = firstVisible; i <= lastVisible; i++)
            {
                var entry = entries[i];
                var rowY = _listRect.Y + i * rowH - ScrollOffset;
                if (rowY + rowH < _listRect.Y || rowY > _listRect.Y + _listRect.Height) continue;

                // Alternating row background
                if ((i & 1) == 1)
                {
                    FillRect(_listRect.X, rowY, _listRect.Width, rowH, RowAltBg);
                }

                // Severity stripe on the left
                var stripeColor = entry.Severity switch
                {
                    NotificationSeverity.Error   => ErrorStripe,
                    NotificationSeverity.Warning => WarnStripe,
                    _                            => InfoStripe
                };
                FillRect(_listRect.X, rowY, 3f * dpiScale, rowH, stripeColor);

                // Timestamp (HH:mm:ss) in the single app-wide site timezone -- never the
                // machine TZ (.ToLocalTime would show UTC on a UTC-set machine).
                var tsText = entry.When.ToOffset(appState.SiteTimeZone).ToString("HH:mm:ss");
                var tsX = _listRect.X + 10f * dpiScale;
                var tsW = 70f * dpiScale;
                DrawText(tsText.AsSpan(), fontPath,
                    tsX, rowY, tsW, rowH, fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                // Message — fills the remaining width. Long messages wrap on the
                // fly would require line splitting; for now we render in one line
                // (info panel status is always one line anyway) and the row widens
                // horizontally via the renderer's own glyph clipping at edge.
                var msgX = tsX + tsW + 8f * dpiScale;
                var msgW = _listRect.X + _listRect.Width - msgX - pad;
                DrawText(entry.Message.AsSpan(), fontPath,
                    msgX, rowY, msgW, rowH, fontSize, BodyText, TextAlign.Near, TextAlign.Center);
            }

            // Scrollbar on the right edge, if content exceeds viewport.
            if (_totalContentHeight > _listRect.Height)
            {
                var trackW = 4f * dpiScale;
                var trackX = _listRect.X + _listRect.Width - trackW - 2f * dpiScale;
                FillRect(trackX, _listRect.Y, trackW, _listRect.Height, new RGBAColor32(0x22, 0x22, 0x2c, 0xff));
                var thumbH = Math.Max(20f * dpiScale, _listRect.Height * (_listRect.Height / _totalContentHeight));
                var thumbY = _listRect.Y + (ScrollOffset / _totalContentHeight) * _listRect.Height;
                FillRect(trackX, thumbY, trackW, thumbH, new RGBAColor32(0x55, 0x55, 0x66, 0xff));
            }
        }

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, var mx, var my, _) when _listRect.Contains(mx, my)
                => HandleScroll(scrollY),
            _ => false
        };

        private bool HandleScroll(float scrollY)
        {
            var delta = (int)(scrollY * BaseRowHeight);
            ScrollOffset = Math.Clamp(ScrollOffset - delta, 0, int.MaxValue);
            return true;
        }
    }
}
