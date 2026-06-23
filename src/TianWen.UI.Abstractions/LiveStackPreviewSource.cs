using System;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class LiveStackPreviewSource : IPreviewSource, IDisposable
{
    private readonly IPlanetaryFrameStream _stream;
    private readonly RollingWindowStacker _stacker;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();

    // Fallback geometry until the first master exists. The Source getter only hands this source to the
    // renderer once HasMaster is true, so these are just defensive defaults for any early accessor.
    private readonly int _expectedWidth;
    private readonly int _expectedHeight;
    private readonly int _expectedChannels;
    private readonly SensorType _expectedSensor;

    private AstroImageDocument? _doc;   // the current published master, as a stats-bearing document
    private Task<Built>? _stackTask;    // single in-flight background stack (null = idle)
    private int _target;                // latest requested playhead
    private int _built = -1;            // playhead the current _doc was built for
    private bool _disposed;

    private readonly record struct Built(AstroImageDocument Doc, int Index);

    /// <summary>
    /// Wraps <paramref name="stream"/> (which this source owns and disposes). Construct off the render
    /// thread -- it pre-warms the stream's timestamp trailer so later render-thread timestamp reads do no
    /// disk I/O.
    /// </summary>
    public LiveStackPreviewSource(IPlanetaryFrameStream stream, string path, RollingWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _path = path;
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
    public static LiveStackPreviewSource Open(string path, RollingWindowOptions? options = null)
        => new LiveStackPreviewSource(SerFrameStream.Open(path), path, options);

    /// <summary>True once at least one master has been built and is ready to display.</summary>
    public bool HasMaster => _doc is not null;

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
        if (task.IsCompletedSuccessfully)
        {
            _doc = task.Result.Doc;
            _built = task.Result.Index;
            return true;
        }

        // Faulted / cancelled: drop it. The next RequestFollow restarts toward the latest target, so a
        // transient stack failure self-heals on the following frame rather than wedging the live view.
        return false;
    }

    private void StartIfIdle()
    {
        if (_disposed || _stackTask is { IsCompleted: false })
        {
            return;
        }

        // Nothing to do when the current master already represents the requested frame.
        if (_doc is not null && _target == _built)
        {
            return;
        }

        var target = _target;
        var token = _cts.Token;
        _stackTask = Task.Run(async () =>
        {
            var master = await _stacker.StackToAsync(target, token).ConfigureAwait(false);
            // Adopt the (linear, [0,1]) master into a stats-bearing document off the render thread; None =
            // no CPU debayer (the master is already RGB / mono). This computes the stretch stats once per
            // published master -- affordable because publishes are infrequent (the window lags the playhead).
            var doc = await AstroImageDocument.AdoptImageAsync(master, DebayerAlgorithm.None, filePath: _path, cancellationToken: token).ConfigureAwait(false);
            return new Built(doc, target);
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
        _cts.Cancel();
        // The background stack reads the memory-mapped SER; it MUST finish before the stream releases the
        // mapping. ViewerController defers disposal until IsBusy is false, so this is a non-blocking safety
        // net; a single in-flight window stack drains quickly.
        try { _stackTask?.Wait(); } catch { /* faulted / cancelled stack has nothing to release */ }
        _stackTask = null;
        _cts.Dispose();
        _stream.Dispose();
    }
}
