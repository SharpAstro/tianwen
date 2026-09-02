using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using SharpAstro.Png;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Measures tile-seam visibility in the in-house N2N denoiser's output on a REAL master. The seam
/// is a property of the stitch geometry meeting <c>N2nLinearRunner</c>'s per-chunk level
/// correction, and neither half shows up in a single-tile fixture -- the parity plate is 160 px
/// against a 256 px tile, so it never stitches at all.
/// </summary>
/// <remarks>
/// <para><b>What is profiled is the correction field <c>out - in</c>, not the output.</b> That
/// subtracts the sky and the nebula, so a per-chunk DC step stands on its own instead of hiding
/// under real structure.</para>
///
/// <para><b>Each seam is scored against its own neighbourhood, and the statistic is a median over
/// seams rather than a maximum.</b> Both matter. A single global noise floor makes any seam that
/// happens to sit on a nebula edge look damning, and a max-over-seams then reports that one
/// coincidence as the result -- which is how an earlier version of this probe read a real feature
/// as a surviving seam. A locally normalised median answers the question actually being asked: are
/// the stride positions distinguishable from everywhere else?</para>
///
/// <para><b>The stride is overridable, and moving it is the control.</b> A residual that stays put
/// when the stride changes is image structure; one that follows the stride is a seam. Nothing
/// cheaper separates them.</para>
/// </remarks>
[Collection("Imaging")]
public class N2nSeamProbe(ITestOutputHelper output)
{
    private const string PathVar = "TIANWEN_N2N_SEAM_FITS";
    private const string OverlapVar = "TIANWEN_N2N_SEAM_OVERLAP";
    private const string PngDirVar = "TIANWEN_N2N_SEAM_PNG_DIR";
    private const string TagVar = "TIANWEN_N2N_SEAM_TAG";

    private const int DefaultOverlap = 64;
    private const int Tile = 256;

    private static int Overlap =>
        int.TryParse(Environment.GetEnvironmentVariable(OverlapVar), out var v) && v > 0 ? v : DefaultOverlap;

    [Fact]
    public async Task ReportSeamStepsOnARealMaster()
    {
        var path = Environment.GetEnvironmentVariable(PathVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), $"{PathVar} not set");
        Assert.SkipUnless(File.Exists(path), $"{PathVar} does not exist: {path}");
        Assert.SkipUnless(new ModelResolver().TryResolve(N2nDenoiser.ModelFileName, out _),
            $"{N2nDenoiser.ModelFileName} not resolvable");

        var ct = TestContext.Current.CancellationToken;
        Image.TryReadFitsFile(path!, out var loaded).ShouldBeTrueForProbe(output, "could not read the frame");
        if (loaded is null) return;

        var input = loaded.ScaleFloatValuesToUnitInPlace();
        var (channels, width, height) = input.Shape;

        var overlap = Overlap;
        var border = AiNafnetInputs.StitchBorderPx;
        var stride = Tile - overlap;
        var retained = Tile - 2 * border;
        var bandWidth = retained - stride;     // == overlap - 2 * border, the shared span

        output.WriteLine($"file    {Path.GetFileName(path)}   {width}x{height} x{channels}");
        output.WriteLine($"geom    tile={Tile} overlap={overlap} border={border} -> stride={stride}, "
            + $"retained={retained}, shared band={bandWidth}px starting at x = k*{stride}");
        for (var c = 0; c < channels; c++)
        {
            var (median, mad) = MedianMad(input.GetChannelSpan(c));
            output.WriteLine($"  ch{c}   input median={median:F6} MAD={mad:F6}  (trained on single-sub noise, MAD near 0.01)");
        }

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new N2nDenoiser(new ModelResolver(), factory.CreateLogger<N2nDenoiser>(), overlap: overlap);
        var denoised = await enhancer.EnhanceAsync(input, 1.0f, ct);

