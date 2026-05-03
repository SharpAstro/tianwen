using Console.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// Row in <see cref="TuiNotificationsTab"/>. Formats a single
/// <see cref="NotificationEntry"/> as <c>HH:mm:ss  [SEV ]  message</c> with
/// severity-coloured tag and dim timestamp.
/// </summary>
internal sealed class NotificationListItem(NotificationEntry entry) : IRowFormatter
{
    private static readonly VtStyle DimStyle = new(SgrColor.BrightBlack, SgrColor.Black);
    private static readonly VtStyle BodyStyle = new(SgrColor.White, SgrColor.Black);
    private static readonly VtStyle InfoStyle = new(SgrColor.BrightCyan, SgrColor.Black);
    private static readonly VtStyle WarnStyle = new(SgrColor.BrightYellow, SgrColor.Black);
    private static readonly VtStyle ErrorStyle = new(SgrColor.BrightRed, SgrColor.Black);

    public string FormatRow(int width, ColorMode colorMode) => FormatRow(width, colorMode, isSelected: false);

    public string FormatRow(int width, ColorMode colorMode, bool isSelected)
    {
        var ts = entry.When.ToLocalTime().ToString("HH:mm:ss");
        var (tag, sevStyle) = entry.Severity switch
        {
            NotificationSeverity.Error => ("ERR ", ErrorStyle),
            NotificationSeverity.Warning => ("WARN", WarnStyle),
            _ => ("INFO", InfoStyle),
        };

        // Prefix: " HH:mm:ss  [SEV ]  " = 1+8+2+6+2 = 19 chars
        const int prefixLen = 19;
        var msgBudget = System.Math.Max(0, width - prefixLen);
        var msg = entry.Message.Length <= msgBudget
            ? entry.Message
            : msgBudget > 1 ? entry.Message[..(msgBudget - 1)] + '\u2026' : entry.Message[..msgBudget];

        var reset = VtStyle.Reset;
        if (isSelected)
        {
            // Cursor row: paint the whole row on a blue background so the user can see
            // which entry is focused. The severity tag keeps its bright fg so the
            // INFO/WARN/ERR colour-coding is still legible against the highlight.
            var selStyle = new VtStyle(SgrColor.BrightWhite, SgrColor.Blue);
            var line = $" {ts}  [{tag}]  {msg}";
            return $"{selStyle.Apply(colorMode)}{line.PadRight(width)}{reset}";
        }

        return
            $"{DimStyle.Apply(colorMode)} {ts}{reset}"
            + $"  {sevStyle.Apply(colorMode)}[{tag}]{reset}"
            + $"  {BodyStyle.Apply(colorMode)}{msg.PadRight(msgBudget)}{reset}";
    }
}
