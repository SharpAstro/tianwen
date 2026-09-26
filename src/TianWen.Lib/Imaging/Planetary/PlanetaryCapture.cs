using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A <b>live planetary capture</b>: streams frames from a camera in video mode into a <see cref="LiveCameraFrameStream"/>,
/// recentres the disk as it drifts, and applies the live controls (exposure, gain, the readout window) between frames.
/// The one capture loop for every host (P5 part 5 of docs/plans/hardware-in-the-server.md, #934): the GUI's
/// <c>PlanetaryCaptureController</c> stacks and shows what it streams, and the node runs it for a client. What is
/// done with the stream above the <see cref="IPlanetaryFrameStream"/> seam (the rolling stack, the preview) is the
/// host's.
/// <para>
/// <b>Vendor-neutral.</b> A camera that implements <see cref="IVideoCameraDriver"/> with
/// <see cref="IVideoCameraDriver.CanVideoCapture"/> streams natively; any other <see cref="ICameraDriver"/> falls back
/// to the universal rapid-exposure loop (<see cref="RapidExposureFramesAsync"/>), a short expose, read and repeat.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Start"/> spawns the capture loop on a background task, which calls the thread-safe
/// <see cref="LiveCameraFrameStream.Push"/> and nothing a host reads besides. A live control is STAGED by whoever sets
/// it and applied by the loop after the next frame, so no driver call crosses onto a host's render thread.
/// </para>
/// </summary>
/// <param name="stackOptions">The rolling window the host stacks over, which sizes the ring: twice its frames, at least
/// 1024.</param>
/// <param name="onFrame">Called on the capture loop once each frame is pushed (a GUI asks for a redraw here).</param>
public sealed class PlanetaryCapture(ITimeProvider timeProvider, ILogger logger, RollingWindowOptions? stackOptions = null, Action? onFrame = null)
    : IAsyncDisposable
{
    private static readonly TimeSpan MinExposure = TimeSpan.FromMilliseconds(1);

    private readonly RollingWindowOptions _stackOptions = stackOptions ?? new RollingWindowOptions();
    private readonly TimeSpan _readyPollInterval = TimeSpan.FromMilliseconds(5);

    private const int NoGain = -1;

    // Created on the first captured frame by the capture loop and read by hosts via Volatile; the stream is internally
    // thread-safe.
    private LiveCameraFrameStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private ICameraDriver? _camera;

    private int _captureActive;                    // 0/1
    private int _framesReceived;
    private long _captureStartTimestamp;

    // Live-control changes staged by a host and drained + applied by the capture loop after each frame. 0 / NoGain =
    // nothing pending.
    private long _pendingExposureTicks;
    private int _pendingGain = NoGain;
    private int _pendingRoiW;
    private int _pendingRoiH;
    private int _pendingJogX;
    private int _pendingJogY;

    // ── COM recenter ─────────────────────────────────────────────────────────────────────────────────
    // Recenter config staged by a host and read by the capture loop each frame. Individual Volatile reads/writes: a
    // transiently-mixed read (e.g. new toggle, old deadband) self-corrects on the next frame, so no lock is needed. The
    // mount, the gate over it and the pixel scale are set once before Start (single-threaded) via AttachMount and only
    // read during the run.
    private IMountDriver? _mount;
    private Uri? _mountUri;
    private IDeviceHub? _mountHub;
    private double _pixelScaleArcsec = double.NaN;
    private int _autoRecenter;                          // 0/1
    private int _mountJogEnabled;                       // 0/1
    private int _recenterDeadbandPx = 4;
    private int _recenterGainPermille = 500;            // gain * 1000 (int so the cross-thread write is atomic)
    private int _flipRa;                                // 0/1
    private int _flipDec;                               // 0/1

    // Mount-pulse single-flight + cooldown so the (rarely-fired, edge-blocked) coarse nudge never stacks pulses faster
    // than they execute, and never blocks the capture loop (it's fired-and-tracked).
    private int _mountPulseBusy;                        // 0/1, Interlocked CAS gate
    private long _lastMountPulseTimestamp;
    private static readonly TimeSpan MountPulseCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxMountPulse = TimeSpan.FromSeconds(1);

    // Latest recenter telemetry for the panel readout (doubles stored as long bits so the cross-thread write is atomic;
    // a host reads a coherent-but-possibly-stale value).
    private long _lastOffsetXBits;
    private long _lastOffsetYBits;
    private int _lastActuator;

    // Frame-arrival signal. A host never needs this -- a GUI ticks on its own frame clock and does not care when a
    // CAPTURED frame landed. A test has no such clock, so without it a test must sleep-poll the wall clock, which is the
    // flake CLAUDE.md warns about. Completing one TCS per fully-processed frame lets a test wait for the producer; the
    // frame gate below makes the producer wait for the test, and only the two together are lock-step.
    private TaskCompletionSource _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the NEXT frame has been captured AND fully processed (pushed, recentred, live controls drained) --
    /// never a frame already received, so a caller cannot observe a half-applied iteration. Also completes when the
    /// capture loop ends, so a waiter re-checks its own predicate and sees <see cref="IsCapturing"/> false rather than
    /// hanging. Test seam.
    /// </summary>
    internal Task WaitForNextFrameAsync(CancellationToken cancellationToken)
        => Volatile.Read(ref _frameSignal).Task.WaitAsync(cancellationToken);

    // Releases everything waiting on the current frame and arms the signal for the next one.
    private void SignalFrame()
        => Interlocked
            .Exchange(ref _frameSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

    // The other half of the lock-step. The frame signal above lets a test wait for the PRODUCER; this lets the producer
    // wait for the TEST. Without it the capture loop signals a frame and captures the next at once (the fake clock
    // advances synchronously, so it never yields), and a test thread that falls behind on a slow or loaded runner finds
    // the loop thousands of frames on: the recenter chase ran its window to the sensor edge that way. Null (a host) = no
    // gate at all: the loop enumerates the camera directly, with no wait and no allocation per frame. Set before Start
    // and read once by the loop.
    private SemaphoreSlim? _frameGate;

    /// <summary>
    /// Test seam: from the next <see cref="Start"/> on, the capture loop captures a frame only after a matching
    /// <see cref="StepFrame"/>. Must be armed before <see cref="Start"/>; cancellation still releases a loop parked on
    /// the gate.
    /// </summary>
    internal void ArmFrameGate()
    {
        if (IsCapturing)
        {
            throw new InvalidOperationException("Arm the frame gate before Start; a running loop has already chosen its frame source.");
        }

        _frameGate ??= new SemaphoreSlim(0);
    }

    /// <summary>Test seam: lets an armed capture loop capture exactly one more frame.</summary>
    internal void StepFrame()
        => (_frameGate ?? throw new InvalidOperationException("StepFrame needs ArmFrameGate first.")).Release();

    // Waits on the gate before asking the camera for each frame, so a frame is CAPTURED (and the fake's clock advanced)
    // only when a step allows it. A cancelled token faults the wait with an OCE, which the capture loop already treats
    // as a stop.
    private static async IAsyncEnumerable<Image> GatedFramesAsync(
        IAsyncEnumerable<Image> frames, SemaphoreSlim gate, [EnumeratorCancellation] CancellationToken token)
    {
        await using var enumerator = frames.GetAsyncEnumerator(token);
        while (true)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    /// <summary>True while a capture loop is running.</summary>
    public bool IsCapturing => Volatile.Read(ref _captureActive) == 1;

    /// <summary>The camera the active capture is streaming from, or null.</summary>
    public ICameraDriver? Camera => _camera;

    /// <summary>
    /// The frame stream the capture fills, or null before its first frame. REPLACED when the frames' size or layout
    /// changes (a live ROI resize), so a host holding one compares it with this on every read and follows the new one.
    /// </summary>
    public LiveCameraFrameStream? Stream => Volatile.Read(ref _stream);

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
    /// Starts streaming from <paramref name="camera"/>. The ROI / sensor type is read from the camera's current sub-frame
    /// config (set the camera's <c>NumX</c>/<c>NumY</c> before calling). Refuses, and gives the claim back, when a capture
    /// is already running. <paramref name="token"/> bounds the capture's life (a host's own lifetime).
    /// </summary>
    /// <param name="claim">The claim on the camera (owner "planetary capture"), taken by the host so a capture is refused
    /// while another run holds it (P0c item 2 of docs/plans/hardware-in-the-server.md). Owned from here: released when the
    /// capture ends, or at once when one is already running.</param>
    /// <returns>Whether this started a capture.</returns>
    public bool Start(ICameraDriver camera, VideoCaptureOptions options, CancellationToken token, DeviceLeaseSet? claim = null)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
        {
            logger.LogInformation("Planetary capture already running; ignoring Start.");
            claim?.Dispose();
            return false;
        }

        // A degenerate 0 ms exposure would spin the capture loop with no pacing; floor it.
        var capture = options.Exposure < MinExposure ? options with { Exposure = MinExposure } : options;

        // The previous capture's stream goes: its loop has drained (captureActive was 0 when we got here).
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

        // Re-arm the frame signal: the previous run's was completed when its loop ended, and a waiter must never be
        // released by the run before the one it is watching.
        Interlocked.Exchange(ref _frameSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        _camera = camera;
        Interlocked.Exchange(ref _framesReceived, 0);
        _captureStartTimestamp = timeProvider.GetTimestamp();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loopToken = _cts.Token;
        _captureTask = Task.Run(() => CaptureLoopAsync(camera, capture, claim, loopToken));

        logger.LogInformation(
            "Planetary capture started: exposure {Exposure}ms, native={Native} (stream sized from the first frame).",
            capture.Exposure.TotalMilliseconds, camera is IVideoCameraDriver { CanVideoCapture: true });
        return true;
    }

    private async Task CaptureLoopAsync(ICameraDriver camera, VideoCaptureOptions options, DeviceLeaseSet? claim, CancellationToken token)
    {
        LiveCameraFrameStream? stream = null;
        try
        {
            var frames = Frames(camera, options, token);
            if (_frameGate is { } gate)
            {
                frames = GatedFramesAsync(frames, gate, token);
            }

            await foreach (var frame in frames.ConfigureAwait(false))
            {
                // Size + layout come from the ACTUAL frame, not the camera's SensorType (a video frame may be mono even
                // on a colour sensor):
                //   >=3 channels                 -> RGB (already-debayered colour, e.g. Canon Live View JPEG)
                //   1 channel + SensorType.RGGB  -> SplitCfa: split each frame into four half-res CFA sub-planes
                //                                   (mirroring SerFrameStream), so the stacker integrates each photosite
                //                                   colour and demosaics once into a colour master.
                //   otherwise                    -> Mono.
                var isBayer = frame.ChannelCount == 1 && frame.ImageMeta.SensorType == SensorType.RGGB;
                var layout = frame.ChannelCount >= 3 ? PlanetaryFrameLayout.Rgb
                    : isBayer ? PlanetaryFrameLayout.SplitCfa
                    : PlanetaryFrameLayout.Mono;
                // SplitCfa sub-planes are half-resolution; the merge+demosaic restores full res.
                var planeW = layout == PlanetaryFrameLayout.SplitCfa ? frame.Width / 2 : frame.Width;
                var planeH = layout == PlanetaryFrameLayout.SplitCfa ? frame.Height / 2 : frame.Height;

                // (Re)build the stream on the first frame AND whenever the frame dimensions change -- a live ROI resize
                // mid-capture yields a different-sized frame. The old stream is left for GC (not disposed) so a stack
                // still in flight on it can't hit a disposed ring; a host swaps to the new stream on its next read.
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

                // A Bayer source is pushed WHOLE: the ring splits it into its four CFA sub-planes itself (it stores
                // half-res planes, exactly as SerFrameStream does on load), straight into recycled planes. Mono / RGB
                // push through unchanged. The stream is sized to this frame above, so the dimensions always match.
                stream.Push(frame, timeProvider.GetUtcNow());
                var received = Interlocked.Increment(ref _framesReceived);

                // COM recenter: measure the disk on the just-captured frame (still alive here, before the Release
                // below) and pull it back to the frame centre -- via the ROI window (fast, mount-free) or, when the ROI
                // is edge-blocked and mount jog is enabled, a coarse mount nudge. A staged ROI jog is drained by
                // ApplyPendingControlsAsync below in the same iteration.
                await MaybeRecenterAsync(camera, frame, token).ConfigureAwait(false);

                // Defensive heartbeat: a steady cadence in the log confirms the capture loop is alive; the last
                // heartbeat before a freeze bounds when the loop (or the thread it feeds) stopped advancing.
                if (received % 250 == 0)
                {
                    logger.LogDebug(
                        "Planetary capture heartbeat: received {Received}, stream {StreamCount}, {Fps:F0} fps, {Dropped} dropped.",
                        received, stream.FrameCount, MeasuredFps, DroppedFrames);
                }

                // The ring deep-copied the frame (and split it, for a Bayer source), so the camera's buffer goes back now.
                frame.Release();
                onFrame?.Invoke();

                // Apply any live-control changes (exposure / gain / ROI size / pan) staged by a host.
                await ApplyPendingControlsAsync(camera, token).ConfigureAwait(false);

                // Last thing in the iteration, so a waiter observes a fully-settled frame: pushed, recentred, and with
                // this frame's staged ROI jog already on the camera.
                SignalFrame();
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
            // The camera is free again once the loop is done with it.
            claim?.Dispose();
            Interlocked.Exchange(ref _captureActive, 0);

            // Release anyone waiting on a frame that will now never arrive. Completed in place WITHOUT re-arming (unlike
            // the per-frame signal), so this is sticky: every later wait returns at once too, and a stopped or faulted
            // loop surfaces as a failed predicate rather than as a hang. Start arms a fresh signal for the next run.
            Volatile.Read(ref _frameSignal).TrySetResult();
        }
    }

    // Native video when the camera supports it; otherwise the universal short-exposure fallback.
    private IAsyncEnumerable<Image> Frames(ICameraDriver camera, VideoCaptureOptions options, CancellationToken token)
        => camera is IVideoCameraDriver { CanVideoCapture: true } video
            ? video.CaptureVideoAsync(options, token)
            : RapidExposureFramesAsync(camera, options, token);

    // Universal fallback: loop short single-shot exposures on any ICameraDriver. CanJogRoi is implicitly false for this
    // path (no IVideoCameraDriver), so the recenter loop uses mount jog only.
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

    // ── Live capture controls (a host stages; the capture loop drains + applies) ─────────────────────────────────
    // The "adjustable while capturing" knobs, mirroring how a real planetary capture lets you tune exposure / gain / ROI
    // on the fly. A host only writes the staging fields (no driver call crosses threads); the loop applies them after
    // the next frame.

    /// <summary>Sets the per-frame exposure for the running stream (takes effect on the next frame).</summary>
    public void SetExposure(TimeSpan exposure)
        => Volatile.Write(ref _pendingExposureTicks, Math.Max(MinExposure.Ticks, exposure.Ticks));

    /// <summary>Sets the gain for the running stream (takes effect on the next frame).</summary>
    public void SetGain(int gain) => Volatile.Write(ref _pendingGain, Math.Max(0, gain));

    /// <summary>Resizes the readout window (ROI) of the running stream; the frame stream rebuilds at the new size on the
    /// next frame (the live stack restarts cleanly at the new framing).</summary>
    public void SetRoiSize(int width, int height)
    {
        Volatile.Write(ref _pendingRoiW, width);
        Volatile.Write(ref _pendingRoiH, height);
    }

    /// <summary>Pans the readout window (ROI) of the running stream by a pixel delta (accumulated until the next frame)
    /// -- the fast, mount-free recenter / framing nudge.</summary>
    public void JogRoi(int dxPixels, int dyPixels)
    {
        Interlocked.Add(ref _pendingJogX, dxPixels);
        Interlocked.Add(ref _pendingJogY, dyPixels);
    }

    // ── COM recenter ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches the coupled mount + the OTA pixel scale (arcsec/px) for the recenter loop's coarse mount-jog fallback.
    /// Call before <see cref="Start"/> (single-threaded); pass <c>null</c> / <see cref="double.NaN"/> to disable the
    /// mount path. The capture claims only the camera, so each nudge asks the ownership gate over
    /// <paramref name="mountUri"/> first and is dropped while another run or a job holds the mount.
    /// </summary>
    public void AttachMount(IMountDriver? mount, Uri? mountUri, IDeviceHub? hub, double pixelScaleArcsec)
    {
        _mount = mount;
        _mountUri = mountUri;
        _mountHub = hub;
        _pixelScaleArcsec = pixelScaleArcsec;
    }

    /// <summary>
    /// Stages the recenter-loop configuration. The capture loop reads these per frame, so a change takes effect on the
    /// next frame. <paramref name="gain"/> is the fraction of the measured offset corrected per frame (0..1];
    /// <paramref name="mountJog"/> opts into the coarse mount nudge when the ROI is at the sensor edge;
    /// <paramref name="flipRa"/>/<paramref name="flipDec"/> invert the (uncalibrated) pixel->mount-direction mapping.
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

    // Measures the disk on the captured frame and acts on the recenter decision. A no-op when auto-recenter is off.
    // Runs on the capture loop; a recenter fault must never kill the capture (logged + swallowed).
    private async Task MaybeRecenterAsync(ICameraDriver camera, Image frame, CancellationToken token)
    {
        if (Volatile.Read(ref _autoRecenter) != 1)
        {
            return;
        }

        try
        {
            // Coarse, every-frame centroid on the luminance proxy (bbox-masked) -- the relative anchor the registration
            // path already uses; cheap on a small planetary ROI.
            var bbox = PlanetaryDisk.BoundingBox(frame);
            var com = PlanetaryDisk.CenterOfMass(frame, bbox);

            var video = camera as IVideoCameraDriver;
            var canJog = video is { CanJogRoi: true };
            // Re-stated as the pattern rather than read back off canJog: the compiler narrows `video` inside the test,
            // which the bool cannot carry.
            var roi = video is { CanJogRoi: true }
                ? video.VideoRoi
                : new RoiRect(0, 0, frame.Width, frame.Height);
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

    // Fires the coarse mount nudge non-blocking + single-flight: a cooldown bounds how often it can fire, and a CAS gate
    // stops a second nudge while one is in flight, so the rarely-needed mount path never stacks pulses or stalls the
    // high-fps capture loop. The mount is not the capture's, so the ownership gate is asked first.
    private void TryPulseMount(RecenterDecision decision, CancellationToken token)
    {
        if (_mount is not { } mount || !mount.CanPulseGuide)
        {
            return;
        }

        if (_mountUri is { } mountUri && DeviceOwnershipGate.Evaluate(_mountHub, mountUri, DeviceAction.Actuate) is { Allowed: false } verdict)
        {
            logger.LogDebug("Planetary recenter mount pulse skipped: {Reason}", verdict.Describe());
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
                await MountNudge.PulseArcsecAsync(mount, dir, Math.Abs(decision.MountRaArcsec), MaxMountPulse, logger, token)
                    .ConfigureAwait(false);
            }

            if (Math.Abs(decision.MountDecArcsec) > 0.0)
            {
                var dir = decision.MountDecArcsec > 0.0 ? GuideDirection.North : GuideDirection.South;
                await MountNudge.PulseArcsecAsync(mount, dir, Math.Abs(decision.MountDecArcsec), MaxMountPulse, logger, token)
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

    // Drains the staged live-control changes and applies them to the camera. Runs on the capture loop, so the awaits
    // are in the loop's own context (no host-thread driver calls). A control-apply fault must not kill the capture, so
    // non-cancellation exceptions are logged and swallowed.
    private async Task ApplyPendingControlsAsync(ICameraDriver camera, CancellationToken token)
    {
        var video = camera as IVideoCameraDriver;

        // ROI size: NumX/NumY (the streaming driver re-reads them per frame; the loop rebuilds the stream when the next
        // frame's dimensions change).
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

            // Defensive breadcrumb: these live-control changes are the actions that correlate with a stall, so log
            // exactly what was applied -- the last such line before a freeze pinpoints the trigger.
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

    /// <summary>Cancels the capture loop, which drains itself in the background.</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Stops the capture loop and waits for it to drain. Safe to call when not capturing.
    /// <para>
    /// <paramref name="drainTimeout"/> <b>bounds</b> the wait for the loop to finish (the wait is NEVER unbounded): pass a
    /// cancelled-on-timeout token so a slow or stuck drain cannot hang shutdown. On timeout the loop is abandoned (it sees
    /// the cancellation and unwinds, or is torn down as the process exits).
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
                // Bounded by the caller's token (never a raw `await task`); the loop polls its own cancellation and
                // unwinds, so a timeout only fires if it is genuinely stuck.
                await task.WaitAsync(drainTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either the loop cancelled cleanly (OCE from the loop) or the bounded drain elapsed (OCE from
                // WaitAsync) -- both are acceptable here; a still-running loop is abandoned at shutdown.
                logger.LogInformation("Planetary capture stop: loop cancelled or drain timed out.");
            }
        }

        _captureTask = null;
        cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Stops the capture within a bounded drain and disposes its stream. A host that stacks the stream disposes what
    /// reads it FIRST, so nothing reads a released ring.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Bound the capture-loop drain so a slow/stuck loop can't hang shutdown. The CTS fires off the injected
        // TimeProvider (FakeTimeProvider-controllable in tests), not the raw system clock.
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider.System);
        await StopAsync(drainTimeout.Token).ConfigureAwait(false);

        _stream?.Dispose();
        _stream = null;
    }
}
