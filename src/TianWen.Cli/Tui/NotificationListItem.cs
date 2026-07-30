using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// Row in <see cref="TuiNotificationsTab"/>. Formats a single
    /// <see cref="NotificationEntry"/> as <c>HH:mm:ss  [SEV ]  message</c> with
    /// severity-coloured tag and dim timestamp.
    /// </summary>
    internal sealed class NotificationListItem(NotificationEntry entry, System.TimeSpan siteTimeZone) : IRowLayout
    {
        /// <summary>Leading space plus <c>HH:mm:ss</c>.</summary>
        private const int TimestampColumns = 9;

        /// <summary>Two-space gap plus <c>[SEV ]</c>.</summary>
        private const int TagColumns = 8;

        public Layout.Node BuildRow(in RowContext context)
        {
            var ts = entry.When.ToOffset(siteTimeZone).ToString("HH:mm:ss");
            var (tag, severity) = entry.Severity switch
            {
                NotificationSeverity.Error => ("ERR ", SgrColor.BrightRed),
                NotificationSeverity.Warning => ("WARN", SgrColor.BrightYellow),
                _ => ("INFO", SgrColor.BrightCyan),
            };

            // Cursor row: the whole row takes the blue selection background so the focused entry is
            // obvious. The severity tag keeps its own bright foreground ON that background, which is what
            // the old string version's comment claimed and could not actually do -- nesting one style
            // inside a styled line meant the inner run's closing reset wiped the selection background for
            // the remainder of the row. Each cell states its own pen here, so the claim is now true.
            var row = context.Selected ? TuiRowPalette.Selected : TuiRowPalette.Body;
            var timestamp = context.Selected ? row : TuiRowPalette.Dim;

            return Layout.Builder.HStack(
                timestamp.Cell($" {ts}", TimestampColumns),
                row.WithForeground(severity).Cell($"  [{tag}]", TagColumns),
                row.Text($"  {entry.Message}"))
                .RowH(1).Bg(row.Background);
        }
    }
}
