using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What a planetary capture is asked to stream: OTA <paramref name="OtaIndex"/>'s camera at <paramref name="Exposure"/>
/// per frame, <paramref name="Gain"/> (null keeps the camera's), through a readout window of
/// <paramref name="RoiWidth"/> x <paramref name="RoiHeight"/>, snapped to the camera's ROI rule.
/// </summary>
/// <param name="RoiWidth">The readout window's width; zero, with <paramref name="RoiHeight"/> zero, for the whole sensor
/// (<see cref="PlanetaryCapture.ConfigureRoi"/>), which is what a live view streams.</param>
/// <param name="BitDepth">The depth the camera streams in (<see cref="VideoCaptureOptions.BitDepth"/>); null for its own.</param>
/// <param name="HighSpeed">The camera's high-speed readout (<see cref="VideoCaptureOptions.HighSpeedMode"/>); null for on.</param>
/// <param name="Bin">The binning, which only a whole-sensor window takes (a live view's, at the Preview's binning); a
/// planetary window is always unbinned.</param>
/// <param name="GuideCamera">Streams the profile's guide camera (<see cref="ProfileData.GuiderCamera"/>) instead of an OTA's
/// (<paramref name="OtaIndex"/> is then ignored): the Guider tab's loop (#1915). Refused where the guider owns its camera (PHD2).</param>
public readonly record struct PlanetaryCaptureRequest(int OtaIndex, TimeSpan Exposure, short? Gain, int RoiWidth, int RoiHeight,
    BitDepth? BitDepth = null, bool? HighSpeed = null, short Bin = 1, bool GuideCamera = false);

/// <summary>
/// What a capture's stream runs at, as an interlude starts it again (<see cref="PlanetaryCapture.InterludeAsync"/>): the
/// stream's own controls, as started and with every live control since folded in, and the readout window's size and binning,
/// zero by zero for the whole sensor (<see cref="PlanetaryCapture.ConfigureRoi"/>). The window's origin is not among them: a
/// stream starts its window centred, and the capture jogs it back on the first frame after the restart to where it stood as
/// the stream stopped (to its centre, when the size changed), so neither a pan nor the recenter's jogs are lost.
/// </summary>
public readonly record struct PlanetaryStreamSettings(VideoCaptureOptions Options, int RoiWidth, int RoiHeight, short Bin);

/// <summary>
/// A pause in a running capture's stream (<see cref="PlanetaryCapture.InterludeAsync"/>), the ONE routine for whatever must
/// happen with the stream stopped: a filter change (#1618), a sequence's step (#1340), a full-sensor still (#1033). The
/// stream is stopped between two frames, <paramref name="Action"/> runs, and the stream starts again, at the settings it had
/// or at <see cref="RestartAt"/>'s, its first <see cref="DropFrames"/> frames dropped. The capture keeps its claim on the
/// camera throughout; whatever else the action drives, its caller claims.
/// </summary>
/// <param name="What">What the pause is for, as it completes a sentence ("a filter change"), said in the refusals.</param>
/// <param name="Action">Run with the stream stopped, on the capture's thread, on a token cancelled when the capture stops or
/// the caller gives up. Its failure is its caller's, thrown to it once the stream is back at the settings it had.</param>
public sealed record PlanetaryInterlude(string What, Func<CancellationToken, ValueTask> Action)
{
    /// <summary>The frames dropped after a restart unless the caller says otherwise.</summary>
    public const int DefaultDropFrames = 2;

    /// <summary>
    /// The settings the stream starts again at, given the ones it had; null for the same. A full-sensor still takes
    /// <c>s =&gt; s with { RoiWidth = 0, RoiHeight = 0 }</c> and a second interlude back, or takes its still in
    /// <see cref="Action"/> and comes back at the window it proposes. Asked only once the action has succeeded.
    /// </summary>
    public Func<PlanetaryStreamSettings, PlanetaryStreamSettings>? RestartAt { get; init; }

    /// <summary>
    /// Frames dropped after the restart before any reaches the ring, the live stack, a recording or the host's <c>onFrame</c>:
    /// what a body reads as its stream starts, and what the optics show while they settle from what the action moved.
    /// </summary>
    public int DropFrames { get; init; } = DefaultDropFrames;

    /// <summary>
    /// Where the window goes once the stream is back: null for where it stood as the stream stopped (the pans and the recenter's
    /// jogs given back), or a window of the size <see cref="RestartAt"/> gives, put exactly there: a reframe (#1033).
    /// </summary>
    public RoiRect? WindowAt { get; init; }
}

/// <summary>What a <see cref="PlanetaryCapture"/> is for, which decides whether it keeps the frames it streams.</summary>
public enum LiveCaptureKind
{
    /// <summary>A planetary capture: every frame is kept in <see cref="PlanetaryCapture.Stream"/> for its host's rolling stack.</summary>
    Planetary,

