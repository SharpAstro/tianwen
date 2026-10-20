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
    // The node the capture on show runs on, from its start until the run ends: where Record and its Stop go, never the view's
    // command target, since a control of the run on screen goes to its own node (CLAUDE.md, Remote Rigs; review round 1 of
    // #1610, Q1). Written by a start and by the run's end, read by a record or a stop, each on its own thread.
    private NodeConnection? _node;

    // The live master's Derive: the derived sharpening's gains, seeded into the wavelet sliders (#1159), one with the viewer's.
    private readonly WaveletDerivation _derivation = new WaveletDerivation();

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
    public IPreviewSource? Source => Reframe is not null && _hasStill ? _still
        : !_state.ShowStacked && _hasLiveFrame ? _liveFrame : _source;

    // The full-sensor still a reframe shows (#1033): handed from the request's thread, copied into _still on the render thread.
    private readonly LiveFramePreviewSource _still = new LiveFramePreviewSource();
    private Image? _pendingStill;
    private bool _hasStill;
    private PlanetaryReframe? _reframe;

    /// <summary>The app's clock now, <c>TIANWEN_NOW</c> included: the instant the panel's rotation light reads a planet at (#1340).</summary>
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <summary>The node the capture on show runs on; null when none runs.</summary>
    internal NodeConnection? Node => Volatile.Read(ref _node);

    /// <summary>The reframe going on (#1033): the still, the live window and the proposal; null when none is.</summary>
    public PlanetaryReframe? Reframe => Volatile.Read(ref _reframe);

    /// <summary>
    /// Shows <paramref name="still"/>, CONSUMED, in place of the stream, with the window the stream ran at and a proposal to move
    /// on it (#1033). Any thread: the still is copied in on the render thread's next tick.
    /// </summary>
    public void BeginReframe(Image still, RoiRect liveWindow, RoiConstraints rules)
    {
        Interlocked.Exchange(ref _pendingStill, still)?.Release();
        Volatile.Write(ref _reframe, new PlanetaryReframe(still.Width, still.Height, liveWindow, rules));
        _state.NeedsTextureUpdate = true;
        _state.NeedsRedraw = true;
    }

    /// <summary>Back to the stream, the still let go: once the node has moved the window, or the operator cancelled.</summary>
    public void EndReframe()
    {
        Volatile.Write(ref _reframe, null);
        _state.NeedsTextureUpdate = true;
        _state.NeedsRedraw = true;
    }

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
            Capture.UpdateFromAnswer(first);
        }
        Volatile.Write(ref _stopAsked, 0);
        var masters = new NodeMasters();
        Interlocked.Exchange(ref _nextSource, new LiveStackPreviewSource(masters, "node://planetary/master", _timeProvider, _logger))?.Dispose();

        _state.IsSequence = true;
        _state.SourceFps = (float)(1000.0 / Math.Max(request.ExposureMs, 1e-3));
        Volatile.Write(ref _settingsSaid, null);
        Volatile.Write(ref _recordingSaid, null);
        _state.NeedsTextureUpdate = true;

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        Interlocked.Exchange(ref _runCts, runCts)?.Dispose();
        Volatile.Write(ref _node, node);
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

                // Stamped before it is sent: a record's answer that arrives meanwhile is newer than this poll's.
                var stamp = Capture.PollStamp;
                var now = await node.Client.GetPlanetaryAsync(cancellationToken).ConfigureAwait(false);
                if (now is { IsSuccess: true, Value: { } state })
                {
                    Capture.UpdateFromPoll(state, stamp);
                    // The transport bar's rate is the current one, never the start's exposure turned into a rate.
                    if (state.CurrentFramesPerSecond is > 0 and var current)
                    {
                        _state.SourceFps = (float)current;
                    }
                    SayIfTheSettingsChanged(state);
                    SayIfARecordingEnded(state);
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
            // Before the end is seen: a start, which waits for it, then writes its own node, never one this clears.
            Volatile.Write(ref _node, null);
            Capture.Ended();
            _state.IsSequence = false;
            _state.NeedsRedraw = true;
        }
    }

    // The settings last said, in words: the first the node applies is the start's, which its notice said already.
    private string? _settingsSaid;

    /// <summary>
    /// Raised with the settings in words (<see cref="SettingsInWords"/>) each time the node applies others than it ran at: a
    /// live change of exposure, gain or window, said from what the NODE applied, never from what a click staged (#1619). On the
    /// watching's thread.
    /// </summary>
    public event Action<string>? SettingsApplied;

    /// <summary>"640x360, 5 ms, gain 110": what a capture runs at, as its state says; null before the node has applied any.</summary>
    public static string? SettingsInWords(PlanetaryStateDto state)
        => state.ExposureMs is { } exposureMs
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{state.RoiWidth}x{state.RoiHeight}, {exposureMs:0.##} ms{(state.Gain is { } gain ? $", gain {gain}" : "")}")
            : null;

    private void SayIfTheSettingsChanged(PlanetaryStateDto state)
    {
        if (SettingsInWords(state) is not { } words)
        {
            return;
        }
        var said = Interlocked.Exchange(ref _settingsSaid, words);
        if (said is not null && said != words)
        {
            SettingsApplied?.Invoke(words);
        }
    }

    // The recording whose end was last said, by its file: each ends once, and the poll sees a written one many times.
    private string? _recordingSaid;

    /// <summary>
    /// Raised once a recording on the node's capture has ENDED and its file is whole, with what it came to in words
    /// (<see cref="RecordingEndedInWords"/>): the start's notice said "Recording 60 s to ...", which stayed on the status line
    /// after the file was done. On the watching's thread.
    /// </summary>
    public event Action<string>? RecordingEnded;

    /// <summary>
    /// "Recorded 10537 frames to Luminance_....ser, 0 dropped", or why it failed: short enough for the status line, since the
    /// panel's RECORD section says why it ended.
    /// </summary>
    public static string RecordingEndedInWords(PlanetaryRecordingDto recording)
    {
        var file = System.IO.Path.GetFileName(recording.Path);
        return recording.FailureReason is { } failed
            ? $"Recording to {file} failed: {failed}"
            : $"Recorded {recording.FramesWritten} frames to {file}, {recording.FramesDropped} dropped";
    }

    private void SayIfARecordingEnded(PlanetaryStateDto state)
    {
        if (state.Recording is not { Recording: false } ended || !(ended.Written || ended.FailureReason is not null))
        {
            return;
        }
        if (Interlocked.Exchange(ref _recordingSaid, ended.Path) != ended.Path)
        {
            RecordingEnded?.Invoke(RecordingEndedInWords(ended));
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

    private int _liveFramesRead;
    private int _liveFramesFromSharedMemory;

    /// <summary>Live frames the reader has taken from the node since this view was made, over every capture it watched.</summary>
    internal int LiveFramesRead => Volatile.Read(ref _liveFramesRead);

    /// <summary>Of <see cref="LiveFramesRead"/>, those that came through shared memory (P4b), not the socket.</summary>
    internal int LiveFramesFromSharedMemory => Volatile.Read(ref _liveFramesFromSharedMemory);

    // The camera's live frame, the newest each time the last has been read (drop-to-latest), and only while RAW is on show:
    // a frame nobody sees is one the node need not send. Each is handed to the render thread, the one it replaces given back.
    private async Task ReadLiveFramesAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, cancellationToken).ConfigureAwait(false);
        var reader = new FrameReader();
        var throughSlots = stream.FramesFromSharedMemory;
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
            Interlocked.Increment(ref _liveFramesRead);
            if (stream.FramesFromSharedMemory != throughSlots)
            {
                throughSlots = stream.FramesFromSharedMemory;
                Interlocked.Increment(ref _liveFramesFromSharedMemory);
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
            // The limb a derivation fitted belongs to the capture it was fitted on (#1201).
            _state.ForgetDerivation(switchOff: false);
            _hasLiveFrame = false; // the last capture's frame is not this one's
        }

        // A reframe's still, copied into its own source and given back at once.
        if (Interlocked.Exchange(ref _pendingStill, null) is { } still)
        {
            try
            {
                _hasStill = _still.AcceptFrame(still, freezeStats: true);
                _state.NeedsTextureUpdate = true;
            }
            finally
            {
                still.Release();
            }
        }
        if (Reframe is null)
        {
            _hasStill = false;
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

        // A finished derivation seeds the sliders before their params are pushed; a Derive asked for starts one over the live master.
        _derivation.Tick(_state, live, capturePath: null, _timeProvider.GetUtcNow(), _logger);
        if (_state.WaveletDirty)
        {
            live.SetSharpen(_state.BuildWaveletOptions(), _state.WaveletLimb, _state.PlanetaryLook);
            _state.WaveletDirty = false;
        }

        var published = live.TryPublishMaster();
        live.RequestFollowLatest();

        // Both in the camera's frames (#1619): the index was the master's over the ring, beside a count of camera frames.
        _state.FrameCount = Capture.FramesReceived;
        _state.FrameIndex = Math.Max(0, Capture.FramesReceived - 1);
        if (published)
        {
            _state.NeedsTextureUpdate = true;
        }
        return published;
    }

    /// <summary>
    /// Asks the node the capture on show runs on to record it to a SER file for <paramref name="duration"/> (#1344): the node
    /// writes it and finishes it unwatched, and the state this view reads shows it (<see cref="NodePlanetaryCapture.Recording"/>).
    /// Answers with the recording as the node's answer shows it, or the refusal in words (no capture on show, one already
    /// recording, a duration that is not positive).
    /// </summary>
    public async Task<PlanetaryRecordAnswer> RecordAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _node) is not { } node)
        {
            return PlanetaryRecordAnswer.Refused(NoCaptureToRecord);
        }
        var request = new PlanetaryRecordRequestDto { DurationSeconds = duration.TotalSeconds };
        var started = await node.Client.StartPlanetaryRecordingAsync(request, cancellationToken).ConfigureAwait(false);
        return started.IsSuccess
            ? Show(started.Value)
            : PlanetaryRecordAnswer.Refused(started.Error ?? "The node did not start the recording");
    }

    /// <summary>
    /// Asks the node the capture on show runs on to end the recording going on sooner than its duration; the capture goes on.
    /// Answers with the recording as the node's answer shows it, ended, or the refusal in words (no recording being made).
    /// </summary>
    public async Task<PlanetaryRecordAnswer> StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _node) is not { } node)
        {
            return PlanetaryRecordAnswer.Refused(NoRecordingToStop);
        }
        var stopped = await node.Client.StopPlanetaryRecordingAsync(cancellationToken).ConfigureAwait(false);
        return stopped.IsSuccess
            ? Show(stopped.Value)
            : PlanetaryRecordAnswer.Refused(stopped.Error ?? "The node did not stop the recording");
    }

    // The node's own words for the same refusals, said here when no capture is on show to ask it.
    internal const string NoCaptureToRecord = "No planetary capture is running to record";
    internal const string NoRecordingToStop = "No recording is being made";

    // The node's answer to a record or a stop it took is its newest state: shown at once, and never undone by a poll sent before
    // it (NodePlanetaryCapture.UpdateFromAnswer); the caller reads the recording off the answer itself, not the state shown.
    private PlanetaryRecordAnswer Show(PlanetaryStateDto? state)
    {
        if (state is not null)
        {
            Capture.UpdateFromAnswer(state);
        }
        _state.NeedsRedraw = true;
        return new PlanetaryRecordAnswer(state?.Recording, Refusal: null);
    }

    /// <summary>
    /// How long the recording going on has left by this computer's clock, which <c>TIANWEN_NOW</c> anchors as it anchors the
    /// node's; zero when none is being made, and for one that ends at a number of frames (a sequence's step, #1340).
    /// </summary>
    public TimeSpan RecordingTimeLeft => Capture.Recording is { Recording: true, EndsUtc: { } ends }
        ? TimeSpan.FromTicks(Math.Max(0, (ends - _timeProvider.GetUtcNow()).Ticks))
        : TimeSpan.Zero;

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
        _derivation.Dispose();
        if (_source is { } source)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        _source = null;
    }

    /// <summary>
    /// The telescope the derived sharpening of this capture's masters assumes, from the profile's OTA <paramref name="otaIndex"/>: its
    /// aperture and design, the owner's choice of 2026-10-02 (the telescope from the profile). Leaves the panel's as it is where the
    /// profile gives no aperture.
    /// </summary>
    public void SeedTelescope(ProfileData? profile, int otaIndex)
    {
        if (profile is { } data && (uint)otaIndex < (uint)data.OTAs.Length && data.OTAs[otaIndex] is { Aperture: > 0 } ota)
        {
            _state.PlanetaryApertureMm = ota.Aperture;
            _state.PlanetaryDesign = ota.OpticalDesign;
        }
    }
}

/// <summary>
/// A record or its stop as the capture's node answered it (<see cref="PlanetaryCaptureController.RecordAsync"/>): the recording
/// its answer shows, or why it was refused, in words.
/// </summary>
public readonly record struct PlanetaryRecordAnswer(PlanetaryRecordingDto? Recording, string? Refusal)
{
    public static PlanetaryRecordAnswer Refused(string refusal) => new PlanetaryRecordAnswer(null, refusal);
}
