using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Notifications tab. Shows the feed of the view on show (<see cref="NotificationFeed"/>): this
    /// computer's <see cref="GuiAppState.Notifications"/>, or a rig's node's notes while the rig is on screen, as a
    /// newest-first scrollable list, with colour-coded severity and a timestamp.
    /// No per-tab state: scroll position lives on the tab instance (session-scoped).
    /// </summary>
    public class NotificationsTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Colours: muted, readable on the standard dark chrome background.
        // Properties, NOT static readonly fields. A field initialiser snapshots the palette at
        // type-init, so this feed would have gone on painting the old scheme through every theme
        // switch. RGBAColor32 is a struct, so reading through per access allocates nothing.
        private static RGBAColor32 ContentBg    => GuiTheme.Palette.ContentBg;
        private static RGBAColor32 HeaderBg     => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 HeaderText   => GuiTheme.Palette.HeaderText;
        private static RGBAColor32 RowAltBg     => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 BodyText     => GuiTheme.Palette.BodyText;
        private static RGBAColor32 DimText      => GuiTheme.Palette.DimText;
        private static RGBAColor32 EmptyText    => GuiTheme.Palette.DimText;
        // Shared with the home card's last-note line, so the same warning is the same colour on both.
        private static RGBAColor32 InfoStripe   => GuiTheme.Palette.Info;
        private static RGBAColor32 WarnStripe   => GuiTheme.Palette.Warn;
        private static RGBAColor32 ErrorStripe  => GuiTheme.Palette.Error;

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
            ViewContext view,
            RectF32 contentRect)
        {
            // Drops last frame's clickable regions, as every other tab's Render does. This was missing, so
            // the Clear button's region was re-registered on every frame for as long as the tab was open --
            // harmless to click (the duplicates are identical) but an unbounded list, and hit testing walks
            // all of them.
            BeginFrame();

            // DPI + font come from the inherited DpiScale / FontPath (host-set); local aliases keep the
            // px math + draw calls unchanged.
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var rowH = BaseRowHeight * dpiScale;
            var fontSize = BaseFontSize * dpiScale;

            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);

            // Header: one declared tree (heading | alert choice | Clear, or two rows where that does not fit). Its height is the
            // engine's measurement of it at this width, never a count of rows.
            var feed = NotificationFeed.Of(view, appState);
            var entries = feed.Entries;
            var ctx = MeasureContext();
            var header = BuildHeader(feed, appState, level => PostSignal(new SetDesktopAlertLevelSignal(level)), AlertSegmentWidth(ctx, dpiScale));
            var headerH = Layout.Engine.Measure(header, new Layout.Size<float>(contentRect.Width, contentRect.Height), ctx).Height;
            RenderLayout(header, new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH));

            // List area
            _listRect = new RectF32(
                contentRect.X,
                contentRect.Y + headerH,
                contentRect.Width,
                contentRect.Height - headerH);

            if (entries.IsEmpty)
            {
                DrawText(feed.EmptyText.AsSpan(), fontPath,
                    _listRect.X, _listRect.Y, _listRect.Width, _listRect.Height,
                    fontSize, EmptyText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Hand the controller this frame's geometry (viewport = list rect, one atom = one row). It owns
            // the offset + wheel accumulator (a trackpad delta no longer truncates to zero) + clamp;
            // VisibleRows() places each row with the smooth sub-row shift and reserves the scrollbar column.
            _scroll.SetExtent(_listRect, rowH, entries.Length, Scale);

            // A smooth-scrolled list shows the row at its edge part way; clip to the list so that row ends at the list's
            // edge and not at its own (it used to draw past the list, over whatever lies below it).
            PushClip(_listRect.X, _listRect.Y, _listRect.Width, _listRect.Height);
            foreach (var (i, rowRect) in _scroll.VisibleRows())
            {
                var entry = entries[i];

                // Timestamp (HH:mm:ss) in the single app-wide site timezone -- never the machine TZ.
                var tsText = entry.When.ToOffset(appState.SiteTimeZone).ToString("HH:mm:ss");

                var row = BuildRow(entry, tsText, alternate: (i & 1) == 1);
                // The severity mark is a keyed baked-icon Fill (SeverityMarks), painted here; DrawCoverageMask is
                // not a re-entry into the layout, so it is safe inside the drawFill callback.
                RenderLayout(row, rowRect, drawFill: (fill, rect) =>
                {
                    if (SeverityMarks.TryPlace(fill.Key, rect, out var mask, out var sq, out var ink))
                    {
                        DrawCoverageMask(mask, sq.X, sq.Y, sq.Width, ink);
                    }
                });
            }

            PopClip();

            // Interactive scrollbar at the right edge (hidden when the list fits).
            _scroll.DrawScrollBar(FillRect);
        }

        // One header row's height, design units. The controls on it fill what its vertical padding leaves, so they share one
        // height and one middle; a header that wraps (narrow) is two of these.
        private static float HeaderRowHeight => GuiTheme.Metrics.HeaderHeight;

        // Room a segment of the alert choice keeps around its word, design units.
        private const float AlertSegmentSlack = 8f;

        // The alert choice's font, design units: smaller than the heading so it reads as a control.
        private const float AlertChoiceFontSize = BaseFontSize * 0.85f;

        /// <summary>
        /// The width, in design units, of one segment of the alert choice: the widest of the three labels as the engine measures
        /// it, plus slack. A segmented control has no intrinsic width of its own, so it is measured here and never written as a
        /// number; an edited label or another face changes it. The pinned DIR.Lib's ButtonGroup has no width sample (only
        /// <c>Text</c> does), so the equal width is stated through <c>ButtonGroupStyle.SegmentWidth</c>.
        /// </summary>
        private float AlertSegmentWidth(PixelMeasureContext<TSurface> ctx, float dpiScale)
        {
            var widest = 0f;
            foreach (var level in Enum.GetValues<DesktopAlertLevel>())
            {
                var width = Layout.Engine.Measure(Layout.Builder.Text(LevelLabel(level), AlertChoiceFontSize),
                    new Layout.Size<float>(float.MaxValue, float.MaxValue), ctx).Width;
                widest = MathF.Max(widest, width / dpiScale);
            }
            return widest + AlertSegmentSlack;
        }

        /// <summary>The words of the three alert levels, as the choice shows them.</summary>
        internal static string LevelLabel(DesktopAlertLevel level) => level switch
        {
            DesktopAlertLevel.Off => "Off",
            DesktopAlertLevel.ErrorsAndSafety => "Errors and safety",
            _ => "All warnings"
        };

        /// <summary>
        /// What the alert choice says on hover: what the levels mean, and why Windows refused the last alert when it did. It names
        /// every kind <see cref="DesktopAlerts.IsSafetyKind"/> holds; a test fails when that list gains one these words do not name.
        /// </summary>
        internal static string AlertTooltip(DesktopAlerts alerts)
            => "Desktop alerts on this computer. Errors and safety: every error, a mount limit, and a run that was aborted, "
               + "stopped because no one was watching it, or ended on a fault. All warnings: every warning too."
               + (alerts.LastRefusal is { } why ? $" Windows did not show the last alert: {why}. Check that notifications are on for this app." : "");

        /// <summary>
        /// The ring round the alert choice, so its extent reads against the header whichever segment is chosen. A control's edge
        /// stands 3:1 off what it is drawn on (ui-changes.md) in every palette: <c>DimText</c> does on the header (5.3:1 day,
        /// 5.1:1 dark, 3.4:1 night, with no blue at night). DIR.Lib's <c>ControlEdge</c> default is tuned to the panel and is
        /// 2.7:1 on the day header and spends blue at night; the separators are under 2:1.
        /// </summary>
        internal static RGBAColor32 AlertChoiceEdge => DimText;

        /// <summary>
        /// How the alert choice shows its levels: the chosen segment is the accent, its word in the ink that measures best on it
        /// (the design language's segmented control), and the others a step off the header with the body text. The chosen
        /// segment was a mix of the accent into the header, which carried its word at 1.7:1 at night.
        /// </summary>
        /// <param name="segmentWidth">Design units each segment takes (see <c>AlertSegmentWidth</c>).</param>
        internal static Layout.ButtonGroupStyle AlertChoiceStyle(float segmentWidth)
        {
            var chosen = GuiTheme.Palette.Accent;
            var rest = GuiTheme.Mix(HeaderBg, BodyText, 0.14f);
            return new Layout.ButtonGroupStyle(chosen, rest, GuiTheme.InkOn(chosen), BodyText, GuiTheme.Hover(rest))
            {
                Gap = 1f,
                InsetFraction = 0.7f,
                SegmentWidth = segmentWidth,
            };
        }

        /// <summary>
        /// The header as one declared tree: [heading | spacer | alert choice | Clear], or, where that does not fit the room, the
        /// same pieces on two rows (<c>OrWhenNarrow</c>, decided by the engine's own measurement). The choice is this computer's
        /// setting, so it shows on a rig's view too; it is left out where the host has no alerts (no
        /// <see cref="GuiAppState.DesktopAlerts"/>, or none on this OS). Clear stays on this computer's own feed.
        /// </summary>
        /// <param name="segmentWidth">Design units each segment of the choice takes (see <c>AlertSegmentWidth</c>).</param>
        internal static Layout.Node BuildHeader(NotificationFeed feed, GuiAppState appState, Action<DesktopAlertLevel> onLevel, float segmentWidth)
        {
            static Layout.Node Row(List<Layout.Node> nodes)
                => Layout.Builder.HStack([.. nodes]).WithGap(8f).Pad(BasePadding, 3f).CrossCenter().WStar().HFixed(HeaderRowHeight).Bg(HeaderBg);

            Layout.Node Heading()
                => Layout.Builder.Text(feed.Header, BaseFontSize * 1.05f, HeaderText, TextAlign.Near, TextAlign.Center).HStar();

            Layout.Node? choice = null;
            if (appState.DesktopAlerts is { IsAvailable: true } alerts)
            {
                var style = AlertChoiceStyle(segmentWidth);
                ReadOnlySpan<Layout.ButtonGroupOption<DesktopAlertLevel>> options =
                [
                    new(DesktopAlertLevel.Off, LevelLabel(DesktopAlertLevel.Off)) { Hit = new HitResult.ButtonHit("DesktopAlertOff") },
                    new(DesktopAlertLevel.ErrorsAndSafety, LevelLabel(DesktopAlertLevel.ErrorsAndSafety)) { Hit = new HitResult.ButtonHit("DesktopAlertErrors") },
                    new(DesktopAlertLevel.AllWarnings, LevelLabel(DesktopAlertLevel.AllWarnings)) { Hit = new HitResult.ButtonHit("DesktopAlertAll") },
                ];
                // The ring is reserved inside the choice's box by the engine, so the box stays the row's height and the segments
                // sit within it.
                choice = Layout.Builder.ButtonGroup(options, alerts.Level, onLevel, style, AlertChoiceFontSize)
                    .Border(AlertChoiceEdge, GuiTheme.Metrics.ControlEdgeWidth, GuiTheme.Metrics.ControlRadius)
                    .WithTooltip(AlertTooltip(alerts)).HStar();
            }

            // Clear: this computer's own notes only, since a rig's are its node's.
            Layout.Node? clear = null;
            if (feed.IsLocal && !feed.Entries.IsEmpty)
            {
                var clearBg = new RGBAColor32(0x3a, 0x3a, 0x46, 0xff);
                clear = Layout.Builder.Text("Clear", BaseFontSize * 0.95f, BodyText, TextAlign.Center, TextAlign.Center)
                    .PadX(12f).HStar()
                    .Bg(clearBg).BgHover(GuiTheme.Hover(clearBg))
                    .Clickable(new HitResult.ButtonHit("NotificationsClear"), _ =>
                    {
                        appState.ClearNotifications();
                        appState.NeedsRedraw = true;
                    });
            }

            var oneRow = new List<Layout.Node> { Heading(), Layout.Builder.Spacer().WStar() };
            if (choice is not null)
            {
                oneRow.Add(choice);
            }
            if (clear is not null)
            {
                oneRow.Add(clear);
            }
            var full = Row(oneRow);
            if (choice is null)
            {
                return full;
            }

            // Narrow: the choice is wider than the room beside the heading and Clear, so it takes a row of its own, at the right.
            var top = new List<Layout.Node> { Heading(), Layout.Builder.Spacer().WStar() };
            if (clear is not null)
            {
                top.Add(clear);
            }
            var twoRows = Layout.Builder.VStack(Row(top), Row([Layout.Builder.Spacer().WStar(), choice])).WStar();
            return full.OrWhenNarrow(twoRows);
        }

        // Width of the severity-mark slot, in design units: the mark's own square. Reserved for Info rows too (an empty
        // spacer), so the timestamp column never moves with a row's severity.
        internal const float SeverityMarkWidth = BaseFontSize;

        /// <summary>
        /// One row tree: [severity stripe | gap | mark slot | gap | timestamp | message] with an alternating background.
        /// A Warning and an Error carry a mark beside the stripe (#2025), so they are told from an accented control by
        /// form as well as colour; an Info row reserves the slot, empty. Was a FillRect bg + FillRect stripe + two
        /// hand-positioned DrawTexts.
        /// </summary>
        internal static Layout.Node BuildRow(NotificationEntry entry, string timestamp, bool alternate)
        {
            var stripeColor = entry.Severity switch
            {
                NotificationSeverity.Error   => ErrorStripe,
                NotificationSeverity.Warning => WarnStripe,
                _                            => InfoStripe
            };

            var row = Layout.Builder.HStack(
                    Layout.Builder.Box(3f, 0f, stripeColor).WFixed(3f).HStar(),
                    Layout.Builder.Spacer().WFixed(7f).HStar(),
                    SeverityMarks.Slot(entry.Severity, SeverityMarkWidth),
                    Layout.Builder.Spacer().WFixed(6f).HStar(),
                    Layout.Builder.Text(timestamp, BaseFontSize * 0.9f, DimText).WFixed(70f).HStar(),
                    Layout.Builder.Spacer().WFixed(8f).HStar(),
                    Layout.Builder.Text(entry.Message, BaseFontSize, BodyText).WStar().HStar(),
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar());
            return alternate ? row.Bg(RowAltBg) : row;
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
