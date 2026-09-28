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
/// Shows this computer's <b>live planetary capture</b>, which its node runs (P6 of docs/plans/hardware-in-the-server.md,
/// #936): the camera loop, the rolling-window stack, the recenter and a recording are the node's (<c>NodePlanetary</c>), so a
/// window that dies or wedges ends none of them. This starts the node's run, sends the panel's live controls as they change
/// (<see cref="Capture"/>), reads the node's telemetry, and shows the masters the node stacks (<c>planetary/master</c>)
/// through the same <see cref="LiveStackPreviewSource"/> a SER playback is shown through, sharpened here as the panel says.
/// <para>
/// <b>Threading.</b> <see cref="Tick"/> and the preview source are render-thread-only (call <see cref="Tick"/> once per
/// render frame). The run's loop (state and controls) and the master stream's reader run in the background and meet the
/// render thread only through <see cref="NodeMasters"/> and a source handed over once per start.
/// </para>
/// </summary>
public sealed class PlanetaryCaptureController : IAsyncDisposable
{
    /// <summary>How often the node's state of the run is read, and the staged controls sent.</summary>
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ViewerState _state;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<PlanetaryCaptureController> _logger;

    private LiveStackPreviewSource? _source;      // render thread only
    private LiveStackPreviewSource? _nextSource;  // a start's source, handed to the render thread by Tick

    // The camera's live frame (planetary/live), for RAW: the newest one read, handed to the render thread by Tick, which
    // copies it into _liveFrame and gives it back. Render thread only, but for the hand-off.
    private readonly LiveFramePreviewSource _liveFrame = new LiveFramePreviewSource();
    private Image? _pendingLiveFrame;
    private bool _hasLiveFrame;
    private CancellationTokenSource? _runCts;
    private Task? _run;
    private int _stopAsked;

    public PlanetaryCaptureController(ViewerState state, ITimeProvider timeProvider, ILogger<PlanetaryCaptureController> logger)
    {
        _state = state;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The panel's side of the capture: its live controls, and the node's telemetry.</summary>
    public NodePlanetaryCapture Capture { get; } = new NodePlanetaryCapture();

    /// <summary>True from a start until the node says the run has ended.</summary>
    public bool IsCapturing => Capture.IsCapturing;

    /// <summary>
    /// The shared <see cref="ViewerState"/> the stack reads/writes (wavelet sharpen, stretch, RAW/STACK). Exposed so the
    /// planetary view widget renders against the SAME state the controller drives.
    /// </summary>
    public ViewerState ViewerState => _state;

    /// <summary>
    /// What the tab renders: the camera's live frame on RAW, the node's rolling stack on STACK (<see cref="ViewerState.ShowStacked"/>),
    /// or null before the first start.
    /// </summary>
    /// <remarks>
    /// RAW is the camera now, at the node's display rate; the stack trails it by its window (500 frames, five seconds at
    /// 92 a second) and by however long a master takes to build. Until 2026-09-28 the view had only the stack: this
    /// computer read planetary/master alone, so RAW showed the stack too and a live view for focusing lagged by seconds.
    /// </remarks>
    public IPreviewSource? Source => !_state.ShowStacked && _hasLiveFrame ? _liveFrame : _source;

    /// <summary>The latest display-ready ([0,1]) master, or null before the first. Render-thread only.</summary>
    public Image? CurrentMaster => _source?.DisplayMaster;

    /// <summary>True once a master is displayable.</summary>
    public bool HasMaster => _source?.HasMaster ?? false;

    /// <summary>
    /// Starts <paramref name="node"/>'s planetary run (it claims the camera, sets the ROI and streams; a camera a run holds
    /// is refused, in the run's name) and shows it. Answers the node's refusal in its words, or null once it runs.
    /// <paramref name="appToken"/> ties the watching to the app's lifetime; the node's run goes on without it.
    /// </summary>
    public async Task<string?> StartAsync(NodeConnection node, PlanetaryRequestDto request, CancellationToken appToken)
    {
        if (IsCapturing)
        {
            return "A planetary capture is already running";
        }

        var started = await node.Client.StartPlanetaryAsync(request, appToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return started.Error ?? "The node did not start the capture";
        }

        Capture.Began();
        if (started.Value is { } first)
        {
            Capture.Update(first);
        }
        Volatile.Write(ref _stopAsked, 0);
        var masters = new NodeMasters();
        Interlocked.Exchange(ref _nextSource, new LiveStackPreviewSource(masters, "node://planetary/master", _timeProvider, _logger))?.Dispose();

        _state.IsSequence = true;
        _state.SourceFps = (float)(1000.0 / Math.Max(request.ExposureMs, 1e-3));
        _state.NeedsTextureUpdate = true;

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        Interlocked.Exchange(ref _runCts, runCts)?.Dispose();
        _run = Task.Run(() => WatchAsync(node, masters, runCts.Token), CancellationToken.None);
        return null;
    }

    // The run's loop: the staged controls sent, the node's state read, the stop asked for, until the node says it ended.
    private async Task WatchAsync(NodeConnection node, NodeMasters masters, CancellationToken cancellationToken)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = ReadMastersAsync(node, masters, reading.Token);
        var live = ReadLiveFramesAsync(node, reading.Token);
        try
        {
            var stopSent = false;
            while (true)
            {
                if (Capture.TakeChanges() is { } controls)
                {
                    var set = await node.Client.SetPlanetaryControlsAsync(controls, cancellationToken).ConfigureAwait(false);
                    if (!set.IsSuccess)
                    {
                        _logger.LogWarning("The node did not take the planetary controls: {Error}", set.Error);
                    }
                }
                if (Volatile.Read(ref _stopAsked) == 1 && !stopSent)
                {
                    stopSent = true;
                    await node.Client.StopPlanetaryAsync(cancellationToken).ConfigureAwait(false);
                }

                var now = await node.Client.GetPlanetaryAsync(cancellationToken).ConfigureAwait(false);
                if (now is { IsSuccess: true, Value: { } state })
                {
                    Capture.Update(state);
                    _state.NeedsRedraw = true;
                    if (!state.Running)
                    {
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
            // The app is going: the node's run goes on, as a node's runs do.
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
            finally
            {
                try
                {
                    await live.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException || reading.IsCancellationRequested)
                {
                    // So does the live frame's.
                }
            }
            Capture.Ended();
            _state.IsSequence = false;
            _state.NeedsRedraw = true;
        }
    }

    // The node's masters, the newest each time the last has been read (drop-to-latest, P5 part 5c), into the view.
    private async Task ReadMastersAsync(NodeConnection node, NodeMasters masters, CancellationToken cancellationToken)
    {
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryMaster, cancellationToken).ConfigureAwait(false);
        var reader = new FrameReader();
        while (await stream.ReadAsync(reader, cancellationToken).ConfigureAwait(false) is { } next)
        {
            masters.Push(next.Frame);
            _state.NeedsRedraw = true;
        }
    }

    // How often the live frame's reader looks again while STACK is on show, when it asks for nothing.
    private static readonly TimeSpan LiveFrameIdlePoll = TimeSpan.FromMilliseconds(100);

    // The camera's live frame, the newest each time the last has been read (drop-to-latest), and only while RAW is on show:
    // a frame nobody sees is one the node need not send. Each is handed to the render thread, the one it replaces given back.
    private async Task ReadLiveFramesAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, cancellationToken).ConfigureAwait(false);
        var reader = new FrameReader();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_state.ShowStacked)
            {
                await _timeProvider.SleepAsync(LiveFrameIdlePoll, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (await stream.ReadAsync(reader, cancellationToken).ConfigureAwait(false) is not { } next)
            {
                return;
            }
            Interlocked.Exchange(ref _pendingLiveFrame, next.Frame)?.Release();
            _state.NeedsRedraw = true;
        }
    }

