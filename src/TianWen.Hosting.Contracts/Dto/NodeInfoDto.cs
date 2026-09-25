namespace TianWen.Hosting.Dto;

/// <summary>
/// <c>GET /api/v1/node</c>: which node this is, and whether a client can talk to it. A client reads it first, to
/// know the node answered at all (readiness after a spawn), which rig it is (the Local context binds by
/// <see cref="NodeId"/>, as a remote rig's binding does) and whether the two speak the same wire.
/// </summary>
public sealed class NodeInfoDto
{
    /// <summary>
    /// The node's stable identity, minted once per machine and user and kept in AppData: the same id the LAN
    /// announcement carries, so a rig is one rig however it was reached.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>The node's build, for display and logs. Compatibility is <see cref="WireVersion"/>, never this.</summary>
    public required string Version { get; init; }

    /// <summary>The wire the node speaks (<see cref="Api.NodeWire.Version"/>).</summary>
    public int WireVersion { get; init; }

    /// <summary>The node's process, for "a node is already running (pid N)" and for a keeper that waits on it.</summary>
    public int ProcessId { get; init; }

    /// <summary>Whether the node is reachable from the LAN (it listens on TCP), not only on this machine's socket.</summary>
    public bool IsShared { get; init; }

    /// <summary>TianWen clients attached to the event stream now. Only the last one asks before a window closes.</summary>
    public int ClientsAttached { get; init; }
}
