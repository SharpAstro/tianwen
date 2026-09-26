using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>How long an interactive run goes on with nobody watching it, and how often that is looked at.</summary>
/// <param name="DetachGrace">Long enough for a window that went away to come back: a respawned GUI (P7 of
/// docs/plans/hardware-in-the-server.md) re-attaches within seconds, a restarted one within a minute.</param>
internal sealed record NodeRunWatchOptions(TimeSpan DetachGrace, TimeSpan Poll)
{
    public static readonly NodeRunWatchOptions Default = new NodeRunWatchOptions(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Ends an INTERACTIVE run once no client has been present for the detach grace (P5 part 4 of
/// docs/plans/hardware-in-the-server.md, #934): polar alignment, and a planetary live view after it, are meaningless unseen,
/// and the window that started one may have gone for good. A session, a flat run and a dark library go on regardless
/// (<see cref="INodeRun.EndsUnwatched"/>).
/// </summary>
/// <remarks>
/// <para><b>Present means a fresh presence BEAT</b> (<see cref="EventHub.PresentClientCount"/>), the same rule a prompt
/// waits by, never an open socket: a window frozen by a GPU wedge keeps its socket, and is watching nothing.</para>
/// <para><b>The grace runs from the moment nobody is present</b>, the run's start included, and a client back within it
/// cancels it; it is the run's own, so the next run starts its own. An abort ends the run through its own ending, the
/// mount restored.</para>
/// </remarks>
internal sealed class NodeRunWatch(IHostedSession hosted, EventHub clients, ITimeProvider timeProvider, NodeRunWatchOptions options,
    ILogger<NodeRunWatch> logger) : BackgroundService
{
    // Touched only by the watch's own loop.
    private INodeRun? _watched;
    private DateTimeOffset? _unwatchedSince;
    private bool _stopped;

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
            // The host is stopping; its own stop ends the run.
        }
    }

    /// <summary>One look: starts, cancels or spends the grace of the interactive run going on.</summary>
    internal void Check()
    {
        var run = hosted.RunningKind is not null && hosted.CurrentRun is { EndsUnwatched: true } interactive ? interactive : null;
        if (!ReferenceEquals(run, _watched))
        {
            _watched = run;
            _unwatchedSince = null;
            _stopped = false;
        }
        // Stopped once: it ends through its own ending from there (the mount restored), which a second abort would not hurry.
        if (run is null || _stopped)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (clients.PresentClientCount > 0)
        {
            if (_unwatchedSince is not null)
            {
                logger.LogInformation("A client is watching the {Kind} run again; it goes on", run.Kind);
                _unwatchedSince = null;
            }
            return;
        }

        if (_unwatchedSince is not { } since)
        {
            _unwatchedSince = now;
            logger.LogInformation("Nobody is watching the {Kind} run; it stops in {Grace} unless a client comes back", run.Kind, options.DetachGrace);
            return;
        }
        if (now - since < options.DetachGrace)
        {
            return;
        }

        var message = $"{Describe(run.Kind)} stopped: no client has watched it for {options.DetachGrace.TotalSeconds:0} s";
        logger.LogWarning("{Message}", message);
        hosted.AddNotification(new NotificationDto { Severity = "Warning", Message = message, TimestampUtc = now });
        hosted.TryAbort(run);
        _stopped = true;
    }

    private static string Describe(NodeRunKind kind) => kind switch
    {
        NodeRunKind.Polar => "Polar alignment",
        NodeRunKind.Planetary => "The planetary capture",
        _ => $"The {kind} run",
    };
}
