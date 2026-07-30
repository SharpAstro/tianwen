using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// A row in the session config scrollable list. Either a group header or a config field.
    /// </summary>
    internal sealed class SessionFieldItem : IRowLayout
    {
        /// <summary>Group header (non-editable separator row).</summary>
        public string? GroupName { get; init; }

        /// <summary>Field descriptor (null for group headers and OTA fields).</summary>
        public ConfigFieldDescriptor? Field { get; init; }

        /// <summary>Flat index among editable fields (for selection tracking). -1 for headers.</summary>
        public int FieldIndex { get; init; } = -1;

        /// <summary>Whether this field is currently selected.</summary>
        public bool IsSelected { get; init; }

        /// <summary>Current formatted value (precomputed from SessionConfiguration).</summary>
        public string FormattedValue { get; init; } = "";

        /// <summary>Label for OTA fields (when Field is null but this is still editable).</summary>
        public string? OtaLabel { get; init; }

        /// <summary>Increment callback for OTA fields.</summary>
        public Action? Increment { get; init; }

        /// <summary>Decrement callback for OTA fields.</summary>
        public Action? Decrement { get; init; }

        /// <summary>
        /// Selection is the row's own fact -- it mirrors <c>TuiSessionState.SelectedFieldIndex</c>, which
        /// the keyboard moves independently of the list cursor -- so this reads <see cref="IsSelected"/>
        /// and ignores <paramref name="context"/>.
        /// </summary>
        public Layout.Node BuildRow(in RowContext context)
        {
            if (GroupName is not null)
            {
                return TuiRowPalette.SectionHeader.Text(TuiRowPalette.SectionHeaderText(GroupName));
            }

            if (Field is null && OtaLabel is null)
            {
                return TuiRowPalette.Body.Rest();
            }

            var label = OtaLabel ?? Field!.Label;
            var unit = Field?.Unit is { Length: > 0 } u ? $" {u}" : "";
            var control = Field?.Kind switch
            {
                ConfigFieldKind.BoolToggle => $"  [{FormattedValue}]",
                ConfigFieldKind.EnumCycle => $"  [{FormattedValue}]",
                null => $"  [←] {FormattedValue} [→]",  // OTA field
                _ => $"  [←] {FormattedValue}{unit} [→]",
            };

            var pen = TuiRowPalette.ForRow(IsSelected);

            // The label column was `Math.Max(18, width / 2)`, which is what two equal Stars with an
            // 18-column floor on the label mean -- and a min-clamped Star holds its floor when the row is
            // too narrow to halve, exactly as the Max did. The row no longer sees `width` at all.
            return Layout.Builder.HStack(
                pen.Gap(2),
                pen.Text(label).WStar(1f, TuiRowPalette.LabelMinColumns),
                pen.Text(control).WStar())
                .RowH(1).Bg(pen.Background);
        }
    }
}
