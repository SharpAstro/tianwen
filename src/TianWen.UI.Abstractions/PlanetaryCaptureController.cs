using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Drives a <b>live planetary capture</b>: streams frames from a camera in video mode into a
/// <see cref="LiveCameraFrameStream"/>, and exposes a <see cref="LiveStackPreviewSource"/> the 🪐 tab
/// renders (the live rolling-window lucky-imaging stack). This is the capture-driven counterpart of
/// <c>ViewerController</c> (which plays back a SER file): it owns the camera capture loop + the frame
/// stream; the stack / preview / wavelet-sharpen pipeline above the <see cref="IPlanetaryFrameStream"/>
/// seam is reused unchanged.
/// <para>
/// <b>Vendor-neutral.</b> A camera that implements <see cref="IVideoCameraDriver"/> with
/// <see cref="IVideoCameraDriver.CanVideoCapture"/> streams natively; any other
/// <see cref="ICameraDriver"/> falls back to the universal rapid-exposure loop
/// (<see cref="RapidExposureFramesAsync"/>) -- a short-exposure expose/read/repeat. No per-vendor adapter
/// class: the fallback is one helper here (the "avoid duplication" call).
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Start"/> spawns the capture loop on a background task; it only ever calls
/// the thread-safe <see cref="LiveCameraFrameStream.Push"/>. <see cref="Tick"/> and the
/// <see cref="LiveStackPreviewSource"/> it drives are render-thread-only (call <see cref="Tick"/> once per
/// render frame). The two threads meet only at the frame stream, which is internally locked.
/// </para>
/// </summary>
public sealed class PlanetaryCaptureController(
    ViewerState state,
    ITimeProvider timeProvider,
    ILogger<PlanetaryCaptureController> logger,
    RollingWindowOptions? stackOptions = null) : IAsyncDisposable
{
    private static readonly TimeSpan MinExposure = TimeSpan.FromMilliseconds(1);

    private readonly RollingWindowOptions _stackOptions = stackOptions ?? new RollingWindowOptions();
    private readonly TimeSpan _readyPollInterval = TimeSpan.FromMilliseconds(5);

    // Created lazily on the first captured frame by the capture loop (background thread) and read by the
    // render thread via Volatile -- LiveCameraFrameStream is internally thread-safe.
    private LiveCameraFrameStream? _stream;
    private LiveStackPreviewSource? _source;       // created + driven on the render thread only
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private ICameraDriver? _camera;

    private int _captureActive;                    // 0/1
    private int _framesReceived;
    private int _mismatchedFrames;
    private long _captureStartTimestamp;

    /// <summary>True while a capture loop is running.</summary>
    public bool IsCapturing => Volatile.Read(ref _captureActive) == 1;

    /// <summary>The camera the active capture is streaming from (for the recenter loop / telemetry), or null.</summary>
    public ICameraDriver? Camera => _camera;

    /// <summary>
    /// The shared <see cref="ViewerState"/> the capture loop reads/writes (wavelet sharpen, stretch, RAW/STACK).
    /// Exposed so the planetary view widget renders against the SAME state the controller drives, whether it's
    /// hosted as the standalone tab or as the Live Session planetary mode.
    /// </summary>
    public ViewerState ViewerState => state;

    /// <summary>The live-stack preview source for the tab to render, or null before the first frame.</summary>
    public IPreviewSource? Source => _source;

    /// <summary>
    /// The latest display-ready ([0,1]) stacked master as an <see cref="Image"/> for a rect-bounded mini
    /// viewer to display, or null before the first stack. Render-thread only (call from <see cref="Tick"/>'s
    /// thread).
    /// </summary>
    public Image? CurrentMaster => _source?.DisplayMaster;

    /// <summary>True once the live stack has built at least one master (something is displayable).</summary>
    public bool HasMaster => _source?.HasMaster ?? false;

    /// <summary>Total frames pushed into the stream since the current capture started.</summary>
    public int FramesReceived => Volatile.Read(ref _framesReceived);

    /// <summary>Frames the camera/SDK reported dropped (buffer starvation), 0 for cameras that can't report it.</summary>
    public int DroppedFrames => (_camera as IVideoCameraDriver)?.DroppedFrames ?? 0;

    /// <summary>Measured delivered frame rate (frames / elapsed capture time), 0 before the first frame.</summary>
    public double MeasuredFps
    {
        get
        {
            var n = Volatile.Read(ref _framesReceived);
            if (n <= 0)
            {
                return 0.0;
            }

            var elapsed = timeProvider.GetElapsedTime(Volatile.Read(ref _captureStartTimestamp)).TotalSeconds;
            return elapsed > 0 ? n / elapsed : 0.0;
        }
    }

    /// <summary>
    /// Starts streaming from <paramref name="camera"/>. The ROI / sensor type is read from the camera's
    /// current sub-frame config (set the camera's <c>NumX</c>/<c>NumY</c> before calling). No-ops if a
    /// capture is already running. <paramref name="appToken"/> ties the capture to the app lifetime.
    /// </summary>
    public void Start(ICameraDriver camera, VideoCaptureOptions options, CancellationToken appToken)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
        {
            logger.LogInformation("Planetary capture already running; ignoring Start.");
            return;
        }

        // A degenerate 0 ms exposure would spin the capture loop with no pacing; floor it.
        var capture = options.Exposure < MinExposure ? options with { Exposure = MinExposure } : options;

        // Reset any prior capture's source/stream. Start runs on the render thread and the previous loop has
        // already drained (captureActive was 0 when we got here), so touching these is safe.
        _source?.Dispose();
        _source = null;
        _stream?.Dispose();
        Volatile.Write(ref _stream, null);

        _camera = camera;
        Interlocked.Exchange(ref _framesReceived, 0);
        _mismatchedFrames = 0;
        _captureStartTimestamp = timeProvider.GetTimestamp();

        state.IsSequence = true;
        state.SourceFps = (float)(1.0 / Math.Max(capture.Exposure.TotalSeconds, 1e-3));
        state.NeedsTextureUpdate = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var token = _cts.Token;
        _captureTask = Task.Run(() => CaptureLoopAsync(camera, capture, token));

        logger.LogInformation(
            "Planetary capture started: exposure {Exposure}ms, native={Native} (stream sized from the first frame).",
            capture.Exposure.TotalMilliseconds, camera is IVideoCameraDriver { CanVideoCapture: true });
    }

    private async Task CaptureLoopAsync(ICameraDriver camera, VideoCaptureOptions options, CancellationToken token)
    {
        LiveCameraFrameStream? stream = null;
        try
        {
            await foreach (var frame in Frames(camera, options, token).ConfigureAwait(false))
            {
                if (stream is null)
                {
                    // Size + layout come from the ACTUAL first frame, not the camera's SensorType: a video
                    // frame may be mono even on a colour sensor (the fake), and a native colour stream is a
                    // 1-channel Bayer mosaic until Phase D splits CFA. 1 channel -> Mono, >=3 -> RGB.
                    var layout = frame.ChannelCount >= 3 ? PlanetaryFrameLayout.Rgb : PlanetaryFrameLayout.Mono;
                    var capacity = Math.Max(_stackOptions.MaxWindowFrames * 2, 1024);
                    stream = new LiveCameraFrameStream(frame.Width, frame.Height, layout, capacity);
                    Volatile.Write(ref _stream, stream);
                    logger.LogInformation(
                        "Planetary capture: first frame {W}x{H}x{C} -> stream layout {Layout}.",
                        frame.Width, frame.Height, frame.ChannelCount, layout);
                }

                if (frame.Width == stream.Width && frame.Height == stream.Height)
                {
                    stream.Push(frame, timeProvider.GetUtcNow());
                    Interlocked.Increment(ref _framesReceived);
                }
                else if (Interlocked.Increment(ref _mismatchedFrames) == 1)
                {
                    logger.LogWarning(
                        "Planetary capture: dropping frame {W}x{H}; stream is {SW}x{SH}.",
                        frame.Width, frame.Height, stream.Width, stream.Height);
                }

                frame.Release();
                state.NeedsRedraw = true;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Planetary capture loop stopped (cancelled).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Planetary capture loop faulted.");
        }
        finally
        {
            Interlocked.Exchange(ref _captureActive, 0);
        }
    }

    // Native video when the camera supports it; otherwise the universal short-exposure fallback.
    private IAsyncEnumerable<Image> Frames(ICameraDriver camera, VideoCaptureOptions options, CancellationToken token)
        => camera is IVideoCameraDriver { CanVideoCapture: true } video
            ? video.CaptureVideoAsync(options, token)
            : RapidExposureFramesAsync(camera, options, token);

    // Universal fallback: loop short single-shot exposures on any ICameraDriver. CanJogRoi is implicitly
    // false for this path (no IVideoCameraDriver), so the recenter loop uses mount jog only.
    private async IAsyncEnumerable<Image> RapidExposureFramesAsync(
        ICameraDriver camera, VideoCaptureOptions options, [EnumeratorCancellation] CancellationToken token)
    {
        if (options.Gain is { } gain)
        {
            await camera.SetGainAsync(gain, token).ConfigureAwait(false);
        }

        while (!token.IsCancellationRequested)
        {
            await camera.StartExposureAsync(options.Exposure, FrameType.Light, token).ConfigureAwait(false);

            var ready = false;
            while (!ready)
            {
                if (token.IsCancellationRequested)
                {
                    yield break;
                }

                ready = await camera.GetImageReadyAsync(token).ConfigureAwait(false);
                if (!ready)
                {
                    var cancelled = false;
                    try
                    {
                        await timeProvider.SleepAsync(_readyPollInterval, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    if (cancelled)
                    {
                        yield break;
                    }
                }
            }

            if (await camera.GetImageAsync(token).ConfigureAwait(false) is { } image)
            {
                yield return image;
            }
        }
    }

    /// <summary>
    /// Render-thread drive, mirroring <c>ViewerController.TickPlayback</c>'s live-stack steps: push changed
    /// wavelet-sharpen params, publish a finished master, then follow the latest frame. Lazily creates the
    /// preview source on the first frame. Returns true when a freshly-built master was published (the caller
    /// re-uploads the texture). Call once per render frame.
    /// </summary>
    public bool Tick()
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return false;
        }

        if (_source is null)
        {
            if (stream.FrameCount <= 0)
            {
                return false; // no frame buffered yet -> nothing to display
            }

            _source = new LiveStackPreviewSource(stream, "live://camera", timeProvider, _stackOptions, ownsStream: false);
        }

        var live = _source;
        if (state.WaveletDirty)
        {
            live.SetSharpen(state.BuildWaveletOptions());
            state.WaveletDirty = false;
        }

        var published = live.TryPublishMaster();
        live.RequestFollowLatest();

        state.FrameCount = stream.FrameCount;
        state.FrameIndex = live.FrameIndex;
        if (published)
        {
            state.NeedsTextureUpdate = true;
        }

        return published;
    }

    /// <summary>
    /// Render-thread stop: cancels the capture loop (it drains itself in the background) and clears the
    /// sequence flag. Use from a UI signal where awaiting the drain isn't needed; use <see cref="StopAsync"/>
    /// when you must await (tests / shutdown).
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        state.IsSequence = false;
    }

    /// <summary>
    /// Stops the capture loop and waits for it to drain. Safe to call when not capturing.
    /// <para>
    /// <paramref name="drainTimeout"/> <b>bounds</b> the wait for the loop to finish (the wait is NEVER
    /// unbounded): pass a cancelled-on-timeout token (e.g. from a <see cref="CancellationTokenSource"/>
    /// created with a TimeSpan + the injected <see cref="ITimeProvider"/>) so a slow or stuck drain cannot
    /// hang process shutdown. The loop is a background <see cref="Task"/>; on timeout it is abandoned (it
    /// sees the cancellation and unwinds, or is torn down as the process exits). The token is required --
    /// every caller must decide its bound (a long-lived caller can pass a generously-timed one).
    /// </para>
    /// </summary>
    public async Task StopAsync(CancellationToken drainTimeout)
    {
        var cts = _cts;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var task = _captureTask;
        if (task is not null)
        {
            try
            {
                // Bounded by the caller's token (never a raw `await task`); the loop polls its own
                // cancellation and unwinds, so a timeout only fires if it is genuinely stuck.
                await task.WaitAsync(drainTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either the loop cancelled cleanly (OCE from the loop) or the bounded drain elapsed (OCE
                // from WaitAsync) -- both are acceptable here; a still-running loop is abandoned at shutdown.
                logger.LogInformation("Planetary capture stop: loop cancelled or drain timed out.");
            }
        }

        _captureTask = null;
        cts?.Dispose();
        _cts = null;
        state.IsSequence = false;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Bound the capture-loop drain so a slow/stuck loop can't hang process shutdown (Not Responding).
        // The CTS fires off the injected TimeProvider (FakeTimeProvider-controllable in tests), not the raw
        // system clock, per the project's time-provider discipline.
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider.System);
        await StopAsync(drainTimeout.Token).ConfigureAwait(false);

        // Dispose the source first -- DisposeAsync AWAITS any in-flight window stack to drain (bounded, no
        // thread-blocking .Wait()) so it stops reading the stream before we release it -- then the stream we
        // own (ownsStream:false on the source means the source never disposes it).
        if (_source is { } source)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        _source = null;
        _stream?.Dispose();
        _stream = null;
    }
}
