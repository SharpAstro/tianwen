using SharpAstro.AppShell;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// The Windows sink for <see cref="DesktopAlerts"/> (#2507): maps a <see cref="DesktopAlert"/> one to one onto AppShell's
/// <see cref="DesktopNotification"/>. Urgent is the Windows reminder that may pass Do Not Disturb, kept for Error alone
/// (the policy only builds Urgent for an Error). Off Windows AppShell answers Unsupported, so nothing is available.
/// </summary>
internal sealed class AppShellDesktopAlert(string scheme) : IDesktopAlert
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public DesktopAlertResult Show(DesktopAlert alert)
    {
        var options = new DesktopNotificationOptions
        {
            Tag = alert.Tag,
            Group = alert.Group,
            Urgency = alert.Urgency is DesktopAlertUrgency.Urgent ? ToastUrgency.Urgent : ToastUrgency.Normal,
            LaunchPayload = alert.Link,
            Scheme = scheme
        };
        var result = DesktopNotification.Show(alert.Title, alert.Body, options);
        return result.IsSuccess
            ? DesktopAlertResult.Ok
            : DesktopAlertResult.Unavailable($"{result.Status}: {result.Reason}");
    }
}
