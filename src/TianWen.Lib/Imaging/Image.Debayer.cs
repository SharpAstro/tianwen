using System;
using System.Runtime.CompilerServices;
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
    /// Uses a simple 2x2 sliding window to calculate the average of 4 pixels, assumes simple 2x2 Bayer matrix.
    /// Is a no-op for monochrome fames.
    /// </summary>
    /// <returns>Debayered monochrome image</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerBilinearMonoAsync(float scale, CancellationToken cancellationToken = default)
    {
        var width = Width;
        var height = Height;
        var debayered = CreateChannelData(1, height, width);
        var dstChannel = debayered[0];
        var srcChannel = data[0];
        var w1 = width - 1;
        var h1 = height - 1;
        var s = (double)scale;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        // Process all rows except the last one in parallel
        await Parallel.ForAsync(0, h1, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            for (int x = 0; x < w1; x++)
            {
                dstChannel[y, x] = (float)(0.25d * s * ((double)srcChannel[y, x] + srcChannel[y + 1, x + 1] + srcChannel[y, x + 1] + srcChannel[y + 1, x]));
            }

            // last column
            dstChannel[y, w1] = (float)(0.25d * s * ((double)srcChannel[y, w1] + srcChannel[y + 1, w1 - 1] + srcChannel[y, w1 - 1] + srcChannel[y + 1, w1]));

            return ValueTask.CompletedTask;
        }, ct));

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
            normalized ? blackLevel / maxValue : blackLevel,
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

        // Process interior pixels in parallel (where full VNG can be applied)
        await Parallel.ForAsync(radius,
            height - radius,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
            async (y, ct) => await Task.Run(() =>
            {
                // Pre-select pattern row based on y % 2
                int patternEven = (y & 1) == 0 ? pattern00 : pattern10;
                int patternOdd = (y & 1) == 0 ? pattern01 : pattern11;

                for (int x = radius; x < width - radius; x++)
                {
                    int knownColor = (x & 1) == 0 ? patternEven : patternOdd;

                    // Copy known value
                    float rawValue = srcChannel[y, x] * scale;
                    debayered[knownColor][y, x] = rawValue;

                    // Interpolate missing colors based on which color we have
                    if (knownColor == G)
                    {
                        // At green pixel: interpolate R and B
                        // Check if R is on horizontal or vertical neighbors
                        int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                        bool rOnHorizontal = neighborColor == R;

                        dstR[y, x] = scale * (rOnHorizontal
                            ? InterpolateHorizontalVNG(srcChannel, x, y)
                            : InterpolateVerticalVNG(srcChannel, x, y));
                        dstB[y, x] = scale * (rOnHorizontal
                            ? InterpolateVerticalVNG(srcChannel, x, y)
                            : InterpolateHorizontalVNG(srcChannel, x, y));
                    }
                    else
                    {
                        // At R or B pixel: interpolate G and the opposite color
                        dstG[y, x] = scale * InterpolateGreenAtRBVNG(srcChannel, x, y);
                        debayered[knownColor == R ? B : R][y, x] = scale * InterpolateDiagonalVNG(srcChannel, x, y);
                    }
                }

                return ValueTask.CompletedTask;
            }, ct)
        );

        // Process edge pixels with simpler bilinear interpolation (not parallelized - small portion)
        ProcessEdgePixels(debayered, width, height, radius, bayerPattern, scale);

        var normalized = scale < 1.0f;
        return new Image(debayered, BitDepth.Float32,
            normalized ? 1.0f : maxValue,
            normalized ? minValue / maxValue : minValue,
            normalized ? blackLevel / maxValue : blackLevel,
            imageMeta with
            {
                SensorType = SensorType.Color,
                BayerOffsetX = 0,
                BayerOffsetY = 0
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static float InterpolateGreenAtRBVNG(float[,] src, int x, int y)
    {
        // Interpolate green at R or B position using 4 cardinal directions
        float center = src[y, x];

        // North: green at y-1, same color at y-2
        float gN = src[y - 1, x];
        float vN = src[y - 2, x];
        float gradN = MathF.Abs(MathF.FusedMultiplyAdd(2, gN, - vN - center));
        float valN = gN + (center - vN) * 0.5f;

        // South
        float gS = src[y + 1, x];
        float vS = src[y + 2, x];
        float gradS = MathF.Abs(MathF.FusedMultiplyAdd(2, gS, - center - vS));
        float valS = MathF.FusedMultiplyAdd(center - vS, 0.5f, gS);

        // West
        float gW = src[y, x - 1];
        float vW = src[y, x - 2];
        float gradW = MathF.Abs(2 * gW - vW - center);
        float valW = MathF.FusedMultiplyAdd(center - vW, 0.5f, gW);

        // East
        float gE = src[y, x + 1];
        float vE = src[y, x + 2];
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
    private static float InterpolateHorizontalVNG(float[,] src, int x, int y)
    {
        // Interpolate R or B at green position from horizontal neighbors
        float center = src[y, x];
        float left = src[y, x - 1];
        float right = src[y, x + 1];

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
    private static float InterpolateVerticalVNG(float[,] src, int x, int y)
    {
        // Interpolate R or B at green position from vertical neighbors
        float center = src[y, x];
        float top = src[y - 1, x];
        float bottom = src[y + 1, x];

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
    private static float InterpolateDiagonalVNG(float[,] src, int x, int y)
    {
        // Interpolate R at B or B at R from 4 diagonal neighbors
        float center = src[y, x];

        float nw = src[y - 1, x - 1];
        float ne = src[y - 1, x + 1];
        float sw = src[y + 1, x - 1];
        float se = src[y + 1, x + 1];

        // Green values at cardinal neighbors
        float gN = src[y - 1, x];
        float gS = src[y + 1, x];
        float gW = src[y, x - 1];
        float gE = src[y, x + 1];

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
    private void ProcessEdgePixels(float[][,] debayered, int width, int height, int radius, int[,] bayerPattern, float scale = 1.0f)
    {
        // Top and bottom edges
        for (int y = 0; y < height; y++)
        {
            if (y >= radius && y < height - radius) continue; // Skip interior rows

            for (int x = 0; x < width; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern, scale);
            }
        }

        // Left and right edges (excluding corners already processed)
        for (int y = radius; y < height - radius; y++)
        {
            for (int x = 0; x < radius; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern, scale);
            }
            for (int x = width - radius; x < width; x++)
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

    private async Task<Image> DebayerAHDAsync(float scale, CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var debayered = CreateChannelData(3, height, width); // RGB output

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

        // Phase 1 & 2: Build horizontal and vertical full-color interpolations in parallel
        var rgbH = CreateChannelData(3, height, width);
        var rgbV = CreateChannelData(3, height, width);

        var srcChannel = data[0];
        var rgbH_R = rgbH[R]; var rgbH_G = rgbH[G]; var rgbH_B = rgbH[B];
        var rgbV_R = rgbV[R]; var rgbV_G = rgbV[G]; var rgbV_B = rgbV[B];

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        // Interpolate green channel using horizontal and vertical directions, then R/B guided by green
        await Parallel.ForAsync(radius, height - radius, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            int patternEven = (y & 1) == 0 ? pattern00 : pattern10;
            int patternOdd = (y & 1) == 0 ? pattern01 : pattern11;

            for (int x = radius; x < width - radius; x++)
            {
                int knownColor = (x & 1) == 0 ? patternEven : patternOdd;
                float rawValue = srcChannel[y, x];

                if (knownColor == G)
                {
                    // At green pixel: green is known for both directions
                    rgbH_G[y, x] = rawValue;
                    rgbV_G[y, x] = rawValue;

                    // Determine which color is on horizontal vs vertical neighbors
                    int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                    bool rOnHorizontal = neighborColor == R;

                    // Simple bilinear average (AHD defers direction choice to homogeneity)
                    float hAvg = (srcChannel[y, x - 1] + srcChannel[y, x + 1]) * 0.5f;
                    float vAvg = (srcChannel[y - 1, x] + srcChannel[y + 1, x]) * 0.5f;

                    rgbH_R[y, x] = rOnHorizontal ? hAvg : vAvg;
                    rgbH_B[y, x] = rOnHorizontal ? vAvg : hAvg;

                    rgbV_R[y, x] = rOnHorizontal ? hAvg : vAvg;
                    rgbV_B[y, x] = rOnHorizontal ? vAvg : hAvg;
                }
                else
                {
                    // At R or B pixel: interpolate green directionally
                    float center = rawValue;

                    // Horizontal green interpolation (Laplacian-corrected)
                    float gW = srcChannel[y, x - 1];
                    float gE = srcChannel[y, x + 1];
                    float greenH = (gW + gE) * 0.5f + (2 * center - srcChannel[y, x - 2] - srcChannel[y, x + 2]) * 0.25f;

                    // Vertical green interpolation (Laplacian-corrected)
                    float gN = srcChannel[y - 1, x];
                    float gS = srcChannel[y + 1, x];
                    float greenV = (gN + gS) * 0.5f + (2 * center - srcChannel[y - 2, x] - srcChannel[y + 2, x]) * 0.25f;

                    rgbH_G[y, x] = greenH;
                    rgbV_G[y, x] = greenV;

                    // Known color is the same for both
                    rgbH[knownColor][y, x] = rawValue;
                    rgbV[knownColor][y, x] = rawValue;

                    // Interpolate opposite color guided by per-pixel color differences
                    int oppositeColor = knownColor == R ? B : R;

                    // Diagonal neighbors hold the opposite color
                    float dNW = srcChannel[y - 1, x - 1];
                    float dNE = srcChannel[y - 1, x + 1];
                    float dSW = srcChannel[y + 1, x - 1];
                    float dSE = srcChannel[y + 1, x + 1];

                    // Per-pixel color-difference: each diagonal pixel's green is estimated
                    // from its 2 nearest cardinal green neighbors
                    float cdNW = dNW - (gN + gW) * 0.5f;
                    float cdNE = dNE - (gN + gE) * 0.5f;
                    float cdSW = dSW - (gS + gW) * 0.5f;
                    float cdSE = dSE - (gS + gE) * 0.5f;
                    float cdAvg = (cdNW + cdNE + cdSW + cdSE) * 0.25f;

                    // Reconstruct: opposite = green_interpolated + color_difference
                    rgbH[oppositeColor][y, x] = greenH + cdAvg;
                    rgbV[oppositeColor][y, x] = greenV + cdAvg;
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        // Phase 3: Compute homogeneity and select best direction
        var dstR = debayered[R]; var dstG = debayered[G]; var dstB = debayered[B];

        await Parallel.ForAsync(totalRadius, height - totalRadius, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            for (int x = totalRadius; x < width - totalRadius; x++)
            {
                // Compute homogeneity for horizontal and vertical in a neighborhood
                int homH = 0, homV = 0;

                float lH = RgbToLuma(rgbH_R[y, x], rgbH_G[y, x], rgbH_B[y, x]);
                float aH = rgbH_R[y, x] - rgbH_G[y, x];
                float bH = rgbH_B[y, x] - rgbH_G[y, x];

                float lV = RgbToLuma(rgbV_R[y, x], rgbV_G[y, x], rgbV_B[y, x]);
                float aV = rgbV_R[y, x] - rgbV_G[y, x];
                float bV = rgbV_B[y, x] - rgbV_G[y, x];

                for (int dy = -homogeneityRadius; dy <= homogeneityRadius; dy++)
                {
                    for (int dx = -homogeneityRadius; dx <= homogeneityRadius; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int ny = y + dy;
                        int nx = x + dx;

                        // Horizontal neighbor differences
                        float nlH = RgbToLuma(rgbH_R[ny, nx], rgbH_G[ny, nx], rgbH_B[ny, nx]);
                        float naH = rgbH_R[ny, nx] - rgbH_G[ny, nx];
                        float nbH = rgbH_B[ny, nx] - rgbH_G[ny, nx];
                        float diffH = MathF.Abs(lH - nlH) + MathF.Abs(aH - naH) + MathF.Abs(bH - nbH);

                        // Vertical neighbor differences
                        float nlV = RgbToLuma(rgbV_R[ny, nx], rgbV_G[ny, nx], rgbV_B[ny, nx]);
                        float naV = rgbV_R[ny, nx] - rgbV_G[ny, nx];
                        float nbV = rgbV_B[ny, nx] - rgbV_G[ny, nx];
                        float diffV = MathF.Abs(lV - nlV) + MathF.Abs(aV - naV) + MathF.Abs(bV - nbV);

                        // Lower difference = more homogeneous
                        if (diffH < diffV) homH++;
                        else if (diffV < diffH) homV++;
                    }
                }

                // Select the direction with higher homogeneity, or average if tied
                if (homH > homV)
                {
                    dstR[y, x] = rgbH_R[y, x];
                    dstG[y, x] = rgbH_G[y, x];
                    dstB[y, x] = rgbH_B[y, x];
                }
                else if (homV > homH)
                {
                    dstR[y, x] = rgbV_R[y, x];
                    dstG[y, x] = rgbV_G[y, x];
                    dstB[y, x] = rgbV_B[y, x];
                }
                else
                {
                    dstR[y, x] = (rgbH_R[y, x] + rgbV_R[y, x]) * 0.5f;
                    dstG[y, x] = (rgbH_G[y, x] + rgbV_G[y, x]) * 0.5f;
                    dstB[y, x] = (rgbH_B[y, x] + rgbV_B[y, x]) * 0.5f;
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        // Fill edge pixels with bilinear interpolation before artifact reduction
        ProcessEdgePixels(debayered, width, height, totalRadius, bayerPattern);

        // Phase 4: Artifact reduction - 3×3 median filter on color differences (R-G) and (B-G)
        // This smooths the abrupt H/V direction switching that causes per-pixel colour noise
        // Reuse rgbH — it is dead after Phase 3 (saves one full 3-channel allocation)
        var filtered = rgbH;
        var filtR = filtered[R]; var filtG = filtered[G]; var filtB = filtered[B];

        await Parallel.ForAsync(0, height, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            Span<float> medianBuf = stackalloc float[9];

            for (int x = 0; x < width; x++)
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

                        filtered[c][y, x] = (gCenter + Median(medianBuf)) * scale;
                    }
                }
                else
                {
                    // Edge pixels: copy as-is
                    filtR[y, x] = dstR[y, x] * scale;
                    filtB[y, x] = dstB[y, x] * scale;
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        var normalized = scale < 1.0f;
        return new Image(filtered, BitDepth.Float32,
            normalized ? 1.0f : maxValue,
            normalized ? minValue / maxValue : minValue,
            normalized ? blackLevel / maxValue : blackLevel,
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
