using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>What the node says about its runs, in one place.</summary>
internal static class NodeRuns
{
    /// <summary>
    /// The refusal of a start while <paramref name="kind"/> is going on: it NAMES the run, since a client told only that
    /// "a session is running" while a dark library holds the node has nothing to stop.
    /// </summary>
    public static string AlreadyGoingOn(NodeRunKind? kind) => kind switch
    {
        NodeRunKind.Session => "A session is already running",
        NodeRunKind.Flats => "A flat run is already running",
        NodeRunKind.Darks => "A dark library is being taken",
        NodeRunKind.Polar => "Polar alignment is running",
        NodeRunKind.Planetary => "A planetary capture is running",
        NodeRunKind.LiveView => "A live view is running",
        _ => "A run is already going on",
    };

    /// <summary>
    /// A run that ended on a FAULT says so in the node's notifications, worded here once: <paramref name="what"/> stopped,
    /// and why. Every client's notification panel shows it (the TUI and a remote rig's window too), where the reason used
    /// to reach only the window watching the run and the node's log: a Canon whose battery died mid live view said so
    /// nowhere a user looks (#1111). A run that ended on a stop, or by itself, has no <paramref name="failure"/> and says
    /// nothing here.
    /// </summary>
    public static void NoteFault(IHostedSession hosted, ITimeProvider timeProvider, string what, string? failure)
    {
        if (failure is { Length: > 0 })
        {
            hosted.AddNotification(new NotificationDto
            {
                Severity = "Warning",
                Message = $"{what} stopped: {failure}",
                TimestampUtc = timeProvider.GetUtcNow(),
            });
        }
    }

    /// <summary>
    /// Why a start path suppresses CA2000: the justification for every <c>StartAsync</c> that builds a run and hands it to
    /// <see cref="IHostedSession.TryStartAsync(INodeRun, System.Guid)"/>, stated once. The attribute stays on each method,
    /// since CA2000 reports where the run is created.
    /// </summary>
    internal const string HandedToTheNode =
        "The run passes to the node once IHostedSession.TryStartAsync accepts it, and the node disposes it when the next run "
        + "replaces it or as the host stops: a hand-off through a method call, which CA2000 cannot follow. Every other way "
        + "out, a throw included, disposes it in the finally.";
}
