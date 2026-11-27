using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a clicked desktop alert carries, and how it resolves back to a view: <c>notifications</c> for this computer's
/// Notifications tab, <c>notifications/&lt;NodeId&gt;</c> for a rig's. The payload comes from outside the process, so it is
/// untrusted: only an exact match of a known NodeId selects a rig, anything else resolves to this computer's view, and it
/// is never used as a path, a command or a format string.
/// </summary>
public static class DesktopAlertLink
{
    private const string Prefix = "notifications";

    /// <summary>The payload for the view of <paramref name="nodeId"/>; null (or empty) is this computer's.</summary>
    public static string For(string? nodeId) => string.IsNullOrEmpty(nodeId) ? Prefix : $"{Prefix}/{nodeId}";

    /// <summary>The view a payload selects in <paramref name="views"/>: a rig only by an exact, known NodeId, else this computer's.</summary>
    public static ViewContext Resolve(string? payload, ViewContexts views)
    {
        ArgumentNullException.ThrowIfNull(views);

        if (payload is not null && payload.StartsWith(Prefix + "/", StringComparison.Ordinal))
        {
            var id = payload.AsSpan(Prefix.Length + 1);
            foreach (var context in views.All)
            {
                if (context.NodeId is { Length: > 0 } known && id.SequenceEqual(known))
                {
                    return context;
                }
            }
        }
        return views.Local;
    }
}
