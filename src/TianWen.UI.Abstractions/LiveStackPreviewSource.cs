using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An <see cref="IPreviewSource"/> that shows a <b>live rolling-window lucky-imaging stack</b> of a SER
/// planetary video, following the raw playhead. Phase 9 of the planetary plan, and the "switch raw -&gt;
/// stacked" the viewer offers: the user toggles between the raw <see cref="SerPreviewSource"/> and this,
/// both previewing the same file, and this one shows the quality-weighted, globally-aligned stack of the
/// ~5 min of capture time ending at the current frame.
/// <para>
/// <b>Follow-the-playhead, off-thread.</b> <see cref="RequestFollow"/> (render thread) sets the target
/// frame; a single background task at a time runs <see cref="RollingWindowStacker.StackToAsync"/> toward
/// the latest target (so a fast-moving playhead naturally coalesces -- the stack lags "a bit behind",
/// folding several frames per published master). Each built master is adopted into a fresh
/// <see cref="AstroImageDocument"/> (off the render thread, computing its stretch stats), and
/// <see cref="TryPublishMaster"/> (render thread) swaps it in. The image / stretch members delegate to
/// that document, so the renderer's GPU debayer is irrelevant here (the master is already demosaiced RGB)
/// but its stretch / white-balance / zoom-pan all work for free, in both <c>tianwen-fits</c> and the GUI
/// viewer tab. The sequence members (<see cref="FrameCount"/>, timestamps) come from the stream so the
/// transport bar still reflects the raw playhead while the stacked image is shown.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="RequestFollow"/> / <see cref="TryPublishMaster"/> / the
/// <see cref="IPreviewSource"/> accessors are all render-thread-only (driven from
/// <c>ViewerController.TickPlayback</c> and <c>Render</c>, same loop iteration). The background stack task
/// is a pure producer that hands its result back through its <see cref="Task{TResult}"/> -- the lock-free
/// hand-off the project prefers over a lock. It owns the <see cref="RollingWindowStacker"/> (and thus the
/// stream's frame reads) exclusively while running.
/// </para>
/// </summary>
public sealed class LiveStackPreviewSource : IPreviewSource, IDisposable, IAsyncDisposable
{
    private readonly IPlanetaryFrameStream _stream;
    private readonly bool _ownsStream;
    private readonly RollingWindowStacker _stacker;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();

    // Fallback geometry until the first master exists. The Source getter only hands this source to the
    // renderer once HasMaster is true, so these are just defensive defaults for any early accessor.
    private readonly int _expectedWidth;
    private readonly int _expectedHeight;
    private readonly int _expectedChannels;
    private readonly SensorType _expectedSensor;

    private AstroImageDocument? _doc;   // the current published master (sharpened), as a stats-bearing document
    private Image? _rawMaster;          // the latest UN-sharpened stacker output, kept so a wavelet-param
                                        // change can re-sharpen without re-running the window integration
    private Image? _displayMaster;      // the [0,1]-normalised display master (== _doc's adopted image),
                                        // exposed so a mini viewer can render the stack without the doc
    private int _builtRaw = -1;         // playhead _rawMaster was stacked for
    private Task<Built>? _stackTask;    // single in-flight background stack/sharpen (null = idle)
    private int _target;                // latest requested playhead
    private int _built = -1;            // playhead the current _doc was built for
    private WaveletSharpenOptions? _requestedSharpen; // latest wavelet params (null = sharpening off)
    private bool _sharpenDirty;         // wavelet params changed -> rebuild the display even if the playhead didn't move
    private CancellationTokenSource? _workCts; // per in-flight task, linked to _cts; cancelled to preempt a stale stack
    private bool _inFlightIsStack;      // the in-flight task is a (slow) window stack, eligible for sharpen-preempt
    private bool _disposed;

    // Clock for the bounded drain in DisposeAsync. Required and always threaded through (never an implicit
    // system clock): a test's FakeTimeProvider then controls the timeout, and production resolves the real
    // ITimeProvider from DI. We read its BCL TimeProvider via .System for WaitAsync(TimeSpan, TimeProvider).
    private readonly ITimeProvider _timeProvider;

    // Identity wavelet (all-1 gains, no denoise) reconstructs the input exactly -- used as the "sharpening
    // off" case so the display image is always a FRESH copy to adopt (AdoptImageAsync normalises in place),
    // never the cached raw master.
    private static readonly WaveletSharpenOptions IdentitySharpen = WaveletSharpenOptions.Uniform(6, 1f);

    // A finished background result. Stacked=false means a sharpen-only re-render (the cached raw master was
    // reused, the playhead did not advance); Stacked=true means the window was re-integrated to Playhead.
    private readonly record struct Built(AstroImageDocument Doc, Image RawMaster, Image Display, int Playhead, bool Stacked);

