using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// This computer's own node, over its socket (P6 of docs/plans/hardware-in-the-server.md, #936): the node the GUI or the TUI
/// found or started (<see cref="LocalNodeLauncher"/>), which holds this computer's devices and runs its sessions, so a
/// window that dies or wedges takes none of them with it. The local view reads it as it reads any rig
/// (<see cref="NodeConnection"/>); what differs is that its profile is the app's own active profile and that its prompts are
/// brought to the front, as a session run in this process always was.
/// </summary>
public sealed class LocalNodeConnection : NodeConnection
{
    private readonly GuiAppState _app;

    private LocalNodeConnection(ViewContext local, NodeTransport transport, NodeInfoDto node, LocalNodeOutcome outcome,
        GuiAppState app, ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
        : base(local, transport, promptsApp: app, timeProvider, logger, cancellationToken)
    {
        _app = app;
        Node = node;
        Outcome = outcome;
    }

    /// <summary>The node's answer when it was found or started: its stable id, its version, whether it is shared.</summary>
    public NodeInfoDto Node { get; }

    /// <summary>
    /// How this client came to the node: found running, started (and outliving this client), or started with this client
    /// because its job would not let the node break away, which the app says, since the rig then ends with the window.
    /// </summary>
    public LocalNodeOutcome Outcome { get; }

    private protected override string Name => "this computer's node";

    private protected override void OnProfileRead(Profile profile) => _app.ActiveProfile = profile;

    private protected override Profile? ProfileOnView => _app.ActiveProfile;

    /// <summary>
    /// Finds the machine's node, or starts one, and connects this computer's view to it. The view takes the node's stable id,
    /// so the rig picker and the Home board leave it out: this computer's node is never a rig of its own. Returns null and
    /// the launcher's reason, in words a user can act on, when there is no node to reach (a broken install, a node that
    /// did not come up).
    /// </summary>
    public static async Task<(LocalNodeConnection? Connection, string Message)> FindOrStartAsync(
        ViewContexts contexts, GuiAppState app, LocalNodeOptions options, ITimeProvider timeProvider, ILogger logger,
        CancellationToken cancellationToken)
    {
        var found = await new LocalNodeLauncher(options, logger).FindOrStartAsync(cancellationToken).ConfigureAwait(false);
        if (found is not { Transport: { } transport, Node: { } node })
        {
            logger.LogWarning("No node to hold this computer's rig ({Outcome}): {Message}", found.Outcome, found.Message);
            return (null, found.Message);
        }

        contexts.Local.NodeId = node.NodeId;
        logger.LogInformation("This computer's node {NodeId} ({Outcome}): {Message}", node.NodeId, found.Outcome, found.Message);
        return (new LocalNodeConnection(contexts.Local, transport, node, found.Outcome, app, timeProvider, logger, cancellationToken),
            found.Message);
    }
}
