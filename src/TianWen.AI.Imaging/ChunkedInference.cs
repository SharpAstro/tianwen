using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Stat;

namespace TianWen.AI.Imaging;

/// <summary>
/// Tile a 2D plane into overlapping chunks for ML inference, then stitch the
/// processed chunks back together while dropping a fixed border around each
/// tile to hide the boundary artefacts that NAFNet-family models produce at
/// chunk edges. C# port of SetiAstroSuite Pro's
/// <c>split_image_into_chunks_with_overlap</c> /
/// <c>stitch_chunks_ignore_border</c> / <c>add_border</c> / <c>remove_border</c>
/// helpers in <c>sharpen_engine.py</c>.
/// </summary>
/// <remarks>
/// Single-plane API by design (caller iterates channels): keeps the math close
/// to the reference Python, lets multi-channel callers parallelise per channel
/// later if needed, and avoids a transpose-vs-broadcast decision in the chunk
/// data layout. Chunks always carry row-major pixel data of size Width *
/// Height -- ready to feed directly into ORT's <c>DenseTensor{float}</c>.
/// </remarks>
public static class ChunkedInference
{
    /// <summary>
    /// A single tile cut out of the source plane. <see cref="Data"/> is a flat
    /// row-major copy of length <see cref="Width"/> * <see cref="Height"/>;
    /// <see cref="X"/> / <see cref="Y"/> locate its top-left corner in the
    /// source plane (in source coordinates, ignoring any added border).
    /// <see cref="IsEdge"/> flags chunks abutting the source boundary so
    /// callers can apply edge-specific handling (e.g. skip border drop) if
    /// desired -- the default <see cref="Stitch"/> behaviour drops the border
    /// uniformly and relies on a prior <see cref="AddBorder"/> pass to keep
    /// the source edges covered.
    /// </summary>
    public readonly record struct Chunk(
        float[] Data,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsEdge);

