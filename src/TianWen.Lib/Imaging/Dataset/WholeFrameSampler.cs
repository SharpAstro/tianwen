using System;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Where a whole frame sits inside the gradient model's square input: scaled so its LONG side fills
/// <see cref="Size"/>, its aspect kept, centred, and everything around it ABSENT
/// (docs/plans/gradient-remover-training.md, section 3 "Edges").
/// </summary>
/// <remarks>
/// <para><b>Aspect kept, never squashed.</b> GraXpert resizes every frame to a square, so a 16:9 field
/// reaches its model stretched 1.8 times along one axis and the shape of a gradient depends on the
/// sensor's aspect. Padding the short side instead costs input area and nothing else, because the pad is
/// absent like the canvas ring and the presence plane says so: the model learns to ignore it the same
/// way, which makes every padded frame an edge example for free.</para>
/// <para><b>Integer boxes that partition the frame.</b> Sample column <c>i</c> of the frame covers source
/// columns <c>floor(i * W / F)</c> to <c>floor((i + 1) * W / F)</c>, so every source pixel lands in
/// exactly one sample and no pixel is weighted twice; a box is never empty while the frame is at least
/// as large as the sample, which every master is.</para>
/// </remarks>
/// <param name="Size">The model's square input, pixels.</param>
/// <param name="SourceWidth">The frame's width, pixels.</param>
/// <param name="SourceHeight">The frame's height, pixels.</param>
/// <param name="FrameWidth">The frame's width inside the sample.</param>
/// <param name="FrameHeight">The frame's height inside the sample.</param>
/// <param name="OffsetX">The frame's first sample column.</param>
/// <param name="OffsetY">The frame's first sample row.</param>
public readonly record struct WholeFramePlacement(
    int Size, int SourceWidth, int SourceHeight, int FrameWidth, int FrameHeight, int OffsetX, int OffsetY)
{
    /// <summary>The placement of a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> frame in a square of <paramref name="size"/>.</summary>
    public static WholeFramePlacement For(int sourceWidth, int sourceHeight, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var longSide = Math.Max(sourceWidth, sourceHeight);
        var frameWidth = Math.Clamp((int)Math.Round((double)sourceWidth * size / longSide), 1, size);
        var frameHeight = Math.Clamp((int)Math.Round((double)sourceHeight * size / longSide), 1, size);
        return new WholeFramePlacement(size, sourceWidth, sourceHeight, frameWidth, frameHeight,
            (size - frameWidth) / 2, (size - frameHeight) / 2);
    }

    /// <summary>Source pixels per sample pixel along the long side.</summary>
    public double SourcePixelsPerSample => (double)Math.Max(SourceWidth, SourceHeight) / Math.Max(FrameWidth, FrameHeight);

    /// <summary>The source columns <c>[Start, End)</c> under sample column <paramref name="x"/>; empty outside the frame.</summary>
    public (int Start, int End) SourceColumns(int x) => Box(x - OffsetX, FrameWidth, SourceWidth);

    /// <summary>The source rows <c>[Start, End)</c> under sample row <paramref name="y"/>; empty outside the frame.</summary>
    public (int Start, int End) SourceRows(int y) => Box(y - OffsetY, FrameHeight, SourceHeight);

    private static (int Start, int End) Box(int i, int frame, int source)
    {
        if (i < 0 || i >= frame)
        {
            return (0, 0);
        }
        var start = (int)((long)i * source / frame);
        var end = (int)((long)(i + 1) * source / frame);
        // Only a frame SMALLER than the sample can produce an empty box; give it its nearest pixel.
        return (start, Math.Max(end, Math.Min(start + 1, source)));
    }
}

/// <summary>A frame reduced to the model's square input.</summary>
/// <param name="Placement">Where the frame sits in the square.</param>
/// <param name="Planes">One <c>Size x Size</c> plane per channel, row-major: the mean of the PRESENT source
/// pixels under each sample pixel, NaN where none is.</param>
/// <param name="Presence">The fraction of each sample pixel's source box that is present, 0 to 1; 0 on the
/// pad around the frame.</param>
public sealed record WholeFrameSample(WholeFramePlacement Placement, float[][] Planes, float[] Presence);

