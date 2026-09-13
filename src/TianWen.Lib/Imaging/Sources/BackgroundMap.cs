using System;
using System.Buffers;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Sources;

/// <summary>
/// Options for <see cref="BackgroundMap.Estimate(ReadOnlySpan{float}, int, int, BackgroundMapOptions?)"/>.
/// </summary>
/// <param name="BlockSize">Side of the mesh cell in pixels. Larger cells follow the sky more slowly and
/// resist bright extended sources better; smaller cells follow a steep gradient. Photutils' default is
/// 50; 64 keeps a 3840 px frame at 60 cells across.</param>
/// <param name="ClipSigma">Sigma-clipping bound about the cell median, in MAD-derived sigmas. Pixels
/// further out are dropped and the cell's statistics re-taken, <paramref name="ClipIterations"/> times
/// or until nothing more is dropped.</param>
/// <param name="ClipIterations">Upper bound on the clipping loop.</param>
/// <param name="FilterSize">Side of the median filter run over the finished mesh (odd; 1 disables it).
/// It removes a cell that a source has captured whole, which the clip cannot.</param>
/// <param name="MinCellFraction">A cell keeps its own estimate only when at least this fraction of its
/// pixels survived as finite and clipped; below it the cell is filled from its neighbours, so a canvas
/// corner no frame reached does not pull the sky to zero.</param>
/// <param name="ExcludeExactZero">Treat exact zero as no data. A TianWen master's canvas ring is exact
/// zero where no frame covered it, and reading it as sky drags the edge cells down.</param>
public sealed record BackgroundMapOptions(
    int BlockSize = 64,
    float ClipSigma = 3f,
    int ClipIterations = 5,
    int FilterSize = 3,
    float MinCellFraction = 0.25f,
    bool ExcludeExactZero = true)
{
    public static BackgroundMapOptions Default { get; } = new BackgroundMapOptions();

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockSize, 4);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClipSigma, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(ClipIterations, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(FilterSize, 1);
        if ((FilterSize & 1) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FilterSize), FilterSize, "the mesh median filter wants an odd side");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MinCellFraction, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinCellFraction, 1f);
    }
}

/// <summary>
/// A two-dimensional background and noise estimate of one image plane: the sky level and its RMS per
/// pixel, from a mesh of sigma-clipped cells (the shape of photutils' <c>Background2D</c> with
/// <c>SExtractorBackground</c> and <c>MADStdBackgroundRMS</c>).
/// </summary>
/// <remarks>
/// <para>What it is for: every source detection needs a threshold that follows the sky, and every
/// mask and every measurement of a source needs the noise where the source is, not one number for
/// the frame. <see cref="ClassicalBackgroundExtractor"/> answers a different question (a smooth model
/// to SUBTRACT, robust to a nebula that fills the frame); this answers "what is the sky and its noise
/// here", and follows structure the extractor is built to ignore, so it is never a substitute for it.</para>
/// <para>Per cell: the finite pixels (exact zero excluded under
/// <see cref="BackgroundMapOptions.ExcludeExactZero"/>) are clipped about their median at
/// <see cref="BackgroundMapOptions.ClipSigma"/> MAD-sigmas until stable, the cell's level is the
/// clipped median and its RMS is <c>1.4826 x MAD</c> of the clipped set, the robust estimate that a
/// star inside the cell does not inflate. A cell with too few survivors is filled by the mean of its
/// valid neighbours, iterated; a median filter of <see cref="BackgroundMapOptions.FilterSize"/> cells
/// then runs over both meshes; the per-pixel values are bilinear between cell centres, clamped at the
/// frame's edge.</para>
/// <para>Noise units: the RMS is in the plane's own units. Callers comparing to
/// <see cref="Image"/> statistics scaled to unit range divide by <c>Image.UnitScaleDivisor</c> as they
/// would any other level.</para>
/// </remarks>
public sealed class BackgroundMap
{
    private readonly float[] _background;
    private readonly float[] _rms;