        var worstMedianRatio = 0.0;
        for (var c = 0; c < channels; c++)
        {
            var (med, p90, loud, count) = SeamStats(
                input.GetChannelSpan(c), denoised.GetChannelSpan(c), width, height, stride, bandWidth);
            worstMedianRatio = Math.Max(worstMedianRatio, med);

            output.WriteLine($"  ch{c}   seam-edge step vs LOCAL structure: median={med:F1}x  p90={p90:F1}x  "
                + $"({loud.Count}/{count} edges at >=3x)");
            if (loud.Count > 0)
            {
                output.WriteLine($"         loud edges: {string.Join(" ", loud.GetRange(0, Math.Min(10, loud.Count)))}");
            }
        }

        output.WriteLine($"VERDICT median seam-edge step is {worstMedianRatio:F1}x the local structure "
            + "(1x means the stride positions are indistinguishable from anywhere else)");

        if (Environment.GetEnvironmentVariable(PngDirVar) is { Length: > 0 } pngDir)
        {
            Directory.CreateDirectory(pngDir);
            var tag = Environment.GetEnvironmentVariable(TagVar) is { Length: > 0 } s ? s : "run";
            // A background-dominated crop spanning four seams, so the grid has somewhere to show.
            const int cropX = 64, cropY = 1200, cropW = 896, cropH = 384;
            if (cropX + cropW <= width && cropY + cropH <= height)
            {
                // The stretch window comes from the INPUT, so the two runs are directly comparable
                // rather than each being auto-scaled to its own output.
                var (med, mad) = MedianMad(input.GetChannelSpan(1));
                var lo = med - mad;
                var hi = med + 8 * mad;
                WriteCrop(Path.Combine(pngDir, $"seam-{tag}-denoised.png"),
                    denoised, 1, width, cropX, cropY, cropW, cropH, lo, hi, stride);
                WriteCrop(Path.Combine(pngDir, $"seam-input.png"),
                    input, 1, width, cropX, cropY, cropW, cropH, lo, hi, stride);
                output.WriteLine($"wrote crops to {pngDir} (window {lo:F6}..{hi:F6}, ticks every {stride}px)");
            }
        }
        denoised.Release();
    }

    /// <summary>
    /// Measures how the net answers when its input is moved INTO its training band, by two routes
    /// that have to be told apart. The rescale route multiplies the pixels by k so the background
    /// sigma lands near the single-sub sigma the conditioning plane was calibrated for (about 0.01),
    /// denoises at full strength and divides by k: the plane stays truthful for the pixels the net is
    /// given and the level rides along into a plausible band. The stretch route is the exporter's own
    /// <see cref="ChunkedNafnetRunner.ApplyInputStretch"/> (one whole-frame per-channel MTF to a
    /// median of 0.25), run, then <see cref="Image.MtfUnstretch"/>: that is the domain every training
    /// tile was stored in, so it is the arm hypothesis H0 of <c>docs/plans/denoiser-training.md</c>
    /// predicts wins, and the one the runner ships once it does. Neither is the rejected conditioning
    /// dial, which keeps the pixels and lies about the plane. Whether an answer survives its round trip
    /// is the measurement, since nobody has shown the net to be scale-equivariant.
    /// </summary>
    /// <remarks>
    /// <para>The scaled frames are deliberately outside the [0, 1] contract at the bright end (a star
    /// near 1.0 lands near k), so the scaled copy stamps MaxValue = 1 to pass the miscalibration
    /// tripwire in <see cref="N2nDenoiser"/>. The runner reads that property only to stamp the
    /// output's metadata, so the stamp changes nothing computed here. A background receptive field
    /// never sees a star pixel, so background statistics stay valid regardless; near-star behaviour
    /// is what the crops, the large-correction count and the star column are for.</para>
    ///
    /// <para><b>The star column is the trainer's own metric</b> (<c>n2n_metrics.star_table</c> and
    /// <c>measure</c>): stars are local maxima of the INPUT luminance standing at least 8 darkest-half
    /// MADs above their surroundings, bucketed by that SNR, and every arm reports the median fraction
    /// of peak amplitude it kept at those positions plus the fraction still 5 sigma above ITS OWN
    /// noise. The two disagree by design: a model that halves a star while quartering the noise makes
    /// it more detectable, not less. The one departure from the trainer is the background under a
    /// peak, a local ring median rather than the tile median, because a whole frame has nebulosity
    /// where a 256 px tile has a level; without it every noise peak on the nebula counts as a faint
    /// star and the faint bucket measures noise suppression instead of photometry.</para>
    ///
    /// <para>The k = 1 row and the stretch arm are the same computation once the runner stretches for
    /// itself (its auto-detect then finds the probe-stretched frame already in band and feeds it
    /// verbatim), so their max |difference| is printed: zero says the runner does exactly what this
    /// arm does and nothing more, a large number says it does not stretch at all.</para>
    /// </remarks>
    [Fact]
    public async Task ReportInputRescaleResponseOnARealMaster()
    {
        var path = Environment.GetEnvironmentVariable(PathVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), $"{PathVar} not set");
        Assert.SkipUnless(File.Exists(path), $"{PathVar} does not exist: {path}");
        Assert.SkipUnless(new ModelResolver().TryResolve(N2nDenoiser.ModelFileName, out _),
            $"{N2nDenoiser.ModelFileName} not resolvable");

        var ct = TestContext.Current.CancellationToken;
        Image.TryReadFitsFile(path!, out var loaded).ShouldBeTrueForProbe(output, "could not read the frame");
        if (loaded is null) return;

        var input = loaded.ScaleFloatValuesToUnitInPlace();
        var (channels, width, height) = input.Shape;

        // The trainer's own sigma statistic (bg_sigma_torch): median minus 25th percentile of the
        // channel-mean image. SIGMA_SCALE = 100 was calibrated so a single sub's ~0.01 puts the
        // conditioning plane near 1.0, so 0.01 / sigma is the scale that lands THIS frame there.
        var sigmaLum = TrainerSigma(input);
        var honestK = 0.01f / sigmaLum;
        var overlap = Overlap;
        var stride = Tile - overlap;
        var bandWidth = (Tile - 2 * AiNafnetInputs.StitchBorderPx) - stride;

        output.WriteLine($"file    {Path.GetFileName(path)}   {width}x{height} x{channels}");
        output.WriteLine($"trainer sigma (P50 - P25 of luminance) = {sigmaLum:E3}  ->  honest scale k = {honestK:F1}");

        var inStats = new (float Median, float Mad)[channels];
        var inAdj = new float[channels];
        for (var c = 0; c < channels; c++)
        {
            inStats[c] = MedianMad(input.GetChannelSpan(c));
            inAdj[c] = AdjacentDiffMad(input.GetChannelSpan(c), width);
            output.WriteLine($"  ch{c}   input median={inStats[c].Median:F6} MAD={inStats[c].Mad:E3} adjMAD={inAdj[c]:E3}");
        }

        // The star table comes from the INPUT once and is scored at the same positions on every
        // arm, so a star that an arm erased still counts against it rather than vanishing from it.
        var lumIn = Luminance(input);
        var stars = DetectStars(lumIn, width, height);
        output.WriteLine($"stars   {stars.Length} input-luminance peaks at >= 8 MAD over their ring, by SNR bucket: {BucketCounts(stars)}");

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new N2nDenoiser(new ModelResolver(), factory.CreateLogger<N2nDenoiser>(), overlap: overlap);

        // One shared stretch window from the INPUT so every arm's crop is directly comparable.
        var (winMed, winMad) = MedianMad(input.GetChannelSpan(1));
        const int cropX = 64, cropY = 1200, cropW = 896, cropH = 384;
        var cropDir = Environment.GetEnvironmentVariable(PngDirVar) is { Length: > 0 } dir
            && cropX + cropW <= width && cropY + cropH <= height ? dir : null;
        if (cropDir is not null)
        {
            Directory.CreateDirectory(cropDir);
            WriteCrop(Path.Combine(cropDir, "rescale-input.png"),
                input.GetChannelSpan(1), width, cropX, cropY, cropW, cropH, winMed - winMad, winMed + 8 * winMad, stride);
        }

        // The exporter's route: every training tile was stored after exactly this stretch, so this
        // arm hands the net the domain it was trained in and inverts the answer with the same
        // parameters the stretch returned.
        var (stretchedInput, stretchApplied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(input);
        var stretchedMedians = new string[channels];
        for (var c = 0; c < channels; c++)
        {
            stretchedMedians[c] = MedianMad(stretchedInput.GetChannelSpan(c)).Median.ToString("F4");
        }
        output.WriteLine($"stretch applied={stretchApplied}  stretched per-channel medians: {string.Join(" ", stretchedMedians)}  (training tiles: 0.25)");
        var stretchDenoised = await enhancer.EnhanceAsync(stretchedInput, 1.0f, ct);
        var stretchLinear = stretchApplied ? stretchDenoised.MtfUnstretch(origMin!, balances!) : stretchDenoised;
        var stretchArm = Planes(stretchLinear);
        stretchDenoised.Release();
        Score("stretch  (exporter MTF -> run -> invert)", stretchArm, "stretch");

        foreach (var k in new[] { 1f, 8f, honestK / 2f, honestK, honestK * 2f })
        {
            var scaled = ScaledCopy(input, k);
            var overOne = FractionAbove(input, 1f / k);
            var denoised = await enhancer.EnhanceAsync(scaled, 1.0f, ct);
            var planes = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                planes[c] = DividedCopy(denoised.GetChannelSpan(c), k);
            }
            denoised.Release();

            Score($"k={k:F1}  scaled-input pixels above 1.0: {overOne:P3}", planes, $"k{k:F0}");
            if (k == 1f)
            {
                output.WriteLine($"         k=1 vs stretch arm: max |diff| = {MaxAbsDifference(planes, stretchArm):E3}  (0 once the runner stretches for itself)");
            }
        }

        // Every arm is scored by the identical statistics on a LINEAR-units plane, whichever route
        // produced it; the heading names the route.
        void Score(string heading, float[][] planes, string cropTag)
        {
            output.WriteLine(heading);
            for (var c = 0; c < channels; c++)
            {
                var outPlane = planes[c];
                var (median, mad) = MedianMad(outPlane);
                var adjMad = AdjacentDiffMad(outPlane, width);
                var (seamMed, _, seamLoud, seamCount) = SeamStats(
                    input.GetChannelSpan(c), outPlane, width, height, stride, bandWidth);
                var loudBg = LargeBackgroundCorrections(
                    input.GetChannelSpan(c), outPlane, inStats[c].Median, inStats[c].Mad);
                output.WriteLine(
                    $"  ch{c}   MAD out={mad:E3} ({mad / inStats[c].Mad:P0} of input)  adjMAD out={adjMad:E3} ({adjMad / inAdj[c]:P0})  "
                    + $"median drag={median - inStats[c].Median:E2}  "
                    + $"seams median={seamMed:F1}x loud={seamLoud.Count}/{seamCount}  bg |corr|>10 MAD: {loudBg:F0}/Mpx");

                if (cropDir is not null && c == 1)
                {
                    WriteCrop(Path.Combine(cropDir, $"rescale-{cropTag}.png"),
                        outPlane, width, cropX, cropY, cropW, cropH, winMed - winMad, winMed + 8 * winMad, stride);
                }
            }
            output.WriteLine($"  stars  {StarReport(lumIn, Luminance(planes), stars, width)}");
        }
    }

    /// <summary>
    /// One hard-stretched grey crop, with a bright tick in the top rows at every seam column so the
    /// eye is pointed at the places under test rather than hunting for them.
    /// </summary>
    private static void WriteCrop(
        string path, Image image, int channel, int width,
        int cropX, int cropY, int cropW, int cropH, float lo, float hi, int stride)
    {
        WriteCrop(path, image.GetChannelSpan(channel), width, cropX, cropY, cropW, cropH, lo, hi, stride);
    }

    private static void WriteCrop(
        string path, ReadOnlySpan<float> span, int width,
        int cropX, int cropY, int cropW, int cropH, float lo, float hi, int stride)
    {
        var gray = new byte[cropW * cropH];
        var scale = hi > lo ? 255f / (hi - lo) : 0f;
        for (var y = 0; y < cropH; y++)
        {
            var src = (cropY + y) * width + cropX;
            for (var x = 0; x < cropW; x++)
            {
                var v = (span[src + x] - lo) * scale;
                gray[y * cropW + x] = (byte)Math.Clamp(v, 0f, 255f);
            }
        }
        for (var seam = stride; seam < cropX + cropW; seam += stride)
        {
            var x = seam - cropX;
            if (x < 0 || x >= cropW) continue;
            for (var y = 0; y < 6; y++) gray[y * cropW + x] = 255;
        }
        File.WriteAllBytes(path, PngWriter.EncodeGray8(gray, cropW, cropH));
    }

    private static List<int> SeamEdges(int width, int stride, int bandWidth)
    {
        var edges = new List<int>();
        for (var seam = stride; seam < width - 1; seam += stride)
        {
            if (seam >= 1) edges.Add(seam);
            var leave = seam + bandWidth;
            if (bandWidth > 0 && leave < width) edges.Add(leave);
        }
        return edges;
    }

    /// <summary>
    /// Median absolute adjacent delta within 25 columns either side of <paramref name="p"/>,
    /// skipping every seam edge so the yardstick is real structure only.
    /// </summary>
    private static double LocalBaseline(float[] profile, int p, List<int> seams)
    {
        var deltas = new List<double>();
        for (var x = Math.Max(1, p - 25); x < Math.Min(profile.Length, p + 25); x++)
        {
            var nearSeam = false;
            foreach (var s in seams)
            {
                if (Math.Abs(x - s) <= 2) { nearSeam = true; break; }
            }
            if (nearSeam) continue;
            deltas.Add(Math.Abs(profile[x] - profile[x - 1]));
        }
        if (deltas.Count == 0) return 0;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    private static float[] ColumnMeanOfDifference(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int width, int height)
    {
        var acc = new double[width];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++) acc[x] += b[row + x] - a[row + x];
        }
        var profile = new float[width];
        for (var x = 0; x < width; x++) profile[x] = (float)(acc[x] / height);
        return profile;
    }

    /// <summary>
    /// Scores every seam edge of the correction field <c>output - input</c> against its own local
    /// structure. Shared by both facts so they measure the identical statistic.
    /// </summary>
    private static (double Median, double P90, List<string> Loud, int Count) SeamStats(
        ReadOnlySpan<float> input, ReadOnlySpan<float> output, int width, int height, int stride, int bandWidth)
    {
        var profile = ColumnMeanOfDifference(input, output, width, height);
        var seams = SeamEdges(width, stride, bandWidth);
        var ratios = new List<double>();
        var loud = new List<string>();

        foreach (var p in seams)
        {
            var local = LocalBaseline(profile, p, seams);
            if (local <= 0) continue;
            var ratio = Math.Abs(profile[p] - profile[p - 1]) / local;
            ratios.Add(ratio);
            if (ratio >= 3.0) loud.Add($"x={p}:{ratio:F0}x");
        }

        ratios.Sort();
        var med = ratios.Count > 0 ? ratios[ratios.Count / 2] : 0;
        var p90 = ratios.Count > 0 ? ratios[(int)(ratios.Count * 0.9)] : 0;
        return (med, p90, loud, ratios.Count);
    }

    /// <summary>
    /// The trainer's bg_sigma_torch on the whole frame: median minus 25th percentile of the
    /// channel-mean image, sampled the same way as <see cref="MedianMad"/>.
    /// </summary>
    private static float TrainerSigma(Image image)
    {
        var (channels, width, height) = image.Shape;
        var total = width * height;
        var stride = Math.Max(1, total / 200_000);
        var lum = new List<float>();
        for (var i = 0; i < total; i += stride)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += image.GetChannelSpan(c)[i];
            lum.Add(sum / channels);
        }
        lum.Sort();
        return lum[lum.Count / 2] - lum[lum.Count / 4];
    }

    private static Image ScaledCopy(Image image, float k)
    {
        var (channels, width, height) = image.Shape;
        var data = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var src = image.GetChannelSpan(c);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++) plane[y, x] = src[row + x] * k;
            }
            data[c] = plane;
        }
        // MaxValue stamped 1 on purpose: the bright end is deliberately out of contract, and the
        // runner never reads the property. See the remarks on the rescale fact.
        return new Image(data, BitDepth.Float32, 1f, 0f, 0f, image.ImageMeta);
    }

    private static float[] DividedCopy(ReadOnlySpan<float> values, float k)
    {
        var result = new float[values.Length];
        var inv = 1f / k;
        for (var i = 0; i < values.Length; i++) result[i] = values[i] * inv;
        return result;
    }

    private static double FractionAbove(Image image, float threshold)
    {
        var (channels, _, _) = image.Shape;
        long count = 0, total = 0;
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] > threshold) count++;
            }
            total += span.Length;
        }
        return (double)count / total;
    }

    /// <summary>
    /// Fabrication proxy: how many background pixels (input below median + 5 MAD) moved by more
    /// than 10 input MADs, per megapixel of background. A denoiser should move background pixels
    /// by roughly one MAD; a large mover on background is an invented or deleted point source.
    /// </summary>
    private static double LargeBackgroundCorrections(
        ReadOnlySpan<float> input, ReadOnlySpan<float> output, float median, float mad)
    {
        if (mad <= 0) return 0;
        var bgCeiling = median + 5 * mad;
        var big = 10 * mad;
        long moved = 0, bg = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] >= bgCeiling) continue;
            bg++;
            if (Math.Abs(output[i] - input[i]) > big) moved++;
        }
        return bg > 0 ? moved * 1_000_000.0 / bg : 0;
    }

    /// <summary>
    /// MAD of horizontal adjacent-pixel differences. White (per-pixel) noise shows here at full
    /// strength while spatially correlated noise largely cancels, so comparing this statistic's
    /// retention with the plain MAD's separates the two components of a residual.
    /// </summary>
    private static float AdjacentDiffMad(ReadOnlySpan<float> values, int width)
    {
        var stride = Math.Max(1, values.Length / 200_000);
        var diffs = new List<float>();
        for (var i = 0; i + 1 < values.Length; i += stride)
        {
            if ((i + 1) % width == 0) continue;
            diffs.Add(values[i + 1] - values[i]);
        }
        diffs.Sort();
        var median = diffs[diffs.Count / 2];
        var devs = new float[diffs.Count];
        for (var i = 0; i < diffs.Count; i++) devs[i] = Math.Abs(diffs[i] - median);
        Array.Sort(devs);
        return devs[devs.Length / 2];
    }

    private static (float Median, float Mad) MedianMad(ReadOnlySpan<float> values)
    {
        var stride = Math.Max(1, values.Length / 200_000);
        var sampled = new List<float>();
        for (var i = 0; i < values.Length; i += stride) sampled.Add(values[i]);
        sampled.Sort();
        var median = sampled[sampled.Count / 2];
        var devs = new float[sampled.Count];
        for (var i = 0; i < sampled.Count; i++) devs[i] = Math.Abs(sampled[i] - median);
        Array.Sort(devs);
        return (median, devs[devs.Length / 2]);
    }

    // ---- the trainer's star metric (n2n_metrics.star_table / measure), on a whole frame ----

    private readonly record struct ProbeStar(int X, int Y, float Snr);

    /// <summary>The trainer's master-SNR buckets (<c>n2n_metrics.BUCKETS</c>); the faint end is where
    /// every failure so far has lived.</summary>
    private static readonly (float Lo, float Hi, string Label)[] SnrBuckets =
    [
        (8f, 15f, "8-15"),
        (15f, 30f, "15-30"),
        (30f, 100f, "30-100"),
        (100f, float.PositiveInfinity, "100+"),
    ];

    /// <summary>Background under a peak: the median of a 21x21 box minus the 7x7 the star sits in.</summary>
    private const int RingOuterHalf = 10;
    private const int RingInnerHalf = 3;

    private static float[] Luminance(Image image)
    {
        var (channels, width, height) = image.Shape;
        var lum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < lum.Length; i++) lum[i] += span[i];
        }
        var inv = 1f / channels;
        for (var i = 0; i < lum.Length; i++) lum[i] *= inv;
        return lum;
    }

    private static float[] Luminance(float[][] planes)
    {
        var lum = new float[planes[0].Length];
        foreach (var plane in planes)
        {
            for (var i = 0; i < lum.Length; i++) lum[i] += plane[i];
        }
        var inv = 1f / planes.Length;
        for (var i = 0; i < lum.Length; i++) lum[i] *= inv;
        return lum;
    }

    private static float[][] Planes(Image image)
    {
        var planes = new float[image.ChannelCount][];
        for (var c = 0; c < planes.Length; c++) planes[c] = image.GetChannelSpan(c).ToArray();
        return planes;
    }

    private static float MaxAbsDifference(float[][] a, float[][] b)
    {
        var worst = 0f;
        for (var c = 0; c < a.Length; c++)
        {
            for (var i = 0; i < a[c].Length; i++) worst = Math.Max(worst, Math.Abs(a[c][i] - b[c][i]));
        }
        return worst;
    }

    /// <summary>
    /// The trainer's <c>bg_stats</c>: the median of everything, and the MAD of the darkest half about
    /// its own median, so nebulosity is not counted as noise. Sampled like <see cref="MedianMad"/>.
    /// </summary>
    private static (float Median, float Mad) DarkHalfStats(ReadOnlySpan<float> values)
    {
        var stride = Math.Max(1, values.Length / 200_000);
        var sampled = new List<float>();
        for (var i = 0; i < values.Length; i += stride)
        {
            if (!float.IsNaN(values[i])) sampled.Add(values[i]);
        }
        sampled.Sort();
        var median = sampled[sampled.Count / 2];
        var half = sampled.Count / 2 + 1;
        var lowMedian = sampled[half / 2];
        var devs = new float[half];
        for (var i = 0; i < half; i++) devs[i] = Math.Abs(sampled[i] - lowMedian);
        Array.Sort(devs);
        return (median, devs[half / 2] + 1e-12f);
    }

    /// <summary>
    /// <c>star_table</c> on the whole input luminance: a pixel no 5x5 neighbour exceeds (ties count,
    /// as <c>maximum_filter</c> has it), standing at least 8 darkest-half MADs above its ring
    /// background. The global threshold is only the cheap pre-filter; the ring is what keeps a noise
    /// peak on bright nebulosity out of the table.
    /// </summary>
    private static ProbeStar[] DetectStars(float[] lum, int width, int height)
    {
        var (median, mad) = DarkHalfStats(lum);
        var threshold = median + 8f * mad;
        var ring = new float[(2 * RingOuterHalf + 1) * (2 * RingOuterHalf + 1)];
        var stars = new List<ProbeStar>();
        for (var y = RingOuterHalf; y < height - RingOuterHalf; y++)
        {
            for (var x = RingOuterHalf; x < width - RingOuterHalf; x++)
            {
                var v = lum[y * width + x];
                if (!(v > threshold) || !IsLocalMaximum(lum, width, x, y, v)) continue;
                var snr = (v - RingMedian(lum, width, x, y, ring)) / mad;
                if (BucketOf(snr) >= 0) stars.Add(new ProbeStar(x, y, snr));
            }
        }
        return stars.ToArray();
    }

    private static bool IsLocalMaximum(float[] lum, int width, int x, int y, float v)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            var row = (y + dy) * width + x;
            for (var dx = -2; dx <= 2; dx++)
            {
                if (lum[row + dx] > v) return false;
            }
        }
        return true;
    }

    private static float RingMedian(float[] lum, int width, int x, int y, float[] scratch)
    {
        var n = 0;
        for (var dy = -RingOuterHalf; dy <= RingOuterHalf; dy++)
        {
            var row = (y + dy) * width + x;
            for (var dx = -RingOuterHalf; dx <= RingOuterHalf; dx++)
            {
                if (Math.Abs(dx) <= RingInnerHalf && Math.Abs(dy) <= RingInnerHalf) continue;
                var v = lum[row + dx];
                if (!float.IsNaN(v)) scratch[n++] = v;
            }
        }
        if (n == 0) return float.NaN;
        Array.Sort(scratch, 0, n);
        return scratch[n / 2];
    }

    private static int BucketOf(float snr)
    {
        for (var b = 0; b < SnrBuckets.Length; b++)
        {
            if (snr >= SnrBuckets[b].Lo && snr < SnrBuckets[b].Hi) return b;
        }
        return -1;
    }

    private static string BucketCounts(ProbeStar[] stars)
    {
        var counts = new int[SnrBuckets.Length];
        foreach (var star in stars) counts[BucketOf(star.Snr)]++;
        var parts = new string[SnrBuckets.Length];
        for (var b = 0; b < SnrBuckets.Length; b++) parts[b] = $"{SnrBuckets[b].Label}: {counts[b]}";
        return string.Join("  ", parts);
    }

    /// <summary>
    /// <c>measure</c>: per bucket, the median fraction of ring-relative peak amplitude the arm kept
    /// at the input's star positions, and the fraction of those peaks still 5 darkest-half MADs
    /// above the ARM's own luminance noise.
    /// </summary>
    private static string StarReport(float[] lumIn, float[] lumOut, ProbeStar[] stars, int width)
    {
        var (_, outMad) = DarkHalfStats(lumOut);
        var ring = new float[(2 * RingOuterHalf + 1) * (2 * RingOuterHalf + 1)];
        var kept = new List<float>[SnrBuckets.Length];
        var detected = new int[SnrBuckets.Length];
        for (var b = 0; b < kept.Length; b++) kept[b] = new List<float>();

        foreach (var star in stars)
        {
            var b = BucketOf(star.Snr);
            var p = star.Y * width + star.X;
            var ampIn = lumIn[p] - RingMedian(lumIn, width, star.X, star.Y, ring);
            var ampOut = lumOut[p] - RingMedian(lumOut, width, star.X, star.Y, ring);
            kept[b].Add(ampOut / Math.Max(ampIn, 1e-9f));
            if (ampOut > 5f * outMad) detected[b]++;
        }

        var parts = new string[SnrBuckets.Length];
        for (var b = 0; b < SnrBuckets.Length; b++)
        {
            if (kept[b].Count == 0)
            {
                parts[b] = $"{SnrBuckets[b].Label}: none";
                continue;
            }
            kept[b].Sort();
            parts[b] = $"{SnrBuckets[b].Label}: amp kept {kept[b][kept[b].Count / 2]:F3} det {(double)detected[b] / kept[b].Count:P0} (n={kept[b].Count})";
        }
        return string.Join("   ", parts);
    }
}