    /// <summary>
    /// Render-thread drive: takes a new start's source, pushes changed wavelet-sharpen params, publishes a finished master,
    /// then follows the latest the node sent. Returns true when a master was published (the caller re-uploads the texture).
    /// </summary>
    public bool Tick()
    {
        if (Interlocked.Exchange(ref _nextSource, null) is { } next)
        {
            _source?.Dispose();
            _source = next;
            _state.WaveletDirty = true;
            _hasLiveFrame = false; // the last capture's frame is not this one's
        }

        // The newest live frame, copied into the RAW source and given back at once (AcceptFrame leases it for the copy).
        if (Interlocked.Exchange(ref _pendingLiveFrame, null) is { } frame)
        {
            try
            {
                if (_liveFrame.AcceptFrame(frame, freezeStats: false))
                {
                    _hasLiveFrame = true;
                    if (!_state.ShowStacked)
                    {
                        _state.NeedsTextureUpdate = true;
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        }
        if (_source is not { } live)
        {
            return false;
        }

        if (_state.WaveletDirty)
        {
            live.SetSharpen(_state.BuildWaveletOptions());
            _state.WaveletDirty = false;
        }

        var published = live.TryPublishMaster();
        live.RequestFollowLatest();

        _state.FrameCount = Capture.FramesReceived;
        _state.FrameIndex = live.FrameIndex;
        if (published)
        {
            _state.NeedsTextureUpdate = true;
        }
        return published;
    }

    /// <summary>Asks the node to stop the capture; the view follows it until the node says it has ended.</summary>
    public void Stop() => Volatile.Write(ref _stopAsked, 1);

    /// <summary>Asks the node to stop and waits for the view to have followed it to its end, bounded by <paramref name="drainTimeout"/>.</summary>
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
        // The watching ends with the app; the node's capture does not (a window that closes is not a stop).
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
        Interlocked.Exchange(ref _nextSource, null)?.Dispose();
        Interlocked.Exchange(ref _pendingLiveFrame, null)?.Release();
        if (_source is { } source)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        _source = null;
    }
}
