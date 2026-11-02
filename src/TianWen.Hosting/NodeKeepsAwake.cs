using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

namespace TianWen.Hosting;

/// <summary>
/// Keeps the computer awake (the display may still sleep) for exactly as long as the node holds hardware: a connected device,
/// a lease, a run or a running job (#1957; <see cref="NodeHoldsHardware"/>, the rule <see cref="NodeIdleExit"/> shares).
/// Taken on the first hold, released when the last is let go and when the service stops; the OS releases it if the node dies.
/// </summary>
/// <remarks>
/// Runs in every <c>tianwen-server</c> whatever its role: a node started by hand holds hardware just the same. It looks once a
/// second and also on a device connecting or disconnecting, so a take is prompt; a lease or a job has no event, and the poll
/// covers those.
/// </remarks>
internal sealed class NodeKeepsAwake(IDeviceHub hub, IHostedSession hosted, NodeJobs jobs, IPowerRequest power,
    ITimeProvider timeProvider, ILogger<NodeKeepsAwake> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    // The poll and a hub event (any thread) both look, and a take and a release must not interleave. Not on a render thread,
    // and a Task hand-off or an Interlocked swap cannot keep the "look, then take or release" pair whole.
    private readonly Lock _gate = new Lock();
    private IDisposable? _request;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hub.DeviceStateChanged += OnDeviceStateChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Check();
                await timeProvider.SleepAsync(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
        finally
        {
            hub.DeviceStateChanged -= OnDeviceStateChanged;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            Release("the node is stopping");
        }
    }

    private void OnDeviceStateChanged(object? sender, DeviceConnectedEventArgs e) => Check();

    /// <summary>One look: takes the request on the first hold, releases it when none is left.</summary>
    internal void Check()
    {
        lock (_gate)
        {
            var holding = NodeHoldsHardware.What(hub, hosted, jobs);
            if (holding is not null && _request is null)
            {
                _request = power.Acquire("TianWen is holding astronomy hardware");
                logger.LogInformation("Keeping the computer awake: {Holding}", holding);
            }
            else if (holding is null && _request is not null)
            {
                ReleaseLocked("nothing holds hardware any more");
            }
        }
    }

    private void Release(string why)
    {
        lock (_gate)
        {
            ReleaseLocked(why);
        }
    }

    private void ReleaseLocked(string why)
    {
        if (_request is { } request)
        {
            _request = null;
            request.Dispose();
            logger.LogInformation("The computer may sleep again: {Why}", why);
        }
    }
}
