using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An <see cref="IPreviewSource"/> backed by a SER planetary-video file: random-access frames via a
/// memory-mapped <see cref="SerReader"/>, with stretch statistics computed ONCE from frame 0 (held in an
/// inner <see cref="AstroImageDocument"/>) and reused for every frame. A Bayer mosaic is kept
/// single-channel for the renderer's GPU debayer.
/// <para>
/// Playback decode stays off the render thread (<see cref="ISequencePlaybackSource"/>): frames are
/// double-buffered. The renderer reads the <i>front</i> buffer via <see cref="GetChannelData"/>; a
/// background decode fills the <i>back</i> buffer and <see cref="TryPublishDecoded"/> swaps them on the
/// render thread. No memory-mapped frame read, and no lazy-trailer (fps/timestamp) fault, ever happens on
/// the render thread -- the timestamp trailer is materialised once here at open (off-thread) and cached
/// in managed fields.
/// </para>
/// </summary>
public sealed class SerPreviewSource : IPreviewSource, ISequencePlaybackSource, IDisposable, IAsyncDisposable
{
    private readonly SerReader _reader;
    private readonly AstroImageDocument _statsFrame; // frame 0: stretch stats + histogram + background
    private readonly ushort[] _rawScratch;           // scratch for the synchronous SelectFrame (front) path
    private readonly ushort[] _decodeScratch;        // scratch for the background decode (back) path -- separate so the two never contend
    private float[][] _front;                        // [channel][y*w + x] -- the buffer GetChannelData returns (render thread only)
    private float[][] _back;                          // decode target for TryStartDecode (background thread writes its contents)
    private readonly SensorType _sensorType;
    private readonly int _bayerOffsetX;
    private readonly int _bayerOffsetY;

    // Timestamp/fps cached at open (off-thread). Reading SerReader.Timestamps/FramesPerSecond faults the
    // lazy trailer (a file-tail disk seek); doing that on the render thread would block the UI, so we
    // capture it once here and the render-thread accessors below read only these managed fields.
    private readonly double? _framesPerSecond;
    private readonly bool _hasTimestamps;
    private readonly ImmutableArray<DateTimeOffset> _timestamps;

    // The statistics, backgrounds and peaks of ONE frame, measured off the render thread (the strip reads the latest finished one).
    private sealed record ShownReadout(int Frame, ImageHistogram[] Statistics, float[] Background, float LumaBackground, ImmutableArray<float> Peaks);

    private volatile ShownReadout _readout;         // seeded with frame 0 in the constructor
    private Task<ShownReadout>? _readoutTask;       // in-flight measurement of a settled frame; null when idle
    private int _readoutTaskFrame = -1;             // the frame _readoutTask measures, which the shown frame may have left since
    private int _lastPolledFrame = -1;              // the shown frame at the previous poll: a frame that has not moved has settled
    private int _failedReadoutFrame = -1;

    private int _frameIndex = -1;
    private Task<int>? _decodeTask; // in-flight background decode of a frame into _back; null when idle
    private bool _disposed;

    // Clock for the bounded drain in DisposeAsync. Required and always threaded through (never an implicit
    // system clock): a test's FakeTimeProvider controls the timeout, production resolves the real
    // ITimeProvider from DI. (The sync Dispose path needs no clock -- it defers the reader release to the
    // decode's continuation rather than waiting.)
    private readonly ITimeProvider _timeProvider;

