namespace TianWen.Hosting.Api;

/// <summary>
/// The version of the wire a node and its clients speak. A client and the node it spawns ship together, but a
/// node from an older install can still be running a night after an update, so the two compare this before
/// anything else (<c>GET /api/v1/node</c>), and never the build version.
/// </summary>
/// <remarks>
/// Raise it when a change would make an older client misread a newer node, or the reverse: a route removed or
/// renamed, a field whose meaning changed, an event a client must handle. Adding an endpoint or an optional
/// field does not raise it.
/// </remarks>
public static class NodeWire
{
    public const int Version = 1;
}

/// <summary>
/// How a node process ends, for whoever started it: a keeper restarts a node that crashed, and must not restart
/// one that exited because another node already runs.
/// </summary>
public static class NodeExitCodes
{
    /// <summary>A clean stop.</summary>
    public const int Stopped = 0;

    /// <summary>The command line was wrong, or named a socket this system cannot bind (<see cref="NodeSocket.TryValidate"/>).</summary>
    public const int InvalidArguments = 2;

    /// <summary>Another node holds the lock on the socket, so this one never started.</summary>
    public const int AlreadyRunning = 3;
}
