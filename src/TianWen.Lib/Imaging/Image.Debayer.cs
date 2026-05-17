using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public Task<Image> DebayerAsync(DebayerAlgorithm debayerAlgorithm, bool normalizeToUnit = false, CancellationToken cancellationToken = default)
    {
        // NO-OP for monochrome or full colour images
        if (imageMeta.SensorType is SensorType.Monochrome or SensorType.Color)
        {
            return Task.FromResult(normalizeToUnit ? ScaleFloatValuesToUnitInPlace() : this);
        }

        var scale = normalizeToUnit && MaxValue > 1.0f ? 1.0f / MaxValue : 1.0f;

        return debayerAlgorithm switch
        {
            DebayerAlgorithm.BilinearMono => DebayerBilinearMonoAsync(scale, cancellationToken),
            DebayerAlgorithm.VNG => DebayerVNGAsync(scale, cancellationToken),
            DebayerAlgorithm.AHD => DebayerAHDAsync(scale, cancellationToken),
            DebayerAlgorithm.None => throw new ArgumentException("Must specify an algorithm", nameof(debayerAlgorithm)),
            _ => throw new NotSupportedException($"Debayer algorithm {debayerAlgorithm} is not supported"),
        };
    }

    /// <summary>
    /// Debayers into pre-allocated destination channels. Zero allocation on the output path —
    /// the caller owns the <paramref name="destination"/> arrays and can reuse them across frames.
    /// Returns an <see cref="Image"/> wrapping the destination arrays (no new arrays allocated).
    /// </summary>
    /// <param name="destination">Pre-allocated channel arrays: 1 for BilinearMono, 3 for AHD/VNG.
    /// Must match this image's (Height, Width). Allocated by caller on first frame, reused thereafter.</param>
    /// <summary>
    /// Debayers into pre-allocated destination <see cref="Channel"/> arrays. Zero allocation on the output path —
    /// the caller owns the channels and can reuse them across frames.
    /// Returns an <see cref="Image"/> wrapping the channel data (no new arrays allocated).
    /// For mono/color: copies + normalizes into destination channels instead of modifying in place.
    /// </summary>
    /// <summary>
    /// Debayers a sub-region of the source into pre-allocated full-canvas
    /// destination channels. Only the pixels inside <paramref name="sourceRect"/>
    /// (plus the algorithm's halo neighbours) are touched; the rest of the
    /// destination keeps whatever the caller put there (typically pool garbage,
    /// harmless because downstream consumers sample inside the rect).
    /// </summary>
    /// <param name="destination">Pre-allocated full-canvas channels (3 for
    /// AHD/VNG, 1 for BilinearMono). Caller owns and reuses across calls.</param>
    /// <param name="debayerAlgorithm">Currently <see cref="DebayerAlgorithm.AHD"/>
    /// is the only sub-region-aware impl; other algorithms throw.</param>
    /// <param name="sourceRect">Pixel rect in source-frame coordinates. Caller
    /// must add halo for the algorithm (AHD needs radius+homogeneityRadius=4
    /// pixels; warp consumers typically add 1 more for bilinear sampling) so
    /// the pixels they care about have valid neighbours inside the rect.</param>
    public Task<Image> DebayerRegionIntoAsync(Channel[] destination, DebayerAlgorithm debayerAlgorithm, System.Drawing.Rectangle sourceRect, CancellationToken cancellationToken = default)
    {
        var destArrays = new float[destination.Length][,];
        for (var c = 0; c < destination.Length; c++) destArrays[c] = destination[c].Data;

        // Non-Bayer sources: no debayer to do, just copy the rect rows of
        // each input channel into the corresponding destination channel.
        // Mirrors DebayerIntoAsync's mono/color short-circuit so callers
        // can stay sensor-agnostic.
        if (imageMeta.SensorType is SensorType.Monochrome or SensorType.Color)
        {
            CopyRectIntoDestination(destArrays, sourceRect);
            return Task.FromResult(new Image(destArrays, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta));
        }

        return debayerAlgorithm switch
        {
            DebayerAlgorithm.AHD => DebayerAHDAsync(scale: 1.0f, cancellationToken, destArrays, sourceRect),
            _ => throw new NotSupportedException(
                $"DebayerRegionIntoAsync currently only supports AHD on Bayer sources; {debayerAlgorithm} would need a sub-region overload of its inner loop."),
        };
    }

    private void CopyRectIntoDestination(float[][,] destArrays, System.Drawing.Rectangle sourceRect)
    {
        var y0 = Math.Max(0, sourceRect.Y);
        var y1 = Math.Min(Height, sourceRect.Y + sourceRect.Height);
        var x0 = Math.Max(0, sourceRect.X);
        var x1 = Math.Min(Width, sourceRect.X + sourceRect.Width);
        if (y0 >= y1 || x0 >= x1) return;
        var rowLen = x1 - x0;
        var copyChannels = Math.Min(data.Length, destArrays.Length);
        for (var c = 0; c < copyChannels; c++)
        {
            var src = data[c];
            var dst = destArrays[c];
            for (var y = y0; y < y1; y++)
            {
                var srcRow = MemoryMarshal.CreateReadOnlySpan(ref src[y, x0], rowLen);
                var dstRow = MemoryMarshal.CreateSpan(ref dst[y, x0], rowLen);
                srcRow.CopyTo(dstRow);
            }
        }
    }

    public async Task<Image> DebayerIntoAsync(Channel[] destination, DebayerAlgorithm debayerAlgorithm, bool normalizeToUnit = false, CancellationToken cancellationToken = default)
    {
        // Extract float[][,] from Channel[] for the internal debayer methods
        var destArrays = new float[destination.Length][,];
        for (var c = 0; c < destination.Length; c++) destArrays[c] = destination[c].Data;

        if (imageMeta.SensorType is SensorType.Monochrome or SensorType.Color)
        {
            // Copy (+ normalize) into destination channels
            var scale = normalizeToUnit && MaxValue > 1.0f + float.Epsilon ? 1.0f / MaxValue : 1.0f;
            for (var c = 0; c < Math.Min(data.Length, destination.Length); c++)
            {
                var src = MemoryMarshal.CreateReadOnlySpan(ref data[c][0, 0], data[c].Length);
                var dst = destination[c].AsMutableSpan();
                if (scale == 1.0f)
                {
                    src.CopyTo(dst);
                }
                else
                {
                    MultiplyScalar(src, scale, dst);
                }
            }

            var normalized = scale < 1.0f;
            return new Image(destArrays, BitDepth.Float32,
                normalized ? 1.0f : maxValue,
                normalized ? minValue / maxValue : minValue,
                normalized ? pedestal / maxValue : pedestal,
                imageMeta);
        }

        var s = normalizeToUnit && MaxValue > 1.0f ? 1.0f / MaxValue : 1.0f;

        return debayerAlgorithm switch
        {
            DebayerAlgorithm.BilinearMono => await DebayerBilinearMonoAsync(s, cancellationToken, destArrays),
            DebayerAlgorithm.AHD => await DebayerAHDAsync(s, cancellationToken, destArrays),
            _ => throw new NotSupportedException($"DebayerIntoAsync does not support {debayerAlgorithm}"),
        };
    }

    /// <summary>
    /// Uses a simple 2x2 sliding window to calculate the average of 4 pixels, assumes simple 2x2 Bayer matrix.
    /// Is a no-op for monochrome fames.
    /// </summary>
    /// <returns>Debayered monochrome image</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerBilinearMonoAsync(float scale, CancellationToken cancellationToken = default, float[][,]? destination = null)
    {
        var width = Width;
        var height = Height;
        var debayered = destination ?? CreateChannelData(1, height, width);
        var dstChannel = debayered[0];
        var srcChannel = data[0];
        var w1 = width - 1;
        var h1 = height - 1;
        var s = (double)scale;

        // Parallel.For runs the body directly on worker threads, no per-row
        // Task.Run wrapper / async lambda / state machine. Default MaxDoP
        // (= ProcessorCount) -- 4x oversubscription was a holdover from the
        // per-row async pattern; for memory-bandwidth-bound debayer work it
        // causes cache-line contention.
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
        };

        // Process all rows except the last one in parallel
        Parallel.For(0, h1, parallelOptions, y =>
        {
            for (int x = 0; x < w1; x++)
            {
                dstChannel[y, x] = (float)(0.25d * s * ((double)srcChannel[y, x] + srcChannel[y + 1, x + 1] + srcChannel[y, x + 1] + srcChannel[y + 1, x]));
            }

            // last column
            dstChannel[y, w1] = (float)(0.25d * s * ((double)srcChannel[y, w1] + srcChannel[y + 1, w1 - 1] + srcChannel[y, w1 - 1] + srcChannel[y + 1, w1]));
        });

        // last row (processed sequentially as it's a single row)
        for (int x = 0; x < w1; x++)
        {
            dstChannel[h1, x] = (float)(0.25d * s * ((double)srcChannel[h1, x] + srcChannel[h1 - 1, x + 1] + srcChannel[h1, x + 1] + srcChannel[h1 - 1, x]));
        }

        // last pixel
        dstChannel[h1, w1] = (float)(0.25d * s * ((double)srcChannel[h1, w1] + srcChannel[h1 - 1, w1 - 1] + srcChannel[h1, w1 - 1] + srcChannel[h1 - 1, w1]));

        var normalized = scale < 1.0f;
        return new Image(debayered, BitDepth.Float32,
            normalized ? 1.0f : maxValue,
            normalized ? minValue / maxValue : minValue,
            normalized ? pedestal / maxValue : pedestal,
            imageMeta with
            {
                SensorType = SensorType.Monochrome,
                BayerOffsetX = 0,
                BayerOffsetY = 0,
                Filter = Filter.Luminance
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerVNGAsync(float scale, CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var debayered = CreateChannelData(3, height, width); // RGB output

        var bayerOffsetX = imageMeta.BayerOffsetX;
        var bayerOffsetY = imageMeta.BayerOffsetY;

        var bayerPattern = imageMeta.SensorType.GetBayerPatternMatrix(bayerOffsetX, bayerOffsetY);

        // Pre-compute pattern for rows (avoids modulo in inner loop)
        var pattern00 = bayerPattern[0, 0];
        var pattern01 = bayerPattern[0, 1];
        var pattern10 = bayerPattern[1, 0];
        var pattern11 = bayerPattern[1, 1];

        const int R = 0, G = 1, B = 2;
        const int radius = 2;

        var srcChannel = data[0];
        var dstR = debayered[R];
        var dstG = debayered[G];
        var dstB = debayered[B];

        // Process interior pixels in parallel (where full VNG can be applied).
        // Same pinned-ref + Unsafe.Add pattern as AHD Phase 1+2 and Phase 3:
        // src + dst channels read/written through ref-to-(0, 0) + idx so the
        // per-pixel hot path has no bounds checks. Helpers take ref + idx
        // and stay [AggressiveInlining] so the call vanishes after JIT.
        var strideVng = width;
        Parallel.For(radius,
            height - radius,
            new ParallelOptions { CancellationToken = cancellationToken },
            y =>
            {
                ref var src0 = ref srcChannel[0, 0];
                ref var dstR0 = ref dstR[0, 0];
                ref var dstG0 = ref dstG[0, 0];
                ref var dstB0 = ref dstB[0, 0];

                // Pre-select pattern row based on y % 2
                int patternEven = (y & 1) == 0 ? pattern00 : pattern10;
                int patternOdd = (y & 1) == 0 ? pattern01 : pattern11;

                for (int x = radius; x < width - radius; x++)
                {
                    int knownColor = (x & 1) == 0 ? patternEven : patternOdd;
                    int idx = y * strideVng + x;

                    // Copy known value to its channel
                    float rawValue = Unsafe.Add(ref src0, idx) * scale;
                    if (knownColor == R) Unsafe.Add(ref dstR0, idx) = rawValue;
                    else if (knownColor == G) Unsafe.Add(ref dstG0, idx) = rawValue;
                    else Unsafe.Add(ref dstB0, idx) = rawValue;

                    // Interpolate missing colors based on which color we have
                    if (knownColor == G)
                    {
                        // At green pixel: interpolate R and B
                        // Check if R is on horizontal or vertical neighbors
                        int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                        bool rOnHorizontal = neighborColor == R;

                        Unsafe.Add(ref dstR0, idx) = scale * (rOnHorizontal
                            ? InterpolateHorizontalVNG(ref src0, idx)
                            : InterpolateVerticalVNG(ref src0, idx, strideVng));
                        Unsafe.Add(ref dstB0, idx) = scale * (rOnHorizontal
                            ? InterpolateVerticalVNG(ref src0, idx, strideVng)
                            : InterpolateHorizontalVNG(ref src0, idx));
                    }
                    else
                    {
                        // At R or B pixel: interpolate G and the opposite color
                        Unsafe.Add(ref dstG0, idx) = scale * InterpolateGreenAtRBVNG(ref src0, idx, strideVng);
                        float diagonal = scale * InterpolateDiagonalVNG(ref src0, idx, strideVng);
                        if (knownColor == R) Unsafe.Add(ref dstB0, idx) = diagonal;
                        else Unsafe.Add(ref dstR0, idx) = diagonal;
                    }
                }
            }
        );

        // Process edge pixels with simpler bilinear interpolation (not parallelized - small portion)
        ProcessEdgePixels(debayered, width, height, radius, bayerPattern, scale);

        var normalized = scale < 1.0f;
        return new Image(debayered, BitDepth.Float32,
            normalized ? 1.0f : maxValue,
            normalized ? minValue / maxValue : minValue,
            normalized ? pedestal / maxValue : pedestal,
            imageMeta with
            {
                SensorType = SensorType.Color,
                BayerOffsetX = 0,
                BayerOffsetY = 0
            });
    }

    // VNG helpers below take a pinned `ref float src0` (the (0, 0) element of
    // the source channel) + a flat idx + the row stride, so the body uses
    // Unsafe.Add instead of bounds-checked float[,] indexing. Caller pins
    // the ref once per row in the Parallel.For body and passes idx/stride
    // forward; helpers stay [AggressiveInlining] so the call vanishes.

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static float InterpolateGreenAtRBVNG(ref float src0, int idx, int stride)
    {
        // Interpolate green at R or B position using 4 cardinal directions
        float center = Unsafe.Add(ref src0, idx);

        // North: green at y-1, same color at y-2
        float gN = Unsafe.Add(ref src0, idx - stride);
        float vN = Unsafe.Add(ref src0, idx - 2 * stride);
        float gradN = MathF.Abs(MathF.FusedMultiplyAdd(2, gN, -vN - center));
        float valN = gN + (center - vN) * 0.5f;

        // South
        float gS = Unsafe.Add(ref src0, idx + stride);
        float vS = Unsafe.Add(ref src0, idx + 2 * stride);
        float gradS = MathF.Abs(MathF.FusedMultiplyAdd(2, gS, -center - vS));
        float valS = MathF.FusedMultiplyAdd(center - vS, 0.5f, gS);

        // West
        float gW = Unsafe.Add(ref src0, idx - 1);
        float vW = Unsafe.Add(ref src0, idx - 2);
        float gradW = MathF.Abs(2 * gW - vW - center);
        float valW = MathF.FusedMultiplyAdd(center - vW, 0.5f, gW);

        // East
        float gE = Unsafe.Add(ref src0, idx + 1);
        float vE = Unsafe.Add(ref src0, idx + 2);
        float gradE = MathF.Abs(2 * gE - center - vE);
        float valE = MathF.FusedMultiplyAdd(center - vE, 0.5f, gE);

        // Find minimum gradient and threshold
        float minGrad = MathF.Min(MathF.Min(gradN, gradS), MathF.Min(gradW, gradE));
        float threshold = minGrad * 1.5f;

        // Average values within threshold
        float sum = 0;
        int count = 0;

        if (gradN <= threshold) { sum += valN; count++; }
        if (gradS <= threshold) { sum += valS; count++; }
        if (gradW <= threshold) { sum += valW; count++; }
        if (gradE <= threshold) { sum += valE; count++; }

        return count > 0 ? sum / count : valN;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static float InterpolateHorizontalVNG(ref float src0, int idx)
    {
        // Interpolate R or B at green position from horizontal neighbors
        float center = Unsafe.Add(ref src0, idx);
        float left = Unsafe.Add(ref src0, idx - 1);
        float right = Unsafe.Add(ref src0, idx + 1);

        float gradL = MathF.Abs(left - center);
        float gradR = MathF.Abs(right - center);

        float minGrad = MathF.Min(gradL, gradR);
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradL <= threshold) { sum += left; count++; }
        if (gradR <= threshold) { sum += right; count++; }

        return count > 0 ? sum / count : (left + right) * 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static float InterpolateVerticalVNG(ref float src0, int idx, int stride)
    {
        // Interpolate R or B at green position from vertical neighbors
        float center = Unsafe.Add(ref src0, idx);
        float top = Unsafe.Add(ref src0, idx - stride);
        float bottom = Unsafe.Add(ref src0, idx + stride);

        float gradT = MathF.Abs(top - center);
        float gradB = MathF.Abs(bottom - center);

        float minGrad = MathF.Min(gradT, gradB);
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradT <= threshold) { sum += top; count++; }
        if (gradB <= threshold) { sum += bottom; count++; }

        return count > 0 ? sum / count : (top + bottom) * 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static float InterpolateDiagonalVNG(ref float src0, int idx, int stride)
    {
        // Interpolate R at B or B at R from 4 diagonal neighbors
        float center = Unsafe.Add(ref src0, idx);

        float nw = Unsafe.Add(ref src0, idx - stride - 1);
        float ne = Unsafe.Add(ref src0, idx - stride + 1);
        float sw = Unsafe.Add(ref src0, idx + stride - 1);
        float se = Unsafe.Add(ref src0, idx + stride + 1);

        // Green values at cardinal neighbors
        float gN = Unsafe.Add(ref src0, idx - stride);
        float gS = Unsafe.Add(ref src0, idx + stride);
        float gW = Unsafe.Add(ref src0, idx - 1);
        float gE = Unsafe.Add(ref src0, idx + 1);

        // Calculate gradients including green channel differences
        float gradNW = MathF.Abs(nw - center) + MathF.Abs(gN - gW);
        float gradNE = MathF.Abs(ne - center) + MathF.Abs(gN - gE);
        float gradSW = MathF.Abs(sw - center) + MathF.Abs(gS - gW);
        float gradSE = MathF.Abs(se - center) + MathF.Abs(gS - gE);

        float minGrad = MathF.Min(MathF.Min(gradNW, gradNE), MathF.Min(gradSW, gradSE));
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradNW <= threshold) { sum += nw; count++; }
        if (gradNE <= threshold) { sum += ne; count++; }
        if (gradSW <= threshold) { sum += sw; count++; }
        if (gradSE <= threshold) { sum += se; count++; }

        return count > 0 ? sum / count : (nw + ne + sw + se) * 0.25f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessEdgePixels(float[][,] debayered, int width, int height, int radius, int[,] bayerPattern, float scale = 1.0f, System.Drawing.Rectangle? activeRect = null)
    {
        // If the caller is debayering a sub-region, restrict edge-pixel
        // filling to the canvas-edge zone INSIDE that rect; pixels outside
        // the rect aren't touched (caller doesn't care about them and the
        // scratch arrays may hold pool-garbage there). Interior rects are
        // typically a no-op (fast intersection check below).
        var rect = activeRect ?? new System.Drawing.Rectangle(0, 0, width, height);
        var rx0 = Math.Max(0, rect.X);
        var ry0 = Math.Max(0, rect.Y);
        var rx1 = Math.Min(width, rect.X + rect.Width);
        var ry1 = Math.Min(height, rect.Y + rect.Height);

        // Top and bottom edges within rect
        for (int y = ry0; y < ry1; y++)
        {
            if (y >= radius && y < height - radius) continue; // Skip interior rows

            for (int x = rx0; x < rx1; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern, scale);
            }
        }

        // Left and right edges within rect (excluding corners already processed)
        var sideY0 = Math.Max(ry0, radius);
        var sideY1 = Math.Min(ry1, height - radius);
        for (int y = sideY0; y < sideY1; y++)
        {
            for (int x = rx0; x < Math.Min(rx1, radius); x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern, scale);
            }
            for (int x = Math.Max(rx0, width - radius); x < rx1; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern, scale);
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessEdgePixel(float[][,] debayered, int x, int y, int width, int height, int[,] bayerPattern, float scale)
    {
        int knownColor = bayerPattern[y & 1, x & 1];
        debayered[knownColor][y, x] = data[0][y, x] * scale;

        for (int c = 0; c < 3; c++)
        {
            if (c != knownColor)
            {
                debayered[c][y, x] = BilinearInterpolateColorFast(x, y, c, width, height, bayerPattern) * scale;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float BilinearInterpolateColorFast(int x, int y, int targetColor, int width, int height, int[,] bayerPattern)
    {
        float sum = 0;
        int count = 0;
        var srcChannel = data[0];

        int yMin = Math.Max(0, y - 2);
        int yMax = Math.Min(height - 1, y + 2);
        int xMin = Math.Max(0, x - 2);
        int xMax = Math.Min(width - 1, x + 2);

        for (int ny = yMin; ny <= yMax; ny++)
        {
            int patternY = ny & 1;
            for (int nx = xMin; nx <= xMax; nx++)
            {
                if (bayerPattern[patternY, nx & 1] == targetColor)
                {
                    sum += srcChannel[ny, nx];
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : 0;
    }

    private async Task<Image> DebayerAHDAsync(float scale, CancellationToken cancellationToken, float[][,]? destination = null, System.Drawing.Rectangle? sourceRect = null)
    {
        var width = Width;
        var height = Height;
        // Scratch arrays for AHD phases — pooled to avoid 6 × H×W allocations per frame
        using var debR = Array2DPool<float>.RentScoped(height, width);
        using var debG = Array2DPool<float>.RentScoped(height, width);
        using var debB = Array2DPool<float>.RentScoped(height, width);
        var debayered = new float[][,] { debR.Array, debG.Array, debB.Array };

        var bayerOffsetX = imageMeta.BayerOffsetX;
        var bayerOffsetY = imageMeta.BayerOffsetY;

        var bayerPattern = imageMeta.SensorType.GetBayerPatternMatrix(bayerOffsetX, bayerOffsetY);

        var pattern00 = bayerPattern[0, 0];
        var pattern01 = bayerPattern[0, 1];
        var pattern10 = bayerPattern[1, 0];
        var pattern11 = bayerPattern[1, 1];

        const int R = 0, G = 1, B = 2;
        const int radius = 2;
        const int homogeneityRadius = 2; // neighborhood radius for homogeneity comparison
        const int totalRadius = radius + homogeneityRadius;

        // Sub-region iteration bounds. sourceRect=null -> full frame (today's
        // behaviour, byte-equivalent). sourceRect=R -> iterate only over R.
        // Each phase consumes the previous phase's output in a halo window,
        // so the inner phases process a slab WIDER than the rect to make sure
        // the rect's edge pixels in the next phase have valid neighbours.
        // Walking the dependency chain backwards from the final Phase 4
        // outputs (which sit at rect rows):
        //  Phase 4 reads dst at y/x ± 1 (3x3 median filter)
        //    -> Phase 3 must cover rect grown by 1.
        //  Phase 3 reads rgbH/V at y/x ± homogeneityRadius
        //    -> Phase 1 must cover rect grown by (1 + homogeneityRadius).
        //  ProcessEdgePixels must fill canvas-edge pixels INSIDE rect-grown-
        //  by-1 so Phase 4's halo reads land on valid edge fill.
        // Pixels inside the rect that fall in the canvas-edge zone get the
        // standard ProcessEdgePixels treatment below.
        var rect = sourceRect ?? new System.Drawing.Rectangle(0, 0, width, height);
        var rectRight = rect.X + rect.Width;
        var rectBottom = rect.Y + rect.Height;
        const int phase4Halo = 1;
        var phase1Grow = phase4Halo + homogeneityRadius;
        // Phase 1 (interior, radius=2 halo around source reads)
        var p1Y0 = Math.Max(radius, rect.Y - phase1Grow);
        var p1Y1 = Math.Min(height - radius, rectBottom + phase1Grow);
        var p1X0 = Math.Max(radius, rect.X - phase1Grow);
        var p1X1 = Math.Min(width - radius, rectRight + phase1Grow);
        // Phase 3 (interior, totalRadius=4 halo: phase 1 result + homogeneity
        // neighbours; grown by 1 so Phase 4's median filter reads land in
        // valid rows)
        var p3Y0 = Math.Max(totalRadius, rect.Y - phase4Halo);
        var p3Y1 = Math.Min(height - totalRadius, rectBottom + phase4Halo);
        var p3X0 = Math.Max(totalRadius, rect.X - phase4Halo);
        var p3X1 = Math.Min(width - totalRadius, rectRight + phase4Halo);
        // Phase 4 (whole rect; 3x3 median filter handles edges via copy-as-is)
        var p4Y0 = Math.Max(0, rect.Y);
        var p4Y1 = Math.Min(height, rectBottom);
        var p4X0 = Math.Max(0, rect.X);
        var p4X1 = Math.Min(width, rectRight);
        // ProcessEdgePixels target: rect grown by Phase 4's halo so dst at
        // (rect.Y - 1, *) etc. is filled with canvas-edge values where it
        // lies in the radius zone.
        var edgeFillRect = sourceRect is null
            ? (System.Drawing.Rectangle?)null
            : new System.Drawing.Rectangle(rect.X - phase4Halo, rect.Y - phase4Halo, rect.Width + 2 * phase4Halo, rect.Height + 2 * phase4Halo);

        // Phase 1 & 2: Build horizontal and vertical full-color interpolations in parallel
        var rgbH = destination ?? CreateChannelData(3, height, width);
        using var vR = Array2DPool<float>.RentScoped(height, width);
        using var vG = Array2DPool<float>.RentScoped(height, width);
        using var vB = Array2DPool<float>.RentScoped(height, width);
        var rgbV = new float[][,] { vR.Array, vG.Array, vB.Array };

        var srcChannel = data[0];
        var rgbH_R = rgbH[R]; var rgbH_G = rgbH[G]; var rgbH_B = rgbH[B];
        var rgbV_R = rgbV[R]; var rgbV_G = rgbV[G]; var rgbV_B = rgbV[B];

        // Parallel.For runs sync bodies directly on worker threads -- no
        // per-row async lambda / Task.Run / ValueTask.CompletedTask overhead.
        // Default MaxDoP = ProcessorCount; the 4x oversubscription was a
        // holdover from per-row task scheduling and causes cache-line
        // contention on the 9 full-image scratch arrays AHD allocates.
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
        };

        // Interpolate green channel using horizontal and vertical directions,
        // then R/B guided by green. Same Unsafe.Add-via-pinned-ref trick as
        // Phase 3 -- per-pixel reads/writes drop their bounds checks.
        var stridePhase1 = width;
        Parallel.For(p1Y0, p1Y1, parallelOptions, y =>
        {
            ref var src0 = ref srcChannel[0, 0];
            ref var rgbH_R0 = ref rgbH_R[0, 0];
            ref var rgbH_G0 = ref rgbH_G[0, 0];
            ref var rgbH_B0 = ref rgbH_B[0, 0];
            ref var rgbV_R0 = ref rgbV_R[0, 0];
            ref var rgbV_G0 = ref rgbV_G[0, 0];
            ref var rgbV_B0 = ref rgbV_B[0, 0];

            int patternEven = (y & 1) == 0 ? pattern00 : pattern10;
            int patternOdd = (y & 1) == 0 ? pattern01 : pattern11;

            for (int x = p1X0; x < p1X1; x++)
            {
                int knownColor = (x & 1) == 0 ? patternEven : patternOdd;
                int idx = y * stridePhase1 + x;
                float rawValue = Unsafe.Add(ref src0, idx);

                if (knownColor == G)
                {
                    // At green pixel: green is known for both directions
                    Unsafe.Add(ref rgbH_G0, idx) = rawValue;
                    Unsafe.Add(ref rgbV_G0, idx) = rawValue;

                    // Determine which color is on horizontal vs vertical neighbors
                    int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                    bool rOnHorizontal = neighborColor == R;

                    // Simple bilinear average (AHD defers direction choice to homogeneity)
                    float hAvg = (Unsafe.Add(ref src0, idx - 1) + Unsafe.Add(ref src0, idx + 1)) * 0.5f;
                    float vAvg = (Unsafe.Add(ref src0, idx - stridePhase1) + Unsafe.Add(ref src0, idx + stridePhase1)) * 0.5f;

                    float rVal = rOnHorizontal ? hAvg : vAvg;
                    float bVal = rOnHorizontal ? vAvg : hAvg;
                    Unsafe.Add(ref rgbH_R0, idx) = rVal;
                    Unsafe.Add(ref rgbH_B0, idx) = bVal;
                    Unsafe.Add(ref rgbV_R0, idx) = rVal;
                    Unsafe.Add(ref rgbV_B0, idx) = bVal;
                }
                else
                {
                    // At R or B pixel: interpolate green directionally
                    float center = rawValue;

                    // Horizontal green interpolation (Laplacian-corrected)
                    float gW = Unsafe.Add(ref src0, idx - 1);
                    float gE = Unsafe.Add(ref src0, idx + 1);
                    float greenH = (gW + gE) * 0.5f + (2 * center - Unsafe.Add(ref src0, idx - 2) - Unsafe.Add(ref src0, idx + 2)) * 0.25f;

                    // Vertical green interpolation (Laplacian-corrected)
                    float gN = Unsafe.Add(ref src0, idx - stridePhase1);
                    float gS = Unsafe.Add(ref src0, idx + stridePhase1);
                    float greenV = (gN + gS) * 0.5f + (2 * center - Unsafe.Add(ref src0, idx - 2 * stridePhase1) - Unsafe.Add(ref src0, idx + 2 * stridePhase1)) * 0.25f;

                    Unsafe.Add(ref rgbH_G0, idx) = greenH;
                    Unsafe.Add(ref rgbV_G0, idx) = greenV;

                    // Diagonal neighbors hold the opposite color
                    float dNW = Unsafe.Add(ref src0, idx - stridePhase1 - 1);
                    float dNE = Unsafe.Add(ref src0, idx - stridePhase1 + 1);
                    float dSW = Unsafe.Add(ref src0, idx + stridePhase1 - 1);
                    float dSE = Unsafe.Add(ref src0, idx + stridePhase1 + 1);

                    // Per-pixel color-difference: each diagonal pixel's green is estimated
                    // from its 2 nearest cardinal green neighbors
                    float cdNW = dNW - (gN + gW) * 0.5f;
                    float cdNE = dNE - (gN + gE) * 0.5f;
                    float cdSW = dSW - (gS + gW) * 0.5f;
                    float cdSE = dSE - (gS + gE) * 0.5f;
                    float cdAvg = (cdNW + cdNE + cdSW + cdSE) * 0.25f;

                    // Reconstruct: opposite = green_interpolated + color_difference.
                    // Branch on knownColor so we can use named refs instead of
                    // rgbH[oppositeColor] (which would defeat the unchecked-access
                    // win by going through the ref array indexer).
                    float oppositeH = greenH + cdAvg;
                    float oppositeV = greenV + cdAvg;
                    if (knownColor == R)
                    {
                        Unsafe.Add(ref rgbH_R0, idx) = rawValue;
                        Unsafe.Add(ref rgbV_R0, idx) = rawValue;
                        Unsafe.Add(ref rgbH_B0, idx) = oppositeH;
                        Unsafe.Add(ref rgbV_B0, idx) = oppositeV;
                    }
                    else // knownColor == B
                    {
                        Unsafe.Add(ref rgbH_B0, idx) = rawValue;
                        Unsafe.Add(ref rgbV_B0, idx) = rawValue;
                        Unsafe.Add(ref rgbH_R0, idx) = oppositeH;
                        Unsafe.Add(ref rgbV_R0, idx) = oppositeV;
                    }
                }
            }
        });

        // Phase 3: Compute homogeneity and select best direction.
        // Hot path: ~150 reads per output pixel x 9M pixels = 1.3 G reads.
        // float[,] indexing pays 2 bounds checks per access; we pin refs to
        // each scratch array's (0,0) and use Unsafe.Add for raw offset reads.
        // Per-row cost is 9 bounds-checked ref grabs paid once; per-pixel
        // cost is 0 bounds checks. Algorithm + arithmetic order unchanged,
        // so DebayerRegressionTests guarantees byte-identical output.
        var dstR = debayered[R]; var dstG = debayered[G]; var dstB = debayered[B];
        var stride = width;

        Parallel.For(p3Y0, p3Y1, parallelOptions, y =>
        {
            ref var rgbH_R0 = ref rgbH_R[0, 0];
            ref var rgbH_G0 = ref rgbH_G[0, 0];
            ref var rgbH_B0 = ref rgbH_B[0, 0];
            ref var rgbV_R0 = ref rgbV_R[0, 0];
            ref var rgbV_G0 = ref rgbV_G[0, 0];
            ref var rgbV_B0 = ref rgbV_B[0, 0];
            ref var dstR0 = ref dstR[0, 0];
            ref var dstG0 = ref dstG[0, 0];
            ref var dstB0 = ref dstB[0, 0];

            for (int x = p3X0; x < p3X1; x++)
            {
                int homH = 0, homV = 0;
                var centerIdx = y * stride + x;

                // Cache center RGB once; reused in the tie-break tail below.
                float cHR = Unsafe.Add(ref rgbH_R0, centerIdx);
                float cHG = Unsafe.Add(ref rgbH_G0, centerIdx);
                float cHB = Unsafe.Add(ref rgbH_B0, centerIdx);
                float lH = RgbToLuma(cHR, cHG, cHB);
                float aH = cHR - cHG;
                float bH = cHB - cHG;

                float cVR = Unsafe.Add(ref rgbV_R0, centerIdx);
                float cVG = Unsafe.Add(ref rgbV_G0, centerIdx);
                float cVB = Unsafe.Add(ref rgbV_B0, centerIdx);
                float lV = RgbToLuma(cVR, cVG, cVB);
                float aV = cVR - cVG;
                float bV = cVB - cVG;

                for (int dy = -homogeneityRadius; dy <= homogeneityRadius; dy++)
                {
                    for (int dx = -homogeneityRadius; dx <= homogeneityRadius; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int neighborIdx = centerIdx + dy * stride + dx;

                        // Horizontal neighbor differences
                        float nHR = Unsafe.Add(ref rgbH_R0, neighborIdx);
                        float nHG = Unsafe.Add(ref rgbH_G0, neighborIdx);
                        float nHB = Unsafe.Add(ref rgbH_B0, neighborIdx);
                        float nlH = RgbToLuma(nHR, nHG, nHB);
                        float naH = nHR - nHG;
                        float nbH = nHB - nHG;
                        float diffH = MathF.Abs(lH - nlH) + MathF.Abs(aH - naH) + MathF.Abs(bH - nbH);

                        // Vertical neighbor differences
                        float nVR = Unsafe.Add(ref rgbV_R0, neighborIdx);
                        float nVG = Unsafe.Add(ref rgbV_G0, neighborIdx);
                        float nVB = Unsafe.Add(ref rgbV_B0, neighborIdx);
                        float nlV = RgbToLuma(nVR, nVG, nVB);
                        float naV = nVR - nVG;
                        float nbV = nVB - nVG;
                        float diffV = MathF.Abs(lV - nlV) + MathF.Abs(aV - naV) + MathF.Abs(bV - nbV);

                        // Lower difference = more homogeneous
                        if (diffH < diffV) homH++;
                        else if (diffV < diffH) homV++;
                    }
                }

                // Select the direction with higher homogeneity, or average if tied.
                // Reuse cached center values instead of re-reading rgbH/V.
                if (homH > homV)
                {
                    Unsafe.Add(ref dstR0, centerIdx) = cHR;
                    Unsafe.Add(ref dstG0, centerIdx) = cHG;
                    Unsafe.Add(ref dstB0, centerIdx) = cHB;
                }
                else if (homV > homH)
                {
                    Unsafe.Add(ref dstR0, centerIdx) = cVR;
                    Unsafe.Add(ref dstG0, centerIdx) = cVG;
                    Unsafe.Add(ref dstB0, centerIdx) = cVB;
                }
                else
                {
                    Unsafe.Add(ref dstR0, centerIdx) = (cHR + cVR) * 0.5f;
                    Unsafe.Add(ref dstG0, centerIdx) = (cHG + cVG) * 0.5f;
                    Unsafe.Add(ref dstB0, centerIdx) = (cHB + cVB) * 0.5f;
                }
            }
        });

        // Fill edge pixels with bilinear interpolation before artifact reduction.
        // For sub-region calls, only process canvas-edge pixels intersecting
        // the rect-grown-by-Phase4-halo (typically zero work for interior strips).
        ProcessEdgePixels(debayered, width, height, totalRadius, bayerPattern, scale: 1.0f, edgeFillRect);

        // Phase 4: Artifact reduction - 3×3 median filter on color differences (R-G) and (B-G)
        // This smooths the abrupt H/V direction switching that causes per-pixel colour noise
        // Reuse rgbH — it is dead after Phase 3 (saves one full 3-channel allocation)
        var filtered = rgbH;
        var filtR = filtered[R]; var filtG = filtered[G]; var filtB = filtered[B];

        Parallel.For(p4Y0, p4Y1, parallelOptions, y =>
        {
            Span<float> medianBuf = stackalloc float[9];

            for (int x = p4X0; x < p4X1; x++)
            {
                // Green channel is kept as-is
                filtG[y, x] = dstG[y, x] * scale;

                if (y >= 1 && y < height - 1 && x >= 1 && x < width - 1)
                {
                    // Apply median filter on color differences for R and B
                    float gCenter = dstG[y, x];

                    for (int c = 0; c < 3; c += 2) // R (0) and B (2) only
                    {
                        var dstC = debayered[c];
                        int idx = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                medianBuf[idx++] = dstC[y + dy, x + dx] - dstG[y + dy, x + dx];
                            }
                        }

                        filtered[c][y, x] = (gCenter + MedianFast(medianBuf)) * scale;
                    }
                }
                else
                {
                    // Edge pixels: copy as-is
                    filtR[y, x] = dstR[y, x] * scale;
                    filtB[y, x] = dstB[y, x] * scale;
                }
            }
        });

        var normalized = scale < 1.0f;
        return new Image(filtered, BitDepth.Float32,
            normalized ? 1.0f : maxValue,
            normalized ? minValue / maxValue : minValue,
            normalized ? pedestal / maxValue : pedestal,
            imageMeta with
            {
                SensorType = SensorType.Color,
                BayerOffsetX = 0,
                BayerOffsetY = 0
            });

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        static float RgbToLuma(float r, float g, float b)
            => MathF.FusedMultiplyAdd(0.2126f, r, MathF.FusedMultiplyAdd(0.7152f, g, 0.0722f * b));
    }
}