    private BackgroundMap(int width, int height, int blockSize, int cellsX, int cellsY, float[] background, float[] rms)
    {
        Width = width;
        Height = height;
        BlockSize = blockSize;
        CellsX = cellsX;
        CellsY = cellsY;
        _background = background;
        _rms = rms;
    }

    public int Width { get; }

    public int Height { get; }

    public int BlockSize { get; }

    /// <summary>Mesh cells across.</summary>
    public int CellsX { get; }

    /// <summary>Mesh cells down.</summary>
    public int CellsY { get; }

    /// <summary>The cell mesh of sky levels, row-major <see cref="CellsY"/> by <see cref="CellsX"/>.</summary>
    public ReadOnlySpan<float> CellBackground => _background;

    /// <summary>The cell mesh of RMS values, row-major <see cref="CellsY"/> by <see cref="CellsX"/>.</summary>
    public ReadOnlySpan<float> CellRms => _rms;

    /// <summary>Median of the cell sky levels: the frame's one-number background.</summary>
    public float GlobalBackground { get; private set; }

    /// <summary>Median of the cell RMS values: the frame's one-number noise.</summary>
    public float GlobalRms { get; private set; }

    /// <summary>Sky level at a pixel, bilinear between cell centres.</summary>
    public float BackgroundAt(int x, int y) => Sample(_background, x, y);

    /// <summary>Noise RMS at a pixel, bilinear between cell centres.</summary>
    public float RmsAt(int x, int y) => Sample(_rms, x, y);

    /// <summary>Fill a full-resolution plane (<see cref="Width"/> x <see cref="Height"/>, row-major) with the sky level.</summary>
    public void FillBackground(Span<float> destination) => Fill(_background, destination);

    /// <summary>Fill a full-resolution plane (<see cref="Width"/> x <see cref="Height"/>, row-major) with the noise RMS.</summary>
    public void FillRms(Span<float> destination) => Fill(_rms, destination);

    /// <summary>
    /// Estimate the map of one channel of an image.
    /// </summary>
    public static BackgroundMap Estimate(Image image, int channel, BackgroundMapOptions? options = null)
        => Estimate(image.GetChannelSpan(channel), image.Width, image.Height, options);

    /// <summary>
    /// Estimate the map of a row-major plane.
    /// </summary>
    public static BackgroundMap Estimate(ReadOnlySpan<float> plane, int width, int height, BackgroundMapOptions? options = null)
        => Estimate(plane, width, height, null, options);