    /// <summary>
    /// A live view (#1111): continuous frames to look at, for framing, focusing, collimation or a flat panel's light, and
    /// none of them kept. Its host sees each through the capture's <c>onFrame</c> alone, so no ring of frames is filled.
    /// </summary>
    LiveView,
}

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
/// <b>Threading.</b> <see cref="Start"/> runs the capture loop on a thread of its own at
/// <see cref="ThreadPriority.AboveNormal"/> (<see cref="CaptureReaderThread"/>), never the thread pool: with the machine's
/// cores busy at normal priority a pool continuation fell behind the driver's hand-off and lost frames (TIA2), and only
/// the reader is lifted, not the host's live stack or view, which consume the stream below it. So the loop's own awaits
/// keep their context (no <c>ConfigureAwait(false)</c> on the loop's path), each resuming on that thread. The loop calls
/// the thread-safe <see cref="LiveCameraFrameStream.Push"/> and nothing a host reads besides. A live control is STAGED by
/// whoever sets it and applied by the loop after the next frame, so no driver call crosses onto a host's render thread.
/// </para>
/// </summary>
/// <param name="stackOptions">The rolling window the host stacks over, which sizes the ring: twice its frames, at least
/// 1024.</param>
/// <param name="onFrame">Called on the capture loop with each frame once it is pushed, BORROWED: it is released as the
/// call returns, so a host that keeps one copies it (a GUI only asks for a redraw here).</param>
/// <param name="kind">A planetary capture keeps its frames for a stack; a live view keeps none
/// (<see cref="LiveCaptureKind.LiveView"/>), and names itself so in its refusals and its claim on the camera.</param>
public sealed class PlanetaryCapture(ITimeProvider timeProvider, ILogger logger, RollingWindowOptions? stackOptions = null, Action<Image>? onFrame = null,
    LiveCaptureKind kind = LiveCaptureKind.Planetary)
    : IAsyncDisposable
{
    /// <summary>What the claim on the camera is called, which a refusal names.</summary>
    public const string LeaseOwner = "planetary capture";

    /// <summary>What a live view's claim on the camera is called (<see cref="LiveCaptureKind.LiveView"/>).</summary>
    public const string LiveViewLeaseOwner = "live view";

    /// <summary>What this capture is called in its refusals and its claim: <see cref="LeaseOwner"/> or <see cref="LiveViewLeaseOwner"/>.</summary>
    public string Name => kind is LiveCaptureKind.LiveView ? LiveViewLeaseOwner : LeaseOwner;

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
    private int _framesOnThreadPool;
    private int _readerPriority = -1;              // the ThreadPriority the newest frame ran at; -1 before the first
    private volatile string? _failure;
    private long _captureStartTimestamp;

    // Live-control changes staged by a host and drained + applied by the capture loop after each frame. 0 / NoGain =
    // nothing pending.
    private long _pendingExposureTicks;
    private int _pendingGain = NoGain;
    private int _pendingRoiW;
    private int _pendingRoiH;
    private int _pendingJogX;
    private int _pendingJogY;
    // 0 and -1 are "nothing pending": a depth is staged as its BitDepth value, the readout mode as 0 or 1.
    private int _pendingBitDepth;
    private int _pendingHighSpeed = -1;
    private int _lastFrameBitDepth;

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
    private int _roiJogEnabled = 1;                     // 0/1

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
            await gate.WaitAsync(token);
            if (!await enumerator.MoveNextAsync())
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    /// <summary>True while a capture loop is running.</summary>
    public bool IsCapturing => Volatile.Read(ref _captureActive) == 1;

    /// <summary>
    /// Completes once the capture loop has ended: stopped through the token it was started on, or on a fault, which it
    /// never throws (<see cref="FailureReason"/> says it). Complete when no capture was started. What a host whose run IS
    /// the capture awaits (a live view, #1111), where a planetary host awaits its stack instead.
    /// </summary>
    public Task Completion => Volatile.Read(ref _captureTask) ?? Task.CompletedTask;

    /// <summary>The camera the capture streams from, or is prepared to (<see cref="TryPrepare"/>), or null.</summary>
    public ICameraDriver? Camera => Volatile.Read(ref _prepared)?.Camera ?? _camera;

    /// <summary>
    /// The frame stream the capture fills, or null before its first frame. REPLACED when the frames' size or layout
    /// changes (a live ROI resize), so a host holding one compares it with this on every read and follows the new one.
    /// </summary>
    public LiveCameraFrameStream? Stream => Volatile.Read(ref _stream);

    /// <summary>Why the current or last capture's loop stopped on a fault, in words; null while it runs well or ended on a stop.</summary>
    public string? FailureReason => _failure;

    /// <summary>Total frames pushed into the stream since the current capture started.</summary>
    public int FramesReceived => Volatile.Read(ref _framesReceived);

    /// <summary>
    /// Frames of the current or last capture whose loop iteration ran on a thread pool thread; none while the loop keeps to its
    /// own thread (<see cref="CaptureReaderThread"/>). Test seam.
    /// </summary>
    internal int FramesOnThreadPool => Volatile.Read(ref _framesOnThreadPool);

    /// <summary>The priority of the thread the newest frame's iteration ran on; null before the first frame. Test seam.</summary>
    internal ThreadPriority? ReaderPriority => Volatile.Read(ref _readerPriority) is >= 0 and var priority ? (ThreadPriority)priority : null;

    /// <summary>The depth the newest frame came in; null before the first.</summary>
    public BitDepth? FrameBitDepth => Volatile.Read(ref _lastFrameBitDepth) is not 0 and var depth ? (BitDepth)depth : null;

    /// <summary>
    /// Frames the camera/SDK reported dropped (buffer starvation) over the whole capture, 0 for cameras that can't report it: the
    /// streams before its interludes, which a driver counts one stream at a time, and the one running.
    /// </summary>
    public int DroppedFrames => Volatile.Read(ref _droppedBeforeInterludes) + ((_camera as IVideoCameraDriver)?.DroppedFrames ?? 0);

    // The frames the streams before this capture's interludes dropped, kept as each stream stopped (StreamSegmentsAsync).
    private int _droppedBeforeInterludes;

    /// <summary>
    /// Measured delivered frame rate (frames / elapsed capture time, less the time its stream stood stopped for interludes),
    /// 0 before the first frame.
    /// </summary>
    public double MeasuredFps
    {
        get
        {
            var n = Volatile.Read(ref _framesReceived);
            if (n <= 0)
            {
                return 0.0;
            }

            var elapsed = (timeProvider.GetElapsedTime(Volatile.Read(ref _captureStartTimestamp)) - new TimeSpan(Interlocked.Read(ref _interludeTicks))).TotalSeconds;
            return elapsed > 0 ? n / elapsed : 0.0;
        }
    }

    // The current rate, the capture loop's (FrameRateWindow), published as one value a frame.
    private readonly FrameRateWindow _rateWindow = new FrameRateWindow();
    private long _currentFpsBits;

    /// <summary>
    /// The CURRENT frame rate (#1619): frames over the last second, or over the last five where those span longer; 0 before two
    /// frames. What a readout shows, where <see cref="MeasuredFps"/> is the run's average and blends every exposure it had.
    /// </summary>
    public double CurrentFps => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _currentFpsBits));

    // The readout window as the stream last ran at it, published only when it moves (one box a change, never one a frame).
    private RoiRect? _window;

    /// <summary>
    /// Where the readout window stands on the sensor now, origin included (#1033), for a stream that reports it; null for one
    /// that does not, or before the first frame. What a full frame's overlay draws as the live window.
    /// </summary>
    public RoiRect? Window => Volatile.Read(ref _windowBox)?.Value;

    private System.Runtime.CompilerServices.StrongBox<RoiRect>? _windowBox;

    // The stream's settings as last applied, published whole each time they change (start, a live control, an interlude).
    private VideoCaptureOptions? _appliedOptions;

    /// <summary>
    /// What the stream runs at now: its exposure, gain, depth and readout as started, with every live control and interlude since
    /// applied; null before the capture's loop starts. What a status line says, never what a host staged.
    /// </summary>
    public VideoCaptureOptions? AppliedOptions => Volatile.Read(ref _appliedOptions);

    /// <summary>Last measured disk centre-of-mass offset from the frame centre (px), for the recenter readout.</summary>
    public (double X, double Y) LastComOffset
        => (BitConverter.Int64BitsToDouble(Volatile.Read(ref _lastOffsetXBits)),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref _lastOffsetYBits)));

    /// <summary>Which actuator the most recent recenter frame engaged (for the panel readout).</summary>
    public RecenterActuator LastRecenterActuator => (RecenterActuator)Volatile.Read(ref _lastActuator);

    /// <summary>
    /// Starts a capture from <paramref name="profile"/>'s devices, the one start rule for every host:
    /// <see cref="TryPrepare"/> and then <see cref="StartPrepared"/>, for a host that starts it where it asks (the GUI).
    /// Refuses in words, holding nothing, when any of it cannot be had.
    /// </summary>
    /// <param name="roi">The readout window applied, after snapping.</param>
    public bool TryStart(in PlanetaryCaptureRequest request, ProfileData profile, IDeviceHub hub, CancellationToken token,
        out (int Width, int Height) roi, [NotNullWhen(false)] out string? refusal)
    {
        if (!TryPrepare(request, profile, hub, out roi, out refusal))
        {
            return false;
        }
        if (!StartPrepared(token))
        {
            refusal = $"A {Name} is already running";
            return false;
        }
        return true;
    }

    // A capture made ready by TryPrepare and not yet started: its camera, what it streams at, and the claim on the camera,
    // which StartPrepared hands to the loop and DisposeAsync gives back if it never starts.
    private sealed record Prepared(ICameraDriver Camera, VideoCaptureOptions Options, DeviceLeaseSet Claim);

    private Prepared? _prepared;

    /// <summary>
    /// Makes a capture from <paramref name="profile"/>'s devices ready to start, without streaming a frame: OTA
    /// <see cref="PlanetaryCaptureRequest.OtaIndex"/>'s camera, connected, claimed (<see cref="Name"/>), its readout
    /// window configured (<see cref="ConfigureRoi"/>), and the profile's mount, when connected, attached for the recenter's
    /// coarse nudge with the OTA's pixel scale. Refuses in words, holding nothing, when any of it cannot be had. The node
    /// prepares as it is asked and starts on its own token once the run is its own (<see cref="StartPrepared"/>), so a
    /// refusal is an answer to the request and the capture never runs on the request's token.
    /// </summary>
    /// <param name="roi">The readout window applied, after snapping.</param>
    public bool TryPrepare(in PlanetaryCaptureRequest request, ProfileData profile, IDeviceHub hub,
        out (int Width, int Height) roi, [NotNullWhen(false)] out string? refusal)
    {
        roi = default;
        if (IsCapturing || Volatile.Read(ref _prepared) is not null)
        {
            refusal = $"A {Name} is already running";
            return false;
        }
        OTAData? ota = null;
        Uri cameraUri;
        if (request.GuideCamera)
        {
            // A URI's host is lower-cased, so the class name is compared without case.
            if (string.Equals(DeviceBase.DeviceClassOf(profile.Guider), nameof(OpenPHD2GuiderDevice), StringComparison.OrdinalIgnoreCase))
            {
                refusal = "PHD2 owns the guide camera, so there is nothing to loop here: loop it in PHD2";
                return false;
            }
            if (profile.GuiderCamera is not { Scheme: not "none" } guideCameraUri)
            {
                refusal = "The profile has no guide camera to loop";
                return false;
            }
            cameraUri = guideCameraUri;
        }
        else
        {
            if (request.OtaIndex < 0 || request.OtaIndex >= profile.OTAs.Length)
            {
                refusal = $"The profile has no OTA #{request.OtaIndex + 1}";
                return false;
            }
            var scope = profile.OTAs[request.OtaIndex];
            ota = scope;
            cameraUri = scope.Camera;
        }
        if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera))
        {
            refusal = $"Connect a camera to start a {Name}";
            return false;
        }
        // What a recording's header says it was taken through (#1179).
        Volatile.Write(ref _telescopeField, ota is { } through ? PlanetaryCaptureName.TelescopeField(through) : "");

        // Claimed for the length of the capture, before the ROI touches the camera: streaming video off a camera a flat
        // run is metering would fight it frame for frame (P0c item 2 of docs/plans/hardware-in-the-server.md). Only the
        // camera: the recenter's nudges ask the gate over the mount instead.
        if (!DeviceLeaseSet.TryAcquire(hub, [cameraUri], Name, out var claim, out var verdict))
        {
            refusal = verdict.Describe();
            return false;
        }

        // The prepared start owns the claim once made; a throw before that (the ROI, the mount wiring) must not strand it.
        var handedOver = false;
        try
        {
            roi = ConfigureRoi(camera, request.RoiWidth, request.RoiHeight, request.Bin);
            if (profile.Mount is { Scheme: not "none" } mountUri && hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount))
            {
                AttachMount(mount, mountUri, hub, ota is { } scope ? CoordinateUtils.PixelScaleArcsec(camera.PixelSizeX, scope.FocalLength) : double.NaN);
            }
            else
            {
                AttachMount(null, null, hub, double.NaN);
            }
            var options = new VideoCaptureOptions(request.Exposure, request.Gain, request.HighSpeed, request.BitDepth);
            handedOver = Interlocked.CompareExchange(ref _prepared, new Prepared(camera, options, claim), null) is null;
        }
        finally
        {
            if (!handedOver)
            {
                claim.Dispose();
            }
        }

        refusal = handedOver ? null : $"A {Name} is already running";
        return handedOver;
    }

    /// <summary>
    /// Starts the capture <see cref="TryPrepare"/> made ready, on <paramref name="token"/>, which bounds its life. False,
    /// holding nothing, when none was prepared or one is already running.
    /// </summary>
    public bool StartPrepared(CancellationToken token)
        => Interlocked.Exchange(ref _prepared, null) is { } prepared && Start(prepared.Camera, prepared.Options, token, prepared.Claim);

    /// <summary>
    /// Configures a camera's sub-frame (ROI) for planetary video: bin 1 + a <paramref name="width"/> x
    /// <paramref name="height"/> readout SIZE, <b>snapped to the camera's <see cref="RoiConstraints"/></b> (step /
    /// alignment / min / max), the single source of truth, so e.g. a ZWO width rounds to a multiple of 8 and a height to
    /// a multiple of 2. Sets only the size (NumX/NumY); the readout origin is left at the driver default until the
    /// recenter loop pans it. Returns the applied (width, height) after snapping. Moved here from the GUI's
    /// <c>PlanetaryCaptureActions</c>.
    /// <para>A size of zero by zero is the WHOLE sensor, from its origin, at <paramref name="bin"/> (clamped to what the
    /// camera can bin): what a live view streams (#1111), at the Preview's binning, since a 26-megapixel frame at full
    /// resolution is a heavy thing to stream for framing. A Canon reads it as its live view unmagnified, since its window
    /// is the zoom, and bins nothing.</para>
    /// </summary>
    public static (int Width, int Height) ConfigureRoi(ICameraDriver camera, int width, int height, short bin = 1)
    {
        // Planetary wants unbinned readout; only the whole sensor bins. BinX must be set before NumX/NumY (their setters
        // validate against the binned sensor size).
        var wholeSensor = width <= 0 && height <= 0;
        var binX = wholeSensor ? Math.Clamp(bin, (short)1, Math.Max(camera.MaxBinX, (short)1)) : (short)1;
        var binY = wholeSensor ? Math.Clamp(bin, (short)1, Math.Max(camera.MaxBinY, (short)1)) : (short)1;
        if (camera.BinX != binX)
        {
            camera.BinX = binX;
        }
        if (camera.BinY != binY)
        {
            camera.BinY = binY;
        }

        // The whole sensor starts at its origin: a window an earlier capture panned would otherwise run past the edge.
        if (wholeSensor)
        {
            (width, height) = (camera.CameraXSize / binX, camera.CameraYSize / binY);
            camera.StartX = 0;
            camera.StartY = 0;
        }

        // Snap the requested size to the camera's real ROI rule (free step-1 default for ASCOM / Alpaca; the fake
        // reports ZWO-style 8 / 2). The whole sensor is a legal size.
        var snapped = camera.RoiConstraints.Snap(new RoiRect(0, 0, width, height));
        camera.NumX = snapped.Width;
        camera.NumY = snapped.Height;
        return (snapped.Width, snapped.Height);
    }

    /// <summary>
    /// Starts streaming from <paramref name="camera"/>. The ROI / sensor type is read from the camera's current sub-frame
    /// config (set the camera's <c>NumX</c>/<c>NumY</c> before calling). Refuses, and gives the claim back, when a capture
    /// is already running. <paramref name="token"/> bounds the capture's life (a host's own lifetime).
    /// </summary>
    /// <param name="claim">The claim on the camera (owner <see cref="Name"/>), taken by the host so a capture is refused
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
        Volatile.Write(ref _pendingBitDepth, 0);
        Volatile.Write(ref _pendingHighSpeed, -1);
        Volatile.Write(ref _lastFrameBitDepth, 0);
        _pendingLensSteps.Clear();

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
        _failure = null;
        Volatile.Write(ref _rapidExposureTicks, capture.Exposure.Ticks);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _framesDroppedAfterInterludes, 0);
        Interlocked.Exchange(ref _droppedBeforeInterludes, 0);
        Interlocked.Exchange(ref _interludeTicks, 0);
        Interlocked.Exchange(ref _currentFpsBits, 0);
        Volatile.Write(ref _appliedOptions, null);
        _captureStartTimestamp = timeProvider.GetTimestamp();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loopToken = _cts.Token;
        Interlocked.Exchange(ref _framesOnThreadPool, 0);
        Volatile.Write(ref _readerPriority, -1);
        _captureTask = CaptureReaderThread.Run($"{Name} reader", () => CaptureLoopAsync(camera, capture, claim, loopToken));

        logger.LogInformation(
            "Capture started ({Capture}): exposure {Exposure}ms, native={Native}.",
            Name, capture.Exposure.TotalMilliseconds, camera is IVideoCameraDriver { CanVideoCapture: true });
        return true;
    }

    private async Task CaptureLoopAsync(ICameraDriver camera, VideoCaptureOptions options, DeviceLeaseSet? claim, CancellationToken token)
    {
        _streamOptions = options;
        Volatile.Write(ref _appliedOptions, options);
        _rateWindow.Reset();
        LiveCameraFrameStream? stream = null;
        // The process's collections over the capture, and how long they held every managed thread, the reader's and the
        // driver's included: said as the loop ends, beside its frames.
        var (gen0From, gen1From, gen2From, pausedFrom) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        try
        {
            var streams = Streams(camera);
            // The stream in segments, an interlude between two (StreamSegmentsAsync): the claim stays this loop's throughout.
            var frames = StreamSegmentsAsync(camera, token);
            if (_frameGate is { } gate)
            {
                frames = GatedFramesAsync(frames, gate, token);
            }

            // No ConfigureAwait(false) on the loop's path: each frame resumes on the reader's own thread (CaptureReaderThread).
            await foreach (var frame in frames)
            {
                var reader = Thread.CurrentThread;
                if (reader.IsThreadPoolThread)
                {
                    Interlocked.Increment(ref _framesOnThreadPool);
                }
                Volatile.Write(ref _readerPriority, (int)reader.Priority);

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
                // A live view keeps no frames, so it fills no ring: its host has each frame through onFrame.
                if (kind is LiveCaptureKind.Planetary
                    && (stream is null || stream.Width != planeW || stream.Height != planeH || stream.Layout != layout))
                {
                    var capacity = Math.Max(_stackOptions.MaxWindowFrames * 2, 1024);
                    // Its planes from the shelf the last ring of this shape left, when there is one: a ring of a thousand
                    // large planes filled fresh for every capture fed the collector 1.2 GB a capture (RingPlaneShelf).
                    var rebuilt = new LiveCameraFrameStream(planeW, planeH, layout, capacity, hasTimestamps: true, RingPlaneShelf.Shared);
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
                // A stream's frame carries the time its driver read it (a DAL camera's does), which the queue between the
                // driver and this loop does not move: the ring and a recording keep the camera's own cadence, and a stall
                // shows in them as the gap it was. A camera that does not stream stamps a frame with when its exposure
                // STARTED, not when it arrived, so its frames, and a stream's that carry no time, are stamped here.
                var arrived = streams && frame.ImageMeta.ExposureStartTime is var stamped && stamped != default ? stamped : timeProvider.GetUtcNow();
                stream?.Push(frame, arrived);
                var received = Interlocked.Increment(ref _framesReceived);
                if (camera is IVideoCameraDriver { CanJogRoi: true } windowed && windowed.VideoRoi is var roi && roi != _window)
                {
                    _window = roi;
                    Volatile.Write(ref _windowBox, new System.Runtime.CompilerServices.StrongBox<RoiRect>(roi));
                }
                Interlocked.Exchange(ref _currentFpsBits,
                    BitConverter.DoubleToInt64Bits(_rateWindow.Note(timeProvider.GetElapsedTime(_captureStartTimestamp).Ticks)));
                Volatile.Write(ref _lastFrameBitDepth, (int)frame.BitDepth);
                if (received == 1 && stream is null)
                {
                    logger.LogInformation("Capture ({Capture}): first frame {W}x{H}x{C} ({Sensor}).",
                        Name, frame.Width, frame.Height, frame.ChannelCount, frame.ImageMeta.SensorType);
                }

                // A recording converts and queues the frame; its own writer does the disk (SerRecording). Stamped with the
                // driver's own read time, so its trailer is honest across a stall the driver recovered from: a jump of
                // the gap's length between two frames, never frames that pretend to be continuous.
                _recording?.TryAppend(frame, arrived);

                // COM recenter: measure the disk on the just-captured frame (still alive here, before the Release
                // below) and pull it back to the frame centre -- via the ROI window (fast, mount-free) or, when the ROI
                // is edge-blocked and mount jog is enabled, a coarse mount nudge. A staged ROI jog is drained by
                // ApplyPendingControlsAsync below in the same iteration.
                await MaybeRecenterAsync(camera, frame, token);

                // Defensive heartbeat: a steady cadence in the log confirms the capture loop is alive; the last
                // heartbeat before a freeze bounds when the loop (or the thread it feeds) stopped advancing. This line and the
                // loop's other Debug lines are written off the reader, so the file log's lock never holds it up (TIA2).
                if (received % 250 == 0)
                {
                    logger.LogDebugOffThread(
                        "Capture heartbeat ({Capture}): received {Received}, stream {StreamCount}, {Fps:F0} fps, {Dropped} dropped.",
                        Name, received, stream?.FrameCount ?? 0, MeasuredFps, DroppedFrames);
                }

                // A host sees the frame before it goes back (and copies it, to keep it). The ring deep-copied it (and
                // split it, for a Bayer source), so the camera's buffer goes back now.
                onFrame?.Invoke(frame);
                frame.Release();

                // Apply any live-control changes (exposure / gain / ROI size / pan) staged by a host.
                await ApplyPendingControlsAsync(camera, token);

                // Last thing in the iteration, so a waiter observes a fully-settled frame: pushed, recentred, and with
                // this frame's staged ROI jog already on the camera.
                SignalFrame();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Capture loop stopped ({Capture}, cancelled).", Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capture loop faulted ({Capture}).", Name);
            _failure = StatusText.FromException(ex);
        }
        finally
        {
            logger.LogInformation(
                "Capture loop ended ({Capture}): {Frames} frames, {OnPool} of them on the thread pool; the process collected {Gen0}/{Gen1}/{Gen2} times (gen 0/1/2), "
                + "pausing {Paused:F0} ms in all, in GC latency mode {Mode}; its heap {Heap:F0} MB, its working set {WorkingSet:F0} MB",
                Name, FramesReceived, FramesOnThreadPool, GC.CollectionCount(0) - gen0From, GC.CollectionCount(1) - gen1From, GC.CollectionCount(2) - gen2From,
                (GC.GetTotalPauseDuration() - pausedFrom).TotalMilliseconds, System.Runtime.GCSettings.LatencyMode,
                GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0, Environment.WorkingSet / 1048576.0);

            // A recording ends with the frames that feed it; what it queued is still written.
            _recording?.End("the capture ended");

            // The camera is free again once the loop is done with it.
            claim?.Dispose();
            Interlocked.Exchange(ref _captureActive, 0);

            // An interlude asked for and never taken, or taken and never streamed after, is told the capture ended. Taken
            // only once the capture is marked ended, so one asked for meanwhile sees that for itself (InterludeAsync).
            if (Volatile.Read(ref _streamUse) is InterludeRequest left && Interlocked.CompareExchange(ref _streamUse, null, left) == left)
            {
                left.Refuse(left.WasTaken
                    ? $"The {Name} did not stream again after {left.Interlude.What}: {_failure ?? "it was stopped"}"
                    : $"The {Name} ended before {left.Interlude.What} could stop its stream");
            }

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

    // Whether the capture takes the camera's own stream (Frames), whose frames carry their own times and whose recording
    // can show a stall as a gap.
    private static bool Streams(ICameraDriver camera) => camera is IVideoCameraDriver { CanVideoCapture: true };

    // ── Interludes: the stream stopped, an action run, the stream started again (#1618, #1340, #1033) ─────────────────

    // What the stream runs at now: as started, and each live control folded in once applied (ApplyPendingControlsAsync), so
    // an interlude starts it again where it was. The loop's own, written and read only on its thread.
    private VideoCaptureOptions _streamOptions = new(MinExposure);

    // The stream's one claim (StreamUse), or null. A recording and an interlude each take it through TryClaimStream, one
    // compare-and-swap, so of two asked for at once exactly one has the stream and the other is told why; a recording is the
    // loop's to append to only once it holds it.
    private StreamUse? _streamUse;

    // What holds the stream's claim: a recording being made (its writer not started yet, so one that loses the claim starts
    // nothing), the recording armed on it (free again once it has ended), or the interlude asked for and not yet finished (taken
    // between two frames by the loop, cleared by whoever finishes it). Closed: these three and nothing else.
    private abstract class StreamUse
    {
        private protected StreamUse() { }

        /// <summary>A recording being made: the claim is held while its file and writer are set up.</summary>
        public sealed class RecordingStarting : StreamUse;

        /// <summary>The recording armed on the stream.</summary>
        public sealed class Recording(SerRecording recording) : StreamUse
        {
            public SerRecording Value => recording;
        }
    }
    private int _framesDroppedAfterInterludes;
    private long _interludeTicks;                  // the time the current capture's stream stood stopped for interludes

    /// <summary>Frames of the current or last capture an interlude dropped (<see cref="PlanetaryInterlude.DropFrames"/>). Test seam.</summary>
    internal int FramesDroppedAfterInterludes => Volatile.Read(ref _framesDroppedAfterInterludes);

    /// <summary>
    /// Why an interlude (<see cref="InterludeAsync"/>) for <paramref name="what"/> cannot be had now, in words; null when it
    /// can. What a host asks before it starts anything the interlude would carry, so a refusal is an answer, not a failed job.
    /// </summary>
    /// <param name="what">What the interlude is for, as it completes a sentence ("a filter change").</param>
    public string? InterludeRefusal(string what)
        => IsCapturing ? InterludeRefusalFor(Volatile.Read(ref _streamUse), what) : NotStreaming(what);

    private string NotStreaming(string what) => $"No {Name} is streaming, so {what} has no stream to stop";

    // Why the stream's claim, as `use` holds it, refuses an interlude for `what`; null when it is free.
    private string? InterludeRefusalFor(StreamUse? use, string what) => use switch
    {
        StreamUse.RecordingStarting or StreamUse.Recording { Value.IsRecording: true } => $"A recording is being made: {what} would stop "
            + "the stream it is made from, and put frames from before and after it in one file. Stop the recording, or let it end, first",
        InterludeRequest other => $"The {Name} stops its stream for {other.Interlude.What}; {what} waits until that is done",
        _ => null,
    };

    // Why the stream's claim, as `use` holds it, refuses a recording; null when it is free.
    private string? RecordingRefusalFor(StreamUse? use) => use switch
    {
        StreamUse.RecordingStarting or StreamUse.Recording { Value.IsRecording: true } => "A recording is already being made",
        InterludeRequest interlude => $"The {Name} stops its stream for {interlude.Interlude.What}; record once it streams again",
        _ => null,
    };

    // Takes the stream's one claim for `claimant`, a recording being made or an interlude, unless what holds it refuses
    // (`refusalFor`): the one place the two are put in order. Retried only when the claim changed between its read and the swap.
    private bool TryClaimStream(StreamUse claimant, Func<StreamUse?, string?> refusalFor, [NotNullWhen(false)] out string? refusal)
    {
        while (true)
        {
            var use = Volatile.Read(ref _streamUse);
            if (refusalFor(use) is { } busy)
            {
                refusal = busy;
                return false;
            }
            if (Interlocked.CompareExchange(ref _streamUse, claimant, use) == use)
            {
                refusal = null;
                return true;
            }
        }
    }

    /// <summary>
    /// Stops the running capture's stream between two frames, runs <paramref name="interlude"/>'s action, and starts the stream
    /// again (<see cref="PlanetaryInterlude"/>): at the settings it had, the live controls since its start included, or at
    /// <see cref="PlanetaryInterlude.RestartAt"/>'s, its first <see cref="PlanetaryInterlude.DropFrames"/> frames dropped. The
    /// capture's claim on the camera is kept throughout, and a recording is not made across one: one going on refuses it, and
    /// none may start until the stream is back.
    /// </summary>
    /// <param name="cancellationToken">Gives up an interlude not yet begun; one begun has its action's token cancelled and
    /// ends once the stream is back, so whatever its caller claimed for the action is never let go while the action runs.</param>
    /// <returns>Null once the stream is back at the first frame past the dropped ones, else why none could be had, in words
    /// (<see cref="InterludeRefusal"/>, or the capture ended first).</returns>
    /// <exception cref="Exception">Whatever the action threw (its cancellation included), once the stream is back at the
    /// settings it had.</exception>
    public async Task<string?> InterludeAsync(PlanetaryInterlude interlude, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interlude);
        if (InterludeRefusal(interlude.What) is { } refusal)
        {
            return refusal;
        }

        // The stream's one claim, which a recording takes too: whichever of the two has it first, the other is refused.
        var request = new InterludeRequest(interlude, cancellationToken);
        if (!TryClaimStream(request, use => InterludeRefusalFor(use, interlude.What), out var busy))
        {
            return busy;
        }

        // Looked at again once the claim is held: a loop that ended before it is seen here, one ending after takes it (the
        // loop's finally).
        if (!IsCapturing)
        {
            Interlocked.CompareExchange(ref _streamUse, null, request);
            return NotStreaming(interlude.What);
        }

        string? outcome;
        using (cancellationToken.Register(static state => (state as InterludeRequest)?.TryWithdraw(), request))
        {
            outcome = await request.Done.Task.ConfigureAwait(false);
        }
        if (request.WasWithdrawn)
        {
            Interlocked.CompareExchange(ref _streamUse, null, request);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return outcome;
    }

    // The camera's frames over every segment of the capture's stream: the stream, and after an interlude taken between two of
    // its frames, the next, started at the settings the interlude left. Leaving a segment's enumeration is what stops it (a
    // driver's stop is its enumerator's end, IVideoCameraDriver), so a stream stopped on purpose is never a stream gone quiet:
    // the DAL's silence watch is the segment's own. The frames an interlude drops are released here and reach nothing. An
    // interlude is looked for as the loop asks for its next frame (behind a test's frame gate, once a step lets it), never
    // after a frame it has not finished. One iterator for the capture, its awaits on the reader's context like the loop's,
    // so no frame allocates.
    private async IAsyncEnumerable<Image> StreamSegmentsAsync(ICameraDriver camera, [EnumeratorCancellation] CancellationToken token)
    {
        InterludeRequest? restarted = null;
        var dropping = 0;
        var stoppedAt = 0L;
        // Where the window stood as the stream stopped: a new stream starts its window centred, so the pans and the recenter's
        // jogs are given back as one jog on its first frame, while the frames after the change are still being dropped.
        RoiRect? returnTo = null;
        while (true)
        {
            InterludeRequest? interlude = null;
            await foreach (var frame in Frames(camera, _streamOptions, token))
            {
                if (returnTo is { } was)
                {
                    returnTo = null;
                    JogBackTo(camera, was);
                }

                if (dropping > 0)
                {
                    dropping--;
                    Interlocked.Increment(ref _framesDroppedAfterInterludes);
                    frame.Release();
                }
                else
                {
                    if (restarted is { } back)
                    {
                        // The rate is the stream's, so the time it stood stopped is not counted against it (MeasuredFps).
                        Interlocked.Add(ref _interludeTicks, timeProvider.GetElapsedTime(stoppedAt).Ticks);
                        // The live stack starts again at this frame, the first the ring keeps after the interlude: what came
                        // before was taken through another filter, focus or window. A ring rebuilt for this frame (its window
                        // changed) starts at it anyway. The ring and its planes, the counters and the rate carry on.
                        Volatile.Read(ref _stream)?.StartRun();
                        restarted = null;
                        Interlocked.CompareExchange(ref _streamUse, null, back);
                        back.Restarted();
                    }
                    yield return frame;
                }

                // Between two frames, and only there: the one just taken is whole in the ring, the recording and the host.
                // Dropped frames run on to the first one kept, with no interlude between.
                if (dropping == 0 && restarted is null && Volatile.Read(ref _streamUse) is InterludeRequest asked && asked.TryTake())
                {
                    interlude = asked;
                    stoppedAt = timeProvider.GetTimestamp();
                    // Read while this stream still runs: once stopped, a driver reports its window as the whole sensor, and
                    // it counts the frames it dropped per stream, so the leaving stream's count is kept here.
                    if (camera is IVideoCameraDriver video)
                    {
                        returnTo = video.CanJogRoi ? asked.Interlude.WindowAt ?? video.VideoRoi : null;
                        Interlocked.Add(ref _droppedBeforeInterludes, video.DroppedFrames);
                    }
                    break;
                }
            }

            if (interlude is null || !await RunInterludeAsync(camera, interlude, token))
            {
                yield break;
            }
            (restarted, dropping) = (interlude, Math.Max(0, interlude.Interlude.DropFrames));
        }
    }

    // Stages the one jog that takes the new stream's window back to `was`: to its origin when the size is the same, to its
    // centre when the interlude restarted at another size. The driver snaps it as it snaps every pan.
    private void JogBackTo(ICameraDriver camera, RoiRect was)
    {
        if (camera is not IVideoCameraDriver { CanJogRoi: true } video)
        {
            return;
        }
        var now = video.VideoRoi;
        var (x, y) = (now.Width, now.Height) == (was.Width, was.Height)
            ? (was.X, was.Y)
            : (was.X + (was.Width - now.Width) / 2, was.Y + (was.Height - now.Height) / 2);
        if ((x, y) != (now.X, now.Y))
        {
            JogRoi(x - now.X, y - now.Y);
        }
    }

    // Runs an interlude the loop took, its stream stopped: the action, on the capture's token and its caller's, then the
    // settings it starts again at, the window put on the camera where they change it. False when the capture is stopping,
    // which the loop's end then tells the interlude.
    private async Task<bool> RunInterludeAsync(ICameraDriver camera, InterludeRequest request, CancellationToken token)
    {
        var interlude = request.Interlude;
        var had = new PlanetaryStreamSettings(_streamOptions, camera.NumX, camera.NumY, (short)camera.BinX);
        logger.LogInformation("Capture ({Capture}): the stream stopped for {What}.", Name, interlude.What);

        var restartAt = had;
        using var either = CancellationTokenSource.CreateLinkedTokenSource(token, request.CallerToken);
        try
        {
            await interlude.Action(either.Token);
            if (interlude.RestartAt is { } transform)
            {
                var asked = transform(had);
                if (asked.Options.Exposure < MinExposure)
                {
                    asked = asked with { Options = asked.Options with { Exposure = MinExposure } };
                }
                if ((asked.RoiWidth, asked.RoiHeight, asked.Bin) != (had.RoiWidth, had.RoiHeight, had.Bin))
                {
                    ConfigureRoi(camera, asked.RoiWidth, asked.RoiHeight, asked.Bin);
                }
                restartAt = asked;
            }
        }
        catch (OperationCanceledException ex) when (either.IsCancellationRequested)
        {
            // The caller gave up, or the capture is stopping: the action's cancel is the caller's to hear.
            logger.LogInformation(ex, "Capture ({Capture}): {What} was cancelled with the stream stopped.", Name, interlude.What);
            request.Failed(ex);
        }
        catch (Exception ex)
        {
            // The caller's to hear once the stream is back; the capture goes on at the settings it had.
            logger.LogWarning(ex, "Capture ({Capture}): {What} failed with the stream stopped; it streams again as it was.", Name, interlude.What);
            request.Failed(ex);
            restartAt = had;
        }

        if (token.IsCancellationRequested)
        {
            logger.LogInformation("Capture ({Capture}): stopped during {What}; the stream is not started again.", Name, interlude.What);
            return false;
        }

        _streamOptions = restartAt.Options;
        Volatile.Write(ref _appliedOptions, restartAt.Options);
        Volatile.Write(ref _rapidExposureTicks, restartAt.Options.Exposure.Ticks);
        logger.LogInformation("Capture ({Capture}): streaming again after {What} at {Width}x{Height} bin {Bin}, {Exposure} ms, dropping {Drop} frames.",
            Name, interlude.What, camera.NumX, camera.NumY, camera.BinX, restartAt.Options.Exposure.TotalMilliseconds, interlude.DropFrames);
        return true;
    }

    // One interlude from its asking to its end. Done answers null once the stream is back, a refusal in words, or the
    // action's failure; Done's continuations never run on the reader.
    private sealed class InterludeRequest(PlanetaryInterlude interlude, CancellationToken callerToken) : StreamUse
    {
        private const int Pending = 0, Taken = 1, Withdrawn = 2;
        private int _state;
        private Exception? _failure;

        public PlanetaryInterlude Interlude => interlude;

        public CancellationToken CallerToken => callerToken;

        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasTaken => Volatile.Read(ref _state) == Taken;

        public bool WasWithdrawn => Volatile.Read(ref _state) == Withdrawn;

        // The loop's, between two frames; false once its caller has given it up.
        public bool TryTake() => Interlocked.CompareExchange(ref _state, Taken, Pending) == Pending;

        // Its caller's, giving up one the loop has not taken.
        public void TryWithdraw()
        {
            if (Interlocked.CompareExchange(ref _state, Withdrawn, Pending) == Pending)
            {
                Done.TrySetResult(null);
            }
        }

        public void Failed(Exception failure) => _failure = failure;

        // The stream is back: its caller hears so, or of its action's failure.
        public void Restarted()
        {
            if (_failure is { } failure)
            {
                Done.TrySetException(failure);
            }
            else
            {
                Done.TrySetResult(null);
            }
        }

        public void Refuse(string why) => Done.TrySetResult(why);
    }

    // Universal fallback: loop short single-shot exposures on any ICameraDriver. CanJogRoi is implicitly false for this
    // path (no IVideoCameraDriver), so the recenter loop uses mount jog only. Each exposure reads the live one
    // (_rapidExposureTicks), so an exposure set while it runs takes effect from the next frame, as on a native stream.
    private long _rapidExposureTicks;

    private async IAsyncEnumerable<Image> RapidExposureFramesAsync(
        ICameraDriver camera, VideoCaptureOptions options, [EnumeratorCancellation] CancellationToken token)
    {
        if (options.Gain is { } gain)
        {
            await camera.SetGainAsync(gain, token);
        }

        // An exposure still in flight when the loop ends (a stop, a fault) is aborted as it goes: left exposing, the camera
        // refuses the next consumer's first exposure, which is what a guide run that replaces a guide-camera loop would meet (#1915).
        var exposing = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await camera.StartExposureAsync(new TimeSpan(Volatile.Read(ref _rapidExposureTicks)), FrameType.Light, token);
                exposing = true;

                var ready = false;
                while (!ready)
                {
                    if (token.IsCancellationRequested)
                    {
                        yield break;
                    }

                    ready = await camera.GetImageReadyAsync(token);
                    if (!ready)
                    {
                        var cancelled = false;
                        try
                        {
                            await timeProvider.SleepAsync(_readyPollInterval, token);
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

                var image = await camera.GetImageAsync(token);
                exposing = false;
                if (image is not null)
                {
                    yield return image;
                }
            }
        }
        finally
        {
            if (exposing)
            {
                await AbortExposureQuietlyAsync(camera);
            }
        }
    }

    private async ValueTask AbortExposureQuietlyAsync(ICameraDriver camera)
    {
        try
        {
            await camera.AbortExposureAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Aborting {Camera}'s exposure as the capture ended failed", camera.Name);
        }
    }

    // ── Recording to disk (P5 part 5d) ────────────────────────────────────────────────────────────────────────────

    private volatile SerRecording? _recording;
    private string _telescopeField = "";

    /// <summary>The recording going on, or the last one to end, until the next starts; null before any.</summary>
    public SerRecording? Recording => _recording;

    /// <summary>
    /// Starts recording every frame the capture takes for <paramref name="duration"/> into the SER file at
    /// <paramref name="path"/> (<see cref="SerRecording"/>). Refuses in words while no capture runs or is prepared to, or
    /// while a recording already goes on.
    /// </summary>
    /// <remarks>
    /// A PREPARED capture takes a recording too (<see cref="TryPrepare"/>): a node answers a start once the run is its own,
    /// and the run's task starts the loop a moment later, so a recording asked for in between was refused (a functional
    /// test failed on it on CI, 2026-10-02). A recording only queues the frames that arrive, and one armed on a capture
    /// that never starts is ended as the capture is disposed.
    /// </remarks>
    public bool TryStartRecording(string path, TimeSpan duration, [NotNullWhen(true)] out SerRecording? recording,
        [NotNullWhen(false)] out string? refusal)
        => TryStartRecording(path, RecordingLength.For(duration), out recording, out refusal);

    /// <summary>
    /// Starts recording every frame the capture takes, for <paramref name="length"/>'s time or its frames (#1340), into the SER
    /// file at <paramref name="path"/>; refused in words as <see cref="TryStartRecording(string, TimeSpan, out SerRecording?, out string?)"/>
    /// is, and for a length that is not positive.
    /// </summary>
    public bool TryStartRecording(string path, RecordingLength length, [NotNullWhen(true)] out SerRecording? recording,
        [NotNullWhen(false)] out string? refusal)
    {
        recording = null;
        if (length.Invalid is { } invalid)
        {
            refusal = invalid;
            return false;
        }
        if (!IsCapturing && Volatile.Read(ref _prepared) is null)
        {
            refusal = "No planetary capture is running to record";
            return false;
        }
        // Asked first, so a refusal makes nothing: a recording starts its writer as it is made.
        if (RecordingRefusalFor(Volatile.Read(ref _streamUse)) is { } busy)
        {
            refusal = busy;
            return false;
        }

        // The stream's one claim, the claim an interlude takes too, is taken BEFORE the recording is made: a recording starts
        // its writer as it is made, so one that lost the claim would start a writer thread and log an empty file. Held as
        // "starting" while it is made, which refuses an interlude and a second recording alike, then handed to the recording.
        var starting = new StreamUse.RecordingStarting();
        if (!TryClaimStream(starting, RecordingRefusalFor, out refusal))
        {
            return false;
        }

        SerRecording made;
        try
        {
            var now = timeProvider.GetUtcNow();
            // The header names the camera and the telescope, so the recording's best stack knows the optics (#1179).
            made = new SerRecording(path, now, now + length.Time, logger, Camera?.Name, Volatile.Read(ref _telescopeField),
                streamed: Camera is { } camera && Streams(camera), frameTarget: length.Frames);
        }
        catch
        {
            Interlocked.CompareExchange(ref _streamUse, null, starting);
            throw;
        }
        // Nothing else can take a claim held as starting, so this swap is the recording's.
        Interlocked.CompareExchange(ref _streamUse, new StreamUse.Recording(made), starting);
        _recording = made;
        recording = made;
        logger.LogInformation("Planetary capture recording to {Path} for {Length}", path, length);
        return true;
    }

    /// <summary>Ends the recording going on, if one is: the frames it queued are still written.</summary>
    public void StopRecording() => _recording?.End("it was stopped");

    // ── Reframing on the full sensor (#1033) ─────────────────────────────────────────────────────────────────────────

    /// <summary>What a full-frame still's interlude is called, which a refusal names.</summary>
    public const string FullFrameOwner = "a full-frame still";

    /// <summary>What a reframe's interlude is called.</summary>
    public const string ReframeOwner = "a reframe";

    /// <summary>
    /// One exposure of the WHOLE sensor, unbinned, at the stream's exposure, taken with the stream stopped (an interlude), the
    /// window put back as it was and the stream started again where it was panned: what the operator reframes on (#1033).
    /// Answers the still, OWNED by the caller to release, or why none could be had, in words.
    /// </summary>
    public Task<(Image? Frame, string? Refusal)> TakeFullFrameAsync(CancellationToken cancellationToken)
        => TakeFullFrameAsync(exposure: null, cancellationToken);

    /// <summary>
    /// <see cref="TakeFullFrameAsync(CancellationToken)"/> at <paramref name="exposure"/> rather than the stream's: a star field to
    /// solve (#1684) wants seconds where a planet's stream runs at milliseconds. Null is the stream's.
    /// </summary>
    public async Task<(Image? Frame, string? Refusal)> TakeFullFrameAsync(TimeSpan? exposure, CancellationToken cancellationToken)
    {
        Image? still = null;
        var refusal = await InterludeAsync(new PlanetaryInterlude(FullFrameOwner, async token =>
        {
            var camera = Camera ?? throw new InvalidOperationException($"The {Name} has no camera");
            var (width, height, bin) = (camera.NumX, camera.NumY, (short)camera.BinX);
            ConfigureRoi(camera, 0, 0, bin: 1);
            try
            {
                still = await ExposeOnceAsync(camera, exposure ?? _streamOptions.Exposure, token);
            }
            finally
            {
                // The window the stream ran at, its size and binning; its origin is given back by the restart's jog.
                ConfigureRoi(camera, width, height, bin);
            }
        }), cancellationToken);
        return (still, refusal ?? (still is null ? "The camera gave no full frame" : null));
    }

    /// <summary>
    /// Moves the readout window to <paramref name="target"/>, snapped to the camera's rule (#1033): a pan of the running window
    /// when its size is the window's now, taken after the next frame; else the stream started again at the new size, the
    /// window put exactly there. Answers null once it is staged or done, else why not, in words.
    /// </summary>
    public async Task<string?> ReframeAsync(RoiRect target, CancellationToken cancellationToken)
    {
        if (Camera is not IVideoCameraDriver { CanJogRoi: true } video)
        {
            return $"The {Name}'s camera cannot place its window";
        }
        var snapped = video.RoiConstraints.Snap(target);
        if (Window is { } now && (now.Width, now.Height) == (snapped.Width, snapped.Height))
        {
            JogRoi(snapped.X - now.X, snapped.Y - now.Y);
            return null;
        }
        return await InterludeAsync(new PlanetaryInterlude(ReframeOwner, static _ => ValueTask.CompletedTask)
        {
            RestartAt = had => had with { RoiWidth = snapped.Width, RoiHeight = snapped.Height, Bin = 1 },
            WindowAt = snapped,
        }, cancellationToken);
    }

    // One exposure on a camera whose stream is stopped: started, waited for within the exposure and a bound, read.
    private async Task<Image?> ExposeOnceAsync(ICameraDriver camera, TimeSpan exposure, CancellationToken token)
    {
        await camera.StartExposureAsync(exposure < MinExposure ? MinExposure : exposure, FrameType.Light, token);
        var deadline = timeProvider.GetUtcNow() + exposure + TimeSpan.FromSeconds(30);
        while (!await camera.GetImageReadyAsync(token))
        {
            if (timeProvider.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"{camera.Name} gave no full frame within {exposure.TotalSeconds + 30:F0} s");
            }
            await timeProvider.SleepAsync(_readyPollInterval, token);
        }
        return await camera.GetImageAsync(token);
    }

    /// <summary>
    /// Where a recording of OTA <paramref name="otaIndex"/>'s capture starting at <paramref name="utcNow"/> goes, beside the
    /// snapshots (<c>PreviewCapture.SaveSnapshotAsync</c>): <c>Planetary/&lt;date&gt;/</c> under the image folder, named for the
    /// <paramref name="planet"/> the mount points at and the <paramref name="filterName"/> it is taken through, where known
    /// (<see cref="PlanetaryCaptureName.RecordingFileName"/>, #1179). A sequence keeps all its files in one folder, the date of its
    /// start (<paramref name="folderDate"/>), so a run past midnight is not split in two (#1340).
    /// </summary>
    public static string RecordingPath(IExternal external, int otaIndex, DateTimeOffset utcNow, CatalogIndex? planet = null, string? filterName = null,
        DateTimeOffset? folderDate = null)
        => System.IO.Path.Combine(external.ImageOutputFolder.FullName, "Planetary",
            (folderDate ?? utcNow).ToString("yyyy-MM-dd", System.Globalization.DateTimeFormatInfo.InvariantInfo),
            external.GetSafeFileName(PlanetaryCaptureName.RecordingFileName(planet, filterName, utcNow, otaIndex)));

    // ── Live capture controls (a host stages; the capture loop drains + applies) ─────────────────────────────────
    // The "adjustable while capturing" knobs, mirroring how a real planetary capture lets you tune exposure / gain / ROI
    // on the fly. A host only writes the staging fields (no driver call crosses threads); the loop applies them after
    // the next frame.

    /// <summary>Sets the per-frame exposure for the running stream (takes effect on the next frame).</summary>
    public void SetExposure(TimeSpan exposure)
        => Volatile.Write(ref _pendingExposureTicks, Math.Max(MinExposure.Ticks, exposure.Ticks));

    /// <summary>Sets the gain for the running stream (takes effect on the next frame).</summary>
    public void SetGain(int gain) => Volatile.Write(ref _pendingGain, Math.Max(0, gain));

    /// <summary>
    /// Changes the depth the running stream reads out in (<see cref="VideoCaptureOptions.BitDepth"/>): a camera restarts
    /// its stream in the new one after the next frame, and one that cannot stream in it keeps its own.
    /// </summary>
    public void SetBitDepth(BitDepth bitDepth) => Volatile.Write(ref _pendingBitDepth, (int)bitDepth);

    /// <summary>Switches the running stream's high-speed readout (<see cref="VideoCaptureOptions.HighSpeedMode"/>).</summary>
    public void SetHighSpeed(bool on) => Volatile.Write(ref _pendingHighSpeed, on ? 1 : 0);

    /// <summary>Resizes the readout window (ROI) of the running stream, snapped to the camera's ROI rule; the frame stream
    /// rebuilds at the new size on the next frame (the live stack restarts cleanly at the new framing).</summary>
    public void SetRoiSize(int width, int height)
    {
        Volatile.Write(ref _pendingRoiW, width);
        Volatile.Write(ref _pendingRoiH, height);
    }

    // Lens steps asked for while streaming, in order, each taken between two frames (a camera's own lens drive, #681).
    private readonly System.Collections.Concurrent.ConcurrentQueue<LensFocusStep> _pendingLensSteps = new();

    /// <summary>Whether the camera streaming now can drive its own lens (<see cref="ILensFocusCamera"/>): a Canon in its live view.</summary>
    public bool CanDriveLens => Volatile.Read(ref _captureActive) == 1 && _camera is ILensFocusCamera { CanDriveLens: true };

    /// <summary>A step of the camera's own lens drive, taken after the next frame; steps asked for in a row are all taken, in order.</summary>
    public void DriveLens(LensFocusStep step) => _pendingLensSteps.Enqueue(step);

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
    /// <paramref name="flipRa"/>/<paramref name="flipDec"/> invert the (uncalibrated) pixel->mount-direction mapping;
    /// <paramref name="roiJog"/> false leaves the window where it is, so the mount alone recentres.
    /// </summary>
    public void ConfigureRecenter(bool auto, bool mountJog, int deadbandPixels, double gain, bool flipRa = false, bool flipDec = false,
        bool roiJog = true)
    {
        Volatile.Write(ref _roiJogEnabled, roiJog ? 1 : 0);
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

        // A frame taken before the last jog reached the camera still shows the offset that jog corrects: measured, it
        // would be corrected twice. A camera pans while it streams on, so frames taken under the old window can arrive
        // after the pan, and a queue between the camera and this loop holds a few more (IVideoCameraDriver.IsUnderVideoRoi).
        if (camera is IVideoCameraDriver { CanJogRoi: true } panning && !panning.IsUnderVideoRoi(frame))
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
                FlipDec: Volatile.Read(ref _flipDec) == 1,
                RoiJogEnabled: Volatile.Read(ref _roiJogEnabled) == 1);

            var decision = PlanetaryRecenterController.Decide(
                com, frame.Width, frame.Height, roi, sensorW, sensorH, canJog, options);

            Interlocked.Exchange(ref _lastOffsetXBits, BitConverter.DoubleToInt64Bits(decision.OffsetX));
            Interlocked.Exchange(ref _lastOffsetYBits, BitConverter.DoubleToInt64Bits(decision.OffsetY));
            Volatile.Write(ref _lastActuator, (int)decision.Actuator);

            if (decision.RoiDx != 0 || decision.RoiDy != 0)
            {
                // Stage the pan -- ApplyPendingControlsAsync drains it onto the camera this same iteration.
                JogRoi(decision.RoiDx, decision.RoiDy);
                logger.LogDebugOffThread(
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
            // Said on every refused frame, so written off the reader too (review round 1 of #1621, N1).
            logger.LogDebugOffThread("Planetary recenter mount pulse skipped: {Reason}", verdict.Describe());
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
        var depth = Interlocked.Exchange(ref _pendingBitDepth, 0);
        var highSpeed = Interlocked.Exchange(ref _pendingHighSpeed, -1);

        // Lens steps first, each its own command: a focus moved by eye wants the next frame to show it. A step that fails is
        // said and dropped, as a control is, never the end of the stream.
        while (_pendingLensSteps.TryDequeue(out var lensStep))
        {
            try
            {
                if (camera is ILensFocusCamera lens)
                {
                    await lens.DriveLensAsync(lensStep, token);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Capture ({Capture}): the lens step {Step} failed; the stream goes on.", Name, lensStep);
            }
        }

        if (rw <= 0 && rh <= 0 && expTicks <= 0 && gain < 0 && jx == 0 && jy == 0 && depth == 0 && highSpeed < 0)
        {
            return; // nothing staged
        }

        try
        {
            if (rw > 0 && rh > 0)
            {
                // Snapped here, for every host, as the start's window is (ConfigureRoi): a size a host already snapped
                // snaps to itself, and one a client asked for over the wire must not reach the camera unsnapped.
                var snapped = camera.RoiConstraints.Snap(new RoiRect(0, 0, rw, rh));
                camera.NumX = snapped.Width;
                camera.NumY = snapped.Height;
            }

            if (video is not null && (expTicks > 0 || gain >= 0 || depth != 0 || highSpeed >= 0))
            {
                await video.ApplyVideoControlsAsync(
                    new VideoCaptureOptions(
                        expTicks > 0 ? new TimeSpan(expTicks) : TimeSpan.Zero,
                        gain >= 0 ? (short)gain : null,
                        highSpeed >= 0 ? highSpeed == 1 : null,
                        depth != 0 ? (BitDepth)depth : null),
                    token);
            }
            else
            {
                // Universal fallback path (no IVideoCameraDriver): the next exposure is the new one, and gain is still a
                // standard camera setting.
                if (expTicks > 0)
                {
                    Volatile.Write(ref _rapidExposureTicks, expTicks);
                }
                if (gain >= 0)
                {
                    await camera.SetGainAsync((short)gain, token);
                }
            }

            // What the stream runs at from here, which an interlude starts it again at (RunInterludeAsync).
            if (expTicks > 0 || gain >= 0 || depth != 0 || highSpeed >= 0)
            {
                var had = _streamOptions;
                _streamOptions = had with
                {
                    Exposure = expTicks > 0 ? new TimeSpan(expTicks) : had.Exposure,
                    Gain = gain >= 0 ? (short)gain : had.Gain,
                    HighSpeedMode = highSpeed >= 0 ? highSpeed == 1 : had.HighSpeedMode,
                    BitDepth = depth != 0 ? (BitDepth)depth : had.BitDepth,
                };
                Volatile.Write(ref _appliedOptions, _streamOptions);
            }

            if (video is { CanJogRoi: true } && (jx != 0 || jy != 0))
            {
                await video.JogRoiAsync(jx, jy, token);
            }

            // Defensive breadcrumb: these live-control changes are the actions that correlate with a stall, so log
            // exactly what was applied -- the last such line before a freeze pinpoints the trigger.
            logger.LogDebugOffThread(
                "Planetary live control applied: roi={RoiW}x{RoiH} exposureTicks={ExpTicks} gain={Gain} jog=({Jx},{Jy}) depth={Depth} highSpeed={HighSpeed}.",
                rw, rh, expTicks, gain, jx, jy, depth, highSpeed);
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
    /// Stops the capture within a bounded drain and disposes its stream, and gives back the claim of one prepared and never
    /// started. A host that stacks the stream disposes what reads it FIRST, so nothing reads a released ring.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _prepared, null)?.Claim.Dispose();

        // Bound the capture-loop drain so a slow/stuck loop can't hang shutdown. The CTS fires off the injected
        // TimeProvider (FakeTimeProvider-controllable in tests), not the raw system clock.
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider.System);
        await StopAsync(drainTimeout.Token).ConfigureAwait(false);

        // The recording's file is whole once its writer has closed it, which the loop's ending began.
        if (_recording is { } recording)
        {
            recording.End("the capture ended");
            try
            {
                await recording.Completion.WaitAsync(drainTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Planetary capture stop: the recording to {Path} was still writing", recording.Path);
            }
        }

        _stream?.Dispose();
        _stream = null;
    }
}