    /// <summary>
    /// Cut <paramref name="plane"/> (row-major, length = <paramref name="width"/>
    /// * <paramref name="height"/>) into overlapping <paramref name="chunkSize"/>
    /// × <paramref name="chunkSize"/> tiles, stepping <c>chunkSize - overlap</c>
    /// pixels at a time. Edge tiles are clipped to the source bounds and
    /// flagged via <see cref="Chunk.IsEdge"/>.
    /// </summary>
    public static ImmutableArray<Chunk> Split(
        ReadOnlySpan<float> plane, int width, int height,
        int chunkSize, int overlap)
    {
        if (plane.Length != width * height)
            throw new ArgumentException($"plane length ({plane.Length}) must equal width * height ({width * height})", nameof(plane));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap));

        var step = chunkSize - overlap;
        var builder = ImmutableArray.CreateBuilder<Chunk>();
        for (var i = 0; i < height; i += step)
        {
            for (var j = 0; j < width; j += step)
            {
                var ei = Math.Min(i + chunkSize, height);
                var ej = Math.Min(j + chunkSize, width);
                if (ei <= i || ej <= j) continue;

                var h = ei - i;
                var w = ej - j;
                var data = new float[h * w];
                for (var r = 0; r < h; r++)
                {
                    var srcRow = plane.Slice((i + r) * width + j, w);
                    srcRow.CopyTo(data.AsSpan(r * w, w));
                }
                var isEdge = i == 0 || j == 0 || i + chunkSize >= height || j + chunkSize >= width;
                builder.Add(new Chunk(data, j, i, w, h, isEdge));
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Stitch <paramref name="chunks"/> back into <paramref name="destPlane"/>
    /// (row-major, length = <paramref name="width"/> * <paramref name="height"/>).
    /// Each chunk's inner region -- with a border of <paramref name="borderSize"/>
    /// pixels dropped on each side -- is summed into the destination; per-pixel
    /// weights are tracked separately so overlapping inner regions average to
    /// the mean. Caller is responsible for zeroing <paramref name="destPlane"/>
    /// before the call; this method writes into it directly.
    /// </summary>
    public static void Stitch(
        IReadOnlyList<Chunk> chunks,
        Span<float> destPlane, int width, int height,
        int borderSize)
    {
        if (destPlane.Length != width * height)
            throw new ArgumentException($"destPlane length ({destPlane.Length}) must equal width * height ({width * height})", nameof(destPlane));
        if (borderSize < 0) throw new ArgumentOutOfRangeException(nameof(borderSize));

        var weights = new float[width * height];
        destPlane.Clear();

        foreach (var chunk in chunks)
        {
            var h = chunk.Height;
            var w = chunk.Width;
            if (h <= 0 || w <= 0) continue;

            var bh = Math.Min(borderSize, h / 2);
            var bw = Math.Min(borderSize, w / 2);

            var y0 = chunk.Y + bh;
            var y1 = chunk.Y + h - bh;
            var x0 = chunk.X + bw;
            var x1 = chunk.X + w - bw;
            if (y1 <= y0 || x1 <= x0) continue;

            // Clip destination to image bounds (chunks at the right/bottom may
            // partially fall outside, though Split already prevents fully-OOB
            // chunks).
            var yy0 = Math.Max(0, y0);
            var yy1 = Math.Min(height, y1);
            var xx0 = Math.Max(0, x0);
            var xx1 = Math.Min(width, x1);
            if (yy1 <= yy0 || xx1 <= xx0) continue;

            // Map clipped destination back into chunk-local coordinates.
            var sy0 = bh + (yy0 - y0);
            var sx0 = bw + (xx0 - x0);

            var data = chunk.Data;
            for (var ry = 0; ry < yy1 - yy0; ry++)
            {
                var srcOff = (sy0 + ry) * w + sx0;
                var dstOff = (yy0 + ry) * width + xx0;
                var span = xx1 - xx0;
                for (var rx = 0; rx < span; rx++)
                {
                    destPlane[dstOff + rx] += data[srcOff + rx];
                    weights[dstOff + rx] += 1f;
                }
            }
        }

        // Divide by weight (clamped at 1 -- unweighted pixels stay at the zero
        // we cleared; the clamp matches SAS Pro's np.maximum(weights, 1.0)).
        for (var i = 0; i < destPlane.Length; i++)
        {
            var weight = weights[i];
            if (weight > 1f) destPlane[i] /= weight;
        }
    }

    /// <summary>
    /// Pad <paramref name="plane"/> with a constant border of
    /// <paramref name="borderSize"/> pixels on all four sides. The padding
    /// value is the plane's median so chunked inference at the original image
    /// edges sees a continuation that matches the local background. Matches
    /// SAS Pro's <c>add_border</c>.
    /// </summary>
    public static float[] AddBorder(
        ReadOnlySpan<float> plane, int width, int height,
        int borderSize, out int newWidth, out int newHeight)
    {
        if (plane.Length != width * height)
            throw new ArgumentException($"plane length ({plane.Length}) must equal width * height ({width * height})", nameof(plane));
        if (borderSize < 0) throw new ArgumentOutOfRangeException(nameof(borderSize));

        newWidth = width + 2 * borderSize;
        newHeight = height + 2 * borderSize;
        var padded = new float[newWidth * newHeight];

        // Median fill -- copy to scratch, median-select, then fill.
        float fill;
        if (plane.Length == 0)
        {
            fill = 0f;
        }
        else
        {
            var scratch = new float[plane.Length];
            plane.CopyTo(scratch);
            fill = StatisticsHelper.MedianFast(scratch);
        }
        padded.AsSpan().Fill(fill);

        for (var r = 0; r < height; r++)
        {
            var srcRow = plane.Slice(r * width, width);
            var dstRow = padded.AsSpan((r + borderSize) * newWidth + borderSize, width);
            srcRow.CopyTo(dstRow);
        }
        return padded;
    }

    /// <summary>
    /// Reverse of <see cref="AddBorder"/>: strip <paramref name="borderSize"/>
    /// pixels from each side. Returned plane has length
    /// (width - 2 * borderSize) * (height - 2 * borderSize).
    /// </summary>
    public static float[] RemoveBorder(
        ReadOnlySpan<float> plane, int width, int height, int borderSize)
    {
        if (plane.Length != width * height)
            throw new ArgumentException($"plane length ({plane.Length}) must equal width * height ({width * height})", nameof(plane));
        if (borderSize < 0) throw new ArgumentOutOfRangeException(nameof(borderSize));
        if (width <= 2 * borderSize || height <= 2 * borderSize)
            throw new ArgumentException($"border ({borderSize}) too large for plane {width}x{height}", nameof(borderSize));

        var innerW = width - 2 * borderSize;
        var innerH = height - 2 * borderSize;
        var output = new float[innerW * innerH];
        for (var r = 0; r < innerH; r++)
        {
            var srcRow = plane.Slice((r + borderSize) * width + borderSize, innerW);
            srcRow.CopyTo(output.AsSpan(r * innerW, innerW));
        }
        return output;
    }
}
