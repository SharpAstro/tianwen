using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.IO;

namespace TianWen.Hosting;

/// <summary>
/// If the computer suspends anyway, stops every connected mount's moving axes first, where the OS gives notice (Windows;
/// #1957), as GSServer does on suspend: abort the slew, stop both axes, stop tracking.
/// </summary>
/// <remarks>
/// <para>
/// <b>This stops a LEASED mount too, which the ownership rules otherwise forbid</b> (<c>docs/architecture/device-ownership.md</c>).
/// The OS is about to freeze this process, so the run that holds the lease cannot act: a mount left slewing or tracking through
/// the suspend drives on unwatched, and the owner asked for it to be stopped (#1957). <c>RigShutdown</c>'s process-shutdown
/// <c>force</c> is the other such exception. Nothing restarts the mounts on resume: the run was frozen with its own belief about
/// them, and the operator is told to restart tracking.
/// </para>
/// <para>
/// Windows allows about two seconds before it suspends, so the whole stop is bounded by <see cref="Budget"/> on the injected
/// clock, every step of every mount goes through <see cref="LoggerCatchExtensions"/> so one failure stops nothing else, and the
/// mounts go in parallel.
/// </para>
/// </remarks>
internal sealed class NodeSuspendStop(IDeviceHub hub, ISuspendNotice notice, ITimeProvider timeProvider,
    ILogger<NodeSuspendStop> logger) : IHostedService
{
    /// <summary>How long the stop may take: Windows waits about two seconds, and the callback itself leaves a margin.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(1.5);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        notice.Suspending += StopMountsAsync;
        notice.Resumed += OnResumed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notice.Suspending -= StopMountsAsync;
        notice.Resumed -= OnResumed;
        return Task.CompletedTask;
    }

    private void OnResumed()
        => logger.LogWarning("The computer has resumed. Mounts were stopped before it suspended; tracking must be restarted");

    /// <summary>Stops every connected mount, within <see cref="Budget"/>, and logs what was stopped.</summary>
    internal async Task StopMountsAsync()
    {
        var mounts = hub.ConnectedDevices.Select(static d => (d.DeviceUri, Mount: d.Driver as IMountDriver))
            .Where(static d => d.Mount is not null)
            .ToArray();
        if (mounts.Length == 0)
        {
            return;
        }

        logger.LogWarning("The computer is about to suspend: stopping {Count} mount(s)", mounts.Length);
        var stopped = new ConcurrentBag<string>();
        using var deadline = timeProvider.CancelAfter(Budget);
        var ct = deadline.Token;

        var work = Task.WhenAll(mounts.Select(m => StopAsync(m.DeviceUri, m.Mount!, stopped, ct)));

        // A driver that ignores its token must not hold the OS: leave the work behind when the budget ends.
        var cutOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cutOff);
        if (await Task.WhenAny(work, cutOff.Task) != work)
        {
            logger.LogWarning("Stopping the mounts took longer than {Budget}; the suspend goes ahead with what was stopped",
                Budget);
        }

        logger.LogWarning("Before the suspend, stopped: {Stopped}",
            stopped.IsEmpty ? "nothing" : string.Join(", ", stopped.OrderBy(static s => s, StringComparer.Ordinal)));
    }

    private async Task StopAsync(Uri uri, IMountDriver mount, ConcurrentBag<string> stopped, CancellationToken ct)
    {
        // The abort first: it is what ends a slew, and both axes with it on most drivers.
        if (await logger.CatchAsync(mount.AbortSlewAsync, ct))
        {
            stopped.Add($"{uri} slew");
        }

        foreach (var axis in new[] { TelescopeAxis.Primary, TelescopeAxis.Seconary })
        {
            if (mount.CanMoveAxis(axis)
                && await logger.CatchAsync(token => mount.MoveAxisAsync(axis, 0, token), ct))
            {
                stopped.Add($"{uri} {axis} axis");
            }
        }

        if (mount.CanSetTracking && await logger.CatchAsync(token => mount.SetTrackingAsync(false, token), ct))
        {
            stopped.Add($"{uri} tracking");
        }
    }
}
