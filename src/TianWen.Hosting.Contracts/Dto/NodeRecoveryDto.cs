using System;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto;

/// <summary>Which kind of run a node is going on with, or was when it died.</summary>
public enum NodeRunKind
{
    /// <summary>An imaging session (<c>POST /api/v1/session/start</c>).</summary>
    Session,

    /// <summary>A flat-frame run on its own (<c>POST /api/v1/session/flats</c>).</summary>
    Flats,
}

/// <summary>
/// What a node found as it started: the journal a node before it left behind when it died instead of stopping
/// (docs/plans/hardware-in-the-server.md, "When the server dies"). <c>GET /api/v1/node</c> carries it until a client
/// dismisses it (<c>DELETE /api/v1/node/recovery</c>), so a client can say which run was interrupted and when, and offer
/// to stop the rig safely or start the run again. The node never resumes a run by itself: a mount that has tracked for
/// unknown minutes is exactly where a blind resume goes wrong.
/// </summary>
public sealed class NodeRecoveryDto
{
    /// <summary>When the node that died last wrote its journal: the last moment the rig is known to have been as below.</summary>
    public DateTimeOffset JournalWrittenUtc { get; init; }

    /// <summary>When this node found it, as it started.</summary>
    public DateTimeOffset FoundUtc { get; init; }

    /// <summary>
    /// Whether this node's keeper started it because the node that wrote the journal had just crashed, so the journal
    /// is seconds old, however long ago it was last written.
    /// </summary>
    public bool AfterCrash { get; init; }

    /// <summary>
    /// Whether the journal is older than the machine's last boot (a power cut, a restart): it describes a rig whose
    /// state nobody knows, so it is shown for information and never acted on.
    /// </summary>
    public bool Stale { get; init; }

    /// <summary>The run that was going on when the node died, if one was.</summary>
    public NodeRunDto? InterruptedRun { get; init; }

    /// <summary>The devices the node held when it died.</summary>
    public NodeHeldDeviceDto[] Devices { get; init; } = [];
}

/// <summary>A node's run: what kind, on which profile, since when, and what it was imaging.</summary>
public sealed class NodeRunDto
{
    public NodeRunKind Kind { get; init; }

    public Guid ProfileId { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>The target it was on, when it had reached one.</summary>
    public string? Target { get; init; }
}

/// <summary>A device a node held, and for a camera what its cooler was being asked to do.</summary>
public sealed class NodeHeldDeviceDto
{
    public required string DeviceUri { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>A camera's cooler intent (<see cref="CoolerIntent"/>); null when nothing had asked anything of it.</summary>
    public CoolerIntentKind? Cooler { get; init; }

    /// <summary>The setpoint a <see cref="CoolerIntentKind.Cool"/> intent holds, in degrees Celsius.</summary>
    public double? CoolerSetpointC { get; init; }
}
