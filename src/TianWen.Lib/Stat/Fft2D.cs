using System;
using System.Numerics;

namespace TianWen.Lib.Stat;

/// <summary>
/// 2D complex FFT by row-column decomposition over <see cref="ComplexFft"/>: transform every row, then
/// every column. Width and height must each be a power of two. <see cref="Forward"/> is unnormalised;
/// <see cref="Inverse"/> divides by <c>width * height</c> (1/width on the row pass, 1/height on the
/// column pass), so <c>Inverse(Forward(x)) == x</c>. The data array is row-major, length
/// <c>width * height</c>.
/// </summary>
public static class Fft2D
{
    /// <summary>Unnormalised forward 2D FFT, in place.</summary>
    public static void Forward(Complex[] data, int width, int height) => RowColumn(data, width, height, inverse: false);

    /// <summary>Inverse 2D FFT (normalised by <c>width * height</c>), in place.</summary>
    public static void Inverse(Complex[] data, int width, int height) => RowColumn(data, width, height, inverse: true);

    private static void RowColumn(Complex[] data, int width, int height, bool inverse)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!ComplexFft.IsPowerOfTwo(width) || !ComplexFft.IsPowerOfTwo(height))
        {
            throw new ArgumentException($"FFT dimensions must each be a power of two, got {width}x{height}.");
        }

        if (data.Length != width * height)
        {
            throw new ArgumentException($"data length {data.Length} != width*height ({width * height}).", nameof(data));
        }

        // Rows.
        for (var y = 0; y < height; y++)
        {
            var row = data.AsSpan(y * width, width);
            if (inverse)
            {
                ComplexFft.Inverse(row);
            }
            else
            {
                ComplexFft.Forward(row);
            }
        }

        // Columns (gathered into a contiguous scratch buffer, since they are strided in the array).
        var col = new Complex[height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                col[y] = data[(y * width) + x];
            }

            var span = col.AsSpan();
            if (inverse)
            {
                ComplexFft.Inverse(span);
            }
            else
            {
                ComplexFft.Forward(span);
            }

            for (var y = 0; y < height; y++)
            {
                data[(y * width) + x] = col[y];
            }
        }
    }
}
