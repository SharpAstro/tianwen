using System;
using System.Collections.Immutable;
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
        /// Builds a control-only stepper <c>[dec | value | inc]</c> as one <see cref="LayoutNode"/>.
        /// The buttons are <c>Fixed</c> at <see cref="StepperStyle.ButtonDesignW"/> design units (the
        /// engine scales by dpiScale); the value cell is <c>Star</c> so it fills the remaining width --
        /// callers size the bounds rect = buttonW + valueW + buttonW so the value column equals the
        /// shared measured width and every stepper stays aligned. Font sizes are raw design units
        /// (PaintLayout re-applies dpiScale). A disabled control keeps its hit regions but drops the
        /// click handlers (dimmed), so the layout/hit-test surface is unchanged.
        /// </summary>
        public static LayoutNode StepperControl(
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

            LayoutNode Btn(string glyph, string hit, Action<InputModifier> onClick) =>
                new LayoutNode.Leaf(new LayoutContent.Text(glyph, btnFont) { Color = btnText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
                {
                    Width = Sizing.Fixed(btnW),
                    Height = Sizing.Star(),
                    Background = btnBg,
                    Hit = new HitResult.ButtonHit(hit),
                    OnClick = canClick ? onClick : null,
                };

            var value = new LayoutNode.Leaf(
                new LayoutContent.Text(valueText, valueFontSize) { Color = valueColor, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
            {
                Width = Sizing.Star(),
                Height = Sizing.Star(),
            };

            return new LayoutNode.Stack([Btn(decGlyph, decHit, onDec), value, Btn(incGlyph, incHit, onInc)], LayoutAxis.Horizontal);
        }

        /// <summary>
        /// Builds a clickable toggle-header row: background + hit + label text.
        /// Used for collapsible sections (Filter table, Cooler Control, Mount Status, Device Settings, Advanced sub-section).
        /// </summary>
        public static LayoutNode ToggleHeaderRow(
            string label, float rowH,
            RGBAColor32 bg, RGBAColor32 textColor, float fontSize,
            HitResult hit, Action<InputModifier>? onClick)
        {
            return new LayoutNode.Leaf(new LayoutContent.Text(label, fontSize) { Color = textColor, HAlign = TextAlign.Near, VAlign = TextAlign.Center })
            {
                Width = Sizing.Star(),
                Height = Sizing.Fixed(rowH),
                Background = bg,
                Hit = hit,
                OnClick = onClick,
            };
        }

        /// <summary>
        /// Builds a horizontal labeled-input row: [label fixed-labelW | Fill *].
        /// The Fill is the escape hatch for the caller's RenderTextInput call (one per RenderLayout).
        /// </summary>
        public static LayoutNode LabeledInputRow(
            string label, float labelW, float rowH, float padding, float fontSize,
            RGBAColor32 textColor, RGBAColor32? bg = null)
        {
            var labelLeaf = new LayoutNode.Leaf(new LayoutContent.Text(label, fontSize) { Color = textColor, HAlign = TextAlign.Near, VAlign = TextAlign.Center })
            {
                Width = Sizing.Fixed(labelW),
                Height = Sizing.Star(),
            };
            var fillLeaf = new LayoutNode.Leaf(new LayoutContent.Fill())
            {
                Width = Sizing.Star(),
                Height = Sizing.Star(),
            };
            return new LayoutNode.Stack([labelLeaf, fillLeaf], LayoutAxis.Horizontal)
            {
                Width = Sizing.Star(),
                Height = Sizing.Fixed(rowH),
                Background = bg,
            };
        }

        /// <summary>
        /// Builds a full stepper row including its own label: [label * | [-] valueText [+]].
        /// Disabled dec/inc = no Hit on the respective button leaf. (The control-only variant, where the
        /// label is drawn by the caller, is <see cref="StepperControl"/>.)
        /// </summary>
        public static LayoutNode StepperRow(
            string label, string valueText, float rowH, float padding,
            float fontSize, float stepBtnW,
            RGBAColor32 rowBg, RGBAColor32 btnBg, RGBAColor32 labelColor, RGBAColor32 valueColor, RGBAColor32 bodyText,
            string decHitKey, Action<InputModifier>? onDec,
            string incHitKey, Action<InputModifier>? onInc,
            bool decEnabled = true, bool incEnabled = true)
        {
            var labelLeaf = new LayoutNode.Leaf(new LayoutContent.Text(label, fontSize) { Color = labelColor, HAlign = TextAlign.Near, VAlign = TextAlign.Center })
            {
                Width = Sizing.Star(),
                Height = Sizing.Star(),
            };
            var decLeaf = new LayoutNode.Leaf(new LayoutContent.Text("-", fontSize) { Color = bodyText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
            {
                Width = Sizing.Fixed(stepBtnW),
                Height = Sizing.Star(),
                Background = decEnabled ? btnBg : (RGBAColor32?)null,
                Hit = decEnabled ? new HitResult.ButtonHit(decHitKey) : null,
                OnClick = decEnabled ? onDec : null,
            };
            var valueLeaf = new LayoutNode.Leaf(new LayoutContent.Text(valueText, fontSize) { Color = valueColor, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
            {
                Width = Sizing.Fixed(stepBtnW * 2f),
                Height = Sizing.Star(),
            };
            var incLeaf = new LayoutNode.Leaf(new LayoutContent.Text("+", fontSize) { Color = bodyText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
            {
                Width = Sizing.Fixed(stepBtnW),
                Height = Sizing.Star(),
                Background = incEnabled ? btnBg : (RGBAColor32?)null,
                Hit = incEnabled ? new HitResult.ButtonHit(incHitKey) : null,
                OnClick = incEnabled ? onInc : null,
            };
            return new LayoutNode.Stack([labelLeaf, decLeaf, valueLeaf, incLeaf], LayoutAxis.Horizontal)
            {
                Width = Sizing.Star(),
                Height = Sizing.Fixed(rowH),
                Background = rowBg,
            };
        }
    }
}
