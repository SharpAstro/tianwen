using System;

namespace TianWen.Lib.Sequencing;

/// <summary>Whether the node running a session is answering (<see cref="NodeContact"/>).</summary>
public enum NodeContactState
{
    /// <summary>The node answered its latest poll, or the session runs in this process and there is no node to ask.</summary>
    Answering,

    /// <summary>No poll has come back yet: a rig just connected, which is neither answering nor silent.</summary>
    Connecting,

    /// <summary>The node did not answer its latest poll, so what a view shows is only what it last said.</summary>
    NotAnswering,
}

/// <summary>
/// Whether the node running a session is answering, and when it last did (<see cref="ISessionTelemetry.Contact"/>, P5b
/// part 6 of docs/plans/hardware-in-the-server.md). A view of a rig that has gone quiet says so, since everything else on
/// it is the last thing the node said and would otherwise read as live.
/// </summary>
/// <param name="State">Answering, still connecting, or not answering.</param>
/// <param name="LastAnsweredUtc">When the node last answered; null for one that has not answered since it was connected
/// to, and for a session in this process.</param>
public readonly record struct NodeContact(NodeContactState State, DateTimeOffset? LastAnsweredUtc)
{
    /// <summary>A session in this process, which is always there to ask.</summary>
    public static NodeContact InProcess => default;
}
