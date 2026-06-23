using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
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
public sealed class SerPreviewSource : IPreviewSource, ISequencePlaybackSource, IDisposable
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

    private int _frameIndex = -1;
    private Task<int>? _decodeTask; // in-flight background decode of a frame into _back; null when idle
    private bool _disposed;

    private SerPreviewSource(SerReader reader, AstroImageDocument statsFrame, int channelCount,
        SensorType sensorType, int bayerOffsetX, int bayerOffsetY)
    {
        _reader = reader;
        _statsFrame = statsFrame;
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
    }

    /// <summary>Opens a SER file and builds the source, computing frame-0 stretch statistics once.</summary>
    public static async Task<SerPreviewSource> OpenAsync(string path, CancellationToken cancellationToken = default)
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
            return new SerPreviewSource(reader, statsFrame, channelCount, sensor, ox, oy);
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

    public StretchUniforms ComputeStretchUniforms(
        StretchMode mode, StretchParameters parameters, LumaWeighting weighting = LumaWeighting.Rec709,
        float lumaBlend = 1f, bool normalize = false, int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f, float curvesMidpoint = 0.25f,
        float hdrAmount = 0f, float hdrKnee = 0.8f, float bgNeutralizationStrength = 1f)
        => _statsFrame.ComputeStretchUniforms(mode, parameters, weighting, lumaBlend, normalize, curvesMode,
            curveLut, curvesBoost, curvesMidpoint, hdrAmount, hdrKnee, bgNeutralizationStrength);

    public int FrameCount => _reader.FrameCount;
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

    /// <inheritdoc/>
    public bool TryStartDecode(int index)
    {
        if (_disposed || IsDecoding || (uint)index >= (uint)_reader.FrameCount || index == _frameIndex)
        {
            return false;
        }

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
        // the mapped pointer, or the read faults a freed pointer. Normally ViewerController defers disposal
        // until IsDecoding is false, so this is a never-blocking safety net; the decode of one small frame
        // is sub-millisecond if it is somehow still running at teardown.
        try { _decodeTask?.Wait(); } catch { /* a faulted/cancelled decode has nothing to release */ }
        _decodeTask = null;
        _reader.Dispose();
    }
}