    private SerPreviewSource(SerReader reader, AstroImageDocument statsFrame, int channelCount,
        SensorType sensorType, int bayerOffsetX, int bayerOffsetY, ITimeProvider timeProvider)
    {
        _reader = reader;
        _statsFrame = statsFrame;
        _timeProvider = timeProvider;
        _sensorType = sensorType;
        _bayerOffsetX = bayerOffsetX;
        _bayerOffsetY = bayerOffsetY;
        _rawScratch = new ushort[reader.SamplesPerFrame];
        _decodeScratch = new ushort[reader.SamplesPerFrame];
        var pixels = reader.Width * reader.Height;
        _front = new float[channelCount][];
        _back = new float[channelCount][];
        for (var c = 0; c < channelCount; c++)
        {
            _front[c] = new float[pixels];
            _back[c] = new float[pixels];
        }

        // Materialise the lazy timestamp trailer ONCE here (we are off the render thread inside
        // OpenAsync's Task.Run). FramesPerSecond derives from the trailer, so this single read also
        // warms fps; the render-thread accessors then never touch the reader's trailer.
        _timestamps = reader.Timestamps;
        _hasTimestamps = !_timestamps.IsDefaultOrEmpty;
        _framesPerSecond = reader.FramesPerSecond;

        SelectFrame(0);

        // The header's facts and frame 0's peaks, once, off the render thread (the strip reads these fields only).
        var header = reader.Header;
        var span = _hasTimestamps ? PreviewCapture.SpanOf(_timestamps.Length, i => _timestamps[i]) : default;
        Capture = new PreviewCapture(header.Instrument, header.Telescope, header.Observer, ColourWords(header.ColorId),
            header.PixelDepthPerPlane, header.FrameCount,
            span.Earliest, span.Latest, span.FramesPerSecond);
        _readout = new ShownReadout(0, _statsFrame.ChannelStatistics, _statsFrame.MeasuredPerChannelBackground, _statsFrame.MeasuredLumaBackground,
            PeaksOf(_front, _statsFrame.ChannelStatistics.Length, sensorType, bayerOffsetX, bayerOffsetY, reader.Width));
    }

    /// <summary>The frame the statistics table describes: the shown one once its measurement has finished, the last one measured until then.</summary>
    public int? StatisticsFrame => _readout.Frame;

    /// <summary>True while the shown frame's statistics are still to be measured, so the render loop keeps polling for them.</summary>
    public bool ReadoutPending => !_disposed && _failedReadoutFrame != _frameIndex && (_readoutTask is not null || _frameIndex != _readout.Frame);

    /// <summary>Test seam: whether a measurement of a shown frame is in flight.</summary>
    internal bool IsMeasuringReadout => _readoutTask is not null;

    /// <summary>
    /// Render thread, once per tick: takes a finished measurement, and once the shown frame has stopped moving (the same at two polls,
    /// no decode in flight) starts measuring it off-thread. Returns true when the table changed and the strip should repaint.
    /// </summary>
    public bool PollShownFrameReadout()
    {
        var changed = false;
        if (_readoutTask is { IsCompleted: true } done)
        {
            _readoutTask = null;
            if (done.IsCompletedSuccessfully)
            {
                _readout = done.Result;
                changed = true;
            }
            else
            {
                _failedReadoutFrame = _readoutTaskFrame;
            }
        }

        var shown = _frameIndex;
        if (_disposed || _readoutTask is not null || IsDecoding || shown == _readout.Frame || shown == _failedReadoutFrame || shown < 0)
        {
            _lastPolledFrame = -1;
            return changed;
        }

        if (shown != _lastPolledFrame)
        {
            _lastPolledFrame = shown;
            return changed;
        }

        _readoutTaskFrame = shown;
        _readoutTask = Task.Run(() => MeasureFrameAsync(shown));
        return changed;
    }

    private async Task<ShownReadout> MeasureFrameAsync(int index)
    {
        var image = SerImageBridge.ToImage(_reader, index);
        var document = await AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None);

