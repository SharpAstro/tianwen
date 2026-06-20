using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Colours + design-unit metrics for the Session configuration form, bundled so
    /// <see cref="SessionConfigLayout.Build"/> reads them from one place (mirrors
    /// <see cref="EquipmentPanelStyle"/>). Every size is a <i>design unit</i> -- the layout engine scales
    /// the whole tree by dpiScale in a single pass, so nothing here is pre-multiplied (this is the
    /// discipline that keeps the form free of the double-DPI bug the earlier whole-panel attempt hit).
    /// </summary>
    public readonly record struct SessionConfigStyle(
        FormRowLayout.StepperStyle Stepper,
        RGBAColor32 HeaderBg, RGBAColor32 HeaderText,
        RGBAColor32 BodyText, RGBAColor32 DimText,
        RGBAColor32 RowBg, RGBAColor32 RowAltBg, RGBAColor32 SelectedRowBg,
        RGBAColor32 ToggleOnBg, RGBAColor32 ToggleOffBg, RGBAColor32 CycleBg,
        RGBAColor32 DisabledBg,
        float FontSize, float HeaderHeight, float ItemHeight,
        float LabelWidth, float Padding,
        float ToggleButtonWidth, float CycleButtonWidth);

    /// <summary>
    /// Builds the entire <see cref="SessionConfiguration"/> form as ONE surface-agnostic
    /// <see cref="Layout.Node"/> tree -- the "full single-panel tree" (Phase 4 of the layout-engine plan).
    /// Earlier tabs adopted the engine per row (each stepper / toggle its own <c>RenderLayout</c>, the
    /// vertical flow still an imperative <c>cursor += itemH</c> walk); this builder owns the <i>whole</i>
    /// vertical flow -- section headers, alternating row backgrounds, the selected-row highlight, the
    /// per-field label + control -- as a single declarative <see cref="Layout.Node.Stack"/>. The host
    /// arranges it once into a tall bounds offset by the scroll position and clips to the panel
    /// (scroll-via-root-bounds-offset), so there is no per-row cursor math left.
    ///
    /// The config form is the cleanest candidate for this because it is flat and data-driven
    /// (<see cref="SessionConfigGroups.Groups"/> -> groups -> typed fields). Every node is a real
    /// Text / Box / control subtree -- NOT a <see cref="Layout.Content.Fill"/> leaf that re-dispatches to an
    /// imperative renderer, which is the facade the EquipmentTab whole-tree attempt degraded into and was
    /// reverted for. Interactive widgets that genuinely resist the static engine (the right-hand camera
    /// settings + observation list) stay imperative; this is only the left config form.
    /// </summary>
    public static class SessionConfigLayout
    {
        private static readonly Action<InputModifier> NoOp = _ => { };

        /// <summary>
        /// The text shown in a stepper's centre value cell: formatted value + optional unit. Public so the
        /// host measures the shared value-column width against the exact strings this builder renders (one
        /// source of truth -- a drift here would mis-size the column).
        /// </summary>
        public static string FormatStepperDisplay(ConfigFieldDescriptor field, SessionConfiguration config)
        {
            var value = field.FormatValue(config);
            return field.Unit.Length > 0 ? $"{value} {field.Unit}" : value;
        }

        /// <summary>
        /// Builds the form tree. <paramref name="valueWidth"/> is the shared (design-unit) stepper value
        /// column width measured by the host so every stepper aligns. The three callbacks are optional so
        /// the builder stays unit-testable; the host wires them to mutate <c>SessionConfiguration</c> +
        /// request a redraw. When <paramref name="running"/> is true the controls keep their hit surface
        /// but drop their handlers (dimmed / inert), matching the rest of the tabs.
        /// </summary>
        public static Layout.Node Build(
            ImmutableArray<ConfigGroup> groups,
            SessionConfiguration config,
            int selectedFieldIndex,
            bool running,
            float valueWidth,
            in SessionConfigStyle style,
            Func<int, Action<InputModifier>?>? onSelectField = null,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onDecrement = null,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onIncrement = null)
        {
            var btnW = style.Stepper.ButtonDesignW;
            var children = ImmutableArray.CreateBuilder<Layout.Node>();
            var globalIdx = 0;

            for (var gi = 0; gi < groups.Length; gi++)
            {
                var group = groups[gi];
                children.Add(Header(group.Name, style));

                for (var fi = 0; fi < group.Fields.Length; fi++)
                {
                    var field = group.Fields[fi];
                    var idx = globalIdx++;
                    var isSelected = idx == selectedFieldIndex;
                    // Alternating background resets per group (matches the old fi % 2 walk).
                    var rowBg = isSelected ? style.SelectedRowBg : (fi % 2 == 0 ? style.RowBg : style.RowAltBg);
                    children.Add(FieldRow(field, idx, rowBg, config, valueWidth, btnW, running, style,
                        onSelectField, onDecrement, onIncrement));
                }

                // Small gap between groups (mirrors the old cursor += padding * 0.5 step).
                children.Add(GroupGap(style.Padding * 0.5f));
            }

            return Layout.Builder.VStack(children.ToImmutable().AsSpan())
                .WStar();
        }

        // Section header: inset label over a header-coloured band. A Text leaf cannot carry padding (the
        // painter draws it at the node's full rect), so the left inset is a fixed pad cell in a Stack.
        private static Layout.Node Header(string name, in SessionConfigStyle style) =>
            Layout.Builder.HStack(
                Pad(style.Padding),
                Layout.Builder.Text(name, style.FontSize, style.HeaderText).Stretch())
            .RowH(style.HeaderHeight).Bg(style.HeaderBg);

        private static Layout.Node GroupGap(float height) =>
            Layout.Builder.Spacer().RowH(height);

        private static Layout.Node Pad(float width) =>
            Layout.Builder.Spacer().ColW(width);

        // One field row: [ pad | label ]  [ control ]  over a full-width row background.
        private static Layout.Node FieldRow(
            ConfigFieldDescriptor field, int idx, RGBAColor32 rowBg,
            SessionConfiguration config, float valueWidth, float btnW, bool running, in SessionConfigStyle style,
            Func<int, Action<InputModifier>?>? onSelectField,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onDecrement,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onIncrement)
        {
            // pad + label = the select-clickable region. Height = Star so the whole-row-height hit matches
            // the old RegisterClickable(rect.X, cursor, labelW + padding, itemH) exactly -- the control
            // buttons register later (children win), so dec/inc still get their own clicks.
            var selectCell = Layout.Builder.HStack(
                Pad(style.Padding),
                Layout.Builder.Text(field.Label, style.FontSize, style.BodyText).WFixed(style.LabelWidth).HStar())
            .HStar()
            .Clickable(new HitResult.ListItemHit("ConfigField", idx), onSelectField?.Invoke(idx));

            var control = Control(field, config, valueWidth, btnW, running, style, onDecrement, onIncrement);

            return Layout.Builder.HStack(selectCell, control)
                .RowH(style.ItemHeight).Bg(rowBg);
        }

        private static Layout.Node Control(
            ConfigFieldDescriptor field, SessionConfiguration config, float valueWidth, float btnW, bool running,
            in SessionConfigStyle style,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onDecrement,
            Func<ConfigFieldDescriptor, Action<InputModifier>?>? onIncrement)
        {
            switch (field.Kind)
            {
                case ConfigFieldKind.BoolToggle:
                {
                    var valueStr = field.FormatValue(config);
                    var isOn = valueStr == "ON";
                    return Layout.Builder.Text(valueStr, style.FontSize, running ? style.DimText : style.BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(style.ToggleButtonWidth).HStar()
                        .Bg(running ? style.DisabledBg : (isOn ? style.ToggleOnBg : style.ToggleOffBg))
                        .Clickable(new HitResult.ButtonHit($"Toggle:{field.Label}"), running ? null : onIncrement?.Invoke(field));
                }

                case ConfigFieldKind.EnumCycle:
                {
                    var valueStr = field.FormatValue(config);
                    return Layout.Builder.Text($"{valueStr} \u25B6", style.FontSize * 0.9f, running ? style.DimText : style.BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(style.CycleButtonWidth).HStar()
                        .Bg(running ? style.DisabledBg : style.CycleBg)
                        .Clickable(new HitResult.ButtonHit($"Cycle:{field.Label}"), running ? null : onIncrement?.Invoke(field));
                }

                default:
                {
                    // [-] value [+] via the shared builder; the value cell is Star, so a Fixed total width of
                    // (2 * button + value) makes it equal the shared measured column and every stepper aligns.
                    var display = FormatStepperDisplay(field, config);
                    var ctrl = FormRowLayout.StepperControl(style.Stepper,
                        "\u2212", $"Dec:{field.Label}", onDecrement?.Invoke(field) ?? NoOp,
                        "+", $"Inc:{field.Label}", onIncrement?.Invoke(field) ?? NoOp,
                        display, style.FontSize, running ? style.DimText : style.BodyText, enabled: !running);

                    return ctrl with { Width = Layout.Sizing.Fixed(btnW * 2f + valueWidth), Height = Layout.Sizing.Star() };
                }
            }
        }
    }
}
