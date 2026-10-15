using System;
using System.Buffers;
using System.Numerics;
using TianWen.Lib.Imaging;

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
    /// <summary>
    /// Columns up to this many samples are gathered into stack scratch (16 bytes each, so 8 KB); a longer
    /// column rents from the shared pool. Either way a transform allocates nothing, which matters because
    /// the planetary aligners run two transforms per frame per tile.
    /// </summary>
    private const int MaxStackColumn = 512;

    /// <summary>Unnormalised forward 2D FFT, in place.</summary>
    public static void Forward(Complex[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        RowColumn(data, width, height, inverse: false);
    }

    /// <summary>Inverse 2D FFT (normalised by <c>width * height</c>), in place.</summary>
    public static void Inverse(Complex[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        RowColumn(data, width, height, inverse: true);
    }

    /// <summary>Unnormalised forward 2D FFT over a span (rented or stack scratch), in place.</summary>
    public static void Forward(Span<Complex> data, int width, int height) => RowColumn(data, width, height, inverse: false);

    /// <summary>Inverse 2D FFT (normalised by <c>width * height</c>) over a span, in place.</summary>
    public static void Inverse(Span<Complex> data, int width, int height) => RowColumn(data, width, height, inverse: true);

    /// <summary>
    /// <see cref="Forward(Complex[], int, int)"/> with its rows, then its columns, spread over every core: for a large grid
    /// transformed one at a time, as a Richardson-Lucy step's four are (#1577: one core held a 20-step deblur for 66 s). Every row
    /// and every column is transformed alone, exactly as the serial pass transforms it, so the result is the same to the bit.
    /// </summary>
    public static void ForwardOnEveryCore(Complex[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        RowColumnOnEveryCore(data, width, height, inverse: false);
    }

    /// <summary><see cref="Inverse(Complex[], int, int)"/> on every core, as <see cref="ForwardOnEveryCore"/>; the same to the bit.</summary>
    public static void InverseOnEveryCore(Complex[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        RowColumnOnEveryCore(data, width, height, inverse: true);
    }

    private static void RowColumnOnEveryCore(Complex[] data, int width, int height, bool inverse)
    {
        Validate(data.Length, width, height);
        ParallelFor.RunBands(height, (from, to) =>
        {
            for (var y = from; y < to; y++)
            {
                Transform(data.AsSpan(y * width, width), inverse);
            }
        });
        // Each band of columns gathers through one scratch of its own.
        ParallelFor.RunBands(width, (from, to) =>
        {
            var col = ArrayPool<Complex>.Shared.Rent(height);
            try
            {
                var column = col.AsSpan(0, height);
                for (var x = from; x < to; x++)
                {
                    TransformColumn(data, column, x, width, inverse);
                }
            }
            finally
            {
                ArrayPool<Complex>.Shared.Return(col);
            }
        });
    }

    private static void Validate(int length, int width, int height)
    {
        if (!ComplexFft.IsPowerOfTwo(width) || !ComplexFft.IsPowerOfTwo(height))
        {
            throw new ArgumentException($"FFT dimensions must each be a power of two, got {width}x{height}.");
        }

        if (length != width * height)
        {
            throw new ArgumentException($"data length {length} != width*height ({width * height}).", "data");
        }
    }

    private static void Transform(Span<Complex> line, bool inverse)
    {
        if (inverse)
        {
            ComplexFft.Inverse(line);
        }
        else
        {
            ComplexFft.Forward(line);
        }
    }

    /// <summary>Column <paramref name="x"/> gathered into <paramref name="column"/>, transformed and put back.</summary>
    private static void TransformColumn(Span<Complex> data, Span<Complex> column, int x, int width, bool inverse)
    {
        for (var y = 0; y < column.Length; y++)
        {
            column[y] = data[(y * width) + x];
        }

        Transform(column, inverse);
        for (var y = 0; y < column.Length; y++)
        {
            data[(y * width) + x] = column[y];
        }
    }

    private static void RowColumn(Span<Complex> data, int width, int height, bool inverse)
    {
        Validate(data.Length, width, height);

        // Rows.
        for (var y = 0; y < height; y++)
        {
            Transform(data.Slice(y * width, width), inverse);
        }

        // Columns (gathered into a contiguous scratch buffer, since they are strided in the array). It used to
        // be a new array per transform.
        Complex[]? rented = null;
        var col = height <= MaxStackColumn
            ? stackalloc Complex[height]
            : (rented = ArrayPool<Complex>.Shared.Rent(height)).AsSpan(0, height);
        try
        {
            for (var x = 0; x < width; x++)
            {
                TransformColumn(data, col, x, width, inverse);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<Complex>.Shared.Return(rented);
            }
        }
    }
}
