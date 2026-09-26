using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shows a <b>live planetary capture</b>: the capture itself is <see cref="PlanetaryCapture"/> (the camera loop, the
/// frame stream, the recenter and the live controls, which the node runs too), and this stacks what it streams into a
/// <see cref="LiveStackPreviewSource"/> the 🪐 panel renders (the live rolling-window lucky-imaging stack). The
/// capture-driven counterpart of <c>ViewerController</c>, which plays back a SER file; the stack / preview /
/// wavelet-sharpen pipeline above the <see cref="IPlanetaryFrameStream"/> seam is the same.
/// <para>
/// <b>Threading.</b> <see cref="Start"/>, <see cref="Tick"/> and the <see cref="LiveStackPreviewSource"/> it drives are
/// render-thread-only (call <see cref="Tick"/> once per render frame). The capture loop runs in the background and meets
/// this only at the frame stream, which is internally locked. The live controls are the capture's
/// (<see cref="Capture"/>), which stages them for its loop.
/// </para>
/// </summary>
public sealed class PlanetaryCaptureController : IAsyncDisposable
{
    private readonly ViewerState _state;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<PlanetaryCaptureController> _logger;
    private readonly RollingWindowOptions _stackOptions;

    private LiveStackPreviewSource? _source;       // created + driven on the render thread only
    private LiveCameraFrameStream? _sourceStream;  // the stream _source wraps (render-thread); swap on rebuild

    public PlanetaryCaptureController(ViewerState state, ITimeProvider timeProvider, ILogger<PlanetaryCaptureController> logger,
        RollingWindowOptions? stackOptions = null)
    {
        _state = state;
        _timeProvider = timeProvider;
        _logger = logger;
        _stackOptions = stackOptions ?? new RollingWindowOptions();
        Capture = new PlanetaryCapture(timeProvider, logger, _stackOptions, onFrame: _ => state.NeedsRedraw = true);
    }

    /// <summary>The capture: its camera loop, live controls, recenter and telemetry.</summary>
    public PlanetaryCapture Capture { get; }

    /// <summary>True while a capture loop is running.</summary>
    public bool IsCapturing => Capture.IsCapturing;

    /// <summary>
    /// The shared <see cref="ViewerState"/> the stack reads/writes (wavelet sharpen, stretch, RAW/STACK). Exposed so the
    /// planetary view widget renders against the SAME state the controller drives, whether it's hosted as the standalone
    /// tab or as the Live Session planetary mode.
    /// </summary>
    public ViewerState ViewerState => _state;

    /// <summary>The live-stack preview source for the tab to render, or null before the first frame.</summary>
    public IPreviewSource? Source => _source;

    /// <summary>
    /// The latest display-ready ([0,1]) stacked master as an <see cref="Image"/> for a rect-bounded mini viewer to
    /// display, or null before the first stack. Render-thread only (call from <see cref="Tick"/>'s thread).
    /// </summary>
    public Image? CurrentMaster => _source?.DisplayMaster;

    /// <summary>True once the live stack has built at least one master (something is displayable).</summary>
    public bool HasMaster => _source?.HasMaster ?? false;

    /// <summary>
    /// Starts streaming from <paramref name="camera"/> (see <see cref="PlanetaryCapture.Start"/>). No-ops, giving the claim
    /// back, if a capture is already running. <paramref name="appToken"/> ties the capture to the app lifetime.
    /// </summary>
    /// <param name="claim">The claim on the camera, taken by the host; owned by the capture from here.</param>
    public void Start(ICameraDriver camera, VideoCaptureOptions options, CancellationToken appToken, DeviceLeaseSet? claim = null)
    {
        if (Capture.IsCapturing)
        {
            _logger.LogInformation("Planetary capture already running; ignoring Start.");
            claim?.Dispose();
            return;
        }

        DropSource();
        if (Capture.Start(camera, options, appToken, claim))
        {
            ShowSequence(options.Exposure);
        }
    }

    /// <summary>
    /// Starts a capture from <paramref name="profile"/>'s devices through <see cref="PlanetaryCapture.TryStart"/>, the one
    /// start rule the node keeps too, or says why it cannot.
    /// </summary>
    public bool TryStart(in PlanetaryCaptureRequest request, ProfileData profile, IDeviceHub hub, CancellationToken appToken,
        out (int Width, int Height) roi, [NotNullWhen(false)] out string? refusal)
    {
        if (Capture.IsCapturing)
        {
            roi = default;
            refusal = "A planetary capture is already running";
            return false;
        }

        DropSource();
        if (!Capture.TryStart(request, profile, hub, appToken, out roi, out refusal))
        {
            return false;
        }
        ShowSequence(request.Exposure);
        return true;
    }

    // The previous capture's source goes before the capture replaces the stream it reads. A start runs on the render
    // thread, which is the only one touching the source.
    private void DropSource()
    {
        _source?.Dispose();
        _source = null;
        _sourceStream = null;
    }

    private void ShowSequence(TimeSpan exposure)
    {
        _state.IsSequence = true;
        _state.SourceFps = (float)(1.0 / Math.Max(exposure.TotalSeconds, 1e-3));
        _state.NeedsTextureUpdate = true;
    }

    /// <summary>
    /// Render-thread drive, mirroring <c>ViewerController.TickPlayback</c>'s live-stack steps: push changed
    /// wavelet-sharpen params, publish a finished master, then follow the latest frame. Lazily creates the preview source
    /// on the first frame. Returns true when a freshly-built master was published (the caller re-uploads the texture).
    /// Call once per render frame.
    /// </summary>
    public bool Tick()
    {
        var stream = Capture.Stream;
        if (stream is null)
        {
            return false;
        }

        // The capture rebuilds the stream on a live ROI resize; drop the stale source so it's recreated against the new
        // stream below (ownsStream:false, so disposing the source never touches the stream).
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

            _source = new LiveStackPreviewSource(stream, "live://camera", _timeProvider, _stackOptions, ownsStream: false, logger: _logger);
            _sourceStream = stream;
        }

        var live = _source;
        if (_state.WaveletDirty)
        {
            live.SetSharpen(_state.BuildWaveletOptions());
            _state.WaveletDirty = false;
        }

        var published = live.TryPublishMaster();
        live.RequestFollowLatest();

        _state.FrameCount = stream.FrameCount;
        _state.FrameIndex = live.FrameIndex;
        if (published)
        {
            _state.NeedsTextureUpdate = true;
        }

        return published;
    }

    /// <summary>
    /// Render-thread stop: cancels the capture loop (it drains itself in the background) and clears the sequence flag.
    /// Use from a UI signal where awaiting the drain isn't needed; use <see cref="StopAsync"/> when you must await (tests /
    /// shutdown).
    /// </summary>
    public void Stop()
    {
        Capture.Stop();
        _state.IsSequence = false;
    }

    /// <summary>
    /// Stops the capture loop and waits for it to drain, bounded by <paramref name="drainTimeout"/>
    /// (<see cref="PlanetaryCapture.StopAsync"/>). Safe to call when not capturing.
    /// </summary>
    public async Task StopAsync(CancellationToken drainTimeout)
    {
        await Capture.StopAsync(drainTimeout).ConfigureAwait(false);
        _state.IsSequence = false;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Bound the capture-loop drain so a slow/stuck loop can't hang process shutdown (Not Responding). The CTS fires
        // off the injected TimeProvider (FakeTimeProvider-controllable in tests), not the raw system clock.
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), _timeProvider.System);
        await StopAsync(drainTimeout.Token).ConfigureAwait(false);

        // The source first -- DisposeAsync AWAITS any in-flight window stack to drain (bounded, no thread-blocking
        // .Wait()) so it stops reading the stream -- then the capture, which releases the stream (ownsStream:false on
        // the source means the source never disposes it).
        if (_source is { } source)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        _source = null;
        _sourceStream = null;
        await Capture.DisposeAsync().ConfigureAwait(false);
    }
}