    /// <summary>
    /// Wraps <paramref name="stream"/>. Construct off the render thread -- it pre-warms the stream's
    /// timestamp trailer so later render-thread timestamp reads do no disk I/O. <paramref name="timeProvider"/>
    /// is required and bounds the <see cref="DisposeAsync"/> drain (never an implicit system clock). When
    /// <paramref name="ownsStream"/> is <see langword="true"/> (the default, for a file-backed SER stream
    /// scoped to this view) the stream is disposed with this source; pass <see langword="false"/> for a
    /// live camera stream that the capture session owns and that outlives any one preview.
    /// </summary>
    public LiveStackPreviewSource(
        IPlanetaryFrameStream stream, string path, ITimeProvider timeProvider,
        RollingWindowOptions? options = null, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _stream = stream;
        _ownsStream = ownsStream;
        _path = path;
        _timeProvider = timeProvider;
        _stacker = new RollingWindowStacker(stream, options);

        // Master geometry once stacked: a split-CFA source demosaics back to the full mosaic resolution;
        // mono / RGB stay at the stream's plane size. Channels: mono -> 1, everything else -> 3 (RGB).
        var split = stream.Layout == PlanetaryFrameLayout.SplitCfa;
        _expectedWidth = split ? stream.Width * 2 : stream.Width;
        _expectedHeight = split ? stream.Height * 2 : stream.Height;
        _expectedChannels = stream.Layout == PlanetaryFrameLayout.Mono ? 1 : 3;
        _expectedSensor = _expectedChannels == 1 ? SensorType.Monochrome : SensorType.Color;

        // Fault the lazy timestamp trailer once here (off the render thread) so the transport bar's
        // per-frame TimestampOf reads hit the warm cache instead of a file-tail seek on the UI thread.
        _ = _stream.HasTimestamps;
    }

    /// <summary>Opens <paramref name="path"/> as a live-stack source over its own SER reader.</summary>
    public static LiveStackPreviewSource Open(string path, ITimeProvider timeProvider, RollingWindowOptions? options = null)
        => new LiveStackPreviewSource(SerFrameStream.Open(path), path, timeProvider, options);

    /// <summary>True once at least one master has been built and is ready to display.</summary>
    public bool HasMaster => _doc is not null;

    /// <summary>
    /// The latest <b>display-ready</b> master: the [0,1]-normalised, optionally wavelet-sharpened stack
    /// image (the same pixels <see cref="_doc"/> shows), or null before the first stack. Exposed for a
    /// live-camera consumer (the GUI planetary tab) that renders the stack into a content rect via an
    /// <c>IMiniViewerWidget</c> rather than the full FITS-viewer chrome. Render-thread only, like the other
    /// follow/publish members. (The raw stacker output is in the input ADU scale and not display-ready, so
    /// it is deliberately not exposed.)
    /// </summary>
    public Image? DisplayMaster => _displayMaster;

    /// <summary>True while a background stack is running (the stream's reader is in use -- don't dispose).</summary>
    public bool IsBusy => _stackTask is { IsCompleted: false };

    // --- Follow-the-playhead drive (render thread) ---

    /// <summary>
    /// Requests the stack to follow to frame <paramref name="playhead"/>. Coalesces: only the latest
    /// target matters, and at most one background stack runs at a time. Call every tick while the stacked
    /// view is shown; cheap when already at the target or already stacking.
    /// </summary>
    public void RequestFollow(int playhead)
    {
        _target = playhead;
        StartIfIdle();
    }

    /// <summary>
    /// Requests the stack to follow the latest frame the stream has (for a live camera with no playhead:
    /// the EAA free-run case). No-ops until the first frame has been pushed. Render-thread only.
    /// </summary>
    public void RequestFollowLatest()
    {
        var count = _stream.FrameCount;
        if (count > 0)
        {
            RequestFollow(count - 1);
        }
    }

    /// <summary>
    /// Sets the wavelet-sharpen parameters applied to the stacked master (<c>null</c> = off). Re-sharpens
    /// the cached master off-thread on the next idle slot -- a slider drag does NOT re-run the window
    /// integration, only the (cheap) wavelet pass. Render-thread only; coalesces like
    /// <see cref="RequestFollow"/>.
    /// </summary>
    public void SetSharpen(WaveletSharpenOptions? options)
    {
        _requestedSharpen = options;
        _sharpenDirty = true;
        StartIfIdle();
    }

