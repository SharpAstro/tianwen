using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>Why the rig is being stopped, which decides what happens to the runs still going.</summary>
public enum RigShutdownMode
{
    /// <summary>
    /// The user quit. Every run is aborted: a session and a flat run end through their own Finalise
    /// (park, warm-up, covers), polar alignment restores the mount. Then the cameras.
    /// </summary>
    Quit,

    /// <summary>
    /// The display died and the process goes on without one (P0a of docs/plans/hardware-in-the-server.md,
    /// #743). Polar alignment stops, since it is interactive and meaningless unseen, while a session and a
    /// flat run go on to their OWN end with their prompts answered unattended. Then the cameras. The
    /// caller's stop request (the user closing the headless window) turns the runs still going into an
    /// abort.
    /// </summary>
    DisplayLost,
}

/// <summary>
/// Stops what this process drives, in the one order that is safe: the runs first, each through its own
/// ending, and the cameras only once every run has ENDED. One sequence for an ordinary quit and for a
/// display that died, so the two cannot drift.
/// </summary>
/// <remarks>
/// <para>The quit used to warm and disconnect every connected camera at the same moment it cancelled the
/// session, so the session's own Finalise and the quit ramped one camera at once, and the quit's forced
/// disconnect could pull a camera out from under Finalise. It also keyed on <c>IsRunning</c> alone, so a
/// flat run and polar alignment were never cancelled, and polar's refine loop then held the shutdown for
/// ever.</para>
/// <para>What it does NOT stop is the caller's: the background work bound to the host's own token (the
/// planetary capture, the mount-limit watcher, the planner), cancelled by the host before this runs.</para>
/// <para>One instance per process. The camera tail runs at most once, so a display that dies while a
/// quit is already stopping the rig joins that stop instead of warming each camera a second time.</para>
/// </remarks>
public sealed class RigShutdown(LiveSessionState local, IDeviceHub? hub, ITimeProvider timeProvider, ILogger logger)
{
    private TaskCompletionSource? _cameras;

    /// <summary>
    /// Stops the rig: completes once every run has ended and every connected camera has been disconnected,
    /// warmed first where its cooler was on. Never throws; a camera that fails to stop is logged.
    /// </summary>
    /// <param name="mode">Whether the runs are aborted or left to finish.</param>
    /// <param name="stopRig">In <see cref="RigShutdownMode.DisplayLost"/>, turns the runs left to finish into an
    /// abort. <see cref="RigShutdownMode.Quit"/> aborts them anyway.</param>
    /// <param name="progress">A one-line status as the stop moves on (what it waits for, which camera it
    /// warms). Called from whichever thread the stop is on.</param>
    public async Task StopAsync(RigShutdownMode mode, CancellationToken stopRig = default, Action<string>? progress = null)
    {
        // Interactive: never outlives the decision to stop. Its own finally reverses the axis or parks.
        LiveSessionState.CancelRun(local.PolarAlignmentCts);

        if (mode is RigShutdownMode.Quit)
        {
            AbortNight();
        }
        else
        {
            // Nobody can see a prompt now: answer the one open, and every later one as it is raised.
            local.AnswerPromptsUnattended = true;
            if (local.PendingPrompt is { } prompt)
            {
                prompt.Respond(prompt.DefaultIfUnanswerable);
                local.PendingPrompt = null;
            }
        }

        // Runs synchronously when the request is already made, so a stop asked for before this point is
        // never lost. It reports the stop itself, on the thread that asks for it, so a caller showing the
        // latest progress can never show "goes on" after the stop was requested.
        using (stopRig.Register(() =>
        {
            AbortNight();
            progress?.Invoke(StoppingTheRig);
        }))
        {
            if (!local.PolarRunEnded.IsCompleted)
            {
                progress?.Invoke("Restoring the mount after polar alignment");
                await local.PolarRunEnded;
            }

            if (!local.FlatRunEnded.IsCompleted)
            {
                progress?.Invoke(Aborting(mode, stopRig) ? "Finishing the flat run" : FlatRunGoesOn);
                await local.FlatRunEnded;
            }

            if (!local.SessionEnded.IsCompleted)
            {
                progress?.Invoke(Aborting(mode, stopRig) ? "Finalising the session: park, warm-up, covers" : SessionGoesOn);
                await local.SessionEnded;
            }
        }

        await StopCamerasOnceAsync(progress);
    }

    /// <summary>The progress while a session is left to finish with no display.</summary>
    public const string SessionGoesOn = "The session goes on";

    /// <summary>The progress while a flat run is left to finish with no display.</summary>
    public const string FlatRunGoesOn = "The flat run goes on";

    /// <summary>The progress the moment the caller's stop request turns the runs left to finish into an abort.</summary>
    public const string StoppingTheRig = "Stopping the rig: park, warm-up, covers";

    // Whether the runs still going are being aborted: always on a quit, and once asked on a display loss.
    private static bool Aborting(RigShutdownMode mode, CancellationToken stopRig)
        => mode is RigShutdownMode.Quit || stopRig.IsCancellationRequested;

    // The session and a flat run, aborted: each still ends through its own Finalise, which is what the
    // wait above then waits for.
    private void AbortNight()
    {
        LiveSessionState.CancelRun(local.SessionCts);
        LiveSessionState.CancelRun(local.FlatsCts);
    }

    private async Task StopCamerasOnceAsync(Action<string>? progress)
    {
        // Published before the work starts, never built inside the exchange (CLAUDE.md, Concurrency): a
        // second stop that loses the race joins the first one's tail rather than starting its own.
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _cameras, mine, null) is { } running)
        {
            await running.Task;
            return;
        }

        try
        {
            await WarmAndDisconnectCamerasAsync(progress);
        }
        finally
        {
            mine.TrySetResult();
        }
    }

    private async Task WarmAndDisconnectCamerasAsync(Action<string>? progress)
    {
        if (hub is null)
        {
            return;
        }

        var cameras = new List<(Uri Uri, string Name)>();
        foreach (var (uri, driver) in hub.ConnectedDevices)
        {
            if (driver is ICameraDriver)
            {
                cameras.Add((uri, hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.Host));
            }
        }

        if (cameras.Count == 0)
        {
            return;
        }

        progress?.Invoke(cameras.Count == 1 ? $"Disconnecting {cameras[0].Name}" : $"Disconnecting {cameras.Count} cameras");
        var stops = new Task[cameras.Count];
        for (var i = 0; i < cameras.Count; i++)
        {
            stops[i] = StopCameraAsync(hub, cameras[i].Uri, cameras[i].Name, progress);
        }
        await Task.WhenAll(stops);
    }

    private async Task StopCameraAsync(IDeviceHub devices, Uri uri, string name, Action<string>? progress)
    {
        try
        {
            // CancellationToken.None throughout: a thermal ramp that has started must finish, whatever
            // else is being torn down.
            var safety = await EquipmentActions.GetDisconnectSafetyAsync(devices, uri, CancellationToken.None);
            if (safety == EquipmentActions.DisconnectSafety.Safe)
            {
                // force: every run has ended, so no lease should remain; a run that failed to release one
                // must not keep the process from exiting (force is for shutdown only, and this is it).
                await devices.DisconnectAsync(uri, force: true, CancellationToken.None);
            }
            else
            {
                progress?.Invoke($"Warming {name}");
                await EquipmentActions.WarmAndDisconnectAsync(devices, uri, timeProvider, logger, force: true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shutdown: stopping camera {Uri} failed", uri);
        }
    }
}
