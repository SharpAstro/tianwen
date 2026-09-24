using System;
using TianWen.Lib.Geometry;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Maps a source pixel <c>(xSrc, ySrc)</c> to its centre position on the drizzle canvas. Implemented as a
/// struct so the JIT monomorphises + inlines <see cref="Map"/> into the deposit loop -- the per-pixel
/// forward-scatter pays no virtual-call cost. <see cref="AffineMap"/> is the affine (deep-sky / whole-disk)
/// case; the planetary AP-mesh path supplies its own per-pixel displacement-field map.
/// </summary>
internal interface ISourceToCanvas
{
    Vector2 Map(int xSrc, int ySrc);
}

/// <summary>The affine source -&gt; canvas map: a single <see cref="Matrix3x2"/> applied per pixel.</summary>
internal readonly struct AffineMap(Matrix3x2 transform) : ISourceToCanvas
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 Map(int xSrc, int ySrc) => Vector2.Transform(new Vector2(xSrc, ySrc), transform);
}

/// <summary>
/// Shared forward-projection kernel for the drizzle family of strategies
/// (<see cref="DrizzleStrategy"/>, <see cref="TilePipelinedDrizzleStrategy"/>).
/// Per-frame, iterates a source-pixel rectangle and deposits each valid CFA
/// sample as a <c>pixfrac</c>-sized "drop" onto the per-channel
/// <c>flux</c> / <c>weight</c> planes the caller owns. The accumulator
/// arrays are not necessarily canvas-sized: the caller passes
/// <paramref name="xStart"/>/<paramref name="xEnd"/>/<paramref name="yStart"/>/<paramref name="yEnd"/>
/// to declare the canvas coordinate range the arrays cover. Drops landing
/// outside that range are clipped. This is the single mechanism that lets
/// <see cref="DrizzleStrategy"/> deposit into a full-canvas accumulator
/// and <see cref="TilePipelinedDrizzleStrategy"/> deposit into a strip-
/// local one without the kernel needing to know the difference.
/// </summary>
internal static class DrizzleKernel
{
    /// <summary>
    /// Iterate the <paramref name="sourceRect"/> sub-region of
    /// <paramref name="raw"/> (a 1-channel calibrated Bayer plane) and
    /// forward-project each valid sample onto <paramref name="flux"/> /
    /// <paramref name="weight"/>. Honours <paramref name="badPixelMask"/> via
    /// a per-64-pixel-chunk word fast-path: when the mask word is zero (which
    /// is the common case -- typical sensor hot-pixel rates are 0.02% so
    /// 99.98% of chunks are clean) the per-pixel mask check is skipped
    /// entirely.
    /// </summary>
    /// <param name="raw">Calibrated 1-channel Bayer source frame.</param>
    /// <param name="transform">Source -> canvas affine for this frame.</param>
    /// <param name="pattern">2x2 Bayer pattern in sensor coordinates;
    /// <c>pattern[ySrc &amp; 1, xSrc &amp; 1]</c> selects which output channel
    /// the source pixel feeds.</param>
    /// <param name="halfP">Half the pixfrac drop extent (i.e. <c>pixfrac/2</c>).
    /// At pixfrac=1.0 each drop is a unit cell; smaller pixfrac sharpens
    /// the output at the cost of needing more frames for coverage.</param>
    /// <param name="flux">Per-channel flux accumulator, indexed as
    /// <c>flux[ch][yCanvas - yStart, xCanvas - xStart]</c>. Must be sized
    /// at least <c>(yEnd - yStart) x (xEnd - xStart)</c> per channel.</param>
    /// <param name="weight">Per-channel coverage-weight accumulator, same
    /// shape as <paramref name="flux"/>.</param>
    /// <param name="xStart">Canvas X coordinate the first column of the
    /// accumulators corresponds to.</param>
    /// <param name="xEnd">Canvas X coordinate one past the last column of
    /// the accumulators (exclusive).</param>
    /// <param name="yStart">Canvas Y coordinate the first row of the
    /// accumulators corresponds to.</param>
    /// <param name="yEnd">Canvas Y coordinate one past the last row of the
    /// accumulators (exclusive).</param>
    /// <param name="sourceRect">Sub-rectangle of <paramref name="raw"/> to
    /// iterate. Pre-clamped to source bounds. Pass the whole frame for the
    /// full-canvas drizzle path.</param>
    /// <param name="badPixelMask">Optional hot-pixel mask. Default value
    /// (<c>!hasBadPixelMask</c>) skips the mask check entirely.</param>
    /// <param name="hasBadPixelMask">True iff <paramref name="badPixelMask"/>
    /// is populated. Hoisted out of the inner loop.</param>
    public static void IterateAndDeposit(
        Image raw,
        Matrix3x2 transform,
        int[,] pattern,
        float halfP,
        float[][,] flux,
        float[][,] weight,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
        => IterateAndDeposit(
            raw, new AffineMap(transform), pattern, halfP, flux, weight,
            xStart, xEnd, yStart, yEnd, sourceRect, badPixelMask, hasBadPixelMask);

    /// <summary>
    /// Generic over the source -&gt; canvas <paramref name="map"/> so the planetary AP-mesh path can
    /// forward-scatter through a per-pixel displacement field while the deep-sky path uses a single affine,
    /// both sharing this one deposit loop. <typeparamref name="TMap"/> is a struct constraint so the JIT
    /// inlines <see cref="ISourceToCanvas.Map"/> with no virtual dispatch. The <see cref="Matrix3x2"/>
    /// overload above is the affine entry point the deep-sky strategies call.
    /// </summary>
    public static void IterateAndDeposit<TMap>(
        Image raw,
        TMap map,
        int[,] pattern,
        float halfP,
        float[][,] flux,
        float[][,] weight,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
        where TMap : struct, ISourceToCanvas
        => Iterate(raw, map, pattern, halfP, new FluxWeightSink(flux, weight),
            xStart, xEnd, yStart, yEnd, sourceRect, badPixelMask, hasBadPixelMask);

    /// <summary>
    /// The statistics pass of a REJECTING drizzle: deposits exactly as <see cref="IterateAndDeposit(Image, Matrix3x2, int[,], float, float[][,], float[][,], int, int, int, int, PixelRect, BitMatrix, bool)"/>
    /// does, and also accumulates each cell's area-weighted sum of squares, so the clipped pass can
    /// read every cell's mean and spread. See <see cref="DrizzleMoments"/>.
    /// </summary>
    public static void IterateAndAccumulateMoments(
        Image raw,
        Matrix3x2 transform,
        int[,] pattern,
        float halfP,
        DrizzleMoments moments,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
        => Iterate(raw, new AffineMap(transform), pattern, halfP, new MomentsSink(moments),
            xStart, xEnd, yStart, yEnd, sourceRect, badPixelMask, hasBadPixelMask);

    /// <summary>
    /// The deposit pass of a REJECTING drizzle: every sample is tested, per cell it lands in, against
    /// that cell's statistics from <paramref name="moments"/> with the sample's OWN contribution taken
    /// out, and deposited into <paramref name="flux"/> / <paramref name="weight"/> only if it passes.
    /// See <see cref="DrizzleClip"/> for why the test leaves the sample out.
    /// </summary>
    /// <returns>How many (sample, cell) deposits were rejected, and how many were judged in all.</returns>
    public static (long Rejected, long Total) IterateAndDepositClipped(
        Image raw,
        Matrix3x2 transform,
        int[,] pattern,
        float halfP,
        DrizzleMoments moments,
        DrizzleClip clip,
        float[][,] flux,
        float[][,] weight,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
    {
        var counts = new long[2];
        Iterate(raw, new AffineMap(transform), pattern, halfP, new ClippedSink(moments, clip, flux, weight, counts),
            xStart, xEnd, yStart, yEnd, sourceRect, badPixelMask, hasBadPixelMask);
        return (counts[0], counts[1]);
    }

    private static void Iterate<TMap, TSink>(
        Image raw,
        TMap map,
        int[,] pattern,
        float halfP,
        TSink sink,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
        where TMap : struct, ISourceToCanvas
        where TSink : struct, IDropSink
        => Iterate(raw, map, pattern, halfP, sink, xStart, xEnd, yStart, yEnd, sourceRect, badPixelMask, hasBadPixelMask,
            clampY0: yStart, clampY1: yEnd);

    /// <summary>Canvas rows one worker owns in a parallel deposit (<see cref="ForEachStrip"/>). Small
    /// enough that a 3,000-row canvas splits into about fifty strips for sixteen cores, large enough that
    /// the rows a strip's source rectangle shares with its neighbours (its halo) cost a few percent.</summary>
    internal const int ParallelStripRows = 64;

    /// <summary>
    /// <see cref="IterateAndDeposit(Image, Matrix3x2, int[,], float, float[][,], float[][,], int, int, int, int, PixelRect, BitMatrix, bool)"/>
    /// over the whole frame into full-canvas accumulators, split across canvas strips in parallel.
    /// Bit-identical to the serial deposit (see <see cref="ForEachStrip"/>).
    /// </summary>
    public static void IterateAndDepositParallel(
        Image raw, Matrix3x2 transform, int[,] pattern, float halfP,
        float[][,] flux, float[][,] weight, int canvasW, int canvasH,
        BitMatrix badPixelMask, bool hasBadPixelMask)
        => ForEachStrip(raw, transform, canvasW, canvasH, (y0, y1, source) =>
            Iterate(raw, new AffineMap(transform), pattern, halfP, new FluxWeightSink(flux, weight),
                0, canvasW, 0, canvasH, source, badPixelMask, hasBadPixelMask, y0, y1));

    /// <summary>The statistics pass of a rejecting drizzle, in parallel strips; bit-identical to
    /// <see cref="IterateAndAccumulateMoments"/> over the whole canvas.</summary>
    public static void IterateAndAccumulateMomentsParallel(
        Image raw, Matrix3x2 transform, int[,] pattern, float halfP,
        DrizzleMoments moments, int canvasW, int canvasH,
        BitMatrix badPixelMask, bool hasBadPixelMask)
        => ForEachStrip(raw, transform, canvasW, canvasH, (y0, y1, source) =>
            Iterate(raw, new AffineMap(transform), pattern, halfP, new MomentsSink(moments),
                0, canvasW, 0, canvasH, source, badPixelMask, hasBadPixelMask, y0, y1));

    /// <summary>The clipped deposit of a rejecting drizzle, in parallel strips; bit-identical to
    /// <see cref="IterateAndDepositClipped"/> over the whole canvas, counts included.</summary>
    public static (long Rejected, long Total) IterateAndDepositClippedParallel(
        Image raw, Matrix3x2 transform, int[,] pattern, float halfP,
        DrizzleMoments moments, DrizzleClip clip, float[][,] flux, float[][,] weight, int canvasW, int canvasH,
        BitMatrix badPixelMask, bool hasBadPixelMask)
    {
        long rejected = 0, total = 0;
        ForEachStrip(raw, transform, canvasW, canvasH, (y0, y1, source) =>
        {
            // Per strip: a counter shared between workers would be a race, not a count.
            var counts = new long[2];
            Iterate(raw, new AffineMap(transform), pattern, halfP, new ClippedSink(moments, clip, flux, weight, counts),
                0, canvasW, 0, canvasH, source, badPixelMask, hasBadPixelMask, y0, y1);
            Interlocked.Add(ref rejected, counts[0]);
            Interlocked.Add(ref total, counts[1]);
        });
        return (rejected, total);
    }

    /// <summary>
    /// Runs <paramref name="body"/> once per canvas strip of <see cref="ParallelStripRows"/> rows, in
    /// parallel, handing it the strip's rows and the source rectangle that can reach them.
    /// </summary>
    /// <remarks>
    /// <para><b>Race-free by ownership:</b> a strip writes only its own rows (the deposit's row clamp),
    /// so no cell is ever written by two workers, and the source rectangle is widened by a halo so every
    /// photosite whose drop reaches the strip is inside it. A drop straddling two strips is iterated by
    /// both, and each deposits only its own part.</para>
    /// <para><b>Bit-identical to the serial deposit:</b> a cell's contributions arrive in row-major
    /// source order either way (the strip visits a sub-rectangle of the frame in the same order), so
    /// every cell's float sums are formed in the same order and round the same.</para>
    /// </remarks>
    private static void ForEachStrip(Image raw, Matrix3x2 transform, int canvasW, int canvasH, Action<int, int, PixelRect> body)
    {
        var halo = StripProjectionHalo(transform);
        var strips = (canvasH + ParallelStripRows - 1) / ParallelStripRows;
        Parallel.For(0, strips, s =>
        {
            var y0 = s * ParallelStripRows;
            var y1 = Math.Min(canvasH, y0 + ParallelStripRows);
            var source = CanvasGeometry.ProjectCanvasRectToSourceRect(
                new PixelRect(0, y0, canvasW, y1 - y0), transform, raw.Width, raw.Height, halo);
            if (source.Width > 0 && source.Height > 0)
            {
                body(y0, y1, source);
            }
        });
    }

    /// <summary>
    /// Source pixels to widen a strip's projected rectangle by. A drop that reaches a strip has its
    /// centre at most one canvas pixel outside it (a drop is at most one cell wide), which is
    /// 1 / sigma source pixels along the transform's shortest direction, sigma its smallest singular
    /// value. Twice that, and never under the two the tile-pipelined drizzle uses; a degenerate
    /// transform takes the whole frame.
    /// </summary>
    internal static int StripProjectionHalo(Matrix3x2 transform)
    {
        var a = transform.M11;
        var b = transform.M12;
        var c = transform.M21;
        var d = transform.M22;
        var squares = (a * a) + (b * b) + (c * c) + (d * d);
        var det = MathF.Abs((a * d) - (b * c));
        var disc = MathF.Sqrt(MathF.Max(0f, (squares * squares) - (4f * det * det)));
        var sigmaMin = MathF.Sqrt(MathF.Max(0f, (squares - disc) / 2f));
        return sigmaMin <= 1e-3f ? int.MaxValue / 4 : Math.Max(2, (int)MathF.Ceiling(2f / sigmaMin));
    }

    /// <summary>
    /// The deposit loop. The accumulators' origin is (<paramref name="xStart"/>, <paramref name="yStart"/>);
    /// cells are written only in rows [<paramref name="clampY0"/>, <paramref name="clampY1"/>), which is the
    /// whole accumulator for a serial deposit and one worker's strip for a parallel one
    /// (<see cref="ForEachStrip"/>).
    /// </summary>
    /// <remarks>
    /// Source pixels are visited row-major over <paramref name="sourceRect"/>, so any one cell receives
    /// its contributions in the same order whichever sub-rectangle of the frame contains all of them:
    /// that is what makes a parallel deposit bit-identical to a serial one. Each source row is read as
    /// one span, resolved once per call, rather than through the image's per-sample indexer.
    /// </remarks>
    private static void Iterate<TMap, TSink>(
        Image raw,
        TMap map,
        int[,] pattern,
        float halfP,
        TSink sink,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        PixelRect sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask,
        int clampY0,
        int clampY1)
        where TMap : struct, ISourceToCanvas
        where TSink : struct, IDropSink
    {
        var srcX0 = sourceRect.X;
        var srcX1 = sourceRect.X + sourceRect.Width;
        var srcY0 = sourceRect.Y;
        var srcY1 = sourceRect.Y + sourceRect.Height;
        var plane = raw.GetChannelSpan(0);
        var width = raw.Width;

        for (var ySrc = srcY0; ySrc < srcY1; ySrc++)
        {
            var row = plane.Slice(ySrc * width, width);
            var chEven = pattern[ySrc & 1, 0];
            var chOdd = pattern[ySrc & 1, 1];
            var xSrc = srcX0;
            while (xSrc < srcX1)
            {
                // 64-wide horizontal chunk -- aligned to word boundary so a
                // single GetWord covers all 64 mask bits at once. ChunkEnd
                // is the next word boundary or the end of the rect,
                // whichever comes first.
                var chunkEnd = Math.Min(((xSrc >> 6) + 1) << 6, srcX1);
                var maskWord = hasBadPixelMask ? badPixelMask.GetWord(ySrc, xSrc >> 6) : 0UL;
                if (maskWord == 0UL)
                {
                    // Fast path: no per-pixel mask check needed.
                    for (; xSrc < chunkEnd; xSrc++)
                    {
                        var v = row[xSrc];
                        if (float.IsNaN(v)) continue;
                        DepositOne(v, (xSrc & 1) == 0 ? chEven : chOdd, xSrc, ySrc, map, halfP, sink,
                            xStart, xEnd, yStart, clampY0, clampY1);
                    }
                }
                else
                {
                    // Slow path: bit-test each pixel against the already-
                    // loaded mask word.
                    for (; xSrc < chunkEnd; xSrc++)
                    {
                        if ((maskWord & (1UL << (xSrc & 63))) != 0UL) continue;
                        var v = row[xSrc];
                        if (float.IsNaN(v)) continue;
                        DepositOne(v, (xSrc & 1) == 0 ? chEven : chOdd, xSrc, ySrc, map, halfP, sink,
                            xStart, xEnd, yStart, clampY0, clampY1);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Forward-project a single source pixel and deposit its area-weighted
    /// share into each canvas cell its drop covers. Coordinate convention
    /// matches <c>Image.SubpixelValue</c> / <c>WarpToReferenceGridAsync</c>:
    /// canvas cell at index <c>(xc, yc)</c> is centered on position
    /// <c>(xc, yc)</c> and occupies <c>[xc-0.5, xc+0.5] x [yc-0.5, yc+0.5]</c>.
    /// A previous version of this kernel mixed half-pixel-shift conventions
    /// and produced dumbbell-shaped stars in combined meridian-flip drizzle
    /// output -- keep this path strictly consistent with the warp path or
    /// the per-frame rotation residuals will reappear as visible artifacts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DepositOne<TMap, TSink>(
        float v, int ch, int xSrc, int ySrc,
        TMap map, float halfP,
        TSink sink,
        int xStart, int xEnd, int yStart, int clampY0, int clampY1)
        where TMap : struct, ISourceToCanvas
        where TSink : struct, IDropSink
    {
        var p = map.Map(xSrc, ySrc);
        var xLo = p.X - halfP;
        var xHi = p.X + halfP;
        var yLo = p.Y - halfP;
        var yHi = p.Y + halfP;

        // Loop bounds: cell index i overlaps the drop iff
        //   i - 0.5 < xHi and i + 0.5 > xLo,
        // i.e. floor(xLo + 0.5) <= i <= ceil(xHi - 0.5).
        // Clamp to the [xStart, xEnd) x [yStart, yEnd) accumulator range
        // (full canvas for the streaming drizzle, strip-local for tiled).
        var x0 = Math.Max(xStart, (int)MathF.Floor(xLo + 0.5f));
        var x1 = Math.Min(xEnd - 1, (int)MathF.Ceiling(xHi - 0.5f));
        var y0 = Math.Max(clampY0, (int)MathF.Floor(yLo + 0.5f));
        var y1 = Math.Min(clampY1 - 1, (int)MathF.Ceiling(yHi - 0.5f));
        if (x1 < x0 || y1 < y0) return;

        for (var yc = y0; yc <= y1; yc++)
        {
            var localY = yc - yStart;
            var cellYLo = yc - 0.5f;
            var cellYHi = yc + 0.5f;
            var dy = MathF.Min(yHi, cellYHi) - MathF.Max(yLo, cellYLo);
            if (dy <= 0f) continue;
            for (var xc = x0; xc <= x1; xc++)
            {
                var localX = xc - xStart;
                var cellXLo = xc - 0.5f;
                var cellXHi = xc + 0.5f;
                var dx = MathF.Min(xHi, cellXHi) - MathF.Max(xLo, cellXLo);
                if (dx <= 0f) continue;
                sink.Add(ch, localY, localX, v, dx * dy);
            }
        }
    }

    /// <summary>
    /// Final pass: <c>master = (flux / weight) * invMaxValue</c>, with NaN
    /// where coverage is zero. Mutates <paramref name="flux"/> in place
    /// (the caller hands the buffer over and treats the result as the
    /// master). <paramref name="weight"/> stays as the coverage map.
    /// Returns the number of cells that had non-zero coverage so the
    /// caller can build the rejection-rate diagnostic.
    /// </summary>
    /// <param name="flux">Per-channel flux accumulator -- on return, holds
    /// the normalised master pixels.</param>
    /// <param name="weight">Per-channel coverage weight accumulator. Read-
    /// only here.</param>
    /// <param name="invMaxValue"><c>1 / sourceMaxValue</c> so the master
    /// lands in <c>[0, 1]</c> -- matches the Integrator's per-frame
    /// normalization output so <c>MasterPostProcessor</c>'s MaxValue=1.0
    /// fix-up and <c>Image.Histogram</c>'s MaxValue&lt;=1.0 branch both see
    /// a self-consistent (range, label) pair.</param>
    /// <param name="height">Accumulator rows.</param>
    /// <param name="width">Accumulator columns.</param>
    public static long FinaliseDivide(float[][,] flux, float[][,] weight, float invMaxValue, int height, int width)
    {
        long coveredCells = 0;
        for (var c = 0; c < flux.Length; c++)
        {
            var f = flux[c];
            var w = weight[c];
            Parallel.For(0, height, () => 0L,
                (y, _, covered) => covered + DivideRow(
                    MemoryMarshal.CreateSpan(ref f[y, 0], width),
                    MemoryMarshal.CreateReadOnlySpan(ref w[y, 0], width),
                    invMaxValue),
                covered => Interlocked.Add(ref coveredCells, covered));
        }
        return coveredCells;
    }

    /// <summary>
    /// One row of <see cref="FinaliseDivide"/>: <c>flux / weight * invMax</c> where weight is positive,
    /// NaN elsewhere, returning the covered count. Vectorised with the same two float operations, in
    /// the same order, as the scalar tail, so every lane rounds exactly as the scalar cell would.
    /// </summary>
    private static long DivideRow(Span<float> flux, ReadOnlySpan<float> weight, float invMax)
    {
        long covered = 0;
        var i = 0;
        if (Vector.IsHardwareAccelerated && flux.Length >= Vector<float>.Count)
        {
            var lanes = Vector<float>.Count;
            var scale = new Vector<float>(invMax);
            var nan = new Vector<float>(float.NaN);
            ref var f0 = ref MemoryMarshal.GetReference(flux);
            ref var w0 = ref MemoryMarshal.GetReference(weight);
            for (; i <= flux.Length - lanes; i += lanes)
            {
                var wv = Vector.LoadUnsafe(ref w0, (nuint)i);
                var fv = Vector.LoadUnsafe(ref f0, (nuint)i);
                var has = Vector.GreaterThan(wv, Vector<float>.Zero);
                Vector.ConditionalSelect(has, fv / wv * scale, nan).StoreUnsafe(ref f0, (nuint)i);
                // A true lane is all ones, which is -1 as an int.
                covered -= Vector.Sum(has);
            }
        }

        for (; i < flux.Length; i++)
        {
            var wv = weight[i];
            if (wv > 0f)
            {
                flux[i] = flux[i] / wv * invMax;
                covered++;
            }
            else
            {
                flux[i] = float.NaN;
            }
        }
        return covered;
    }
}
