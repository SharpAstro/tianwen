using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// In-memory <see cref="IIntegrationSink"/>: backs the canvas with a
/// managed <c>float[][,]</c>. This is the default path the integrators
/// use today, factored out from inline <see cref="Image.CreateChannelData"/>
/// calls so Phase 10's mmap sink can drop in via the same interface.
/// </summary>
/// <remarks>
/// Allocation cost: <c>channelCount * width * height * sizeof(float)</c>.
/// On the 244-frame SoL canvas (3008² × 3 ch) that's ~108 MB sitting in
/// the GC heap until <see cref="FinaliseAsImage"/> hands the array to the
/// <see cref="Image"/>. Dispose is a no-op -- the array survives because
/// the returned Image holds the only reference.
/// </remarks>
internal sealed class ArraySink : IIntegrationSink
{
    private readonly float[][,] _data;

    public ArraySink(int channelCount, int width, int height)
    {
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        _data = Image.CreateChannelData(channelCount, height, width);
        Shape = (channelCount, width, height);
    }

    public (int ChannelCount, int Width, int Height) Shape { get; }

    public Span<float> GetRow(int channel, int row)
        => MemoryMarshal.CreateSpan(ref _data[channel][row, 0], Shape.Width);

    public Image FinaliseAsImage(BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta meta)
        => new Image(_data, bitDepth, maxValue, minValue, pedestal, meta);

    public void Dispose()
    {
        // No-op: the managed array is owned by the Image returned from
        // FinaliseAsImage. Caller-after-finalise sees a no-op disposer,
        // caller-before-finalise leaks the buffer to GC (which then reclaims).
    }
}