    /// <summary>
    /// Estimate the map of a row-major plane with the pixels set in <paramref name="exclude"/> left out of
    /// every cell's statistics: a source mask from a first detection pass, so a nebula wider than a cell
    /// is not read as sky (photutils' <c>Background2D(mask=...)</c>). A cell with too few unmasked pixels
    /// is filled from its neighbours.
    /// </summary>
    public static BackgroundMap Estimate(ReadOnlySpan<float> plane, int width, int height, BitMatrix? exclude, BackgroundMapOptions? options = null)
    {
        options ??= BackgroundMapOptions.Default;
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (plane.Length != width * height)
        {
            throw new ArgumentException($"plane has {plane.Length} samples for {width}x{height}", nameof(plane));
        }

        if (exclude is { } ex && (ex.GetLength(0) != height || ex.GetLength(1) != width))
        {
            throw new ArgumentException($"exclusion mask is {ex.GetLength(1)}x{ex.GetLength(0)} for a {width}x{height} plane", nameof(exclude));
        }

        var block = Math.Min(options.BlockSize, Math.Min(width, height));
        var cellsX = (width + block - 1) / block;
        var cellsY = (height + block - 1) / block;
        var background = new float[cellsX * cellsY];
        var rms = new float[cellsX * cellsY];
        var valid = new bool[cellsX * cellsY];
        var cellBuffer = ArrayPool<float>.Shared.Rent(block * block);
        try
        {
            for (var cy = 0; cy < cellsY; cy++)
            {
                for (var cx = 0; cx < cellsX; cx++)
                {
                    var x0 = cx * block;
                    var y0 = cy * block;
                    var x1 = Math.Min(width, x0 + block);
                    var y1 = Math.Min(height, y0 + block);
                    var n = 0;
                    for (var y = y0; y < y1; y++)
                    {
                        var row = plane.Slice(y * width + x0, x1 - x0);
                        for (var i = 0; i < row.Length; i++)
                        {
                            if (exclude is { } mask && mask[y, x0 + i])
                            {
                                continue;
                            }

                            var v = row[i];
                            if (float.IsFinite(v) && !(options.ExcludeExactZero && v == 0f))
                            {
                                cellBuffer[n++] = v;
                            }
                        }
                    }

                    var cellPixels = (x1 - x0) * (y1 - y0);
                    var idx = cy * cellsX + cx;
                    if (n < Math.Max(8, options.MinCellFraction * cellPixels))
                    {
                        valid[idx] = false;
                        continue;
                    }

                    var (median, sigma, kept) = ClippedMedianAndSigma(cellBuffer.AsSpan(0, n), options.ClipSigma, options.ClipIterations);
                    if (kept < Math.Max(8, options.MinCellFraction * cellPixels))
                    {
                        valid[idx] = false;
                        continue;
                    }

                    background[idx] = median;
                    rms[idx] = sigma;
                    valid[idx] = true;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(cellBuffer);
        }

        FillInvalidCells(background, valid, cellsX, cellsY);
        FillInvalidCells(rms, valid, cellsX, cellsY);
        if (options.FilterSize > 1)
        {
            MedianFilterMesh(background, cellsX, cellsY, options.FilterSize);
            MedianFilterMesh(rms, cellsX, cellsY, options.FilterSize);
        }

        var map = new BackgroundMap(width, height, block, cellsX, cellsY, background, rms);
        map.GlobalBackground = MedianOf(background);
        map.GlobalRms = MedianOf(rms);
        return map;
    }

    /// <summary>
    /// Iterated sigma clip about the median: the clipped median, <c>1.4826 x MAD</c> of the clipped set,
    /// and how many samples survived. Permutes <paramref name="values"/>.
    /// </summary>
    internal static (float Median, float Sigma, int Kept) ClippedMedianAndSigma(Span<float> values, float clipSigma, int iterations)
    {
        var n = values.Length;
        var (median, mad) = MedianAndMadPreserving(values, n);
        var sigma = StatisticsHelper.MAD_TO_SD * mad;
        for (var it = 0; it < iterations && sigma > 0f; it++)
        {
            var lo = median - clipSigma * sigma;
            var hi = median + clipSigma * sigma;
            var m = 0;
            for (var i = 0; i < n; i++)
            {
                var v = values[i];
                if (v >= lo && v <= hi)
                {
                    values[m++] = v;
                }
            }

            if (m == n || m < 3)
            {
                break;
            }

            n = m;
            (median, mad) = MedianAndMadPreserving(values, n);
            sigma = StatisticsHelper.MAD_TO_SD * mad;
        }

        return (median, sigma, n);
    }

    /// <summary>
    /// <see cref="StatisticsHelper.MedianAndMad(Span{float})"/> leaves the buffer holding deviations; the
    /// clip loop needs the values back, so the median and MAD are taken on a scratch copy.
    /// </summary>
    private static (float Median, float Mad) MedianAndMadPreserving(Span<float> values, int n)
    {
        var scratch = ArrayPool<float>.Shared.Rent(n);
        try
        {
            values[..n].CopyTo(scratch);
            return StatisticsHelper.MedianAndMad(scratch.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    /// <summary>Cells without an estimate take the mean of their valid 8-neighbours, repeated until every cell has one.</summary>
    private static void FillInvalidCells(float[] mesh, bool[] valid, int cellsX, int cellsY)
    {
        var state = (bool[])valid.Clone();
        var remaining = 0;
        foreach (var v in state)
        {
            if (!v)
            {
                remaining++;
            }
        }

        if (remaining == state.Length)
        {
            // Nothing measured anywhere: a blank plane. Leave zeros.
            return;
        }

        var next = new bool[state.Length];
        while (remaining > 0)
        {
            Array.Copy(state, next, state.Length);
            var filled = 0;
            for (var cy = 0; cy < cellsY; cy++)
            {
                for (var cx = 0; cx < cellsX; cx++)
                {
                    var idx = cy * cellsX + cx;
                    if (state[idx])
                    {
                        continue;
                    }

                    var sum = 0.0;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= cellsX || ny >= cellsY)
                            {
                                continue;
                            }

                            var nidx = ny * cellsX + nx;
                            if (state[nidx])
                            {
                                sum += mesh[nidx];
                                count++;
                            }
                        }
                    }

                    if (count > 0)
                    {
                        mesh[idx] = (float)(sum / count);
                        next[idx] = true;
                        filled++;
                    }
                }
            }

            if (filled == 0)
            {
                break;
            }

            Array.Copy(next, state, state.Length);
            remaining -= filled;
        }
    }

    /// <summary>An odd-sided median filter over the mesh, edges clamped.</summary>
    private static void MedianFilterMesh(float[] mesh, int cellsX, int cellsY, int size)
    {
        var half = size / 2;
        var source = (float[])mesh.Clone();
        Span<float> window = stackalloc float[size * size];
        for (var cy = 0; cy < cellsY; cy++)
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                var n = 0;
                for (var dy = -half; dy <= half; dy++)
                {
                    var ny = Math.Clamp(cy + dy, 0, cellsY - 1);
                    for (var dx = -half; dx <= half; dx++)
                    {
                        var nx = Math.Clamp(cx + dx, 0, cellsX - 1);
                        window[n++] = source[ny * cellsX + nx];
                    }
                }

                mesh[cy * cellsX + cx] = StatisticsHelper.MedianFast(window[..n]);
            }
        }
    }

