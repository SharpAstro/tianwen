using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A guiding run's state as the Guider tab draws it (#1915), read off <see cref="GuidingStateDto"/> once per poll, never per frame:
/// the phase, the calibration the node holds, and, while it runs, the guider as a session reports it (the steps, the RMS, the star).
/// </summary>
public sealed record GuidingRunView(
    bool Running,
    GuideRunMode Mode,
    GuidingPhase Phase,
    string? FailureReason,
    GuiderCalibrationDto? Calibration,
    CalibrationOverlayData? Overlay,
    ImmutableArray<GuideErrorSample> Samples,
    GuideStats? Stats,
    string? GuiderState,
    SettleProgress? Settle,
    TimeSpan Exposure,
    (double X, double Y)? StarPosition,
    double? StarSnr,
    (float[] H, float[] V)? Profile)
{
    public static GuidingRunView From(GuidingStateDto state)
    {
        var guider = state.Guider;
        var steps = guider?.RecentSteps ?? [];
        var samples = ImmutableArray.CreateBuilder<GuideErrorSample>(steps.Length);
        foreach (var step in steps)
        {
            samples.Add(new GuideErrorSample(step.Timestamp, JsonNumber.FromWire(step.RaError), JsonNumber.FromWire(step.DecError),
                JsonNumber.FromWire(step.RaCorrectionMs), JsonNumber.FromWire(step.DecCorrectionMs), step.IsDither, step.IsSettling));
        }

        // The overlay of the calibration the node holds: a run's own L is in the guider's state, the remembered one in the calibration.
        var overlay = (state.Calibration?.Overlay ?? guider?.Calibration)?.ToOverlay();
        return new GuidingRunView(
            state.Running,
            state.Mode,
            state.Phase,
            state.FailureReason,
            state.Calibration,
            overlay,
            samples.MoveToImmutable(),
            guider is { TotalRMS: not null }
                ? GuideStats.FromRms(JsonNumber.FromWire(guider.TotalRMS), JsonNumber.FromWire(guider.RaRMS), JsonNumber.FromWire(guider.DecRMS),
                    JsonNumber.FromWire(guider.PeakRa), JsonNumber.FromWire(guider.PeakDec), guider.LastRaErr, guider.LastDecErr,
                    guider.LastRaPulseMs, guider.LastDecPulseMs)
                : null,
            guider?.State,
            guider?.Settle?.ToSettle(),
            TimeSpan.FromSeconds(guider?.GuideExposureSeconds ?? 0),
            guider is { GuideStarX: { } x, GuideStarY: { } y } ? (x, y) : null,
            guider?.GuideStarSNR,
            guider is { StarProfileH: { IsDefaultOrEmpty: false } h, StarProfileV: { IsDefaultOrEmpty: false } v } ? ([.. h], [.. v]) : null);
    }
}

/// <summary>
/// Calibrates and guides off the Guider tab's loop of the guide camera (#1915): starts the node's guiding run (which replaces the
/// loop and puts it back), watches its state, and hands the tab the calibration, the guide steps and the RMS as a
/// <see cref="GuidingRunView"/>. The loop's own controller (<see cref="GuideLoopController.View"/>) is told to ride out the
/// handover (<see cref="KeepsLoopWatching"/>), so the frames and the Loop button carry on across it.
/// </summary>
public sealed class GuidingRunController(ITimeProvider timeProvider, ILogger logger)
{
    /// <summary>How often the node's state of the run is read.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long after a run ends the loop's watching waits for the node to put the loop back.</summary>
    internal static readonly TimeSpan HandoverGrace = TimeSpan.FromSeconds(15);

    private GuidingRunView? _view;
    private int _starting;
    private long _endedAt;

    /// <summary>Asks the host for a frame; set once by whoever wires the signals.</summary>
    public Action? RedrawRequested { get; set; }

    /// <summary>The node's state of the run, or its memory of the last one, as last read; null before the first answer.</summary>
    public GuidingRunView? View => Volatile.Read(ref _view);

    /// <summary>Whether a guiding run goes on, or is being started.</summary>
    public bool IsRunning => View is { Running: true } || Volatile.Read(ref _starting) == 1;

    /// <summary>
    /// Whether the loop's view should keep watching through a run that has replaced it: while one is going on or being started,
    /// and for <see cref="HandoverGrace"/> after, for the node to put the loop back.
    /// </summary>
    public bool KeepsLoopWatching()
        => IsRunning || (Volatile.Read(ref _endedAt) is not 0 and var ended && timeProvider.GetElapsedTime(ended) < HandoverGrace);

    /// <summary>
    /// Starts the node's run in <paramref name="mode"/> and watches it. Answers the node's refusal in its words (a run going on,
    /// a mount another run holds, PHD2), or null once it runs.
    /// </summary>
    public async Task<string?> StartAsync(NodeConnection node, GuideRunMode mode, GuidingRequestDto request, CancellationToken appToken)
    {
        if (IsRunning)
        {
            return "Guiding is already running";
        }

        Volatile.Write(ref _starting, 1);
        try
        {
            var started = mode is GuideRunMode.Calibrate
                ? await node.Client.CalibrateGuiderAsync(request, appToken).ConfigureAwait(false)
                : await node.Client.StartGuidingAsync(request, appToken).ConfigureAwait(false);
            if (started is not { IsSuccess: true, Value: { } state })
            {
                return started.Error ?? "The node did not start guiding";
            }

            Volatile.Write(ref _endedAt, 0);
            Publish(state);
            _ = Task.Run(() => WatchAsync(node, appToken), CancellationToken.None);
            return null;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    /// <summary>Reads the node's state once: what its guider's calibration is, before any run of this window's.</summary>
    public async Task RefreshAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        if (!IsRunning && await node.Client.GetGuidingAsync(cancellationToken).ConfigureAwait(false) is { IsSuccess: true, Value: { } state })
        {
            Publish(state);
        }
    }

    /// <summary>Asks the node to end the run; it answers once it has, with the loop on its way back. Null once it has, else why not.</summary>
    public async Task<string?> StopAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        var stopped = await node.Client.StopGuidingAsync(cancellationToken).ConfigureAwait(false);
        if (stopped is { IsSuccess: true, Value: { } state })
        {
            Publish(state);
            return null;
        }
        return stopped.IsNotFound ? null : stopped.Error ?? "The node did not stop guiding";
    }

    internal void Publish(GuidingStateDto state)
    {
        var view = GuidingRunView.From(state);
        Volatile.Write(ref _view, view);
        if (!view.Running && Volatile.Read(ref _endedAt) == 0)
        {
            Volatile.Write(ref _endedAt, timeProvider.GetTimestamp());
        }
        RedrawRequested?.Invoke();
    }

    // The run's state, read until the node says it has ended; the last state, with its calibration and failure, stays.
    private async Task WatchAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (await node.Client.GetGuidingAsync(cancellationToken).ConfigureAwait(false) is { IsSuccess: true, Value: { } state })
                {
                    Publish(state);
                    if (!state.Running)
                    {
                        if (state.FailureReason is { } failure)
                        {
                            logger.LogWarning("Guiding ended: {Failure}", failure);
                        }
                        return;
                    }
                }
                await timeProvider.SleepAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is going: the node's run ends by itself once no client watches it.
        }
    }
}
