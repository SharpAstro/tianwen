using System.Text.Json.Serialization;

namespace TianWen.UI.Abstractions;

/// <summary>How loudly the OS should present a desktop alert. Our own enum: no platform type appears in this assembly,
/// and a platform sink maps these one to one.</summary>
public enum DesktopAlertUrgency
{
    Normal,
    Urgent
}

/// <summary>
/// One alert for the desktop, as <see cref="DesktopAlerts"/> decided it.
/// </summary>
/// <param name="Title">Where it happened: the rig's display name, or "This computer".</param>
/// <param name="Body">The note's message, with how many repeats were held back since the last alert of its tag.</param>
/// <param name="Tag">The note's kind (its severity when the kind is None): an OS replaces an earlier alert of the same tag and group.</param>
/// <param name="Group">The node the note came from; "this-computer" for this computer's own notes and its own node's.</param>
/// <param name="Urgency">Error is urgent, Warning normal.</param>
/// <param name="Link">What a click carries (<see cref="DesktopAlertLink"/>).</param>
public sealed record DesktopAlert(string Title, string Body, string Tag, string Group, DesktopAlertUrgency Urgency, string Link);

/// <summary>What a sink did with an alert.</summary>
/// <param name="Shown">Whether the OS took it.</param>
/// <param name="Reason">Why not, when it did not (the sink's own words); null when shown.</param>
public readonly record struct DesktopAlertResult(bool Shown, string? Reason = null)
{
    public static DesktopAlertResult Ok => new(true);

    public static DesktopAlertResult Unavailable(string reason) => new(false, reason);
}

/// <summary>
/// Where desktop alerts go: the OS's toast on Windows (the platform leg maps <see cref="DesktopAlert"/> one to one onto
/// AppShell's notification), a fake in tests, nothing in a host that has none. Called only from
/// <see cref="DesktopAlerts"/>'s own consumer, never on a socket or render thread.
/// </summary>
public interface IDesktopAlert
{
    /// <summary>Whether this OS shows alerts at all; when false no alert is built.</summary>
    bool IsAvailable { get; }

    /// <summary>Shows one alert.</summary>
    DesktopAlertResult Show(DesktopAlert alert);
}

/// <summary>Which notes become a desktop alert. A per-computer setting, kept under <c>Notifications/desktop-alerts.json</c>
/// (<see cref="DesktopAlertSettings"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DesktopAlertLevel>))]
public enum DesktopAlertLevel
{
    /// <summary>Never.</summary>
    Off,

    /// <summary>Errors, and the warnings on <see cref="DesktopAlerts.IsSafetyKind"/>'s list. The default.</summary>
    ErrorsAndSafety,

    /// <summary>Every warning and error.</summary>
    AllWarnings
}
