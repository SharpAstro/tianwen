using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
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

    private const int NoGain = -1;

    // Created lazily on the first captured frame by the capture loop (background thread) and read by the
    // render thread via Volatile -- LiveCameraFrameStream is internally thread-safe.
    private LiveCameraFrameStream? _stream;
    private LiveStackPreviewSource? _source;       // created + driven on the render thread only
    private LiveCameraFrameStream? _sourceStream;  // the stream _source wraps (render-thread); swap on rebuild
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private ICameraDriver? _camera;

    private int _captureActive;                    // 0/1
    private int _framesReceived;
    private long _captureStartTimestamp;

    // Live-control changes staged by the render thread (the panel's Exp / Gain / Size / Pan steppers during
    // capture) and drained + applied by the capture loop after each frame -- so no driver call crosses onto
    // the render thread. 0 / NoGain = nothing pending.
    private long _pendingExposureTicks;
    private int _pendingGain = NoGain;
    private int _pendingRoiW;
    private int _pendingRoiH;
    private int _pendingJogX;
    private int _pendingJogY;

    // ── COM recenter loop (Phase C) ──────────────────────────────────────────────────────────────────
    // Recenter config staged by the render thread (the panel's RECENTER toggles/steppers) and read by the
    // capture loop each frame. Individual Volatile reads/writes: a transiently-mixed read (e.g. new toggle,
    // old deadband) self-corrects on the next frame, so no lock is needed. The mount + pixel scale are set
    // once on Start (single-threaded) via AttachMount and only read during the run.
    private IMountDriver? _mount;
    private double _pixelScaleArcsec = double.NaN;
    private int _autoRecenter;                          // 0/1
    private int _mountJogEnabled;                       // 0/1
    private int _recenterDeadbandPx = 4;
    private int _recenterGainPermille = 500;            // gain * 1000 (int so the cross-thread write is atomic)
    private int _flipRa;                                // 0/1
    private int _flipDec;                               // 0/1

    // Mount-pulse single-flight + cooldown so the (rarely-fired, edge-blocked) coarse nudge never stacks
    // pulses faster than they execute, and never blocks the capture loop (it's fired-and-tracked).
    private int _mountPulseBusy;                        // 0/1, Interlocked CAS gate
    private long _lastMountPulseTimestamp;
    private static readonly TimeSpan MountPulseCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxMountPulse = TimeSpan.FromSeconds(1);

    // Latest recenter telemetry for the panel readout (doubles stored as long bits so the cross-thread
    // write is atomic; the UI reads a coherent-but-possibly-stale value).
    private long _lastOffsetXBits;
    private long _lastOffsetYBits;
    private int _lastActuator;

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

    /// <summary>Last measured disk centre-of-mass offset from the frame centre (px), for the recenter readout.</summary>
    public (double X, double Y) LastComOffset
        => (BitConverter.Int64BitsToDouble(Volatile.Read(ref _lastOffsetXBits)),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref _lastOffsetYBits)));

    /// <summary>Which actuator the most recent recenter frame engaged (for the panel readout).</summary>
    public RecenterActuator LastRecenterActuator => (RecenterActuator)Volatile.Read(ref _lastActuator);

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
        _sourceStream = null;
        _stream?.Dispose();
        Volatile.Write(ref _stream, null);

        // Clear any live-control changes staged before/after the previous run so they don't bleed into this one.
        Volatile.Write(ref _pendingExposureTicks, 0);
        Volatile.Write(ref _pendingGain, NoGain);
        Volatile.Write(ref _pendingRoiW, 0);
        Volatile.Write(ref _pendingRoiH, 0);
        Interlocked.Exchange(ref _pendingJogX, 0);
        Interlocked.Exchange(ref _pendingJogY, 0);

        // Clear stale recenter telemetry / pulse gate (the toggles + deadband persist across runs).
        Interlocked.Exchange(ref _mountPulseBusy, 0);
        Volatile.Write(ref _lastMountPulseTimestamp, 0);
        Volatile.Write(ref _lastActuator, (int)RecenterActuator.None);
        Interlocked.Exchange(ref _lastOffsetXBits, 0);
        Interlocked.Exchange(ref _lastOffsetYBits, 0);

        _camera = camera;
        Interlocked.Exchange(ref _framesReceived, 0);
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
                // Size + layout come from the ACTUAL frame, not the camera's SensorType (a video frame may be
                // mono even on a colour sensor):
                //   >=3 channels                 -> RGB (already-debayered colour, e.g. Canon Live View JPEG)
                //   1 channel + SensorType.RGGB  -> SplitCfa: split each frame into four half-res CFA sub-planes
                //                                   (mirroring SerFrameStream), so the stacker integrates each
                //                                   photosite colour and demosaics once into a colour master.
                //   otherwise                    -> Mono.
                var isBayer = frame.ChannelCount == 1 && frame.ImageMeta.SensorType == SensorType.RGGB;
                var layout = frame.ChannelCount >= 3 ? PlanetaryFrameLayout.Rgb
                    : isBayer ? PlanetaryFrameLayout.SplitCfa
                    : PlanetaryFrameLayout.Mono;
                // SplitCfa sub-planes are half-resolution; the merge+demosaic restores full res.
                var planeW = layout == PlanetaryFrameLayout.SplitCfa ? frame.Width / 2 : frame.Width;
                var planeH = layout == PlanetaryFrameLayout.SplitCfa ? frame.Height / 2 : frame.Height;

                // (Re)build the stream on the first frame AND whenever the frame dimensions change -- a live
                // ROI resize mid-capture (the panel's Size stepper) yields a different-sized frame. The old
                // stream is left for GC (not disposed) so a render-thread stack still in flight on it can't hit
                // a disposed ring; the render thread swaps its preview source to the new stream in Tick().
                if (stream is null || stream.Width != planeW || stream.Height != planeH || stream.Layout != layout)
                {
                    var capacity = Math.Max(_stackOptions.MaxWindowFrames * 2, 1024);
                    var rebuilt = new LiveCameraFrameStream(planeW, planeH, layout, capacity);
                    Volatile.Write(ref _stream, rebuilt);
                    logger.LogInformation(
                        "Planetary capture: {Kind} {W}x{H}x{C} ({Sensor}) -> stream {SW}x{SH} layout {Layout}.",
                        stream is null ? "first frame" : "ROI resized",
                        frame.Width, frame.Height, frame.ChannelCount, frame.ImageMeta.SensorType,
                        rebuilt.Width, rebuilt.Height, layout);
                    stream = rebuilt;
                }

                // A Bayer source is split into its four CFA sub-planes here (the ring stores half-res planes,
                // exactly as SerFrameStream does on load); mono / RGB push through unchanged. The stream is sized
                // to this frame above, so the dimensions always match.
                var toPush = stream.Layout == PlanetaryFrameLayout.SplitCfa ? frame.SplitBayerChannels() : frame;
                stream.Push(toPush, timeProvider.GetUtcNow());
                var received = Interlocked.Increment(ref _framesReceived);

                // COM recenter (Phase C): measure the disk on the just-captured frame (still alive here, before
                // the Release below) and pull it back to the frame centre -- via the ROI window (fast, mount-free)
                // or, when the ROI is edge-blocked and mount jog is enabled, a coarse mount nudge. Runs on the
                // capture loop (off the render thread); a staged ROI jog is drained by ApplyPendingControlsAsync
                // below in the same iteration.
                await MaybeRecenterAsync(camera, frame, token).ConfigureAwait(false);

                // Defensive heartbeat: a steady cadence in the log confirms the capture loop is alive; the last
                // heartbeat before a freeze bounds when the loop (or the thread it feeds) stopped advancing.
                if (received % 250 == 0)
                {
                    logger.LogDebug(
                        "Planetary capture heartbeat: received {Received}, stream {StreamCount}, {Fps:F0} fps, {Dropped} dropped.",
                        received, stream.FrameCount, MeasuredFps, DroppedFrames);
                }

                // The split is a transient (the ring deep-copied it); release it separately from the camera frame.
                if (!ReferenceEquals(toPush, frame))
                {
                    toPush.Release();
                }
                frame.Release();
                state.NeedsRedraw = true;

                // Apply any live-control changes (exposure / gain / ROI size / pan) staged by the render thread.
                await ApplyPendingControlsAsync(camera, token).ConfigureAwait(false);
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

    // ── Live capture controls (render thread stages; the capture loop drains + applies) ──────────────
    // These are the "adjustable while capturing" knobs, mirroring how a real planetary capture lets you tune
    // exposure / gain / ROI on the fly. The render thread only writes the staging fields (no driver call
    // crosses threads); the background loop applies them after the next frame.

    /// <summary>Sets the per-frame exposure for the running stream (takes effect on the next frame).</summary>
    public void SetExposure(TimeSpan exposure)
        => Volatile.Write(ref _pendingExposureTicks, Math.Max(MinExposure.Ticks, exposure.Ticks));

    /// <summary>Sets the gain for the running stream (takes effect on the next frame).</summary>
    public void SetGain(int gain) => Volatile.Write(ref _pendingGain, Math.Max(0, gain));

    /// <summary>Resizes the readout window (ROI) of the running stream; the frame stream rebuilds at the new
    /// size on the next frame (the live stack restarts cleanly at the new framing).</summary>
    public void SetRoiSize(int width, int height)
    {
        Volatile.Write(ref _pendingRoiW, width);
        Volatile.Write(ref _pendingRoiH, height);
    }

    /// <summary>Pans the readout window (ROI) of the running stream by a pixel delta (accumulated until the
    /// next frame) -- the fast, mount-free recenter / framing nudge.</summary>
    public void JogRoi(int dxPixels, int dyPixels)
    {
        Interlocked.Add(ref _pendingJogX, dxPixels);
        Interlocked.Add(ref _pendingJogY, dyPixels);
    }

    // ── COM recenter (Phase C) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches the coupled mount + the OTA pixel scale (arcsec/px) for the recenter loop's coarse mount-jog
    /// fallback. Call before <see cref="Start"/> (single-threaded); pass <c>null</c> / <see cref="double.NaN"/>
    /// to disable the mount path. The pixel scale comes from
    /// <c>CoordinateUtils.PixelScaleArcsec(camera.PixelSizeX, ota.FocalLength)</c>.
    /// </summary>
    public void AttachMount(IMountDriver? mount, double pixelScaleArcsec)
    {
        _mount = mount;
        _pixelScaleArcsec = pixelScaleArcsec;
    }

    /// <summary>
    /// Stages the recenter-loop configuration (render thread). The capture loop reads these per frame, so a
    /// change takes effect on the next frame. <paramref name="gain"/> is the fraction of the measured offset
    /// corrected per frame (0..1]; <paramref name="mountJog"/> opts into the coarse mount nudge when the ROI is
    /// at the sensor edge; <paramref name="flipRa"/>/<paramref name="flipDec"/> invert the (uncalibrated)
    /// pixel->mount-direction mapping.
    /// </summary>
    public void ConfigureRecenter(bool auto, bool mountJog, int deadbandPixels, double gain, bool flipRa = false, bool flipDec = false)
    {
        Volatile.Write(ref _autoRecenter, auto ? 1 : 0);
        Volatile.Write(ref _mountJogEnabled, mountJog ? 1 : 0);
        Volatile.Write(ref _recenterDeadbandPx, Math.Max(0, deadbandPixels));
        Volatile.Write(ref _recenterGainPermille, (int)Math.Round(Math.Clamp(gain, 0.0, 1.0) * 1000.0));
        Volatile.Write(ref _flipRa, flipRa ? 1 : 0);
        Volatile.Write(ref _flipDec, flipDec ? 1 : 0);
    }

    // Measures the disk on the captured frame and acts on the recenter decision. A no-op when auto-recenter is
    // off. Runs on the capture loop; a recenter fault must never kill the capture (logged + swallowed).
    private async Task MaybeRecenterAsync(ICameraDriver camera, Image frame, CancellationToken token)
    {
        if (Volatile.Read(ref _autoRecenter) != 1)
        {
            return;
        }

        try
        {
            // Coarse, every-frame centroid on the luminance proxy (bbox-masked) -- the relative anchor the
            // registration path already uses; cheap on a small planetary ROI.
            var bbox = PlanetaryDisk.BoundingBox(frame);
            var com = PlanetaryDisk.CenterOfMass(frame, bbox);

            var video = camera as IVideoCameraDriver;
            var canJog = video is { CanJogRoi: true };
            var roi = canJog ? video!.VideoRoi : new RoiRect(0, 0, frame.Width, frame.Height);
            var sensorW = camera.CameraXSize > 0 ? camera.CameraXSize : frame.Width;
            var sensorH = camera.CameraYSize > 0 ? camera.CameraYSize : frame.Height;

            var options = new RecenterOptions(
                DeadbandPixels: Volatile.Read(ref _recenterDeadbandPx),
                Gain: Volatile.Read(ref _recenterGainPermille) / 1000.0,
                MountJogEnabled: Volatile.Read(ref _mountJogEnabled) == 1,
                PixelScaleArcsec: _pixelScaleArcsec,
                FlipRa: Volatile.Read(ref _flipRa) == 1,
                FlipDec: Volatile.Read(ref _flipDec) == 1);

            var decision = PlanetaryRecenterController.Decide(
                com, frame.Width, frame.Height, roi, sensorW, sensorH, canJog, options);

            Interlocked.Exchange(ref _lastOffsetXBits, BitConverter.DoubleToInt64Bits(decision.OffsetX));
            Interlocked.Exchange(ref _lastOffsetYBits, BitConverter.DoubleToInt64Bits(decision.OffsetY));
            Volatile.Write(ref _lastActuator, (int)decision.Actuator);

            if (decision.RoiDx != 0 || decision.RoiDy != 0)
            {
                // Stage the pan -- ApplyPendingControlsAsync drains it onto the camera this same iteration.
                JogRoi(decision.RoiDx, decision.RoiDy);
                logger.LogDebug(
                    "Planetary recenter: ROI jog ({Dx},{Dy}) for COM offset ({Ox:F1},{Oy:F1}) px.",
                    decision.RoiDx, decision.RoiDy, decision.OffsetX, decision.OffsetY);
            }

            if (decision.MountRaArcsec != 0.0 || decision.MountDecArcsec != 0.0)
            {
                TryPulseMount(decision, token);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planetary recenter failed for a frame; capture continues.");
        }
    }

    // Fires the coarse mount nudge non-blocking + single-flight: a cooldown bounds how often it can fire, and a
    // CAS gate stops a second nudge while one is in flight, so the rarely-needed mount path never stacks pulses
    // or stalls the high-fps capture loop.
    private void TryPulseMount(RecenterDecision decision, CancellationToken token)
    {
        if (_mount is not { } mount || !mount.CanPulseGuide)
        {
            return;
        }

        if (timeProvider.GetElapsedTime(Volatile.Read(ref _lastMountPulseTimestamp)) < MountPulseCooldown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _mountPulseBusy, 1, 0) != 0)
        {
            return; // a pulse is already running
        }

        Volatile.Write(ref _lastMountPulseTimestamp, timeProvider.GetTimestamp());
        _ = PulseMountAsync(mount, decision, token);
    }

    private async Task PulseMountAsync(IMountDriver mount, RecenterDecision decision, CancellationToken token)
    {
        try
        {
            if (Math.Abs(decision.MountRaArcsec) > 0.0)
            {
                var dir = decision.MountRaArcsec > 0.0 ? GuideDirection.East : GuideDirection.West;
                await MountActions.PulseGuideArcsecAsync(
                    mount, dir, Math.Abs(decision.MountRaArcsec), timeProvider, MaxMountPulse, logger, token)
                    .ConfigureAwait(false);
            }

            if (Math.Abs(decision.MountDecArcsec) > 0.0)
            {
                var dir = decision.MountDecArcsec > 0.0 ? GuideDirection.North : GuideDirection.South;
                await MountActions.PulseGuideArcsecAsync(
                    mount, dir, Math.Abs(decision.MountDecArcsec), timeProvider, MaxMountPulse, logger, token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Planetary recenter mount pulse cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planetary recenter mount pulse failed; capture continues.");
        }
        finally
        {
            Interlocked.Exchange(ref _mountPulseBusy, 0);
        }
    }

    // Drains the staged live-control changes and applies them to the camera. Runs on the capture loop, so the
    // awaits are in the loop's own context (no render-thread driver calls). A control-apply fault must not
    // kill the capture, so non-cancellation exceptions are logged and swallowed.
    private async Task ApplyPendingControlsAsync(ICameraDriver camera, CancellationToken token)
    {
        var video = camera as IVideoCameraDriver;

        // ROI size: NumX/NumY (the streaming driver re-reads them per frame; the loop rebuilds the stream when
        // the next frame's dimensions change).
        var rw = Interlocked.Exchange(ref _pendingRoiW, 0);
        var rh = Interlocked.Exchange(ref _pendingRoiH, 0);
        var expTicks = Interlocked.Exchange(ref _pendingExposureTicks, 0);
        var gain = Interlocked.Exchange(ref _pendingGain, NoGain);
        var jx = Interlocked.Exchange(ref _pendingJogX, 0);
        var jy = Interlocked.Exchange(ref _pendingJogY, 0);

        if (rw <= 0 && rh <= 0 && expTicks <= 0 && gain < 0 && jx == 0 && jy == 0)
        {
            return; // nothing staged
        }

        try
        {
            if (rw > 0 && rh > 0)
            {
                camera.NumX = rw;
                camera.NumY = rh;
            }

            if (video is not null && (expTicks > 0 || gain >= 0))
            {
                await video.ApplyVideoControlsAsync(
                    new VideoCaptureOptions(expTicks > 0 ? new TimeSpan(expTicks) : TimeSpan.Zero, gain >= 0 ? (short)gain : null),
                    token).ConfigureAwait(false);
            }
            else if (gain >= 0)
            {
                // Universal fallback path (no IVideoCameraDriver): gain is still a standard camera setting.
                await camera.SetGainAsync((short)gain, token).ConfigureAwait(false);
            }

            if (video is { CanJogRoi: true } && (jx != 0 || jy != 0))
            {
                await video.JogRoiAsync(jx, jy, token).ConfigureAwait(false);
            }

            // Defensive breadcrumb: these live-control changes are the actions that correlate with a stall, so
            // log exactly what was applied -- the last such line before a freeze pinpoints the trigger.
            logger.LogDebug(
                "Planetary live control applied: roi={RoiW}x{RoiH} exposureTicks={ExpTicks} gain={Gain} jog=({Jx},{Jy}).",
                rw, rh, expTicks, gain, jx, jy);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planetary capture: applying a live control change failed; capture continues.");
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

        // The capture loop rebuilds the stream on a live ROI resize; drop the stale source so it's recreated
        // against the new stream below (ownsStream:false, so disposing the source never touches the stream).
        if (_source is not null && !ReferenceEquals(_sourceStream, stream))
        {
            _source.Dispose();
            _source = null;
        }

        if (_source is null)
        {
            if (stream.FrameCount <= 0)
            {
                return false; // no frame buffered yet -> nothing to display
            }

            _source = new LiveStackPreviewSource(stream, "live://camera", timeProvider, _stackOptions, ownsStream: false, logger: logger);
            _sourceStream = stream;
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
        _sourceStream = null;
        _stream?.Dispose();
        _stream = null;
    }
}
