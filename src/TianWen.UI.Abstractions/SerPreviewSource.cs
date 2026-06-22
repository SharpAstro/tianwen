using System;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// An <see cref="IPreviewSource"/> backed by a SER planetary-video file: random-access frames via a
/// memory-mapped <see cref="SerReader"/>, with stretch statistics computed ONCE from frame 0 (held in an
/// inner <see cref="AstroImageDocument"/>) and reused for every frame. Per frame, <see cref="SelectFrame"/>
/// only refills reused [0,1] channel buffers (no allocation, no stats recompute), so playback stays off
/// the heavy document path. A Bayer mosaic is kept single-channel for the renderer's GPU debayer.
/// </summary>
public sealed class SerPreviewSource : IPreviewSource, IDisposable
{
    private readonly SerReader _reader;
    private readonly AstroImageDocument _statsFrame; // frame 0: stretch stats + histogram + background
    private readonly ushort[] _rawScratch;
    private readonly float[][] _channels;            // reused per-frame [0,1] buffers, [channel][y*w + x]
    private readonly SensorType _sensorType;
    private readonly int _bayerOffsetX;
    private readonly int _bayerOffsetY;
    private int _frameIndex = -1;
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
        var pixels = reader.Width * reader.Height;
        _channels = new float[channelCount][];
        for (var c = 0; c < channelCount; c++)
        {
            _channels[c] = new float[pixels];
        }

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

    /// <summary>Frame rate derived from the file's timestamps, or null when unavailable.</summary>
    public double? FramesPerSecond => _reader.FramesPerSecond;

    public int Width => _reader.Width;
    public int Height => _reader.Height;
    public int ChannelCount => _channels.Length;
    public SensorType SensorType => _sensorType;
    public int BayerOffsetX => _bayerOffsetX;
    public int BayerOffsetY => _bayerOffsetY;
    public ReadOnlySpan<float> GetChannelData(int channel) => _channels[channel];

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

    public bool SelectFrame(int index)
    {
        if (index < 0 || index >= _reader.FrameCount || index == _frameIndex)
        {
            return false;
        }

        SerImageBridge.FillUnitFloat(_reader, index, _rawScratch, _channels);
        _frameIndex = index;
        return true;
    }

    public bool HasTimestamps => _reader.HasTimestamps;

    public DateTimeOffset TimestampOf(int index)
        => _reader.HasTimestamps && (uint)index < (uint)_reader.Timestamps.Length
            ? _reader.Timestamps[index]
            : DateTimeOffset.MinValue;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
    }
}