        var planes = new float[_front.Length][];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = new float[_reader.Width * _reader.Height];
        }
        SerImageBridge.FillUnitFloat(_reader, index, new ushort[_reader.SamplesPerFrame], planes);
        return new ShownReadout(index, document.ChannelStatistics, document.MeasuredPerChannelBackground, document.MeasuredLumaBackground,
            PeaksOf(planes, document.ChannelStatistics.Length, _sensorType, _bayerOffsetX, _bayerOffsetY, _reader.Width));
    }

    /// <summary>The capture's header facts, cached at open.</summary>
    public PreviewCapture Capture { get; }

    /// <summary>The brightest sample per statistics channel of <see cref="StatisticsFrame"/>, on the unit scale.</summary>
    public ImmutableArray<float> ChannelPeaks => _readout.Peaks;

    ImageHistogram[] IPreviewSource.MeasuredChannelStatistics => _readout.Statistics;
    float[] IPreviewSource.MeasuredPerChannelBackground => _readout.Background;
    float IPreviewSource.MeasuredLumaBackground => _readout.LumaBackground;

    private static string ColourWords(SerColorId id) => id switch
    {
        SerColorId.Mono => "Mono",
        SerColorId.Rgb => "RGB",
        SerColorId.Bgr => "BGR",
        _ => id.ToString() is ['B', 'a', 'y', 'e', 'r', .. var pattern] ? $"{pattern} Bayer" : id.ToString(),
    };

    // A mosaic has one plane but three statistics channels (R, G, B by photosite colour), so its peaks are read per photosite colour.
    private static ImmutableArray<float> PeaksOf(float[][] planes, int statisticsChannels, SensorType sensor, int ox, int oy, int width)
    {
        if (sensor is SensorType.RGGB && planes.Length == 1 && statisticsChannels == 3)
        {
            var plane = planes[0];
            var peaks = new float[3];
            for (var i = 0; i < plane.Length; i++)
            {
                var x = (i % width) + ox;
                var y = (i / width) + oy;
                var colour = (x & 1) == 0 && (y & 1) == 0 ? 0 : (x & 1) == 1 && (y & 1) == 1 ? 2 : 1;
                if (plane[i] > peaks[colour])
                {
                    peaks[colour] = plane[i];
                }
            }
            return [.. peaks];
        }

        var result = ImmutableArray.CreateBuilder<float>(planes.Length);
        foreach (var plane in planes)
        {
            var peak = 0f;
            foreach (var value in plane)
            {
                peak = Math.Max(peak, value);
            }
            result.Add(peak);
        }
        return result.ToImmutable();
    }

    /// <summary>Opens a SER file and builds the source, computing frame-0 stretch statistics once.</summary>
    public static async Task<SerPreviewSource> OpenAsync(string path, ITimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        var reader = SerReader.Open(path);
        try
        {
            // SerReader.Open is synchronous and uncancellable; if this load was superseded while it
            // ran (the user navigated to another file), bail before the decode + stretch-stats pass.
            cancellationToken.ThrowIfCancellationRequested();
            var (sensor, ox, oy) = reader.ColorId.ToSensorType();
            var channelCount = reader.ColorId.IsColor ? 3 : 1;
            // Frame 0 establishes the stretch / histogram / background stats (reused for all frames). Match
            // the FITS RGGB path: don't CPU-debayer a mosaic -- the GPU shader debayers it.
            var frame0 = SerImageBridge.ToImage(reader, 0);
            var statsFrame = await AstroImageDocument.AdoptImageAsync(
                frame0, DebayerAlgorithm.None, filePath: path, cancellationToken: cancellationToken);
            return new SerPreviewSource(reader, statsFrame, channelCount, sensor, ox, oy, timeProvider);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Frame rate derived from the file's timestamps, or null when unavailable. Cached at open.</summary>
    public double? FramesPerSecond => _framesPerSecond;

    public int Width => _reader.Width;
    public int Height => _reader.Height;
    public int ChannelCount => _front.Length;
    public SensorType SensorType => _sensorType;
    public int BayerOffsetX => _bayerOffsetX;
    public int BayerOffsetY => _bayerOffsetY;
    public ReadOnlySpan<float> GetChannelData(int channel) => _front[channel];

    // Stats / stretch / histogram all come from the frame-0 document (computed once).
    public ImageHistogram[] ChannelStatistics => _statsFrame.ChannelStatistics;
    public float[] PerChannelBackground => _statsFrame.PerChannelBackground;
    public float LumaBackground => _statsFrame.LumaBackground;

    // The capture's header, as frame 0 carries it: every frame of a SER shares it but the timestamp.
    ImageMeta? IPreviewSource.ImageMeta => ((IPreviewSource)_statsFrame).ImageMeta;

    // The divisor SerImageBridge.FillUnitFloat scales every frame by: 255 for an 8-bit capture, the depth's
    // full scale for a 16-bit one.
    float? IPreviewSource.FullScaleAdu => _reader.MaxSampleValue;

    public StretchUniforms ComputeStretchUniforms(
        StretchMode mode, StretchParameters parameters, LumaWeighting weighting = LumaWeighting.Rec709,
        float lumaBlend = 1f, bool normalize = false, int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f, float curvesMidpoint = 0.25f,
        float hdrAmount = 0f, float hdrKnee = 0.8f, float bgNeutralizationStrength = 1f,
        (float R, float G, float B)? manualWhiteBalance = null, bool applyColorCalibration = true)
        => _statsFrame.ComputeStretchUniforms(mode, parameters, weighting, lumaBlend, normalize, curvesMode,
            curveLut, curvesBoost, curvesMidpoint, hdrAmount, hdrKnee, bgNeutralizationStrength, manualWhiteBalance,
            applyColorCalibration);

    public int FrameCount => _reader.FrameCount;

    /// <summary>The capture's header's Telescope field, which a TianWen recording fills with its optics (#1179).</summary>
    public string Telescope => _reader.Header.Telescope;

    /// <summary>SER is a planetary/lunar capture stream; see <see cref="IPreviewSource.IsVideoStream"/>.</summary>
    public bool IsVideoStream => true;
    public int FrameIndex => _frameIndex;

    /// <summary>
    /// Synchronous front-buffer fill. Used only to seed frame 0 at open (off-thread). The hot
    /// playback/scrub path goes through <see cref="TryStartDecode"/>/<see cref="TryPublishDecoded"/>
    /// instead, never this -- so it is never called on the render thread. No-op while a background decode
    /// is in flight (the front buffer is the renderer's; only the decode-ahead path mutates frames once
    /// playback is live).
    /// </summary>
    public bool SelectFrame(int index)
    {
        if (index < 0 || index >= _reader.FrameCount || index == _frameIndex || IsDecoding)
        {
            return false;
        }

        SerImageBridge.FillUnitFloat(_reader, index, _rawScratch, _front);
        _frameIndex = index;
        return true;
    }

    // --- ISequencePlaybackSource: off-thread decode-ahead ---

    /// <inheritdoc/>
    public bool IsDecoding => _decodeTask is { IsCompleted: false };

    /// <inheritdoc/>
    public bool IsDecodeReady => _decodeTask is { IsCompletedSuccessfully: true };

    /// <summary>How many frame decodes this source has started, which a test counts to show a view costs none (#1875).</summary>
    internal int DecodesStarted { get; private set; }

    /// <inheritdoc/>
    public bool TryStartDecode(int index)
    {
        if (_disposed || IsDecoding || (uint)index >= (uint)_reader.FrameCount || index == _frameIndex)
        {
            return false;
        }

        DecodesStarted++;

        // Decode into the back buffer on a background thread; the reader does a memory-mapped frame read
        // (the only disk-touching step) entirely off the render thread. _back + _decodeScratch are owned
        // by this single in-flight task (IsDecoding gates re-entry), and the reader read is concurrency-
        // safe with the render thread reading the disjoint front buffer.
        var target = _back;
        _decodeTask = Task.Run(() =>
        {
            SerImageBridge.FillUnitFloat(_reader, index, _decodeScratch, target);
            return index;
        });
        return true;
    }

    /// <inheritdoc/>
    public bool TryPublishDecoded(out int frameIndex)
    {
        var published = false;
        if (_decodeTask is { IsCompleted: true } task)
        {
            _decodeTask = null;
            if (task.IsCompletedSuccessfully)
            {
                // Swap on the render thread: the just-decoded back buffer becomes the front the renderer
                // reads; the old front becomes the next decode target. The prior upload of the old front
                // completed in an earlier frame, so reusing it as the back buffer is safe.
                (_front, _back) = (_back, _front);
                _frameIndex = task.Result;
                published = true;
            }
        }

        frameIndex = _frameIndex;
        return published;
    }

    public bool HasTimestamps => _hasTimestamps;

    public DateTimeOffset TimestampOf(int index)
        => _hasTimestamps && (uint)index < (uint)_timestamps.Length
            ? _timestamps[index]
            : DateTimeOffset.MinValue;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The background decode reads the memory-mapped file; it MUST complete before the reader releases
        // the mapped pointer, or the read faults a freed pointer -- but we NEVER block a thread to wait (no
        // .Wait()). Normally ViewerController defers disposal until IsDecoding is false, so the task is
        // already done and we free inline. If one is still in flight (e.g. at FITS-viewer shutdown), defer
        // the reader release to a continuation that runs once the (uncancellable, sub-ms) decode finishes,
        // rather than tearing the mapping out from under it. A caller that must await the drain uses
        // DisposeAsync.
        if (PendingWork() is { IsCompleted: false } task)
        {
            task.ContinueWith(
                static (_, s) =>
                {
                    if (s is SerPreviewSource self)
                    {
                        self.DisposeCore();
                    }
                }, this, TaskScheduler.Default);
        }
        else
        {
            DisposeCore();
        }
    }

    /// <summary>
    /// Async disposal that <b>awaits</b> the in-flight frame decode to drain before releasing the reader --
    /// for callers that dispose in an async context (e.g. <c>ViewerController.ShutdownAsync</c>). Never
    /// blocks a thread: the await is non-blocking and bounded via the injected <see cref="ITimeProvider"/>.
    /// The decode is uncancellable (a single sub-ms memory-mapped read), so on the pathological timeout we
    /// defer the reader release to the decode's continuation rather than freeing the mapping while the read
    /// is still touching it (use-after-free).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (PendingWork() is { } task)
        {
            try
            {
                // Bounded via the injected TimeProvider (a test's FakeTimeProvider controls it) -- not the
                // raw system clock -- per the project's time-provider discipline.
                await task.WaitAsync(TimeSpan.FromSeconds(3), _timeProvider.System).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The uncancellable decode is still reading the mapped file; freeing the reader now would
                // fault a freed pointer. Defer the release to a continuation that runs once the read finishes
                // (intentionally not awaited -- the disposing caller must not block on a stuck disk read).
                _ = task.ContinueWith(
                static (_, s) =>
                {
                    if (s is SerPreviewSource self)
                    {
                        self.DisposeCore();
                    }
                }, this, TaskScheduler.Default);
                return;
            }
            catch
            {
                // A faulted decode threw before completing the read; it has nothing left to release.
            }
        }
        DisposeCore();
    }

    // The decode and the readout both read the mapped file; the reader is released only after both have stopped.
    private Task? PendingWork() => (_decodeTask, _readoutTask) switch
    {
        ({ } d, { } r) => Task.WhenAll(d, r),
        ({ } d, null) => d,
        (null, { } r) => r,
        _ => null,
    };

    // Frees the decode-task reference and the memory-mapped reader. Runs only after any in-flight decode has
    // stopped reading the mapping -- the sync path defers it to the decode's continuation (or runs it inline
    // when not decoding); the async path awaits the decode first.
    private void DisposeCore()
    {
        _decodeTask = null;
        _reader.Dispose();
    }
}
