using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A single image channel: a 2D float array with associated metadata.
/// Channels are the fundamental unit of image data — cameras produce them,
/// debayer writes into them, and viewers upload them to the GPU.
/// </summary>
/// <param name="Data">Pixel data in row-major [Height, Width] layout.</param>
/// <param name="Filter">Filter used for this channel (e.g. Luminance, H-Alpha, Red).</param>
/// <param name="MinValue">Minimum pixel value in the data.</param>
/// <param name="MaxValue">Maximum pixel value in the data.</param>
/// <param name="Index">Channel index within the parent image (0 for mono, 0-2 for RGB).</param>
public readonly record struct Channel(float[,] Data, Filter Filter, float MinValue, float MaxValue, byte Index)
{
    /// <summary>Image height (number of rows).</summary>
    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data.GetLength(0);
    }

    /// <summary>Image width (number of columns).</summary>
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data.GetLength(1);
    }

    /// <summary>Total number of pixels (Height × Width).</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data.Length;
    }

    /// <summary>Returns a flat read-only span over all pixel data (row-major).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(ref Data[0, 0], Data.Length);

    /// <summary>Returns a flat writable span over all pixel data (row-major).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> AsMutableSpan()
        => MemoryMarshal.CreateSpan(ref Data[0, 0], Data.Length);

    /// <summary>Pixel value at (row, col).</summary>
    public float this[int row, int col]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[row, col];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[row, col] = value;
    }

    /// <summary>
    /// Creates a new channel with the specified dimensions and metadata.
    /// </summary>
    public static Channel Create(int height, int width, Filter filter = default, byte index = 0)
        => new Channel(new float[height, width], filter, 0f, 0f, index);

    /// <summary>
    /// Transposes and converts W×H <c>int[,]</c> source data (ASCOM convention) to a H×W <see cref="Channel"/>.
    /// </summary>
    public static Channel FromWxHImageData(int[,] sourceData)
    {
        var width = sourceData.GetLength(0);
        var height = sourceData.GetLength(1);

        var maxValue = 0f;
        var minValue = float.MaxValue;
        var data = new float[height, width];

        for (var h = 0; h < height; h++)
        {
            for (var w = 0; w < width; w++)
            {
                float val = sourceData[w, h];
                data[h, w] = val;
                if (val > maxValue) maxValue = val;
                if (val < minValue) minValue = val;
            }
        }

        return new Channel(data, default, minValue, maxValue, 0);
    }
}
