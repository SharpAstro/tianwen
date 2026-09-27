using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Stops the rig this computer's node holds, in the one order that is safe (decision 1 of
/// docs/plans/hardware-in-the-server.md, #936): the node's run first, through the run's own ending (a session's and a flat
/// run's Finalise, polar alignment's mount restore), and the devices only once the node says the run has ENDED, each warmed
/// up where it is a cooled camera and disconnected, as the node's own job. The node does all of it; this asks and follows
/// it, so the window can say how far it has got.
/// </summary>
/// <remarks>
/// <para>The rig used to be this process's, and so was the order: the quit once warmed every camera at the moment it cancelled
/// the session, so the session's Finalise and the quit ramped one camera at once. The node keeps that order now whoever asks,
/// and a window that goes away mid-stop leaves the node to finish it: a warm-up job is the node's, and the node's run ends
/// through its Finalise whether or not anyone watches.</para>
/// <para>A dead display no longer stops anything: the runs are the node's and go on, so the window's process only leaves
/// (P7 starts a successor).</para>
/// </remarks>
public sealed class RigShutdown(ITimeProvider timeProvider, ILogger logger)
{
    /// <summary>How often the node is asked whether its run has ended.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Stops the run of the node <paramref name="client"/> talks to, waits for it to end, then warms up and disconnects every
    /// device the node has connected and waits for each to finish. Never throws for a refusal or a device that fails to stop,
    /// which are logged; only <paramref name="cancellationToken"/> ends it early, and the node goes on with whatever it was
    /// asked by then.
    /// </summary>
    /// <param name="progress">A one-line status as the stop moves on (what it waits for, which device it warms).</param>
    public async Task StopAsync(TianWenNodeClient client, Action<string>? progress, CancellationToken cancellationToken)
    {
        if ((await client.GetNodeAsync(cancellationToken).ConfigureAwait(false)).Value?.Run is { } run)
        {
            progress?.Invoke(Ending(run.Kind));
            var stop = await StopRunAsync(client, run.Kind, cancellationToken).ConfigureAwait(false);
            if (!stop.IsSuccess && !stop.IsNotFound)
            {
                logger.LogWarning("Stopping the rig: the node did not stop its {Kind} run: {Error}", run.Kind, stop.Error);
            }

            // The run ends through its own ending on the node, however long that takes (a session's warm-up included).
            while (await client.GetNodeAsync(cancellationToken).ConfigureAwait(false) is { IsSuccess: true, Value.Run: not null })
            {
                await timeProvider.SleepAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        await WarmUpAndDisconnectAsync(client, progress, untilDone: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the node to warm up and disconnect every device it has connected, each as a job of its own (a camera ramps first
    /// where its cooler is on; anything else disconnects). With <paramref name="untilDone"/> it follows each job to its end;
    /// without, it returns once the node has taken them, and the node finishes them after this window has gone.
    /// </summary>
    public async Task WarmUpAndDisconnectAsync(TianWenNodeClient client, Action<string>? progress, bool untilDone, CancellationToken cancellationToken)
    {
        var states = await client.GetDeviceStatesAsync(cancellationToken).ConfigureAwait(false);
        if (states.Value is not { } devices)
        {
            logger.LogWarning("Stopping the rig: the node did not list its devices: {Error}", states.Error);
            return;
        }

        var jobs = new List<(JobDto Job, DeviceStateDto Device)>();
        foreach (var device in devices.Where(static d => d.Connected))
        {
            var started = await client.WarmAndDisconnectDeviceAsync(new Uri(device.DeviceUri), cancellationToken).ConfigureAwait(false);
            if (started is { IsSuccess: true, Value: { } job })
            {
                jobs.Add((job, device));
            }
            else
            {
                logger.LogWarning("Stopping the rig: the node did not take {Device}'s warm-up and disconnect: {Error}", NameOf(device), started.Error);
            }
        }

        if (!untilDone)
        {
            return;
        }

        foreach (var (job, device) in jobs)
        {
            progress?.Invoke(device.Camera is { CoolerOn: true } ? $"Warming {NameOf(device)}" : $"Disconnecting {NameOf(device)}");
            var ended = await client.UntilEndedAsync(job, timeProvider, cancellationToken).ConfigureAwait(false);
            if (ended.Value is { State: not JobState.Succeeded } failed)
            {
                logger.LogWarning("Stopping the rig: {Device} did not stop: {Error}", NameOf(device), failed.Error ?? failed.State.ToString());
            }
        }
    }

    /// <summary>What the stop waits for while the run of <paramref name="kind"/> ends.</summary>
    public static string Ending(NodeRunKind kind) => kind switch
    {
        NodeRunKind.Session => "Finalising the session: park, warm-up, covers",
        NodeRunKind.Flats => "Finishing the flat run",
        NodeRunKind.Polar => "Restoring the mount after polar alignment",
        NodeRunKind.Planetary => "Stopping the planetary capture",
        NodeRunKind.Darks => "Stopping the dark library",
        _ => "Stopping the run",
    };

    // Each kind of run stops through its own route (a stop names the run it means).
    private static async Task<NodeResult<string>> StopRunAsync(TianWenNodeClient client, NodeRunKind kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case NodeRunKind.Session or NodeRunKind.Flats:
                return await client.AbortSessionAsync(cancellationToken).ConfigureAwait(false);
            case NodeRunKind.Polar:
                return Of(await client.StopPolarAlignmentAsync(cancellationToken).ConfigureAwait(false));
            case NodeRunKind.Planetary:
                return Of(await client.StopPlanetaryAsync(cancellationToken).ConfigureAwait(false));
            case NodeRunKind.Darks:
                return Of(await client.StopDarkLibraryAsync(cancellationToken).ConfigureAwait(false));
            default:
                return new NodeResult<string>(null, $"A run of kind {kind} has no stop", 400);
        }

        static NodeResult<string> Of<T>(NodeResult<T> result) => new NodeResult<string>(result.IsSuccess ? "Stopped" : null, result.Error, result.StatusCode);
    }

    private static string NameOf(DeviceStateDto device) => device.DisplayName ?? device.DeviceUri;
}
