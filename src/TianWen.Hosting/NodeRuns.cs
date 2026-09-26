using TianWen.Hosting.Dto;

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
        _ => "A run is already going on",
    };
}
