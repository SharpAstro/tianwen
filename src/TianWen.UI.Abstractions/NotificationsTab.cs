using System;
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
            var pad = BasePadding * dpiScale;
            var narrow = IsNarrow(contentRect.Width, dpiScale);
            var headerH = HeaderHeight(appState, narrow) * dpiScale;

            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);

            // Header: one declared row (heading | alert choice | Clear), or two when narrow, so the engine places them.
            var feed = NotificationFeed.Of(view, appState);
            var entries = feed.Entries;
            RenderLayout(BuildHeader(feed, appState, level => PostSignal(new SetDesktopAlertLevelSignal(level)), narrow),
                new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH));

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

        // Header control height, design units: the header's own 28 less its 3-unit margins, one height for the choice and Clear.
        private const float HeaderControlHeight = 22f;

        // The alert choice's width, design units: 3 segments of 118 (the longest label, 103, plus its inset) and 2 gaps of 1.
        private const float AlertChoiceWidth = 340f;

        // One header row, design units, and the width below which the choice moves to a second row: the pad, a long heading
        // ("Notifications (99)", about 130), the gap, the choice, the gap, Clear (70) and the pad come to about 580.
        private const float HeaderRowHeight = 28f;
        private const float NarrowHeaderBelow = 600f;

        /// <summary>The words of the three alert levels, as the choice shows them.</summary>
        internal static string LevelLabel(DesktopAlertLevel level) => level switch
        {
            DesktopAlertLevel.Off => "Off",
            DesktopAlertLevel.ErrorsAndSafety => "Errors and safety",
            _ => "All warnings"
        };

        /// <summary>What the alert choice says on hover: what the levels mean, and why Windows refused the last alert when it did.</summary>
        internal static string AlertTooltip(DesktopAlerts alerts)
            => "Desktop alerts on this computer. Errors and safety: errors, an aborted run and a mount limit. All warnings: every warning too."
               + (alerts.LastRefusal is { } why ? $" Windows did not show the last alert: {why}. Check that notifications are on for this app." : "");

        /// <summary>
        /// The header as one row: [heading | alert choice | Clear]. The choice is this computer's setting, so it shows on a rig's
        /// view too; it is left out where the host has no alerts (no <see cref="GuiAppState.DesktopAlerts"/>, or none on this OS).
        /// Clear stays on this computer's own feed. Every size is a design unit, the engine measures the rest.
        /// </summary>
        internal static Layout.Node BuildHeader(NotificationFeed feed, GuiAppState appState, Action<DesktopAlertLevel> onLevel, bool narrow)
        {
            var heading = Layout.Builder.Text(feed.Header, BaseFontSize * 1.05f, HeaderText, TextAlign.Near, TextAlign.Center).WStar().HStar();
            Layout.Node? choiceNode = null;
            Layout.Node? clearNode = null;

            if (appState.DesktopAlerts is { IsAvailable: true } alerts)
            {
                var segmentActive = GuiTheme.Mix(HeaderBg, GuiTheme.Palette.Accent, 0.55f);
                var segmentInactive = GuiTheme.Palette.Separator;
                var style = new Layout.ButtonGroupStyle(segmentActive, segmentInactive, BodyText, BodyText, GuiTheme.Hover(segmentInactive))
                {
                    Gap = 1f,
                    InsetFraction = 0.7f,
                };
                ReadOnlySpan<Layout.ButtonGroupOption<DesktopAlertLevel>> options =
                [
                    new(DesktopAlertLevel.Off, LevelLabel(DesktopAlertLevel.Off)) { Hit = new HitResult.ButtonHit("DesktopAlertOff") },
                    new(DesktopAlertLevel.ErrorsAndSafety, LevelLabel(DesktopAlertLevel.ErrorsAndSafety)) { Hit = new HitResult.ButtonHit("DesktopAlertErrors") },
                    new(DesktopAlertLevel.AllWarnings, LevelLabel(DesktopAlertLevel.AllWarnings)) { Hit = new HitResult.ButtonHit("DesktopAlertAll") },
                ];
                var choice = Layout.Builder.ButtonGroup(options, alerts.Level, onLevel, style, BaseFontSize * 0.85f)
                    .WithTooltip(AlertTooltip(alerts));
                // A segmented control has no intrinsic width, so it states its widest state: three equal segments, each wide
                // enough for the longest label ("Errors and safety", about 103 units at this font) and its inset.
                choiceNode = Centred(choice.WFixed(AlertChoiceWidth));
            }

            // Clear: this computer's own notes only, since a rig's are its node's.
            if (feed.IsLocal && !feed.Entries.IsEmpty)
            {
                var clearBg = new RGBAColor32(0x3a, 0x3a, 0x46, 0xff);
                var clear = Layout.Builder.Text("Clear", BaseFontSize * 0.95f, BodyText, TextAlign.Center, TextAlign.Center)
                    .Bg(clearBg).BgHover(GuiTheme.Hover(clearBg))
                    .Clickable(new HitResult.ButtonHit("NotificationsClear"), _ =>
                    {
                        appState.ClearNotifications();
                        appState.NeedsRedraw = true;
                    });
                clearNode = Centred(clear.WFixed(70f));
            }

            static Layout.Node Row(params Layout.Node?[] nodes)
            {
                var items = new System.Collections.Generic.List<Layout.Node> { Layout.Builder.Spacer().WFixed(BasePadding).HStar() };
                foreach (var node in nodes)
                {
                    if (node is not null)
                    {
                        items.Add(node);
                    }
                }
                items.Add(Layout.Builder.Spacer().WFixed(BasePadding).HStar());
                return Layout.Builder.HStack(items.ToArray());
            }

            static Layout.Node Gap() => Layout.Builder.Spacer().WFixed(8f).HStar();

            if (!narrow || choiceNode is null)
            {
                return Row(heading, choiceNode is null ? null : Gap(), choiceNode, clearNode is null ? null : Gap(), clearNode).Bg(HeaderBg);
            }

            // Narrow: the choice is wider than the room beside the heading and Clear, so it takes a row of its own, at the right.
            return Layout.Builder.VStack(
                Row(heading, clearNode is null ? null : Gap(), clearNode).WStar().HFixed(HeaderRowHeight),
                Row(Layout.Builder.Spacer().WStar().HStar(), choiceNode).WStar().HFixed(HeaderRowHeight)).Bg(HeaderBg);
        }

        /// <summary>The header's height in design units: one row, or two when <paramref name="narrow"/> and the choice is drawn.</summary>
        internal static float HeaderHeight(GuiAppState appState, bool narrow)
            => narrow && appState.DesktopAlerts is { IsAvailable: true } ? HeaderRowHeight * 2f : HeaderRowHeight;

        /// <summary>Whether a content width (device pixels) is too narrow for the choice beside the heading and Clear.</summary>
        internal static bool IsNarrow(float contentWidth, float dpiScale) => contentWidth < NarrowHeaderBelow * dpiScale;

        // One height and one middle for the controls on the header row.
        private static Layout.Node Centred(Layout.Node control)
            => Layout.Builder.VStack(
                Layout.Builder.Spacer().HStar(),
                control.HFixed(HeaderControlHeight),
                Layout.Builder.Spacer().HStar()).HStar();

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
