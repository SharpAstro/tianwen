using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>How long a node a client started goes on with nothing using it, and how often that is looked at.</summary>
/// <param name="Grace">Long enough for a window that closed to be opened again without paying for a new node (about two
/// seconds to start one), short enough that an idle node does not hold the build's files, or answer a newer client with
/// older code, for long.</param>
internal sealed record NodeIdleExitOptions(TimeSpan Grace, TimeSpan Poll)
{
    public static readonly NodeIdleExitOptions Default = new NodeIdleExitOptions(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Ends a node a CLIENT started once nothing has used it for the grace: no client present, no device connected, no run
/// and no job (decision 2 of docs/plans/hardware-in-the-server.md, amended 2026-09-30). Its exit is clean, so its keeper
/// ends too, and the next client starts a node of its own build.
/// </summary>
/// <remarks>
/// <para><b>It never exits while it holds hardware</b>: a connected device, a run or a job (a warm-up is a job) keeps it,
/// as a node always did. Nor does one started by hand, or one sharing the rig on the LAN, which serves clients it cannot
/// count. <b>Present means a fresh presence BEAT</b> (<see cref="EventHub.PresentClientCount"/>), the rule a prompt waits by
/// and <see cref="NodeRunWatch"/> stops an unwatched run by.</para>
/// <para>What it replaced: a node stayed until logoff. Measured on 2026-09-30, one held a Canon's session (so nothing else
/// could use the camera), locked every build of the tree it ran from, and served a burst with the previous build's code,
/// since a client of the same wire version attaches to it without a word.</para>
/// </remarks>
internal sealed class NodeIdleExit(NodeRole role, NodeListening listening, IDeviceHub hub, IHostedSession hosted, NodeJobs jobs,
    EventHub clients, IHostApplicationLifetime lifetime, ITimeProvider timeProvider, NodeIdleExitOptions options,
    ILogger<NodeIdleExit> logger) : BackgroundService
{
    // Touched only by the loop.
    private DateTimeOffset? _idleSince;
    private bool _exiting;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await timeProvider.SleepAsync(options.Poll, stoppingToken);
                Check();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping, whoever asked.
        }
    }

    /// <summary>What is using the node now, or null when nothing is.</summary>
    internal string? InUse()
    {
        if (clients.PresentClientCount > 0)
        {
            return "a client is present";
        }
        if (hub.ConnectedDevices.Count > 0)
        {
            return "a device is connected";
        }
        if (hosted.RunningKind is { } kind)
        {
            return $"a {kind} run is going on";
        }
        if (jobs.List().Any(static job => job.State is JobState.Running))
        {
            return "a job is running";
        }
        return null;
    }

    /// <summary>One look: starts, cancels or spends the grace, and exits once it is spent.</summary>
    internal void Check()
    {
        if (_exiting || !role.Spawned || listening.IsShared)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (InUse() is { } use)
        {
            if (_idleSince is not null)
            {
                logger.LogInformation("The node is in use again ({Use}); it stays", use);
                _idleSince = null;
            }
            return;
        }

        if (_idleSince is not { } since)
        {
            _idleSince = now;
            logger.LogInformation("Nothing is using this node (no client, device, run or job); it exits in {Grace} s unless that changes",
                options.Grace.TotalSeconds);
            return;
        }
        if (now - since < options.Grace)
        {
            return;
        }

        logger.LogInformation("The node exits: nothing has used it for {Grace} s", options.Grace.TotalSeconds);
        _exiting = true;
        lifetime.StopApplication();
    }
}
