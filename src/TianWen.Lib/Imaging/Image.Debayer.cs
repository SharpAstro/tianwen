using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public Task<Image> DebayerAsync(DebayerAlgorithm debayerAlgorithm, CancellationToken cancellationToken = default)
    {
        // NO-OP for monochrome or full colour images
        if (imageMeta.SensorType is SensorType.Monochrome or SensorType.Color)
        {
            return Task.FromResult(this);
        }

        return debayerAlgorithm switch
        {
            DebayerAlgorithm.BilinearMono => DebayerBilinearMonoAsync(cancellationToken),
            DebayerAlgorithm.VNG => DebayerVNGAsync(cancellationToken),
            DebayerAlgorithm.AHD => DebayerAHDAsync(cancellationToken),
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
    private async Task<Image> DebayerBilinearMonoAsync(CancellationToken cancellationToken = default)
    {
        var width = Width;
        var height = Height;
        var debayered = new float[1, height, width];
        var w1 = width - 1;
        var h1 = height - 1;

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
                debayered[0, y, x] = (float)(0.25d * ((double)data[0, y, x] + data[0, y + 1, x + 1] + data[0, y, x + 1] + data[0, y + 1, x]));
            }

            // last column
            debayered[0, y, w1] = (float)(0.25d * ((double)data[0, y, w1] + data[0, y + 1, w1 - 1] + data[0, y, w1 - 1] + data[0, y + 1, w1]));

            return ValueTask.CompletedTask;
        }, ct));

        // last row (processed sequentially as it's a single row)
        for (int x = 0; x < w1; x++)
        {
            debayered[0, h1, x] = (float)(0.25d * ((double)data[0, h1, x] + data[0, h1 - 1, x + 1] + data[0, h1, x + 1] + data[0, h1 - 1, x]));
        }

        // last pixel
        debayered[0, h1, w1] = (float)(0.25d * ((double)data[0, h1, w1] + data[0, h1 - 1, w1 - 1] + data[0, h1, w1 - 1] + data[0, h1 - 1, w1]));

        return new Image(debayered, BitDepth.Float32, maxValue, minValue, blackLevel, imageMeta with
        {
            SensorType = SensorType.Monochrome,
            BayerOffsetX = 0,
            BayerOffsetY = 0,
            Filter = Filter.Luminance
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerVNGAsync(CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var debayered = new float[3, height, width]; // RGB output

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
                    float rawValue = data[0, y, x];
                    debayered[knownColor, y, x] = rawValue;

                    // Interpolate missing colors based on which color we have
                    if (knownColor == G)
                    {
                        // At green pixel: interpolate R and B
                        // Check if R is on horizontal or vertical neighbors
                        int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                        bool rOnHorizontal = neighborColor == R;

                        debayered[R, y, x] = rOnHorizontal
                            ? InterpolateHorizontalVNG(x, y)
                            : InterpolateVerticalVNG(x, y);
                        debayered[B, y, x] = rOnHorizontal
                            ? InterpolateVerticalVNG(x, y)
                            : InterpolateHorizontalVNG(x, y);
                    }
                    else
                    {
                        // At R or B pixel: interpolate G and the opposite color
                        debayered[G, y, x] = InterpolateGreenAtRBVNG(x, y);
                        debayered[knownColor == R ? B : R, y, x] = InterpolateDiagonalVNG(x, y);
                    }
                }

                return ValueTask.CompletedTask;
            }, ct)
        );

        // Process edge pixels with simpler bilinear interpolation (not parallelized - small portion)
        ProcessEdgePixels(debayered, width, height, radius, bayerPattern);

        return new Image(debayered, BitDepth.Float32, maxValue, minValue, blackLevel, imageMeta with
        {
            SensorType = SensorType.Color,
            BayerOffsetX = 0,
            BayerOffsetY = 0
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float InterpolateGreenAtRBVNG(int x, int y)
    {
        // Interpolate green at R or B position using 4 cardinal directions
        float center = data[0, y, x];

        // North: green at y-1, same color at y-2
        float gN = data[0, y - 1, x];
        float vN = data[0, y - 2, x];
        float gradN = MathF.Abs(MathF.FusedMultiplyAdd(2, gN, - vN - center));
        float valN = gN + (center - vN) * 0.5f;

        // South
        float gS = data[0, y + 1, x];
        float vS = data[0, y + 2, x];
        float gradS = MathF.Abs(MathF.FusedMultiplyAdd(2, gS, - center - vS));
        float valS = MathF.FusedMultiplyAdd(center - vS, 0.5f, gS);

        // West
        float gW = data[0, y, x - 1];
        float vW = data[0, y, x - 2];
        float gradW = MathF.Abs(2 * gW - vW - center);
        float valW = MathF.FusedMultiplyAdd(center - vW, 0.5f, gW);

        // East
        float gE = data[0, y, x + 1];
        float vE = data[0, y, x + 2];
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
    private float InterpolateHorizontalVNG(int x, int y)
    {
        // Interpolate R or B at green position from horizontal neighbors
        float center = data[0, y, x];
        float left = data[0, y, x - 1];
        float right = data[0, y, x + 1];

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
    private float InterpolateVerticalVNG(int x, int y)
    {
        // Interpolate R or B at green position from vertical neighbors
        float center = data[0, y, x];
        float top = data[0, y - 1, x];
        float bottom = data[0, y + 1, x];

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
    private float InterpolateDiagonalVNG(int x, int y)
    {
        // Interpolate R at B or B at R from 4 diagonal neighbors
        float center = data[0, y, x];

        float nw = data[0, y - 1, x - 1];
        float ne = data[0, y - 1, x + 1];
        float sw = data[0, y + 1, x - 1];
        float se = data[0, y + 1, x + 1];

        // Green values at cardinal neighbors
        float gN = data[0, y - 1, x];
        float gS = data[0, y + 1, x];
        float gW = data[0, y, x - 1];
        float gE = data[0, y, x + 1];

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
    private void ProcessEdgePixels(float[,,] debayered, int width, int height, int radius, int[,] bayerPattern)
    {
        // Top and bottom edges
        for (int y = 0; y < height; y++)
        {
            if (y >= radius && y < height - radius) continue; // Skip interior rows

            for (int x = 0; x < width; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
        }

        // Left and right edges (excluding corners already processed)
        for (int y = radius; y < height - radius; y++)
        {
            for (int x = 0; x < radius; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
            for (int x = width - radius; x < width; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessEdgePixel(float[,,] debayered, int x, int y, int width, int height, int[,] bayerPattern)
    {
        int knownColor = bayerPattern[y & 1, x & 1];
        debayered[knownColor, y, x] = data[0, y, x];

        for (int c = 0; c < 3; c++)
        {
            if (c != knownColor)
            {
                debayered[c, y, x] = BilinearInterpolateColorFast(x, y, c, width, height, bayerPattern);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float BilinearInterpolateColorFast(int x, int y, int targetColor, int width, int height, int[,] bayerPattern)
    {
        float sum = 0;
        int count = 0;

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
                    sum += data[0, ny, nx];
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : 0;
    }

    private async Task<Image> DebayerAHDAsync(CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var debayered = new float[3, height, width]; // RGB output

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
        var rgbH = new float[3, height, width];
        var rgbV = new float[3, height, width];

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
                float rawValue = data[0, y, x];

                if (knownColor == G)
                {
                    // At green pixel: green is known for both directions
                    rgbH[G, y, x] = rawValue;
                    rgbV[G, y, x] = rawValue;

                    // Determine which color is on horizontal vs vertical neighbors
                    int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                    bool rOnHorizontal = neighborColor == R;

                    // Simple bilinear average (AHD defers direction choice to homogeneity)
                    float hAvg = (data[0, y, x - 1] + data[0, y, x + 1]) * 0.5f;
                    float vAvg = (data[0, y - 1, x] + data[0, y + 1, x]) * 0.5f;

                    rgbH[R, y, x] = rOnHorizontal ? hAvg : vAvg;
                    rgbH[B, y, x] = rOnHorizontal ? vAvg : hAvg;

                    rgbV[R, y, x] = rOnHorizontal ? hAvg : vAvg;
                    rgbV[B, y, x] = rOnHorizontal ? vAvg : hAvg;
                }
                else
                {
                    // At R or B pixel: interpolate green directionally
                    float center = rawValue;

                    // Horizontal green interpolation (Laplacian-corrected)
                    float gW = data[0, y, x - 1];
                    float gE = data[0, y, x + 1];
                    float greenH = (gW + gE) * 0.5f + (2 * center - data[0, y, x - 2] - data[0, y, x + 2]) * 0.25f;

                    // Vertical green interpolation (Laplacian-corrected)
                    float gN = data[0, y - 1, x];
                    float gS = data[0, y + 1, x];
                    float greenV = (gN + gS) * 0.5f + (2 * center - data[0, y - 2, x] - data[0, y + 2, x]) * 0.25f;

                    rgbH[G, y, x] = greenH;
                    rgbV[G, y, x] = greenV;

                    // Known color is the same for both
                    rgbH[knownColor, y, x] = rawValue;
                    rgbV[knownColor, y, x] = rawValue;

                    // Interpolate opposite color guided by per-pixel color differences
                    int oppositeColor = knownColor == R ? B : R;

                    // Diagonal neighbors hold the opposite color
                    float dNW = data[0, y - 1, x - 1];
                    float dNE = data[0, y - 1, x + 1];
                    float dSW = data[0, y + 1, x - 1];
                    float dSE = data[0, y + 1, x + 1];

                    // Per-pixel color-difference: each diagonal pixel's green is estimated
                    // from its 2 nearest cardinal green neighbors
                    float cdNW = dNW - (gN + gW) * 0.5f;
                    float cdNE = dNE - (gN + gE) * 0.5f;
                    float cdSW = dSW - (gS + gW) * 0.5f;
                    float cdSE = dSE - (gS + gE) * 0.5f;
                    float cdAvg = (cdNW + cdNE + cdSW + cdSE) * 0.25f;

                    // Reconstruct: opposite = green_interpolated + color_difference
                    rgbH[oppositeColor, y, x] = greenH + cdAvg;
                    rgbV[oppositeColor, y, x] = greenV + cdAvg;
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        // Phase 3: Compute homogeneity and select best direction
        await Parallel.ForAsync(totalRadius, height - totalRadius, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            for (int x = totalRadius; x < width - totalRadius; x++)
            {
                // Compute homogeneity for horizontal and vertical in a neighborhood
                int homH = 0, homV = 0;

                float lH = RgbToLuma(rgbH[R, y, x], rgbH[G, y, x], rgbH[B, y, x]);
                float aH = rgbH[R, y, x] - rgbH[G, y, x];
                float bH = rgbH[B, y, x] - rgbH[G, y, x];

                float lV = RgbToLuma(rgbV[R, y, x], rgbV[G, y, x], rgbV[B, y, x]);
                float aV = rgbV[R, y, x] - rgbV[G, y, x];
                float bV = rgbV[B, y, x] - rgbV[G, y, x];

                for (int dy = -homogeneityRadius; dy <= homogeneityRadius; dy++)
                {
                    for (int dx = -homogeneityRadius; dx <= homogeneityRadius; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int ny = y + dy;
                        int nx = x + dx;

                        // Horizontal neighbor differences
                        float nlH = RgbToLuma(rgbH[R, ny, nx], rgbH[G, ny, nx], rgbH[B, ny, nx]);
                        float naH = rgbH[R, ny, nx] - rgbH[G, ny, nx];
                        float nbH = rgbH[B, ny, nx] - rgbH[G, ny, nx];
                        float diffH = MathF.Abs(lH - nlH) + MathF.Abs(aH - naH) + MathF.Abs(bH - nbH);

                        // Vertical neighbor differences
                        float nlV = RgbToLuma(rgbV[R, ny, nx], rgbV[G, ny, nx], rgbV[B, ny, nx]);
                        float naV = rgbV[R, ny, nx] - rgbV[G, ny, nx];
                        float nbV = rgbV[B, ny, nx] - rgbV[G, ny, nx];
                        float diffV = MathF.Abs(lV - nlV) + MathF.Abs(aV - naV) + MathF.Abs(bV - nbV);

                        // Lower difference = more homogeneous
                        if (diffH < diffV) homH++;
                        else if (diffV < diffH) homV++;
                    }
                }

                // Select the direction with higher homogeneity, or average if tied
                if (homH > homV)
                {
                    debayered[R, y, x] = rgbH[R, y, x];
                    debayered[G, y, x] = rgbH[G, y, x];
                    debayered[B, y, x] = rgbH[B, y, x];
                }
                else if (homV > homH)
                {
                    debayered[R, y, x] = rgbV[R, y, x];
                    debayered[G, y, x] = rgbV[G, y, x];
                    debayered[B, y, x] = rgbV[B, y, x];
                }
                else
                {
                    debayered[R, y, x] = (rgbH[R, y, x] + rgbV[R, y, x]) * 0.5f;
                    debayered[G, y, x] = (rgbH[G, y, x] + rgbV[G, y, x]) * 0.5f;
                    debayered[B, y, x] = (rgbH[B, y, x] + rgbV[B, y, x]) * 0.5f;
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        // Fill edge pixels with bilinear interpolation before artifact reduction
        ProcessEdgePixels(debayered, width, height, totalRadius, bayerPattern);

        // Phase 4: Artifact reduction - 3×3 median filter on color differences (R-G) and (B-G)
        // This smooths the abrupt H/V direction switching that causes per-pixel colour noise
        var filtered = new float[3, height, width];

        await Parallel.ForAsync(0, height, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            Span<float> medianBuf = stackalloc float[9];

            for (int x = 0; x < width; x++)
            {
                // Green channel is kept as-is
                filtered[G, y, x] = debayered[G, y, x];

                if (y >= 1 && y < height - 1 && x >= 1 && x < width - 1)
                {
                    // Apply median filter on color differences for R and B
                    float gCenter = debayered[G, y, x];

                    for (int c = 0; c < 3; c += 2) // R (0) and B (2) only
                    {
                        int idx = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                medianBuf[idx++] = debayered[c, y + dy, x + dx] - debayered[G, y + dy, x + dx];
                            }
                        }

                        filtered[c, y, x] = gCenter + Median(medianBuf);
                    }
                }
                else
                {
                    // Edge pixels: copy as-is
                    filtered[R, y, x] = debayered[R, y, x];
                    filtered[B, y, x] = debayered[B, y, x];
                }
            }

            return ValueTask.CompletedTask;
        }, ct));

        return new Image(filtered, BitDepth.Float32, maxValue, minValue, blackLevel, imageMeta with
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
