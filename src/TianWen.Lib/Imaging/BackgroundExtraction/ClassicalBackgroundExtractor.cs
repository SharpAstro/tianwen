using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>
    /// Classical background extraction: a robust iterative fit of a stiff polynomial (plus an optional
    /// inpainted low-pass surface) to the sky, on a block-mean downsampled working grid, applied in
    /// linear units with the sky level preserved per channel. It is both the headless
    /// <see cref="IBackgroundExtractor"/> and the AI-free <see cref="IGradientCorrector"/> the sharpen
    /// pipeline can run when no GraXpert weights are installed.
    /// </summary>
    /// <remarks>
    /// <para><b>A one-channel OSC mosaic is fitted per photosite colour, not as mono.</b> Every colour
    /// frame the viewer hands an enhancer is a raw CFA mosaic (it never CPU-debayers), and a single
    /// smooth plane subtracted from a mosaic removes the average gradient while leaving each colour's
    /// own residual behind as a colour gradient. So a <see cref="SensorType.RGGB"/> single-channel
    /// input is split into its four photosite planes (<see cref="Image.SplitBayerChannels"/>), each
    /// fitted at half the downsample factor (the same working resolution), corrected there, and
    /// merged back (<see cref="Image.MergeBayerChannels"/>); the background comes back as a mosaic
    /// too. An odd-sized mosaic cannot be split without losing a row, so it is treated as mono and
    /// logged.</para>
    /// <para><b>The level restored is the median of the model, per fitted plane.</b> Subtract mode
    /// gives <c>source - background + median(background)</c>, so the sky sits where it was and the
    /// image's pedestal is untouched: the AI sibling accumulates its level onto the pedestal field,
    /// which is what forced <c>MasterPreviewRenderer.WithZeroPedestal</c> on GraXpert-flattened
    /// masters, and there is no reason to repeat that here. Per plane rather than one scalar, because
    /// a shared scalar would equalise the channel backgrounds, which is background neutralisation and
    /// a separate step by design.</para>
    /// <para>Nothing here is random, so a run is deterministic by construction.</para>
    /// </remarks>
    public sealed class ClassicalBackgroundExtractor(
        BackgroundExtractionOptions? defaultOptions = null,
        ILogger<ClassicalBackgroundExtractor>? logger = null)
        : IBackgroundExtractor, IGradientCorrector
    {
        private const float DivideEpsilon = 1e-6f;

        /// <summary>The options the <see cref="IGradientCorrector"/> entry points run with.</summary>
        public BackgroundExtractionOptions Options { get; } = defaultOptions ?? BackgroundExtractionOptions.Default;

        public string Name => Options.SurfaceRefinement
            ? "GradientCorrector (classical: robust polynomial + inpainted surface)"
            : "GradientCorrector (classical: robust polynomial)";

        public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var result = await ExtractAsync(input, Options, cancellationToken);
            result.Background.Release();
            return result.Cleaned;
        }

        public async Task<(Image Corrected, Image? Background)> EnhanceAndEstimateBackgroundAsync(Image input, CancellationToken cancellationToken = default)
        {
            var result = await ExtractAsync(input, Options, cancellationToken);
            return (result.Cleaned, result.Background);
        }

        public Task<BackgroundExtractionResult> ExtractAsync(Image source, BackgroundExtractionOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            return Task.Run(() => Run(source, options, cancellationToken), cancellationToken);
        }

        private BackgroundExtractionResult Run(Image source, BackgroundExtractionOptions options, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var (channels, width, height) = source.Shape;

            var isMosaic = channels == 1 && source.ImageMeta.SensorType is SensorType.RGGB;
            var cfa = isMosaic && (width & 1) == 0 && (height & 1) == 0 && width >= 2 && height >= 2;
            if (isMosaic && !cfa)
            {
                logger?.LogWarning(
                    "ClassicalBackgroundExtractor: {W}x{H} CFA mosaic has an odd dimension and cannot be split into photosite planes; fitting it as one plane.",
                    width, height);
            }

            // The planes the fit sees: the four photosite planes of a mosaic, else the channels as they are.
            var planes = cfa ? source.SplitBayerChannels() : source;
            var factor = cfa ? Math.Max(1, options.Downsample / 2) : options.Downsample;
            var fullPixelsPerWorkingPixel = factor * (cfa ? 2 : 1);
            var (planeCount, planeWidth, planeHeight) = planes.Shape;
            var ws = Math.Max(1, planeWidth / factor);
            var hs = Math.Max(1, planeHeight / factor);

            var excluded = RasterizeExclusions(options.Exclusions, ws, hs, fullPixelsPerWorkingPixel);
            var smallModels = Image.CreateChannelData(planeCount, hs, ws);
            var small = new float[ws * hs];
            var outcomes = new RobustBackgroundFit.FitOutcome[planeCount];
            var levels = new float[planeCount];
            for (var p = 0; p < planeCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                BlockMean(planes.GetChannelSpan(p), planeWidth, planeHeight, factor, ws, hs, small);
                var model = MemoryMarshal.CreateSpan(ref smallModels[p][0, 0], ws * hs);
                outcomes[p] = RobustBackgroundFit.Run(small, ws, hs, excluded, options, model, ct);
                levels[p] = options.PreserveLevel
                    ? MedianOf(model, small)
                    : options.Correction == BackgroundCorrection.Divide ? 1f : 0f;
            }

            // Upsample the models to plane resolution in one go (bilinear, pixel-centre convention), then
            // correct at plane resolution; a mosaic's planes are merged back afterwards, so each photosite
            // colour is corrected against its own model and its own level.
            var (smallMin, smallMax) = Extrema(smallModels);
            var modelAtPlaneRes = new Image(smallModels, BitDepth.Float32, smallMax, smallMin, 0f, source.ImageMeta)
                .BilinearResize(planeWidth, planeHeight);

            var corrected = Image.CreateChannelData(planeCount, planeHeight, planeWidth);
            var correctedMin = float.PositiveInfinity;
            var correctedMax = float.NegativeInfinity;
            for (var p = 0; p < planeCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var src = planes.GetChannelSpan(p);
                var bg = modelAtPlaneRes.GetChannelSpan(p);
                var dst = MemoryMarshal.CreateSpan(ref corrected[p][0, 0], planeWidth * planeHeight);
                Correct(src, bg, levels[p], options.Correction, dst, ref correctedMin, ref correctedMax);
            }
            if (correctedMin > correctedMax)
            {
                correctedMin = correctedMax = 0f;
            }

            var cleanedPlanes = new Image(corrected, BitDepth.Float32, correctedMax, correctedMin, source.Pedestal, source.ImageMeta);
            var cleaned = cfa ? cleanedPlanes.MergeBayerChannels() : cleanedPlanes;
            var background = cfa ? modelAtPlaneRes.MergeBayerChannels() : modelAtPlaneRes;

            var diagnostics = ImmutableArray.CreateBuilder<ChannelFitDiagnostics>(planeCount);
            var summary = new StringBuilder();
            for (var p = 0; p < planeCount; p++)
            {
                var o = outcomes[p];
                diagnostics.Add(new ChannelFitDiagnostics(p, o.Iterations, o.Converged, o.KeptFraction, o.ExcludedFraction, o.ResidualSigma, o.ResidualRms, levels[p]));
                summary.Append(p).Append(':').Append(o.Iterations).Append(o.Converged ? "it" : "it(cap)")
                    .Append(" kept=").Append(o.KeptFraction.ToString("F3"))
                    .Append(" excluded=").Append(o.ExcludedFraction.ToString("F3"))
                    .Append(" sigma=").Append(o.ResidualSigma.ToString("E2"))
                    .Append(" level=").Append(levels[p].ToString("F5")).Append(' ');
            }

            logger?.LogInformation(
                "ClassicalBackgroundExtractor: {W}x{H}x{C}{Cfa} degree {Degree}{Surface} at 1/{Factor} ({Ws}x{Hs}) {Mode} in {Ms} ms; {Summary}",
                width, height, channels, cfa ? " (CFA, 4 photosite planes)" : string.Empty,
                options.PolynomialDegree, options.SurfaceRefinement ? "+surface" : string.Empty,
                factor, ws, hs, options.Correction, sw.ElapsedMilliseconds, summary.ToString().TrimEnd());

            return new BackgroundExtractionResult(cleaned, background, diagnostics.MoveToImmutable());
        }

        /// <summary>NaN-aware block mean: a block with no finite pixel is NaN, which the fit then excludes.</summary>
        internal static void BlockMean(ReadOnlySpan<float> src, int width, int height, int factor, int ws, int hs, Span<float> dst)
        {
            for (var y = 0; y < hs; y++)
            {
                var y0 = y * factor;
                for (var x = 0; x < ws; x++)
                {
                    var x0 = x * factor;
                    var sum = 0.0;
                    var count = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var row = (y0 + dy) * width;
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var v = src[row + x0 + dx];
                            if (float.IsFinite(v))
                            {
                                sum += v;
                                count++;
                            }
                        }
                    }
                    dst[y * ws + x] = count > 0 ? (float)(sum / count) : float.NaN;
                }
            }
        }

        private static bool[] RasterizeExclusions(ImmutableArray<ExclusionPolygon> exclusions, int ws, int hs, int fullPixelsPerWorkingPixel)
        {
            if (exclusions.IsDefaultOrEmpty)
            {
                return [];
            }
            var mask = new bool[ws * hs];
            for (var y = 0; y < hs; y++)
            {
                var fy = (y + 0.5f) * fullPixelsPerWorkingPixel;
                for (var x = 0; x < ws; x++)
                {
                    var fx = (x + 0.5f) * fullPixelsPerWorkingPixel;
                    foreach (var polygon in exclusions)
                    {
                        if (polygon.Contains(fx, fy))
                        {
                            mask[y * ws + x] = true;
                            break;
                        }
                    }
                }
            }
            return mask;
        }

        /// <summary>Median of a finite plane; <paramref name="scratch"/> is overwritten.</summary>
        private static float MedianOf(ReadOnlySpan<float> values, Span<float> scratch)
        {
            var n = StatisticsHelper.CompactFinite(values, scratch);
            return n > 0 ? StatisticsHelper.MedianFast(scratch[..n]) : 0f;
        }

        private static void Correct(ReadOnlySpan<float> src, ReadOnlySpan<float> background, float level, BackgroundCorrection correction,
            Span<float> dst, ref float min, ref float max)
        {
            for (var i = 0; i < src.Length; i++)
            {
                var s = src[i];
                if (!float.IsFinite(s))
                {
                    dst[i] = s;
                    continue;
                }
                float v;
                if (correction == BackgroundCorrection.Divide)
                {
                    v = s / Math.Max(background[i], DivideEpsilon) * level;
                }
                else
                {
                    v = s - background[i] + level;
                    if (v < 0f)
                    {
                        v = 0f;
                    }
                }
                dst[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        private static (float Min, float Max) Extrema(float[][,] planes)
        {
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            foreach (var plane in planes)
            {
                var span = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
                foreach (var v in span)
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            return min <= max ? (min, max) : (0f, 0f);
        }
    }
}