    /// <summary>
    /// If the in-flight stack finished, swaps its master in as the displayed document and returns true (the
    /// caller re-uploads the texture). Render-thread only; no I/O. Call before <see cref="RequestFollow"/>
    /// each tick so a completed result is consumed before the next stack is kicked.
    /// </summary>
    public bool TryPublishMaster()
    {
        if (_stackTask is not { IsCompleted: true } task)
        {
            return false;
        }

        _stackTask = null;
        var published = false;
        if (task.IsCompletedSuccessfully)
        {
            var b = task.Result;
            // Cache the freshly-integrated master on the render thread (so the background side never shares
            // these fields); a sharpen-only result reused the existing one and leaves them untouched.
            if (b.Stacked)
            {
                _rawMaster = b.RawMaster;
                _builtRaw = b.Playhead;
            }
            _doc = b.Doc;
            _displayMaster = b.Display;
            _built = b.Playhead;
            published = true;
        }
        // else: cancelled (preempted) or faulted -> drop it. A cancelled stack invalidated its window, so
        // its next StackToAsync rebuilds; the live view self-heals rather than wedging.

        // Pump any work deferred while this task ran -- the preempting sharpen, or a playhead that moved on.
        StartIfIdle();
        return published;
    }

    private void StartIfIdle()
    {
        if (_disposed)
        {
            return;
        }

        if (_stackTask is { IsCompleted: false })
        {
            // A task is already running. If the wavelet params just changed and the in-flight task is a
            // (slow) window stack, PREEMPT it so the cheap sharpen takes the slot now -- sharpen priority,
            // even mid-stack. Playback stacks (no sharpen change) are left to finish and coalesce, so steady
            // playhead advance never thrashes. The cancelled stack invalidates its window (StackToAsync) and
            // re-pumps from TryPublishMaster.
            if (_sharpenDirty && _inFlightIsStack && _rawMaster is not null)
            {
                _workCts?.Cancel();
            }
            return;
        }

        // A window stack needs at least one buffered frame; a live camera stream starts empty, so guard
        // against kicking a stack the RollingWindowStacker would reject (it throws on an empty stream). A
        // file-backed SER stream always reports a positive count, so this is a no-op for that path.
        var hasFrames = _stream.FrameCount > 0;
        var needStack = hasFrames && (_rawMaster is null || _target != _builtRaw); // master stale vs the requested playhead
        var needSharpen = _sharpenDirty && _rawMaster is not null;                 // re-sharpen only an existing master
        if (!needStack && !needSharpen)
        {
            return; // nothing to do yet
        }

        // SHARPEN PRIORITY: if the wavelet params changed and we already have a master, re-sharpen THAT now
        // and defer the (slower) window re-stack to a later iteration. A slider drag then gets instant
        // feedback off the cached master even while playback would otherwise keep re-stacking every tick.
        var doStack = needStack && !(needSharpen && _rawMaster is not null);

        var target = _target;
        var sharpen = _requestedSharpen;
        var rawForSharpen = _rawMaster;                       // captured on the render thread; immutable snapshot
        var resultPlayhead = doStack ? target : _builtRaw;     // a sharpen-only result keeps the displayed playhead
        _sharpenDirty = false;
        _inFlightIsStack = doStack;

        // Fresh per-work CTS linked to the lifetime token, so a newer request can cancel just this task
        // (not tear down the whole source). The previous task has completed (we are past the in-flight
        // guard above), so disposing its CTS here is safe.
        _workCts?.Dispose();
        _workCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _workCts.Token;
        _stackTask = Task.Run(async () =>
        {
            // Re-stack only when following the playhead; a sharpen-only pass reuses the captured master so it
            // re-runs just the (cheap) wavelet pass, not the window integration.
            var raw = doStack
                ? await _stacker.StackToAsync(target, token).ConfigureAwait(false)
                : rawForSharpen!;

            // Always produce a FRESH image to adopt (AdoptImageAsync normalises in place); identity gains
            // when sharpening is off, so the cached raw master is never consumed. WaveletSharpen.Sharpen
            // returns a new image and leaves the raw master intact for the next re-sharpen.
            var display = WaveletSharpen.Sharpen(raw, sharpen ?? IdentitySharpen);

            // Adopt the (linear, [0,1]) display master into a stats-bearing document off the render thread;
            // None = no CPU debayer (already RGB / mono).
            var doc = await AstroImageDocument.AdoptImageAsync(display, DebayerAlgorithm.None, filePath: _path, cancellationToken: token).ConfigureAwait(false);
            // `display` is normalised to [0,1] in place by AdoptImageAsync and its arrays are now owned by
            // `doc`; we retain it read-only as the display master (a mini viewer renders it; doc renders the
            // same pixels via IPreviewSource).
            return new Built(doc, raw, display, resultPlayhead, doStack);
        }, token);
    }

