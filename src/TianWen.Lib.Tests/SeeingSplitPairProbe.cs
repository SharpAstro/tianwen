using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using nom.tam.fits;
using nom.tam.util;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Deconvolution;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;
using static TianWen.Lib.Tests.DeconvolutionProbeMeasures;

namespace TianWen.Lib.Tests;

/// <summary>
/// E2.10 (deconvolver-training.md): the first REAL-blur measurement. A session's sharpest third and
/// softest third, stacked on one reference (<c>tools/psf-seeing-split.py</c>'s recipe), are a truth
/// and an input whose difference is the night's own seeing, focus and wind rather than a drawn Moffat.
/// This probe does to that pair what <see cref="DeconvolutionOracleCeilingProbe"/> does to a synthetic
/// one: estimates the difference kernel from the two frames' own stars (<see cref="PsfProfileFit"/>
/// with the signal floor, the width by Moffat composition), runs the Richardson-Lucy oracle on the soft
/// master with it, and reads the recovered width, the star count and the ringing against the sharp
/// master. Whether the estimator step built on synthetic blur (E1c, E1d, E1e) transfers to blur of an
/// unknown shape is the question; the kernel's beta is not known for real seeing, so two arms carry
/// the synthetic convention (beta 4) and the soft frame's own fitted shape.
/// </summary>
/// <remarks>
/// Skipped unless <c>TIANWEN_E210_PAIR_DIR</c> names a directory holding <c>sharp/master_*.fits</c> and
/// <c>soft/master_*.fits</c> (the FULL masters, not the autocrops). <b>The two masters are on different
/// canvases</b>: the canvas is the union of each run's frame footprints, so two subsets of one manifest
/// share the reference and not the extent or the origin (the first pair came out 3045x3063 against
/// 3171x3088). They are overlaid through the <c>CANVASX0</c>/<c>CANVASY0</c> cards the stacker writes,
/// each master's pixel (0, 0) in reference-frame pixels; a master without them cannot be aligned and the
/// probe says so. The shipped SAS graph's arm needs the GPU and runs only under <c>TIANWEN_E210_SAS=1</c>.
/// The crop is a centred square of the region both masters cover, up to 1024 px, because the oracle
/// convolves serially; the measures are per channel.
/// </remarks>
public class SeeingSplitPairProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_E210_PAIR_DIR";
    private const string SasVar = "TIANWEN_E210_SAS";
    private const int MaxSide = 1024;
    private const int MinSide = 384;
    private const int Iterations = 60;
    private static readonly int[] Checkpoints = [20, 40, Iterations];
    /// <summary>The beta the synthetic arms injected, so the first arm reads like E1d's est-c.</summary>
    private const double SyntheticKernelBeta = 4.0;
    private const float EstimatorSnrMin = 5f;
    private const int EstimatorMaxStars = 3000;
    /// <summary>Below this the estimated difference is "no blur" and the oracle is not run.</summary>
    private const double MinKernelFwhm = 0.1;
    /// <summary>A crop with more uncovered pixels than this in either master is shrunk and recentred.</summary>
    private const double MaxUncoveredFraction = 0.02;

    /// <summary>A master in the deployed estimator's UNIT range (<see cref="Image"/>, a rewrap of
    /// <see cref="Original"/>), with its origin in reference-frame pixels.</summary>
    private sealed record Master(Image Image, Image Original, string Path, int OriginX, int OriginY)
    {
        public int Width => Image.Shape.Width;
        public int Height => Image.Shape.Height;
    }

    private sealed record Pair(Master Sharp, Master Soft) : IDisposable
    {
        public void Dispose()
        {
            Sharp.Original.Release();
            Soft.Original.Release();
        }
    }

    /// <summary>A square common to both masters: its top-left in each master's own pixels, its side,
    /// and the stars the sharp master's crop offers at snr 20, which is what chose it.</summary>
    private readonly record struct Region(int SharpX, int SharpY, int SoftX, int SoftY, int Side, int SharpStars);

    private static string? FindMaster(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "master_*.fits")
                .Where(p => !p.Contains("_autocrop", StringComparison.OrdinalIgnoreCase)
                         && !p.Contains(".rejection", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;

    /// <summary>The <c>CANVASX0</c>/<c>CANVASY0</c> cards, or null on a master written before they existed.</summary>
    private static (int X, int Y)? ReadOrigin(string path)
    {
        using var reader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
        using var fits = new Fits(reader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
        var header = fits.ReadFirstImageHduHeaderOnly()?.Header;
        if (header is null)
        {
            return null;
        }

        var x = header.GetIntValue("CANVASX0", int.MinValue);
        var y = header.GetIntValue("CANVASY0", int.MinValue);
        return x == int.MinValue || y == int.MinValue ? null : (x, y);
    }

    private static Master? LoadMaster(string path, out string skip)
    {
        if (ReadOrigin(path) is not { } origin)
        {
            skip = $"{path} carries no CANVASX0/CANVASY0 cards (stacked before the origin was written); re-stack it";
            return null;
        }

        if (!Image.TryReadFitsFile(path, out var image) || image is null)
        {
            skip = $"unreadable {path}";
            return null;
        }

        // The deployed deconvolver measures its PSF on a unit-range image, so every width and count
        // here is taken in that domain too; the first run measured the sharp master in native units
        // and the soft one in unit range and read a B/A of 0.37 that was the domain, not the sky.
        skip = string.Empty;
        return new Master(image.ScaleFloatValuesToUnitInPlace(), image, path, origin.X, origin.Y);
    }

    private static Pair? Load(out string skip)
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        if (string.IsNullOrWhiteSpace(root))
        {
            skip = $"{DirVar} not set";
            return null;
        }

        var sharpPath = FindMaster(Path.Combine(root, "sharp"));
        var softPath = FindMaster(Path.Combine(root, "soft"));
        if (sharpPath is null || softPath is null)
        {
            skip = $"no sharp/master_*.fits and soft/master_*.fits under {root}";
            return null;
        }

        var sharp = LoadMaster(sharpPath, out skip);
        if (sharp is null)
        {
            return null;
        }

        var soft = LoadMaster(softPath, out skip);
        if (soft is null)
        {
            sharp.Image.Release();
            return null;
        }

        if (sharp.Image.Shape.ChannelCount != soft.Image.Shape.ChannelCount)
        {
            var shapes = $"{sharp.Image.Shape} against {soft.Image.Shape}";
            sharp.Image.Release();
            soft.Image.Release();
            skip = $"the two masters differ in channel count ({shapes})";
            return null;
        }

        return new Pair(sharp, soft);
    }

    /// <summary>
    /// The square inside the region BOTH masters cover that offers the sharp master the MOST stars.
    /// The common region is found in reference-frame space through the origin cards; candidate squares
    /// step across it at half a side; one with more than two percent of uncovered (NaN or exact zero)
    /// pixels in either master is dropped, since the union canvas's corners are empty where only some
    /// frames reached; the rest are ranked by the sharp crop's star count at snr 20. Chosen by count
    /// rather than by geometry because the geometric centre of this first pair was M42's core, where a
    /// 1024 px crop held 17 stars and the profile fit refused on both sides.
    /// </summary>
    private static async Task<Region?> CommonSquareAsync(Pair pair, int channel, CancellationToken ct)
    {
        var a = pair.Sharp;
        var b = pair.Soft;
        // Each master's extent in reference-frame pixels.
        var left = Math.Max(a.OriginX, b.OriginX);
        var top = Math.Max(a.OriginY, b.OriginY);
        var right = Math.Min(a.OriginX + a.Width, b.OriginX + b.Width);
        var bottom = Math.Min(a.OriginY + a.Height, b.OriginY + b.Height);
        if (right - left < MinSide || bottom - top < MinSide)
        {
            return null;
        }

        var side = Math.Min(MaxSide, Math.Min(right - left, bottom - top));
        while (side >= MinSide)
        {
            Region? best = null;
            var step = Math.Max(64, side / 2);
            for (var y0 = top; y0 + side <= bottom; y0 += step)
            {
                for (var x0 = left; x0 + side <= right; x0 += step)
                {
                    var region = new Region(x0 - a.OriginX, y0 - a.OriginY, x0 - b.OriginX, y0 - b.OriginY, side, 0);
                    if (UncoveredFraction(a.Image, channel, region.SharpX, region.SharpY, side) > MaxUncoveredFraction
                        || UncoveredFraction(b.Image, channel, region.SoftX, region.SoftY, side) > MaxUncoveredFraction)
                    {
                        continue;
                    }

                    var crop = Wrap(Cut(a.Image, channel, region.SharpX, region.SharpY, side, side), side, side);
                    int stars;
                    try
                    {
                        stars = (await crop.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct)).Count;
                    }
                    finally
                    {
                        crop.Release();
                    }

                    if (best is null || stars > best.Value.SharpStars)
                    {
                        best = region with { SharpStars = stars };
                    }
                }
            }

            if (best is { } found)
            {
                return found;
            }

            side = (int)(side * 0.875);
        }

        return null;
    }

    private static double UncoveredFraction(Image image, int channel, int x0, int y0, int side)
    {
        var (_, width, _) = image.Shape;
        var src = image.GetChannelSpan(channel);
        var uncovered = 0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var v = src[((y0 + y) * width) + x0 + x];
                if (!float.IsFinite(v) || v == 0f)
                {
                    uncovered++;
                }
            }
        }

        return (double)uncovered / (side * (double)side);
    }

    [Fact]
    public async Task ReportWhatTheOracleRecoversOnARealSeeingSplit()
    {
        using var pair = Load(out var skip);
        Assert.SkipWhen(pair is null, skip);
        var ct = TestContext.Current.CancellationToken;
        var channels = pair!.Sharp.Image.Shape.ChannelCount;

        output.WriteLine($"sharp     {pair.Sharp.Path} ({pair.Sharp.Width} x {pair.Sharp.Height}, origin {pair.Sharp.OriginX}, {pair.Sharp.OriginY} in reference px)");
        output.WriteLine($"soft      {pair.Soft.Path} ({pair.Soft.Width} x {pair.Soft.Height}, origin {pair.Soft.OriginX}, {pair.Soft.OriginY})");
        output.WriteLine($"crop      the square of the region both cover (overlaid through CANVASX0/CANVASY0) with the most sharp-master stars at snr 20, at most {MaxSide} px; "
            + $"{channels} channel(s); both masters in the deployed estimator's unit range");
        output.WriteLine($"estimator PsfProfileFit on each crop's own detections (snr >= {EstimatorSnrMin}, <= {EstimatorMaxStars} stars, SignalFloor); "
            + $"kernel width by Moffat composition; est-c takes beta {SyntheticKernelBeta}, est-cb the soft frame's fitted beta");
        output.WriteLine($"oracle    Richardson-Lucy, {Iterations} iterations, read at {string.Join(", ", Checkpoints)}; widths are the deployed estimator's median FWHM in px");
        output.WriteLine("");
        output.WriteLine($"{"ch",2} {"arm",-7} {"kernel",7} {"beta",5} {"iter",4} {"rec",6} {"rec/A",6} {"recov%",7} {"stars",6} {"vs A",6} {"ring",6} {"null",6} {"excess",7}");

        for (var c = 0; c < channels; c++)
        {
            if (await CommonSquareAsync(pair, c, ct) is not { } r)
            {
                output.WriteLine($"{c,2} (no common covered square of at least {MinSide} px; skipped)");
                continue;
            }

            var truth = Cut(pair.Sharp.Image, c, r.SharpX, r.SharpY, r.Side, r.Side);
            var observed = Cut(pair.Soft.Image, c, r.SoftX, r.SoftY, r.Side, r.Side);
            var (bg, mad) = BackgroundStats(truth);
            if (!(mad > 0f))
            {
                output.WriteLine($"{c,2} (flat crop, no MAD; skipped)");
                continue;
            }

            var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, r.Side, ct);
            var (blurFwhm, blurStars) = await MeasuredFwhmAsync(observed, r.Side, ct);
            StarList stars;
            var truthImage = Wrap(truth, r.Side, r.Side);
            try
            {
                stars = await truthImage.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct);
            }
            finally
            {
                truthImage.Release();
            }

            var (fitA, diagA) = await FitStarProfileAsync(truth, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
            var (fitB, diagB) = await FitStarProfileAsync(observed, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
            var (nullRing, nullOver) = Ringing(observed, r.Side, stars, bg, mad);

            output.WriteLine($"{c,2} crop {r.Side} px at sharp ({r.SharpX}, {r.SharpY}) / soft ({r.SoftX}, {r.SoftY}), chosen for {r.SharpStars} sharp stars at snr 20; A (sharp) {truthFwhm:F2} px over {truthStars} stars, "
                + $"B (soft) {blurFwhm:F2} px over {blurStars}, B/A {blurFwhm / truthFwhm:F3}; {stars.Count} truth stars at snr 20; B's own ring null {nullRing:F2} MAD, {nullOver:P0} over one");
            output.WriteLine($"{c,2} fit A {(fitA is { } a ? $"{a.Fwhm:F2} px beta {a.MoffatBeta:F2}" : Describe(diagA))}; "
                + $"fit B {(fitB is { } b ? $"{b.Fwhm:F2} px beta {b.MoffatBeta:F2}" : Describe(diagB))}"
                + (fitA is { } a2 && fitB is { } b2 ? $"; fitted B/A {b2.Fwhm / a2.Fwhm:F3}" : ""));

            if (fitA is not { } cleanFit || fitB is not { } softFit)
            {
                output.WriteLine($"{c,2} (no estimate; no oracle run)");
                continue;
            }

            var arms = new (string Label, double Width, double Beta)[]
            {
                ("est-c", MoffatComposition.DifferenceFwhm(cleanFit.Fwhm, cleanFit.MoffatBeta, softFit.Fwhm, SyntheticKernelBeta), SyntheticKernelBeta),
                ("est-cb", MoffatComposition.DifferenceFwhm(cleanFit.Fwhm, cleanFit.MoffatBeta, softFit.Fwhm, softFit.MoffatBeta), softFit.MoffatBeta),
            };

            foreach (var (label, kernelWidth, kernelBeta) in arms)
            {
                if (!double.IsFinite(kernelWidth) || kernelWidth < MinKernelFwhm)
                {
                    output.WriteLine($"{c,2} {label,-7} (difference width {kernelWidth:F2} px: no blur to remove, oracle not run)");
                    continue;
                }

                var kernel = PsfKernel.Moffat(kernelWidth, kernelBeta);
                var snapshots = new Dictionary<int, float[]>();
                var result = RichardsonLucy.Deconvolve(observed, r.Side, r.Side, kernel, Iterations, (iteration, estimate) =>
                {
                    if (Array.IndexOf(Checkpoints, iteration) >= 0 && iteration != Iterations)
                    {
                        snapshots[iteration] = estimate.ToArray();
                    }
                });
                snapshots[Iterations] = result.Estimate;

                foreach (var iteration in Checkpoints)
                {
                    var estimate = snapshots[iteration];
                    var (recFwhm, recStars) = await MeasuredFwhmAsync(estimate, r.Side, ct);
                    var (ring, overOne) = Ringing(estimate, r.Side, stars, bg, mad);
                    var recovered = float.IsFinite(recFwhm) && blurFwhm > truthFwhm
                        ? (blurFwhm - recFwhm) / (blurFwhm - truthFwhm)
                        : double.NaN;
                    output.WriteLine($"{c,2} {label,-7} {kernelWidth,7:F2} {kernelBeta,5:F2} {iteration,4} {recFwhm,6:F2} {recFwhm / truthFwhm,6:F3} {recovered,7:P0} "
                        + $"{recStars,6} {(truthStars > 0 ? (double)recStars / truthStars : double.NaN),6:F2} {ring,6:F2} {nullRing,6:F2} {overOne - nullOver,7:P0}");
                }
            }
        }

        output.WriteLine("");
        output.WriteLine("Read rec/A against E1's ceiling at the same B/A ratio (1.00 to 1.02 through 1.6x with the exact kernel), stars vs A");
        output.WriteLine("against the truth-anchored count (over 1 with rec/A under 1 is fabrication), and ring excess against B's own null.");
    }

    /// <summary>
    /// The shipped SAS AI4 graph on the soft crop, whole-image psf01 as it ships, read against the sharp
    /// crop with the same measures. GPU; opt-in.
    /// </summary>
    [Fact]
    public async Task ReportWhatTheShippedGraphDoesOnARealSeeingSplit()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(SasVar) == "1", $"{SasVar} is not 1 (needs the GPU)");
        using var pair = Load(out var skip);
        Assert.SkipWhen(pair is null, skip);
        var resolver = new ModelResolver();
        Assert.SkipUnless(resolver.TryResolve(OnnxNonStellarDeconvolver.Model, out _), $"{OnnxNonStellarDeconvolver.Model} does not resolve");

        var ct = TestContext.Current.CancellationToken;
        var channels = pair!.Sharp.Image.Shape.ChannelCount;
        var region = await CommonSquareAsync(pair, 0, ct);
        Assert.SkipWhen(region is null, "no common covered square");
        var r = region!.Value;

        // The soft crop with every channel. The masters are already in unit range, so the crop is too;
        // MaxValue is the crop's own peak, which the deconvolver's range check reads.
        var planes = new float[channels][,];
        var max = 0f;
        for (var c = 0; c < channels; c++)
        {
            var cut = Cut(pair.Soft.Image, c, r.SoftX, r.SoftY, r.Side, r.Side);
            planes[c] = new float[r.Side, r.Side];
            for (var y = 0; y < r.Side; y++)
            {
                for (var x = 0; x < r.Side; x++)
                {
                    var v = cut[(y * r.Side) + x];
                    planes[c][y, x] = v;
                    if (v > max) max = v;
                }
            }
        }

        var unit = new Image(planes, BitDepth.Float32, max <= 0f ? 1f : max, 0f, 0f,
            new ImageMeta { SensorType = channels == 1 ? SensorType.Monochrome : SensorType.Color });
        using var deconvolver = new OnnxNonStellarDeconvolver(resolver, new HfdPsfEstimator(), chunkSize: 256, overlap: 64);

        output.WriteLine($"sharp     {pair.Sharp.Path}");
        output.WriteLine($"soft      {pair.Soft.Path}");
        output.WriteLine($"graph     {OnnxNonStellarDeconvolver.Model}, whole-image psf01 over the shipped range; crop {r.Side} px at sharp ({r.SharpX}, {r.SharpY}) / soft ({r.SoftX}, {r.SoftY})");
        output.WriteLine("");
        output.WriteLine($"{"ch",2} {"A fwhm",6} {"A n",5} {"B fwhm",6} {"B n",5} {"B/A",6} {"out",6} {"out/A",6} {"recov%",7} {"out n",6} {"vs A",6} {"ring",6} {"null",6} {"excess",7} {"s",5}");

        var started = DateTime.UtcNow;
        var result = await deconvolver.EnhanceAsync(unit, ct);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        try
        {
            for (var c = 0; c < channels; c++)
            {
                var truth = Cut(pair.Sharp.Image, c, r.SharpX, r.SharpY, r.Side, r.Side);
                var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, r.Side, ct);
                StarList stars;
                var truthImage = Wrap(truth, r.Side, r.Side);
                try
                {
                    stars = await truthImage.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct);
                }
                finally
                {
                    truthImage.Release();
                }

                // Input and output are in the unit range, so their ring statistics use their own MADs.
                var observedUnit = FullPlane(unit, c);
                var (bgIn, madIn) = BackgroundStats(observedUnit);
                var (blurFwhm, blurStars) = await MeasuredFwhmAsync(observedUnit, r.Side, ct);
                var (nullRing, nullOver) = Ringing(observedUnit, r.Side, stars, bgIn, madIn);
                var outPlane = FullPlane(result, c);
                var (bgOut, madOut) = BackgroundStats(outPlane);
                var (outFwhm, outStars) = await MeasuredFwhmAsync(outPlane, r.Side, ct);
                var (ring, overOne) = Ringing(outPlane, r.Side, stars, bgOut, madOut);
                var recovered = float.IsFinite(outFwhm) && blurFwhm > truthFwhm
                    ? (blurFwhm - outFwhm) / (blurFwhm - truthFwhm)
                    : double.NaN;
                output.WriteLine($"{c,2} {truthFwhm,6:F2} {truthStars,5} {blurFwhm,6:F2} {blurStars,5} {blurFwhm / truthFwhm,6:F3} {outFwhm,6:F2} {outFwhm / truthFwhm,6:F3} {recovered,7:P0} "
                    + $"{outStars,6} {(truthStars > 0 ? (double)outStars / truthStars : double.NaN),6:F2} {ring,6:F2} {nullRing,6:F2} {overOne - nullOver,7:P0} {(c == 0 ? seconds : 0),5:F0}");
            }
        }
        finally
        {
            result.Release();
        }
    }
}
