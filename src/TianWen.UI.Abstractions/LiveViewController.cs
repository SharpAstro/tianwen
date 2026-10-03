using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shows a <b>live view</b> in the Preview mode (P4 of docs/plans/live-session-preview.md, #1111): the node's run streams
/// the OTA's camera and keeps nothing (<c>NodeLiveView</c>); this starts it, sends the exposure and gain as the Preview
/// steppers change them, reads the node's state, and hands the newest frame (<see cref="FrameSources.LiveView"/>) to the
/// preview pane through <see cref="Source"/>. The planetary mode's controller, without the stack.
/// <para>
/// <b>Threading.</b> <see cref="Tick"/> and <see cref="Source"/> are render-thread-only (call <see cref="Tick"/> once per
/// frame the pane draws). The run's loop (state and controls) and the frame reader run in the background and meet the
/// render thread only through one frame handed over at a time.
/// </para>
/// </summary>
public sealed class LiveViewController : IAsyncDisposable
{
    /// <summary>How often the node's state of the run is read, and the staged controls sent.</summary>
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<LiveViewController> _logger;

    // The newest frame read, handed to the render thread by Tick, which copies it into _frame and gives it back.
    private readonly LiveFramePreviewSource _frame = new LiveFramePreviewSource();
    private Image? _pendingFrame;
    private bool _hasFrame; // render thread only

    // What taking a frame in costs the render thread, summed over the last 100 (render thread only).
    private int _taken;
    private TimeSpan _takeTime;

    // The frames shown in the current second, and when it began (render thread only).
    private int _shownInWindow;
    private long _windowStart;

    private CancellationTokenSource? _runCts;
    private Task? _run;
    private int _stopAsked;
    private int _restartShownRate;
    private int _liveOta = -1;
    private LiveViewStateDto? _state;

    // Staged controls, sent by the run's loop: -1 ticks and int.MinValue gain are "nothing staged".
    private long _pendingExposureTicks = -1;
    private int _pendingGain = int.MinValue;

    public LiveViewController(ITimeProvider timeProvider, ILogger<LiveViewController> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Asks the host for a frame: a new live frame arrived, or the node's state of the run changed. Called from the
    /// background, so it only raises a flag; set once by whoever wires the signals (<c>AppSignalHandler</c>).
    /// </summary>
    public Action? RedrawRequested { get; set; }

    /// <summary>The OTA whose camera this view streams, from a start until the node says the run ended; null otherwise.</summary>
    public int? LiveOta => Volatile.Read(ref _liveOta) is >= 0 and var ota ? ota : null;

    /// <summary>True from a start until the node says the run has ended.</summary>
    public bool IsLive => LiveOta is not null;

    /// <summary>The node's state of the run, as last read; null before its first answer.</summary>
    public LiveViewStateDto? State => Volatile.Read(ref _state);

    /// <summary>The live frame for the pane, once one has arrived since the start; null before. Render thread only.</summary>
    public IPreviewSource? Source => _hasFrame ? _frame : null;

    /// <summary>
    /// How many frames a second this window takes in and shows, over the last second: what the eye gets, which a slow
    /// window holds below the camera's rate (<see cref="LiveViewStateDto.FramesPerSecond"/>), since it asks for each.
    /// Render thread only.
    /// </summary>
    public double ShownFps { get; private set; }

    /// <summary>
    /// Starts <paramref name="node"/>'s live view of the OTA in <paramref name="request"/> (it claims the camera and streams
    /// its whole sensor; a camera a run holds is refused, in the run's name) and shows it. Answers the node's refusal in its
    /// words, or null once it runs. <paramref name="appToken"/> ties the watching to the app's lifetime; the node's run goes
    /// on without it until its detach grace.
    /// </summary>
    public async Task<string?> StartAsync(NodeConnection node, LiveViewRequestDto request, CancellationToken appToken)
    {
        if (IsLive)
        {
            return "A live view is already running";
        }

        var started = await node.Client.StartLiveViewAsync(request, appToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return started.Error ?? "The node did not start the live view";
        }

        Interlocked.Exchange(ref _pendingExposureTicks, -1);
        Interlocked.Exchange(ref _pendingGain, int.MinValue);
        Volatile.Write(ref _state, started.Value);
        Volatile.Write(ref _restartShownRate, 1);
        Volatile.Write(ref _stopAsked, 0);
        Volatile.Write(ref _liveOta, request.OtaIndex);
        RedrawRequested?.Invoke();

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        Interlocked.Exchange(ref _runCts, runCts)?.Dispose();
        _run = Task.Run(() => WatchAsync(node, runCts.Token), CancellationToken.None);
        return null;
    }

    /// <summary>A new exposure for the live view going on, sent with the next state read; nothing while none runs.</summary>
    public void SetExposure(TimeSpan exposure)
    {
        if (IsLive)
        {
            Volatile.Write(ref _pendingExposureTicks, exposure.Ticks);
        }
    }

    /// <summary>A new gain (a Canon's ISO) for the live view going on; nothing while none runs.</summary>
    public void SetGain(short gain)
    {
        if (IsLive)
        {
            Volatile.Write(ref _pendingGain, gain);
        }
    }

    // What has changed since the last send, taken, or null when nothing has.
    private LiveViewControlsDto? TakeChanges()
    {
        var ticks = Interlocked.Exchange(ref _pendingExposureTicks, -1);
        var gain = Interlocked.Exchange(ref _pendingGain, int.MinValue);
        if (ticks < 0 && gain == int.MinValue)
        {
            return null;
        }
        return new LiveViewControlsDto
        {
            ExposureMs = ticks >= 0 ? TimeSpan.FromTicks(ticks).TotalMilliseconds : null,
            Gain = gain != int.MinValue ? (short)gain : null,
        };
    }

    // The run's loop: the staged controls sent, the node's state read, the stop asked for, until the node says it ended.
    private async Task WatchAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = ReadFramesAsync(node, reading.Token);
        try
        {
            var stopSent = false;
            while (true)
            {
                if (TakeChanges() is { } controls)
                {
                    var set = await node.Client.SetLiveViewControlsAsync(controls, cancellationToken).ConfigureAwait(false);
                    if (!set.IsSuccess)
                    {
                        _logger.LogWarning("The node did not take the live view's controls: {Error}", set.Error);
                    }
                }
                if (Volatile.Read(ref _stopAsked) == 1 && !stopSent)
                {
                    stopSent = true;
                    await node.Client.StopLiveViewAsync(cancellationToken).ConfigureAwait(false);
                }

                var now = await node.Client.GetLiveViewAsync(cancellationToken).ConfigureAwait(false);
                if (now is { IsSuccess: true, Value: { } state })
                {
                    Volatile.Write(ref _state, state);
                    RedrawRequested?.Invoke();
                    if (!state.Running)
                    {
                        if (state.FailureReason is { } failure)
                        {
                            _logger.LogWarning("The live view ended: {Failure}", failure);
                        }
                        return;
                    }
                }
                else if (now.IsNotFound)
                {
                    return;
                }
                await _timeProvider.SleepAsync(StatePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is going: the node's run ends by itself once no client watches it.
        }
        finally
        {
            await reading.CancelAsync().ConfigureAwait(false);
            try
            {
                await frames.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException || reading.IsCancellationRequested)
            {
                // The reader ends with the watching.
            }
            Volatile.Write(ref _liveOta, -1);
            RedrawRequested?.Invoke();
        }
    }

    // The node's live frames, the newest each time the last has been read (drop-to-latest), each handed to the render
    // thread, the one it replaces given back.
    private async Task ReadFramesAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.LiveView, cancellationToken).ConfigureAwait(false);
        var reader = new FrameReader();
        while (await stream.ReadAsync(reader, cancellationToken).ConfigureAwait(false) is { } next)
        {
            Interlocked.Exchange(ref _pendingFrame, next.Frame)?.Release();
            RedrawRequested?.Invoke();
        }
    }

    /// <summary>
    /// Render-thread drive: copies the newest frame into <see cref="Source"/> and gives it back. True when the pane has a
    /// new frame to upload. A view that is no longer live keeps its last frame until the next start replaces it.
    /// </summary>
    public bool Tick()
    {
        // A new start counts its own rate (the flag is set off the render thread, the counters are read on it).
        if (Interlocked.Exchange(ref _restartShownRate, 0) == 1)
        {
            (ShownFps, _shownInWindow, _windowStart) = (0, 0, 0);
        }
        if (Interlocked.Exchange(ref _pendingFrame, null) is not { } frame)
        {
            return false;
        }
        try
        {
            var start = _timeProvider.GetTimestamp();
            if (!_frame.AcceptFrame(frame, freezeStats: false))
            {
                return false;
            }
            var end = _timeProvider.GetTimestamp();
            _takeTime += _timeProvider.GetElapsedTime(start, end);
            _shownInWindow++;
            var window = _timeProvider.GetElapsedTime(_windowStart, end);
            if (_windowStart == 0)
            {
                (_windowStart, _shownInWindow) = (end, 0);
            }
            else if (window >= TimeSpan.FromSeconds(1))
            {
                ShownFps = _shownInWindow / window.TotalSeconds;
                (_windowStart, _shownInWindow) = (end, 0);
            }
            if (++_taken == 100)
            {
                _logger.LogDebug("Live view: taking a {Width}x{Height}x{Channels} frame in cost the render thread {Ms:F1} ms on average over {Frames}",
                    frame.Width, frame.Height, frame.ChannelCount, _takeTime.TotalMilliseconds / _taken, _taken);
                (_taken, _takeTime) = (0, TimeSpan.Zero);
            }
            _hasFrame = true;
            return true;
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>Asks the node to stop the live view; the view follows it until the node says it has ended.</summary>
    public void Stop() => Volatile.Write(ref _stopAsked, 1);

    /// <summary>
    /// Asks the node to stop and waits for the view to have followed it to its end, bounded by <paramref name="drainTimeout"/>:
    /// once it returns the camera is free for a still (a Canon cannot take one while its Live View runs).
    /// </summary>
    public async Task StopAsync(CancellationToken drainTimeout)
    {
        Stop();
        if (_run is { } run)
        {
            try
            {
                await run.WaitAsync(drainTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Bounded: the node's run ends on its own.
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The watching ends with the app; the node's live view ends by itself once nobody watches it.
        if (Interlocked.Exchange(ref _runCts, null) is { } runCts)
        {
            await runCts.CancelAsync().ConfigureAwait(false);
            if (_run is { } run)
            {
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3), _timeProvider.System);
                try
                {
                    await run.WaitAsync(drain.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Bounded, so a stuck read cannot hang shutdown.
                }
            }
            runCts.Dispose();
        }
        Interlocked.Exchange(ref _pendingFrame, null)?.Release();
    }
}
