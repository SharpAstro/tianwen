using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

public enum GuiTab
{
    Planner,
    Session,
    Equipment,
    SkyMap,
    LiveSession,
    Guider,
    Notifications
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A single notification recorded from a <see cref="GuiAppState.StatusMessage"/> write
/// (or explicit <see cref="GuiAppState.AppendNotification"/> call). Newest first in
/// <see cref="GuiAppState.Notifications"/>.
/// </summary>
public readonly record struct NotificationEntry(
    DateTimeOffset When,
    NotificationSeverity Severity,
    string Message);

public class GuiAppState
{
    /// <summary>Device URI registry for resolving camera URIs to device instances.</summary>
    public IDeviceHub? DeviceHub { get; init; }

    public GuiTab ActiveTab { get; set; } = GuiTab.Planner;
    public Profile? ActiveProfile { get; set; }
    public bool NeedsRedraw { get; set; } = true;
    public (float X, float Y) MouseScreenPosition { get; set; }
    public InputModifier LastClickModifiers { get; set; }

    /// <summary>Transient status message shown in the chrome status bar.
    /// Does NOT auto-record to the notifications history — callers that want a
    /// history entry should use <see cref="AppendNotification"/> instead (which
    /// sets this too, timestamped via the caller's time provider).</summary>
    public string? StatusMessage { get; set; }

    /// <summary>The currently focused text input across all tabs. Single source of truth.</summary>
    public TextInputState? ActiveTextInput { get; set; }

    /// <summary>
    /// True when the user has requested exit but background tasks (session Finalise) are still running.
    /// The render loop continues during this phase, showing shutdown progress.
    /// </summary>
    public bool ShuttingDown { get; set; }

    /// <summary>
    /// True when the user pressed X/Escape to quit while a session was running.
    /// After abort is confirmed, shutdown proceeds automatically.
    /// </summary>
    public bool QuitRequested { get; set; }

    /// <summary>
    /// Set by CheckNeedsRedraw when ShuttingDown and no pending tasks remain.
    /// The loop checks this to call Stop() (since CheckNeedsRedraw can't reference loop).
    /// </summary>
    public bool ShutdownComplete { get; set; }

    /// <summary>
    /// Two-click-confirm state for a Sun slew from the sky-map info panel.
    /// The first Goto click on <see cref="CatalogIndex.Sol"/> stashes the index
    /// here with an expiry timestamp; a second click within the window performs
    /// the slew. The button label flips to "CONFIRM" while this is populated.
    /// </summary>
    public CatalogIndex? PendingSunSlewIndex { get; set; }

    /// <summary>Wall-clock expiry of <see cref="PendingSunSlewIndex"/>.</summary>
    public DateTimeOffset? PendingSunSlewExpiresAt { get; set; }

    /// <summary>
    /// Bounded ring of recent notifications. Newest entry is at index 0. Capped at
    /// <see cref="MaxNotifications"/>; older entries are dropped silently.
    /// Writes are atomic via <c>ImmutableArray</c> reference swap (safe to read from
    /// the render thread while background tasks append).
    /// </summary>
    public ImmutableArray<NotificationEntry> Notifications { get; private set; } = [];

    /// <summary>Unread-count since the user last opened the Notifications tab.
    /// Reset to 0 when that tab becomes active.</summary>
    public int UnreadNotificationCount { get; set; }

    /// <summary>Cap on <see cref="Notifications"/>. ~Two screens' worth of entries.</summary>
    public const int MaxNotifications = 500;

    /// <summary>
    /// Record a notification at the given timestamp and severity without changing
    /// <see cref="StatusMessage"/>. Caller supplies the timestamp from their own
    /// <c>ITimeProvider</c> — this class does not hold a time source.
    /// </summary>
    public void RecordNotification(DateTimeOffset when, NotificationSeverity severity, string message)
    {
        var entry = new NotificationEntry(when, severity, message);
        var current = Notifications;
        var next = current.Insert(0, entry);
        if (next.Length > MaxNotifications)
        {
            next = next.RemoveRange(MaxNotifications, next.Length - MaxNotifications);
        }
        Notifications = next;
        if (ActiveTab != GuiTab.Notifications)
        {
            UnreadNotificationCount++;
        }
    }

    /// <summary>
    /// Record a notification AND update <see cref="StatusMessage"/>. Use this
    /// at any call site where you'd otherwise just assign StatusMessage and
    /// also want a persistent history entry. Caller supplies the timestamp.
    /// </summary>
    public void AppendNotification(DateTimeOffset when, NotificationSeverity severity, string message)
    {
        StatusMessage = message;
        RecordNotification(when, severity, message);
    }

    /// <summary>Empty the notification history. Called by the Notifications tab's Clear button.</summary>
    public void ClearNotifications()
    {
        Notifications = [];
        UnreadNotificationCount = 0;
    }
}
