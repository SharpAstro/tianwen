using Console.Lib;
using DIR.Lib;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// The foreground/background pair a row cell states for itself, plus the two leaf shapes every TUI
    /// row is built from.
    /// <para>
    /// <b>A cell must state its own colour.</b> Rows used to be formatted strings that emitted a
    /// foreground and relied on whatever background a previous write happened to leave in effect. A real
    /// terminal forgives that; the diffing cell buffer cannot, because it stores a colour per cell -- so
    /// an inheriting row recorded cells with no colour at all and painted as a gap. Stating the pair
    /// together is what makes that unrepresentable: there is no way to name a foreground here without
    /// also naming what it sits on.
    /// </para>
    /// <para>
    /// It also removes the mechanism <see cref="EquipmentFieldItem"/> needed to survive nesting one style
    /// inside another: a nested segment's closing reset wiped the enclosing row's background mid-line, so
    /// each segment had to re-apply the outer style on exit and the row had to count the escape bytes it
    /// had emitted (<c>VisibleOverhead</c>) to know how far to pad. A tree has no escape bytes in its
    /// content and no enclosing pen to restore.
    /// </para>
    /// </summary>
    /// <param name="Foreground">Text colour.</param>
    /// <param name="Background">Cell colour behind the text.</param>
    internal readonly record struct RowPen(RGBAColor32 Foreground, RGBAColor32 Background)
    {
        public RowPen(SgrColor foreground, SgrColor background)
            : this(foreground.ToRgba(), background.ToRgba()) { }

        /// <summary>
        /// The pen a caller already holds as a <see cref="VtStyle"/>. An unstated component stays
        /// unstated (alpha zero, which resolves to the terminal's own default rather than to black).
        /// </summary>
        public RowPen(VtStyle style) : this(style.Foreground, style.Background) { }

        /// <summary>
        /// This pen with a different foreground. For a run that varies only its text colour against the
        /// row it sits on -- the On/Off segments of a slot row, a severity tag -- so the background
        /// travels with it instead of being restated (and mis-stated) per segment.
        /// </summary>
        public RowPen WithForeground(SgrColor foreground) => this with { Foreground = foreground.ToRgba() };

        /// <inheritdoc cref="WithForeground(SgrColor)"/>
        public RowPen WithForeground(RGBAColor32 foreground) => this with { Foreground = foreground };

        /// <summary>This pen as the style a text or button row takes.</summary>
        public VtStyle Style => new VtStyle(Foreground, Background);

        /// <summary>A width-filling, one-row-high text leaf in this pen: a whole row, or a Star column of one.</summary>
        public Layout.Node Text(string text, TextAlign hAlign = TextAlign.Near)
            => Layout.Builder.Text(text, TuiRowPalette.CellFontSize, Foreground, hAlign).RowH(1).Bg(Background);

        /// <summary>
        /// A fixed-width text cell in this pen -- a column that must line up across rows, or a button.
        /// Width is in CELLS (see <see cref="TuiRowPalette.CellFontSize"/>).
        /// </summary>
        public Layout.Node Cell(string text, int columns, TextAlign hAlign = TextAlign.Near)
            => Layout.Builder.Text(text, TuiRowPalette.CellFontSize, Foreground, hAlign)
                .WFixed(columns).HStar().Bg(Background);

        /// <summary>An empty run of this pen's background, <paramref name="columns"/> wide.</summary>
        public Layout.Node Gap(int columns) => Layout.Builder.Spacer().WFixed(columns).HStar().Bg(Background);

        /// <summary>Fills the rest of the row with this pen's background.</summary>
        public Layout.Node Rest() => Layout.Builder.Spacer().Stretch().Bg(Background);
    }

    /// <summary>
    /// The pens the TUI's list rows share. Six row types independently spelled out
    /// "selected is bright white on blue"; a tree states a colour per LEAF rather than per row, so the
    /// number of places that pair could be typed differently went up, not down.
    /// <para>
    /// <b>It is the TUI's ONE statement of what a colour means</b> (#2126), as <c>GuiTheme</c> is the GUI's: a row asks a
    /// role here and never names an <see cref="SgrColor"/> itself (<c>TuiPaletteSourceTests</c> fails on one). The rule is
    /// the GUI's: the accent, the logo's orange, is the colour of an ACTION, and green is a state that is good. A terminal
    /// with 24-bit colour shows the orange itself; one with sixteen takes the nearest of them, the dark yellow (SGR 33),
    /// which <c>VtStyle</c> picks, so no pen here needs a second value.
    /// </para>
    /// </summary>
    internal static class TuiRowPalette
    {
        /// <summary>
        /// One design unit, which under <see cref="CellMeasureContext.CellAuthored"/> (the
        /// <see cref="ScrollableList{T}"/> default) is exactly one character cell. A row authored for a
        /// GPU surface instead counts in pixels and would use a real font size -- see
        /// <see cref="TuiHomeTab"/>, the one tree shared across surface kinds.
        /// </summary>
        public const float CellFontSize = 1f;

        /// <summary>The cursor row.</summary>
        public static RowPen Selected { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.Blue);

        /// <summary>An ordinary row.</summary>
        public static RowPen Body { get; } = new RowPen(SgrColor.White, SgrColor.Black);

        /// <summary>Secondary text -- a timestamp, an inactive value, a hint.</summary>
        public static RowPen Dim { get; } = new RowPen(SgrColor.BrightBlack, SgrColor.Black);

        /// <summary>A section separator row.</summary>
        public static RowPen SectionHeader { get; } = new RowPen(SgrColor.BrightBlue, SgrColor.Black);

        /// <summary>A stepper's <c>[-]</c> / <c>[+]</c> affordance.</summary>
        public static RowPen Button { get; } = new RowPen(SgrColor.White, SgrColor.BrightBlack);

        /// <summary>What every pen here sits on: the TUI states black, never the terminal's own background.</summary>
        public static RGBAColor32 Ground { get; } = SgrColor.Black.ToRgba();

        /// <summary>The accent: <c>GuiTheme</c>'s Dark accent, the logo's orange.</summary>
        public static RGBAColor32 AccentColour { get; } = TianWen.UI.Abstractions.GuiTheme.DarkPalette.Accent;

        /// <summary>An action named in text, or the thing the run is on now: the accent on the ground.</summary>
        public static RowPen Accent { get; } = new RowPen(AccentColour, Ground);

        /// <summary>A button that starts something, or an action row under the cursor: the accent as a fill, under the ink measured for it.</summary>
        public static RowPen ActionFill { get; } = new RowPen(TianWen.UI.Abstractions.GuiTheme.InkOn(AccentColour), AccentColour);

        /// <summary>A button that ends or cancels something.</summary>
        public static RowPen DangerFill { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.Red);

        /// <summary>A state that is good: connected, aligned, on target, the active profile, a pinned target, progress made.</summary>
        public static RowPen Good { get; } = new RowPen(SgrColor.BrightGreen, SgrColor.Black);

        /// <summary><see cref="Good"/> as a badge.</summary>
        public static RowPen GoodFill { get; } = new RowPen(SgrColor.Black, SgrColor.BrightGreen);

        /// <summary>A badge for work that is going well and not finished.</summary>
        public static RowPen GoodFillQuiet { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.Green);

        /// <summary>Informational: a heading, an info tag.</summary>
        public static RowPen Info { get; } = new RowPen(SgrColor.BrightCyan, SgrColor.Black);

        /// <summary><see cref="Info"/> as a badge: a step under way.</summary>
        public static RowPen InfoFill { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.Cyan);

        /// <summary>A warning, a marginal reading, or something in transit.</summary>
        public static RowPen Warn { get; } = new RowPen(SgrColor.BrightYellow, SgrColor.Black);

        /// <summary><see cref="Warn"/> as a badge: the mount is moving.</summary>
        public static RowPen WarnFill { get; } = new RowPen(SgrColor.Black, SgrColor.BrightYellow);

        /// <summary>An error, a bad reading, a thing that is off or missing.</summary>
        public static RowPen Error { get; } = new RowPen(SgrColor.BrightRed, SgrColor.Black);

        /// <summary><see cref="Error"/> as a badge.</summary>
        public static RowPen ErrorFill { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.Red);

        /// <summary>The cursor on a row that is not the pinned one: grey, where <see cref="Selected"/> is blue.</summary>
        public static RowPen SelectedQuiet { get; } = new RowPen(SgrColor.BrightWhite, SgrColor.BrightBlack);

        /// <summary><see cref="Selected"/> when the cursor is on the row, <see cref="Body"/> otherwise.</summary>
        public static RowPen ForRow(bool selected) => selected ? Selected : Body;

        /// <summary>
        /// Floor for a label column that otherwise takes half the row. Three row shapes independently
        /// wrote <c>Math.Max(18, width / 2)</c>; as a Star minimum it is stated once and the halving is
        /// the engine's.
        /// </summary>
        public const float LabelMinColumns = 18f;

        /// <summary>
        /// A section separator's text. Shared because the equipment and session lists drew the identical
        /// rule-name-rule and the equipment one also has to know its exact length, so two spellings of it
        /// were two chances to mis-measure the row.
        /// </summary>
        public static string SectionHeaderText(string sectionName) => $"── {sectionName} ──";
    }
}
