using System;

namespace TianWen.Hosting.Dto;

// Control over the LAN by grant (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021): over TCP a client
// sees, and commands only once the rig's machine has granted it control; another application (Alpaca, ninaAPI), which
// cannot ask, is allowed by its address or its host name.

/// <summary><c>POST /api/v1/node/control/requests</c>: a client over TCP asks the rig's machine for control.</summary>
public sealed class ControlRequestDto
{
    /// <summary>Who is asking, for a person to read: a machine and an application ("Laptop, TianWen").</summary>
    public string Label { get; set; } = "";
}

/// <summary>The request made: its id, and the secret its asker polls with, which nobody else is told.</summary>
public sealed class ControlRequestTicketDto
{
    public required string Id { get; init; }

    public required string Secret { get; init; }
}

/// <summary>
/// <c>POST /api/v1/node/control/requests/{id}/poll</c>: the asker is still there, and wants to know where its request
/// stands. A request whose asker stops polling is withdrawn (<see cref="Api.NodeWire.ControlRequestLapse"/>).
/// </summary>
public sealed class ControlRequestPollDto
{
    public string Secret { get; set; } = "";
}

/// <summary>Where a request for control stands. Numeric on the wire, as every enum there is.</summary>
public enum ControlRequestState
{
    /// <summary>Nothing is known under that id, or not with that secret.</summary>
    Unknown = 0,

    /// <summary>Waiting for the rig's machine to answer.</summary>
    Pending = 1,

    /// <summary>Allowed: the answer carries the token, this once.</summary>
    Granted = 2,

    Declined = 3,

    /// <summary>It ended unanswered: its asker stopped polling.</summary>
    Withdrawn = 4,
}

/// <summary>A request's state, and once it is granted, the token, handed to its asker on the first poll after.</summary>
public sealed class ControlRequestOutcomeDto
{
    public ControlRequestState State { get; init; }

    /// <summary>The bearer token, on the one answer that hands it over; null on every other.</summary>
    public string? Token { get; init; }
}

/// <summary><c>POST /api/v1/node/control/requests/{id}/answer</c>, from the rig's machine or a client granted control.</summary>
public sealed class ControlAnswerDto
{
    public bool Allow { get; set; }
}

/// <summary>A request waiting for its answer.</summary>
public sealed class PendingControlRequestDto
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>Where the request came from.</summary>
    public string? Address { get; init; }

    public DateTimeOffset At { get; init; }
}

/// <summary>A client granted control, until revoked.</summary>
public sealed class GrantDto
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public DateTimeOffset GrantedAt { get; init; }
}

/// <summary>Which protocol another application commands the rig through. Numeric on the wire.</summary>
public enum AppProtocol
{
    Alpaca = 0,

    NinaV2 = 1,
}

/// <summary>An address another application is allowed to command from, until the node restarts.</summary>
public sealed class AllowedAppDto
{
    public required string Address { get; init; }

    /// <summary>The address's forward-confirmed host name, when it has one: what Always allow would remember.</summary>
    public string? Host { get; init; }
}

/// <summary>
/// Another application's command refused, one record per address, a retry updating it: the refusal is its request, since
/// it cannot ask. Allow lets it in until the node restarts; Always allow remembers its <see cref="Host"/>.
/// </summary>
public sealed class RefusedAppDto
{
    public required string Address { get; init; }

    /// <summary>The address's forward-confirmed host name, once the lookup has answered; null when it has none.</summary>
    public string? Host { get; init; }

    /// <summary>What the application says it is (its <c>User-Agent</c>).</summary>
    public string? UserAgent { get; init; }

    /// <summary>The Alpaca <c>ClientID</c> it sent, when it sent one.</summary>
    public string? ClientId { get; init; }

    public AppProtocol Protocol { get; init; }

    /// <summary>What it tried, the last time: a method and a path.</summary>
    public required string What { get; init; }

    public DateTimeOffset FirstAt { get; init; }

    public DateTimeOffset LastAt { get; init; }

    public int Attempts { get; init; }
}

/// <summary><c>POST /api/v1/node/access/apps</c>: lets a refused application in.</summary>
public sealed class AppAllowDto
{
    public string Address { get; set; } = "";

    /// <summary>
    /// Remember the address's host name, so it is let in after a restart and a DHCP renewal too; refused for an address
    /// with no forward-confirmed name, which can only be allowed until the node restarts.
    /// </summary>
    public bool Always { get; set; }
}

/// <summary>
/// <c>GET /api/v1/node/access</c>: who may command the node and who was refused, for the Sharing panel; from the rig's
/// machine or a client granted control.
/// </summary>
public sealed class NodeAccessDto
{
    /// <summary>The machine's "Share this rig on the LAN" setting.</summary>
    public bool Shared { get; init; }

    /// <summary>Whether the node listens on the LAN now.</summary>
    public bool Listening { get; init; }

    /// <summary>The request for control waiting for its answer, if one is.</summary>
    public PendingControlRequestDto? Pending { get; init; }

    public GrantDto[] Grants { get; set; } = [];

    public AllowedAppDto[] AppsAllowed { get; set; } = [];

    public string[] HostsAlwaysAllowed { get; set; } = [];

    public RefusedAppDto[] Refused { get; set; } = [];
}