/// <summary>
/// Reduces a whole frame to the gradient model's input: the area mean over PRESENT pixels and the
/// fraction present, per sample pixel. The ONE implementation the exporter and the runner share, so a
/// frame is reduced for inference exactly as it was for training.
/// </summary>
/// <remarks>
/// <para><b>Absent pixels never enter a mean.</b> The canvas ring is exact zero or NaN, and a mean that
/// took it in would pull every edge sample toward zero, which is what GraXpert's resize does and why its
/// background is wrong wherever a frame's edge is not clean. A source pixel counts when it is outside
/// <c>absent</c> (normally <see cref="Image.AbsentPixels"/>) and finite in every channel; fill interior
/// holes first (<see cref="Image.FillInteriorHolesInPlace"/>), so only the edge reads as missing.</para>
/// </remarks>
public static class WholeFrameSampler
{
    /// <summary>
    /// Samples <paramref name="image"/> into <paramref name="placement"/>'s square. <paramref name="absent"/>,
    /// when given, is indexed <c>[y, x]</c> on the image's own geometry.
    /// </summary>
    public static WholeFrameSample Sample(Image image, BitMatrix? absent, WholeFramePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (channels, width, height) = image.Shape;
        if (width != placement.SourceWidth || height != placement.SourceHeight)
        {
            throw new ArgumentException(
                $"the placement is for a {placement.SourceWidth}x{placement.SourceHeight} frame, the image is {width}x{height}", nameof(placement));
        }

        var size = placement.Size;
        var columnToSample = new int[width];
        Array.Fill(columnToSample, -1);
        for (var x = 0; x < size; x++)
        {
            var (start, end) = placement.SourceColumns(x);
            for (var c = start; c < end; c++)
            {
                columnToSample[c] = x;
            }
        }

        var planes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            planes[c] = new float[size * size];
            Array.Fill(planes[c], float.NaN);
        }
        var presence = new float[size * size];
        var sums = new double[channels][];
        for (var c = 0; c < channels; c++)
        {
            sums[c] = new double[size];
        }
        var counts = new int[size];
        var boxWidths = new int[size];
        for (var x = 0; x < size; x++)
        {
            var (start, end) = placement.SourceColumns(x);
            boxWidths[x] = end - start;
        }

        var rows = new float[channels][];
        for (var y = 0; y < size; y++)
        {
            var (rowStart, rowEnd) = placement.SourceRows(y);
            if (rowEnd <= rowStart)
            {
                continue;
            }

            for (var c = 0; c < channels; c++)
            {
                Array.Clear(sums[c]);
            }
            Array.Clear(counts);

            for (var r = rowStart; r < rowEnd; r++)
            {
                for (var c = 0; c < channels; c++)
                {
                    rows[c] ??= new float[width];
                    image.GetChannelSpan(c).Slice(r * width, width).CopyTo(rows[c]);
                }

                for (var col = 0; col < width; col++)
                {
                    var s = columnToSample[col];
                    if (s < 0 || (absent is { } mask && mask[r, col]))
                    {
                        continue;
                    }
                    var finite = true;
                    for (var c = 0; c < channels && finite; c++)
                    {
                        finite = float.IsFinite(rows[c][col]);
                    }
                    if (!finite)
                    {
                        continue;
                    }
                    for (var c = 0; c < channels; c++)
                    {
                        sums[c][s] += rows[c][col];
                    }
                    counts[s]++;
                }
            }

            var boxHeight = rowEnd - rowStart;
            for (var x = 0; x < size; x++)
            {
                if (boxWidths[x] == 0)
                {
                    continue;
                }
                var index = y * size + x;
                presence[index] = (float)counts[x] / (boxWidths[x] * boxHeight);
                if (counts[x] == 0)
                {
                    continue;
                }
                for (var c = 0; c < channels; c++)
                {
                    planes[c][index] = (float)(sums[c][x] / counts[x]);
                }
            }
        }

        return new WholeFrameSample(placement, planes, presence);
    }
}
