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

    public string FormatRow(int width, ColorMode colorMode)
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
        return
            $"{DimStyle.Apply(colorMode)} {ts}{reset}"
            + $"  {sevStyle.Apply(colorMode)}[{tag}]{reset}"
            + $"  {BodyStyle.Apply(colorMode)}{msg.PadRight(msgBudget)}{reset}";
    }
}
