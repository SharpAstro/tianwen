using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Surface-neutral, app-neutral builders for the common interactive panel rows shared across tabs
    /// (Equipment, Session, and the rest). These were originally split between
    /// <see cref="EquipmentPanelLayout"/> (which kept generic row builders) and a private
    /// <c>StepperControl</c> in the Session tab; consolidating them here gives every tab one source of
    /// truth, so the GPU panel and a future TUI panel build the same trees. Equipment-panel structure
    /// that is genuinely equipment-typed (the per-OTA <see cref="EquipmentPanelLayout.Build"/>,
    /// <c>SlotRow</c>, header/properties/filter-table) stays in <see cref="EquipmentPanelLayout"/>.
    /// <para>
    /// Built with the <c>Layout.Builder</c> DSL: factories (<c>Text</c>/<c>HStack</c>/<c>Spacer</c>) plus
    /// fluent modifiers (<c>.WFixed()</c>/<c>.RowH()</c>/<c>.Bg()</c>/<c>.Clickable()</c>) emit the same
    /// <c>Layout.Node</c> records a hand-written initializer would, with the chrome read as a chain.
    /// </para>
    /// </summary>
    public static class FormRowLayout
    {
        /// <summary>
        /// Button styling for a <see cref="StepperControl"/>: the normal / disabled colours plus the
        /// button glyph font size and button width, all in <i>design units</i> (the engine scales them
        /// by dpiScale). Bundled so callers pass one value instead of six positional args.
        /// </summary>
        public readonly record struct StepperStyle(
            RGBAColor32 ButtonBg, RGBAColor32 ButtonText,
            RGBAColor32 DisabledBg, RGBAColor32 DisabledText,
            float ButtonFontSize, float ButtonDesignW);

        /// <summary>
        /// Builds a control-only stepper <c>[dec | value | inc]</c> as one <see cref="Layout.Node"/>.
        /// The buttons are <c>Fixed</c> at <see cref="StepperStyle.ButtonDesignW"/> design units (the
        /// engine scales by dpiScale); the value cell is <c>Star</c> so it fills the remaining width --
        /// callers size the bounds rect = buttonW + valueW + buttonW so the value column equals the
        /// shared measured width and every stepper stays aligned. Font sizes are raw design units
        /// (PaintLayout re-applies dpiScale). A disabled control keeps its hit regions but drops the
        /// click handlers (dimmed), so the layout/hit-test surface is unchanged.
        /// </summary>
        public static Layout.Node StepperControl(
            in StepperStyle style,
            string decGlyph, string decHit, Action<InputModifier> onDec,
            string incGlyph, string incHit, Action<InputModifier> onInc,
            string valueText, float valueFontSize, RGBAColor32 valueColor, bool enabled)
        {
            var btnBg = enabled ? style.ButtonBg : style.DisabledBg;
            var btnText = enabled ? style.ButtonText : style.DisabledText;
            var btnFont = style.ButtonFontSize;
            var btnW = style.ButtonDesignW;
            var canClick = enabled;

            Layout.Node Btn(string glyph, string hit, Action<InputModifier> onClick) =>
                Layout.Builder.Text(glyph, btnFont, btnText, TextAlign.Center, TextAlign.Center)
                    .WFixed(btnW).HStar().Bg(btnBg)
                    .Clickable(new HitResult.ButtonHit(hit), canClick ? onClick : null);

            var value = Layout.Builder.Text(valueText, valueFontSize, valueColor, TextAlign.Center, TextAlign.Center).Stretch();

            return Layout.Builder.HStack(Btn(decGlyph, decHit, onDec), value, Btn(incGlyph, incHit, onInc));
        }

        /// <summary>
        /// One "pill" button for a confirmation / segmented strip: a full-cell click target whose coloured
        /// background is inset vertically to <paramref name="fillFraction"/> of the cell height (the classic
        /// inset-pill look), with the label centred over the whole cell. The engine binds both
        /// <see cref="Layout.Node.Background"/> and <see cref="Layout.Node.Hit"/> to a node's <i>whole</i> rect, so
        /// an inset background and a full-height hit cannot share one leaf -- this is a vertical Stack
        /// <c>[spacer | pill | spacer]</c> with the Hit on the outer Stack (full height) and the Background on
        /// the inner pill (the inset band). The spacers are <c>Star</c>-sized, so the inset stays a true
        /// fraction of the row at any DPI with no pixel/design-unit conversion. A null <paramref name="hit"/>
        /// (or <paramref name="onClick"/>) leaves the cell inert -- clicks fall through to whatever is behind.
        /// </summary>
        public static Layout.Node InsetPillButton(
            string label, float fontSize, RGBAColor32 bg, RGBAColor32 textColor,
            HitResult? hit, Action<InputModifier>? onClick,
            float widthWeight = 1f, float fillFraction = 0.7f)
        {
            var insetWeight = (1f - fillFraction) * 0.5f;
            Layout.Node Spacer() => Layout.Builder.Spacer().HStar(insetWeight);
            var pill = Layout.Builder.Text(label, fontSize, textColor, TextAlign.Center, TextAlign.Center)
                .WStar().HStar(fillFraction).Bg(bg);
            return Layout.Builder.VStack(Spacer(), pill, Spacer())
                .WStar(widthWeight).HStar()
                .Clickable(hit, onClick);
        }

        /// <summary>
        /// Builds a clickable toggle-header row: background + hit + label text.
        /// Used for collapsible sections (Filter table, Cooler Control, Mount Status, Device Settings, Advanced sub-section).
        /// </summary>
        public static Layout.Node ToggleHeaderRow(
            string label, float rowH,
            RGBAColor32 bg, RGBAColor32 textColor, float fontSize,
            HitResult hit, Action<InputModifier>? onClick)
        {
            return Layout.Builder.Text(label, fontSize, textColor)
                .RowH(rowH).Bg(bg).Clickable(hit, onClick);
        }

        /// <summary>
        /// Builds a horizontal labeled-input row: [label fixed-labelW | Fill *].
        /// The Fill is the escape hatch for the caller's RenderTextInput call (one per RenderLayout).
        /// </summary>
        public static Layout.Node LabeledInputRow(
            string label, float labelW, float rowH, float padding, float fontSize,
            RGBAColor32 textColor, RGBAColor32? bg = null)
        {
            var labelLeaf = Layout.Builder.Text(label, fontSize, textColor).WFixed(labelW).HStar();
            var fillLeaf = Layout.Builder.Fill().Stretch();
            var row = Layout.Builder.HStack(labelLeaf, fillLeaf).WStar().HFixed(rowH);
            return bg is { } b ? row.Bg(b) : row;
        }

        /// <summary>
        /// Builds a full stepper row including its own label: [label * | [-] valueText [+]].
        /// Disabled dec/inc = no Hit on the respective button leaf. (The control-only variant, where the
        /// label is drawn by the caller, is <see cref="StepperControl"/>.)
        /// </summary>
        public static Layout.Node StepperRow(
            string label, string valueText, float rowH, float padding,
            float fontSize, float stepBtnW,
            RGBAColor32 rowBg, RGBAColor32 btnBg, RGBAColor32 labelColor, RGBAColor32 valueColor, RGBAColor32 bodyText,
            string decHitKey, Action<InputModifier>? onDec,
            string incHitKey, Action<InputModifier>? onInc,
            bool decEnabled = true, bool incEnabled = true)
        {
            var labelLeaf = Layout.Builder.Text(label, fontSize, labelColor).Stretch();

            // A disabled step button keeps its cell but drops the background fill + hit + handler (dimmed).
            Layout.Node StepBtn(string glyph, string hitKey, Action<InputModifier>? onClick, bool stepEnabled)
            {
                var btn = Layout.Builder.Text(glyph, fontSize, bodyText, TextAlign.Center, TextAlign.Center).WFixed(stepBtnW).HStar();
                return stepEnabled
                    ? btn.Bg(btnBg).Clickable(new HitResult.ButtonHit(hitKey), onClick)
                    : btn;
            }

            var decLeaf = StepBtn("-", decHitKey, onDec, decEnabled);
            var valueLeaf = Layout.Builder.Text(valueText, fontSize, valueColor, TextAlign.Center, TextAlign.Center).WFixed(stepBtnW * 2f).HStar();
            var incLeaf = StepBtn("+", incHitKey, onInc, incEnabled);

            return Layout.Builder.HStack(labelLeaf, decLeaf, valueLeaf, incLeaf).RowH(rowH).Bg(rowBg);
        }
    }
}