    private static float MedianOf(float[] mesh)
    {
        var copy = (float[])mesh.Clone();
        return StatisticsHelper.MedianFast(copy);
    }

    private float Sample(float[] mesh, int x, int y)
    {
        // Cell centres sit at (cx + 0.5) * block; between them bilinear, beyond the outermost centres clamped.
        var fx = (x + 0.5f) / BlockSize - 0.5f;
        var fy = (y + 0.5f) / BlockSize - 0.5f;
        var cx0 = Math.Clamp((int)MathF.Floor(fx), 0, CellsX - 1);
        var cy0 = Math.Clamp((int)MathF.Floor(fy), 0, CellsY - 1);
        var cx1 = Math.Min(cx0 + 1, CellsX - 1);
        var cy1 = Math.Min(cy0 + 1, CellsY - 1);
        var tx = Math.Clamp(fx - cx0, 0f, 1f);
        var ty = Math.Clamp(fy - cy0, 0f, 1f);
        var top = mesh[cy0 * CellsX + cx0] * (1f - tx) + mesh[cy0 * CellsX + cx1] * tx;
        var bottom = mesh[cy1 * CellsX + cx0] * (1f - tx) + mesh[cy1 * CellsX + cx1] * tx;
        return top * (1f - ty) + bottom * ty;
    }

    private void Fill(float[] mesh, Span<float> destination)
    {
        if (destination.Length != Width * Height)
        {
            throw new ArgumentException($"destination has {destination.Length} samples for {Width}x{Height}", nameof(destination));
        }

        for (var y = 0; y < Height; y++)
        {
            var row = destination.Slice(y * Width, Width);
            for (var x = 0; x < Width; x++)
            {
                row[x] = Sample(mesh, x, y);
            }
        }
    }
}
