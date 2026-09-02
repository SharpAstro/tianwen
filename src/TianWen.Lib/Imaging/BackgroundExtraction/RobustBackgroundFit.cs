using System;
using System.Threading;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>
    /// The per-plane fit behind <see cref="ClassicalBackgroundExtractor"/>: pure arithmetic over a
    /// working-resolution plane, no <see cref="Image"/> in sight, so it is testable on arrays.
    /// </summary>
    /// <remarks>
    /// <para><b>Stage 1, the stiff polynomial, iterates.</b> Fit on the kept pixels, take the residual,
    /// robust median and sigma (1.4826 x MAD) over the kept pixels, then re-select: keep what sits within
    /// <c>[median - dark x sigma, median + bright x sigma]</c> and is not structure, never fewer than the
    /// minimum. Stop when the kept fraction moves by less than the tolerance (PixInsight's "automatic
    /// convergence"). A polynomial has no holes, so a pixel it rejects costs nothing to the pixels it
    /// keeps, and the loop cannot feed on itself.</para>
    /// <para><b>Stage 2, the inpainted surface, runs once, and that is deliberate.</b> The surface
    /// reproduces the kept pixels and low-passes them at the model radius, so its residual is a
    /// high-pass: a smooth feature of scale <c>s</c> leaks about <c>(sigma_blur / s)^2</c> of its
    /// amplitude into it. On a deep master that leakage exceeds the block-mean noise at the peak of an
    /// ordinary light-pollution dome, and an iterated sigma rejection then carves the peak out, the
    /// harmonic hole-fill undershoots it, the residual grows, and the hole widens every pass until the
    /// dome is gone (measured on the synthetic dome: 7e-4 RMS model error against 1e-4 for a single
    /// pass). So the surface stage rejects compact structure ONCE, by a one-pixel high-pass that a star
    /// cannot pass and a gradient barely touches, fits the surface, marks structure once against it,
    /// refits, and smooths.</para>
    /// <para>Structure is seeded where the residual exceeds <c>k x sigma</c> above the median, the seed
    /// map is low-passed with radius <c>modelRadius x (0.5 + amount)</c> and thresholded at
    /// <c>(1 - amount) x 0.5</c>: an isolated hot pixel never grows into structure (its blurred weight is
    /// far below the threshold), a compact bright region does, and its dim wings leave the fit with it.
    /// The two stages use different <c>k</c> because they ask different questions: above the stiff
    /// polynomial a dome IS structure and should leave that fit; above the surface only what the
    /// surface cannot follow should, and a dome leaks a few sigma there where a nebula core leaks
    /// tens.</para>
    /// <para>The box blur normalises each window by the pixels it actually covers, so a constant is
    /// preserved at the edges while a slope is biased there by up to half a radius; the polynomial is
    /// therefore never blurred, only the surface (whose residual has little slope left in it).</para>
    /// </remarks>
    internal static class RobustBackgroundFit
    {
        internal readonly record struct FitOutcome(
            int Iterations, bool Converged, float KeptFraction, float ExcludedFraction, float ResidualSigma, float ResidualRms);

        private const float MadToSigma = 1.4826f;

        /// <summary>
        /// Fits <paramref name="plane"/> (row-major, <paramref name="width"/> x <paramref name="height"/>,
        /// NaN = no data) and writes the model, finite everywhere, into <paramref name="model"/>.
        /// </summary>
        /// <param name="excluded">Pixels the fit may never use (caller exclusions); empty for none.</param>
        internal static FitOutcome Run(
            ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<bool> excluded,
            BackgroundExtractionOptions options, Span<float> model, CancellationToken ct)
        {
            var n = width * height;
            if (plane.Length != n || model.Length != n)
            {
                throw new ArgumentException("plane and model must both be width x height");
            }

            var valid = new bool[n];
            var validCount = 0;
            for (var i = 0; i < n; i++)
            {
                var ok = float.IsFinite(plane[i]) && !(excluded.Length > 0 && excluded[i]);
                valid[i] = ok;
                if (ok)
                {
                    validCount++;
                }
            }
            var excludedFraction = (float)(n - validCount) / n;
            if (validCount == 0)
            {
                model.Clear();
                return new FitOutcome(0, false, 0f, excludedFraction, 0f, 0f);
            }

            var kept = (bool[])valid.Clone();
            var keptCount = validCount;
            var residual = new float[n];
            var scratch = new float[n];
            var radius = SurfaceRadius(width, height, options.SurfaceScalePercent);
            var structure = options.ProtectStructure ? new bool[n] : null;
            var seed = options.ProtectStructure ? new float[n] : null;
            var compact = options.ProtectStructure || options.SurfaceRefinement ? new bool[n] : null;
            var eligible = options.ProtectStructure ? new bool[n] : null;
            var temp = compact is not null ? new float[n] : null;
            var minKeep = Math.Min(validCount, Math.Max(16, (int)Math.Ceiling(options.MinKeptFraction * validCount)));

            var prevFraction = 1f;
            var iterations = 0;
            var converged = false;
            var sigma = 0f;
            var rms = 0f;

            while (iterations < options.MaxIterations)
            {
                ct.ThrowIfCancellationRequested();
                iterations++;

                FitPolynomial(plane, width, height, kept, options.PolynomialDegree, model);

                var m = 0;
                var sumSq = 0.0;
                for (var i = 0; i < n; i++)
                {
                    if (!valid[i])
                    {
                        continue;
                    }
                    var r = plane[i] - model[i];
                    residual[i] = r;
                    if (kept[i])
                    {
                        scratch[m++] = r;
                        sumSq += (double)r * r;
                    }
                }
                rms = (float)Math.Sqrt(sumSq / m);
                var (median, mad) = StatisticsHelper.MedianAndMad(scratch.AsSpan(0, m));
                sigma = MadToSigma * mad;
                if (sigma <= 0f)
                {
                    // A majority of exactly-on-model pixels (noiseless synthetic data): fall back to the RMS
                    // about the median so that whatever does differ can still be rejected, and if nothing
                    // differs there is nothing left to do.
                    sigma = RmsAbout(residual, kept, n, median);
                    if (sigma <= 0f)
                    {
                        converged = true;
                        break;
                    }
                }

                if (structure is not null)
                {
                    // Stars are not structure. On block-mean noise even a star's faint wings clear the seed
                    // threshold, so without this every bright star seeded a five-by-five cluster and grew into
                    // a protected disc (measured: 69 percent kept on a sixty-star field). A star is COMPACT: it
                    // fails a one-pixel high-pass, which a nebula wider than a few blocks passes untouched.
                    MarkCompact(residual, valid, width, height, options, temp!, scratch, compact!);
                    for (var i = 0; i < n; i++)
                    {
                        eligible![i] = valid[i] && !compact![i];
                    }
                    MarkStructure(residual, eligible!, width, height, median, options.StructureThresholdSigma * sigma,
                        radius, options.StructureAmount, seed!, scratch, structure);
                }

                var lo = median - options.RejectDarkSigma * sigma;
                var hi = median + options.RejectBrightSigma * sigma;
                var newCount = 0;
                for (var i = 0; i < n; i++)
                {
                    var keep = valid[i] && residual[i] >= lo && residual[i] <= hi && !(structure is not null && structure[i]);
                    kept[i] = keep;
                    if (keep)
                    {
                        newCount++;
                    }
                }
                if (newCount < minKeep)
                {
                    newCount = KeepClosestToMedian(residual, valid, n, median, minKeep, kept, scratch);
                }

                var fraction = (float)newCount / validCount;
                keptCount = newCount;
                if (Math.Abs(fraction - prevFraction) < options.ConvergenceTolerance)
                {
                    converged = true;
                    break;
                }
                prevFraction = fraction;
            }

            if (options.SurfaceRefinement)
            {
                ct.ThrowIfCancellationRequested();
                (keptCount, sigma, rms) = RefineWithSurface(plane, width, height, valid, validCount, minKeep, options, radius,
                    model, kept, residual, scratch, temp!, compact!, seed, structure);
            }

            return new FitOutcome(iterations, converged, (float)keptCount / validCount, excludedFraction, sigma, rms);
        }

        /// <summary>
        /// Stage 2: the inpainted low-pass surface on the polynomial's residual, fitted once (see the type
        /// remarks for why once). On return <paramref name="model"/> holds polynomial plus surface and
        /// <paramref name="kept"/> the pixels the surface was fitted on.
        /// </summary>
        private static (int KeptCount, float Sigma, float Rms) RefineWithSurface(
            ReadOnlySpan<float> plane, int width, int height, bool[] valid, int validCount, int minKeep,
            BackgroundExtractionOptions options, int radius, Span<float> model, bool[] kept,
            float[] residual, float[] scratch, float[] temp, bool[] compact, float[]? seed, bool[]? structure)
        {
            var n = width * height;
            var surface = new float[n];

            for (var i = 0; i < n; i++)
            {
                if (valid[i])
                {
                    residual[i] = plane[i] - model[i];
                }
            }

            // Compact structure (stars) leaves; everything extended is admitted, whatever its brightness.
            MarkCompact(residual, valid, width, height, options, temp, scratch, compact);
            var keptCount = 0;
            for (var i = 0; i < n; i++)
            {
                var keep = valid[i] && !compact[i];
                kept[i] = keep;
                if (keep)
                {
                    keptCount++;
                }
            }
            if (keptCount < minKeep)
            {
                Array.Copy(valid, kept, n);
                keptCount = validCount;
            }
            var compactKept = (bool[])kept.Clone();

            InpaintSurface(residual, kept, width, height, radius, options.SurfaceInpaintPasses, surface, scratch);

            var m = 0;
            if (structure is not null && seed is not null)
            {
                // Structure against the surface, marked ONCE: what the surface itself cannot follow.
                m = 0;
                for (var i = 0; i < n; i++)
                {
                    if (kept[i])
                    {
                        scratch[m++] = residual[i] - surface[i];
                    }
                }
                var (median2, mad2) = StatisticsHelper.MedianAndMad(scratch.AsSpan(0, m));
                var sigma2 = MadToSigma * mad2;
                if (sigma2 > 0f)
                {
                    for (var i = 0; i < n; i++)
                    {
                        scratch[i] = kept[i] ? residual[i] - surface[i] : 0f;
                    }
                    MarkStructure(scratch, kept, width, height, median2, options.SurfaceStructureThresholdSigma * sigma2,
                        radius, options.StructureAmount, seed, surface, structure);
                    var protectedCount = 0;
                    for (var i = 0; i < n; i++)
                    {
                        var keep = kept[i] && !structure[i];
                        kept[i] = keep;
                        if (keep)
                        {
                            protectedCount++;
                        }
                    }
                    if (protectedCount < minKeep)
                    {
                        Array.Copy(compactKept, kept, n);
                    }
                    else
                    {
                        keptCount = protectedCount;
                    }
                    InpaintSurface(residual, kept, width, height, radius, options.SurfaceInpaintPasses, surface, scratch);
                }
            }

            if (options.SurfaceSmoothness > 0f)
            {
                var smoothRadius = (int)Math.Round(radius * options.SurfaceSmoothness);
                if (smoothRadius > 0)
                {
                    BoxBlur3(surface, width, height, smoothRadius, scratch);
                }
            }

            for (var i = 0; i < n; i++)
            {
                model[i] += surface[i];
            }

            m = 0;
            var sumSq = 0.0;
            for (var i = 0; i < n; i++)
            {
                if (kept[i])
                {
                    var r = plane[i] - model[i];
                    scratch[m++] = r;
                    sumSq += (double)r * r;
                }
            }
            var (_, madFinal) = StatisticsHelper.MedianAndMad(scratch.AsSpan(0, m));
            return (keptCount, MadToSigma * madFinal, (float)Math.Sqrt(sumSq / m));
        }

        internal static int SurfaceRadius(int width, int height, float scalePercent)
            => Math.Max(1, (int)Math.Round(scalePercent / 100f * Math.Min(width, height)));

        private static float RmsAbout(float[] residual, bool[] kept, int n, float centre)
        {
            var sumSq = 0.0;
            var m = 0;
            for (var i = 0; i < n; i++)
            {
                if (kept[i])
                {
                    var d = residual[i] - centre;
                    sumSq += (double)d * d;
                    m++;
                }
            }
            return m > 0 ? (float)Math.Sqrt(sumSq / m) : 0f;
        }

        /// <summary>
        /// Marks compact structure: valid pixels whose residual rises above a one-working-pixel high-pass
        /// (the residual minus its radius-1 three-pass box blur) by more than the bright rejection bound, in
        /// sigma of that high-pass, plus their eight neighbours. A star occupies one or two blocks and leaks
        /// nearly all of itself into the high-pass, and the ring around it carries its wings (3 percent of
        /// the peak per block for a 1.5 px star, tens of block-mean sigma), hence the one-block dilation; a
        /// gradient, a dome or a nebula wider than a few blocks leaks almost nothing and passes whatever its
        /// brightness. Only the positive side counts: a bright star's blur shadow drives the high-pass of
        /// CLEAN blocks two and three away strongly negative, and flagging those cost a third of the grid on
        /// a thirty-star field.
        /// </summary>
        private static void MarkCompact(float[] residual, bool[] valid, int width, int height,
            BackgroundExtractionOptions options, float[] temp, float[] scratch, bool[] compact)
        {
            var n = width * height;
            var sum = 0.0;
            var validCount = 0;
            for (var i = 0; i < n; i++)
            {
                if (valid[i])
                {
                    sum += residual[i];
                    validCount++;
                }
            }
            // A neutral fill at no-data pixels, so the blur is not dragged toward zero at a border.
            var fill = validCount > 0 ? (float)(sum / validCount) : 0f;
            for (var i = 0; i < n; i++)
            {
                temp[i] = valid[i] ? residual[i] : fill;
            }
            BoxBlur3(temp, width, height, 1, scratch);
            var m = 0;
            for (var i = 0; i < n; i++)
            {
                if (valid[i])
                {
                    scratch[m++] = residual[i] - temp[i];
                }
            }
            var (median, mad) = StatisticsHelper.MedianAndMad(scratch.AsSpan(0, m));
            var sigma = MadToSigma * mad;
            var hi = median + options.RejectBrightSigma * sigma;
            // Cores into temp as flags (temp is free once the high-pass is taken), then dilate by one block.
            for (var i = 0; i < n; i++)
            {
                temp[i] = valid[i] && sigma > 0f && residual[i] - temp[i] > hi ? 1f : 0f;
            }
            for (var y = 0; y < height; y++)
            {
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    if (!valid[i])
                    {
                        compact[i] = false;
                        continue;
                    }
                    var x0 = Math.Max(0, x - 1);
                    var x1 = Math.Min(width - 1, x + 1);
                    var hit = false;
                    for (var yy = y0; yy <= y1 && !hit; yy++)
                    {
                        for (var xx = x0; xx <= x1; xx++)
                        {
                            if (temp[yy * width + xx] > 0f)
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                    compact[i] = hit;
                }
            }
        }

        /// <summary>Percentile fallback: keep the <paramref name="minKeep"/> valid pixels closest to the median residual.</summary>
        private static int KeepClosestToMedian(float[] residual, bool[] valid, int n, float median, int minKeep, bool[] kept, float[] scratch)
        {
            var k = 0;
            for (var i = 0; i < n; i++)
            {
                if (valid[i])
                {
                    scratch[k++] = Math.Abs(residual[i] - median);
                }
            }
            var threshold = StatisticsHelper.NthSmallest(scratch.AsSpan(0, k), minKeep - 1);
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var keep = valid[i] && Math.Abs(residual[i] - median) <= threshold;
                kept[i] = keep;
                if (keep)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Least-squares tensor polynomial <c>x^i y^j, i + j &lt;= degree</c> over the kept pixels, on
        /// coordinates normalised to <c>[-1, 1]</c>, through the normal equations (accumulated in double,
        /// never materialising a design matrix of one row per pixel). A rank-deficient system (kept pixels
        /// all on one line, a one-pixel-wide plane) falls back one degree at a time down to the constant.
        /// The model is evaluated everywhere, kept or not.
        /// </summary>
        internal static void FitPolynomial(ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<bool> kept, int degree, Span<float> model)
        {
            var sx = width > 1 ? 2.0 / (width - 1) : 0.0;
            var sy = height > 1 ? 2.0 / (height - 1) : 0.0;

            for (var d = degree; d >= 0; d--)
            {
                var terms = (d + 1) * (d + 2) / 2;
                var expX = new int[terms];
                var expY = new int[terms];
                var t = 0;
                for (var total = 0; total <= d; total++)
                {
                    for (var i = total; i >= 0; i--)
                    {
                        expX[t] = i;
                        expY[t] = total - i;
                        t++;
                    }
                }

                var ata = new double[terms, terms];
                var atb = new double[terms];
                var term = new double[terms];
                var xp = new double[d + 1];
                var yp = new double[d + 1];
                var any = false;
                for (var y = 0; y < height; y++)
                {
                    var yn = y * sy - 1.0;
                    yp[0] = 1.0;
                    for (var k = 1; k <= d; k++)
                    {
                        yp[k] = yp[k - 1] * yn;
                    }
                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var idx = row + x;
                        if (!kept[idx])
                        {
                            continue;
                        }
                        any = true;
                        var xn = x * sx - 1.0;
                        xp[0] = 1.0;
                        for (var k = 1; k <= d; k++)
                        {
                            xp[k] = xp[k - 1] * xn;
                        }
                        for (var a = 0; a < terms; a++)
                        {
                            term[a] = xp[expX[a]] * yp[expY[a]];
                        }
                        var v = (double)plane[idx];
                        for (var a = 0; a < terms; a++)
                        {
                            var ta = term[a];
                            atb[a] += ta * v;
                            for (var b = 0; b <= a; b++)
                            {
                                ata[a, b] += ta * term[b];
                            }
                        }
                    }
                }
                if (!any)
                {
                    model.Clear();
                    return;
                }
                for (var a = 0; a < terms; a++)
                {
                    for (var b = 0; b < a; b++)
                    {
                        ata[b, a] = ata[a, b];
                    }
                }

                var coefficients = PolynomialLeastSquares.SolveNormalEquations(ata, atb);
                if (coefficients is null)
                {
                    continue;
                }

                for (var y = 0; y < height; y++)
                {
                    var yn = y * sy - 1.0;
                    yp[0] = 1.0;
                    for (var k = 1; k <= d; k++)
                    {
                        yp[k] = yp[k - 1] * yn;
                    }
                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var xn = x * sx - 1.0;
                        xp[0] = 1.0;
                        for (var k = 1; k <= d; k++)
                        {
                            xp[k] = xp[k - 1] * xn;
                        }
                        var sum = 0.0;
                        for (var a = 0; a < terms; a++)
                        {
                            sum += coefficients[a] * xp[expX[a]] * yp[expY[a]];
                        }
                        model[row + x] = (float)sum;
                    }
                }
                return;
            }
        }

        /// <summary>
        /// Masked low-pass inpainting: fill the non-kept holes with the kept mean, then repeatedly blur
        /// and restore the kept pixels, then blur once more. Each pass diffuses background about one
        /// radius into the holes, where a single normalised convolution would collapse toward the fill
        /// value inside a large hole.
        /// </summary>
        internal static void InpaintSurface(ReadOnlySpan<float> values, ReadOnlySpan<bool> kept, int width, int height, int radius, int passes, Span<float> surface, Span<float> scratch)
        {
            var n = width * height;
            var sum = 0.0;
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                if (kept[i])
                {
                    sum += values[i];
                    count++;
                }
            }
            var fill = count > 0 ? (float)(sum / count) : 0f;
            for (var i = 0; i < n; i++)
            {
                surface[i] = kept[i] ? values[i] : fill;
            }
            for (var p = 0; p < passes; p++)
            {
                BoxBlur3(surface, width, height, radius, scratch);
                for (var i = 0; i < n; i++)
                {
                    if (kept[i])
                    {
                        surface[i] = values[i];
                    }
                }
            }
            BoxBlur3(surface, width, height, radius, scratch);
        }

        /// <summary>
        /// Seeds where <paramref name="residual"/> exceeds <paramref name="threshold"/> above
        /// <paramref name="median"/> among <paramref name="eligible"/> pixels, grown by a low-pass of
        /// radius <c>radius x (0.5 + amount)</c> and cut at <c>(1 - amount) x 0.5</c>.
        /// </summary>
        private static void MarkStructure(
            ReadOnlySpan<float> residual, bool[] eligible, int width, int height,
            float median, float threshold, int radius, float amount, float[] seed, float[] scratch, bool[] structure)
        {
            var n = width * height;
            for (var i = 0; i < n; i++)
            {
                seed[i] = eligible[i] && residual[i] - median > threshold ? 1f : 0f;
            }
            var growRadius = Math.Max(1, (int)Math.Round(radius * (0.5f + amount)));
            BoxBlur3(seed, width, height, growRadius, scratch);
            var cut = (1f - amount) * 0.5f;
            for (var i = 0; i < n; i++)
            {
                structure[i] = seed[i] > cut;
            }
        }

        /// <summary>Three passes of a separable box blur of the given radius, in place; approximately Gaussian.</summary>
        internal static void BoxBlur3(Span<float> data, int width, int height, int radius, Span<float> scratch)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlurRows(data, scratch, width, height, radius);
                BoxBlurColumns(scratch, data, width, height, radius);
            }
        }

        /// <summary>Horizontal box blur, each window normalised by the pixels it covers (no padding).</summary>
        private static void BoxBlurRows(ReadOnlySpan<float> src, Span<float> dst, int width, int height, int radius)
        {
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                var window = Math.Min(radius, width - 1);
                var sum = 0.0;
                for (var x = 0; x <= window; x++)
                {
                    sum += src[row + x];
                }
                var count = window + 1;
                for (var x = 0; x < width; x++)
                {
                    dst[row + x] = (float)(sum / count);
                    var addX = x + radius + 1;
                    var removeX = x - radius;
                    if (addX < width)
                    {
                        sum += src[row + addX];
                        count++;
                    }
                    if (removeX >= 0)
                    {
                        sum -= src[row + removeX];
                        count--;
                    }
                }
            }
        }

        /// <summary>Vertical box blur, same normalisation, walking a running window down each column.</summary>
        private static void BoxBlurColumns(ReadOnlySpan<float> src, Span<float> dst, int width, int height, int radius)
        {
            var window = Math.Min(radius, height - 1);
            var sums = new double[width];
            var count = window + 1;
            for (var y = 0; y <= window; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    sums[x] += src[row + x];
                }
            }
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                var inv = 1.0 / count;
                for (var x = 0; x < width; x++)
                {
                    dst[row + x] = (float)(sums[x] * inv);
                }
                var addY = y + radius + 1;
                var removeY = y - radius;
                if (addY < height)
                {
                    var addRow = addY * width;
                    for (var x = 0; x < width; x++)
                    {
                        sums[x] += src[addRow + x];
                    }
                    count++;
                }
                if (removeY >= 0)
                {
                    var removeRow = removeY * width;
                    for (var x = 0; x < width; x++)
                    {
                        sums[x] -= src[removeRow + x];
                    }
                    count--;
                }
            }
        }
    }
}