    // --- IPreviewSource: image + stretch members delegate to the current master document ---

    // AstroImageDocument implements the geometry members EXPLICITLY, so they are reachable only through an
    // IPreviewSource reference (the implicit conversion from _doc); the stats members below are public.
    private IPreviewSource? Img => _doc;

    public int Width => Img?.Width ?? _expectedWidth;
    public int Height => Img?.Height ?? _expectedHeight;
    public int ChannelCount => Img?.ChannelCount ?? _expectedChannels;
    public SensorType SensorType => Img?.SensorType ?? _expectedSensor;
    public int BayerOffsetX => 0; // the master is already demosaiced -> never a raw mosaic
    public int BayerOffsetY => 0;

    public ReadOnlySpan<float> GetChannelData(int channel)
        => Img is { } img ? img.GetChannelData(channel) : ReadOnlySpan<float>.Empty;

    public ImageHistogram[] ChannelStatistics => _doc?.ChannelStatistics ?? [];
    public float[] PerChannelBackground => _doc?.PerChannelBackground ?? [];
    public float LumaBackground => _doc?.LumaBackground ?? 0f;

    public StretchUniforms ComputeStretchUniforms(
        StretchMode mode, StretchParameters parameters, LumaWeighting weighting = LumaWeighting.Rec709,
        float lumaBlend = 1f, bool normalize = false, int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f, float curvesMidpoint = 0.25f,
        float hdrAmount = 0f, float hdrKnee = 0.8f, float bgNeutralizationStrength = 1f,
        (float R, float G, float B)? manualWhiteBalance = null)
        => _doc is { } doc
            ? doc.ComputeStretchUniforms(mode, parameters, weighting, lumaBlend, normalize, curvesMode,
                curveLut, curvesBoost, curvesMidpoint, hdrAmount, hdrKnee, bgNeutralizationStrength, manualWhiteBalance)
            : new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);

    // --- IPreviewSource: sequence members come from the stream (so the transport tracks the raw playhead) ---

    public int FrameCount => _stream.FrameCount;
    public int FrameIndex => _built;
    public bool SelectFrame(int index) => false; // the live stack follows the raw playhead, not a direct seek
    public bool HasTimestamps => _stream.HasTimestamps;

    public DateTimeOffset TimestampOf(int index)
        => _stream.TimestampOf(index) ?? DateTimeOffset.MinValue;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel(); // cancels the linked per-work CTS too; the stacker loops poll it and unwind
        // The background stack reads the memory-mapped SER; it MUST finish before the stream releases the
        // mapping -- but we NEVER block a thread to wait (no .Wait()). ViewerController only disposes once
        // IsBusy is false (a busy source is deferred to _pendingDispose), so the task is normally already
        // done and we free inline. If one is still in flight (e.g. at FITS-viewer shutdown), defer the
        // resource release to a continuation that runs once the (now-cancelled) stack unwinds, rather than
        // tearing the stream out from under it. A caller that must await the drain uses DisposeAsync.
        if (_stackTask is { IsCompleted: false } task)
        {
            task.ContinueWith(static (_, s) => ((LiveStackPreviewSource)s!).DisposeCore(), this, TaskScheduler.Default);
        }
        else
        {
            DisposeCore();
        }
    }

    /// <summary>
    /// Async disposal that <b>awaits</b> the in-flight window stack to drain before releasing the stream --
    /// for callers that dispose WITHOUT gating on <see cref="IsBusy"/> (e.g. <c>PlanetaryCaptureController</c>
    /// at shutdown). Never blocks a thread: the await is non-blocking and bounded (a slow/stuck stack can't
    /// stall process exit -- the stacker loops poll the token we cancel, so the drain is normally instant).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel(); // cancels the linked per-work CTS too; the stacker loops poll it and unwind
        if (_stackTask is { } task)
        {
            try
            {
                // Bounded via the injected TimeProvider (a test's FakeTimeProvider controls it) -- not the
                // raw system clock -- per the project's time-provider discipline.
                await task.WaitAsync(TimeSpan.FromSeconds(3), _timeProvider.System).ConfigureAwait(false);
            }
            catch
            {
                // Faulted / cancelled / timed-out stack has nothing left to release; fall through to free.
            }
        }
        DisposeCore();
    }

    // Frees the per-work + lifetime CTS and (when owned) the stream. Runs only after any in-flight stack has
    // stopped reading the stream -- the sync path defers it to the stack's continuation (or runs it inline
    // when not busy); the async path awaits the stack first.
    private void DisposeCore()
    {
        _stackTask = null;
        _workCts?.Dispose();
        _cts.Dispose();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
