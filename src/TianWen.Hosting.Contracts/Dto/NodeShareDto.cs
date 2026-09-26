namespace TianWen.Hosting.Dto;

/// <summary>
/// <c>PUT /api/v1/node/share</c>: turns "Share this rig on the LAN" on or off (docs/plans/hardware-in-the-server.md,
/// decision 3). A MACHINE setting, kept by the node, never per profile or per session.
/// </summary>
public sealed class NodeShareRequest
{
    public bool Shared { get; init; }
}

/// <summary>Where the share setting stands after a change, and where the node's listening stands.</summary>
public sealed class NodeShareDto
{
    /// <summary>The setting: the node listens on the LAN, announces itself and starts at logon.</summary>
    public bool Shared { get; init; }

    /// <summary>Whether the node listens on the LAN now. The setting takes effect when the node starts.</summary>
    public bool Listening { get; init; }

    /// <summary>Whether the node is restarting now to apply it: an idle node a client started does.</summary>
    public bool Restarting { get; init; }

    /// <summary>What happened, in words a client can show.</summary>
    public required string Message { get; init; }
}
