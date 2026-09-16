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

        // A stacked master is unit-referred by convention (DATAMAX 1, median about 0.5) with star peaks
        // far over 1, so this rescale is a no-op on it and every measurement below normalises its own
        // crop to a peak of 1 (DeconvolutionProbeMeasures.Wrap); the first run measured the sharp side
        // with the crop's raw peak as MaxValue and the soft side normalised and read a B/A of 0.37 that
        // was the detector's convention, not the sky.
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
    /// <summary>The training draws' median kernel over composed width on the SH61 pool's rows
    /// (`EffectiveKernelFwhmPx / ComposedFwhmPx`, p10 / p50 / p90 = 0.26 / 0.52 / 0.69, 2026-09-14),
    /// which is what "the frame's width times a fixed fraction" can offer as a single-frame kernel.</summary>
    private const double PoolKernelFraction = 0.52;

    /// <summary>The SH61 pool's clean widths in the exporter's statistic (the deployed estimator's
    /// HFD-based FWHM on a 256 px cell): the per-session medians run 1.83 to 3.82 px, p10 1.89, p50 2.48.</summary>
    private static readonly (string Label, double Fwhm)[] PoolFloors = [("floor-p50", 2.48), ("floor-p10", 1.89)];

    /// <summary>
    /// E7.1 (deconvolver-training.md): what kernel ONE frame can justify. The pair's kernel (est-c, the
    /// sharp master composed against the soft) is the answer inference never has; this reads, per channel
    /// on the same crop, the soft master's own widths in both statistics and the candidate single-frame
    /// rules against est-c: a fixed fraction of the measured width (the training draws' median), and the
    /// width composed down to a pool floor. The kernel-sensitivity sweep in the plan says how far off a
    /// rule may land.
    /// </summary>
    [Fact]
    public async Task ReportWhatOneFrameCanSayAboutItsKernel()
    {
        using var pair = Load(out var skip);
        Assert.SkipWhen(pair is null, skip);
        var ct = TestContext.Current.CancellationToken;
        var channels = pair!.Sharp.Image.Shape.ChannelCount;

        output.WriteLine($"sharp     {pair.Sharp.Path}");
        output.WriteLine($"soft      {pair.Soft.Path}");
        output.WriteLine($"widths    hfd: the deployed estimator's HFD-based FWHM (the exporter's CleanFwhmPx statistic); fit: PsfProfileFit core width (the pair probe's)");
        output.WriteLine($"rules     est-c: the pair's kernel (fit A composed against fit B, beta {SyntheticKernelBeta}); frac: {PoolKernelFraction} x B hfd; "
            + string.Join("; ", PoolFloors.Select(f => $"{f.Label}: B hfd composed down to {f.Fwhm} px (core beta {MoffatComposition.DefaultCoreBeta}, kernel beta {SyntheticKernelBeta})")));
        output.WriteLine("");
        output.WriteLine($"{"ch",2} {"A hfd",6} {"B hfd",6} {"B/A",5} {"fit A",6} {"fit B",6} {"B/A",5} {"est-c",6} {"frac",6} {"/est-c",6} "
            + string.Join(" ", PoolFloors.Select(f => $"{f.Label,9} {"/est-c",6}")));

        for (var c = 0; c < channels; c++)
        {
            if (await CommonSquareAsync(pair, c, ct) is not { } r)
            {
                output.WriteLine($"{c,2} (no common covered square of at least {MinSide} px; skipped)");
                continue;
            }

            var truth = Cut(pair.Sharp.Image, c, r.SharpX, r.SharpY, r.Side, r.Side);
            var observed = Cut(pair.Soft.Image, c, r.SoftX, r.SoftY, r.Side, r.Side);
            var (aHfd, _) = await MeasuredFwhmAsync(truth, r.Side, ct);
            var (bHfd, _) = await MeasuredFwhmAsync(observed, r.Side, ct);
            var (fitA, diagA) = await FitStarProfileAsync(truth, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
            var (fitB, diagB) = await FitStarProfileAsync(observed, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
            if (fitA is not { } a || fitB is not { } b)
            {
                output.WriteLine($"{c,2} {aHfd,6:F2} {bHfd,6:F2} {bHfd / aHfd,5:F3} (fit A {(fitA is null ? Describe(diagA) : "ok")}; fit B {(fitB is null ? Describe(diagB) : "ok")}; no est-c)");
                continue;
            }

            var estC = MoffatComposition.DifferenceFwhm(a.Fwhm, a.MoffatBeta, b.Fwhm, SyntheticKernelBeta);
            var frac = PoolKernelFraction * bHfd;
            var floors = PoolFloors.Select(f => MoffatComposition.DifferenceFwhm(f.Fwhm, MoffatComposition.DefaultCoreBeta, bHfd, SyntheticKernelBeta)).ToArray();
            output.WriteLine($"{c,2} {aHfd,6:F2} {bHfd,6:F2} {bHfd / aHfd,5:F3} {a.Fwhm,6:F2} {b.Fwhm,6:F2} {b.Fwhm / a.Fwhm,5:F3} {estC,6:F2} {frac,6:F2} {frac / estC,6:F2} "
                + string.Join(" ", floors.Select(k => $"{k,9:F2} {k / estC,6:F2}")));
        }

        output.WriteLine("");
        output.WriteLine("read: a rule's kernel over est-c near 1.0 lands the pair's answer; the plan's E7.1 sweep says how far from 1.0 the prior tolerates. NaN from a floor means the frame is already sharper than that floor.");
    }

    private const string WindowVar = "TIANWEN_E210_WINDOW";
    private const int DefaultWindow = 512;
    /// <summary>Two detections this close are the same star. The masters share a reference frame, so
    /// the residual is registration, not pointing.</summary>
    private const double MatchTolerancePx = 2.0;

    /// <summary>Every window of <paramref name="side"/> px that both masters cover, tiled left to right
    /// and top to bottom over the common region with no overlap, the last column and row flush against
    /// its far edge so the frame's own corners are read rather than dropped.</summary>
    private static IEnumerable<Region> TileCommonRegion(Pair pair, int channel, int side)
    {
        var a = pair.Sharp;
        var b = pair.Soft;
        var left = Math.Max(a.OriginX, b.OriginX);
        var top = Math.Max(a.OriginY, b.OriginY);
        var right = Math.Min(a.OriginX + a.Width, b.OriginX + b.Width);
        var bottom = Math.Min(a.OriginY + a.Height, b.OriginY + b.Height);
        if (right - left < side || bottom - top < side)
        {
            yield break;
        }

        var xs = new List<int>();
        for (var x = left; x + side <= right; x += side)
        {
            xs.Add(x);
        }

        if (xs[^1] + side < right)
        {
            xs.Add(right - side);
        }

        var ys = new List<int>();
        for (var y = top; y + side <= bottom; y += side)
        {
            ys.Add(y);
        }

        if (ys[^1] + side < bottom)
        {
            ys.Add(bottom - side);
        }

        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                var region = new Region(x - a.OriginX, y - a.OriginY, x - b.OriginX, y - b.OriginY, side, 0);
                if (UncoveredFraction(a.Image, channel, region.SharpX, region.SharpY, side) <= MaxUncoveredFraction
                    && UncoveredFraction(b.Image, channel, region.SoftX, region.SoftY, side) <= MaxUncoveredFraction)
                {
                    yield return region;
                }
            }
        }
    }

    /// <summary>
    /// The two frames' widths over the stars they BOTH have, matched by centroid inside
    /// <paramref name="tolPx"/>. A per-frame median is over each frame's OWN detections, so it moves
    /// when the two frames detect to different depths: on one Orion window the soft crop offered 68
    /// percent MORE detections than the sharp one and its median read wider, which is a brightness
    /// selection and not a blur. Matching first removes that, and the match count says how much of
    /// each frame took part.
    /// </summary>
    private static (double RatioHfd, double MedianA, double MedianB, int Matched, double MedianSnr) MatchedWidths(
        StarList a, StarList b, double tolPx)
    {
        var bs = b.ToList();
        var used = new bool[bs.Count];
        var wa = new List<double>();
        var wb = new List<double>();
        var snr = new List<double>();
        foreach (var star in a)
        {
            var best = -1;
            var bestD = tolPx * tolPx;
            for (var i = 0; i < bs.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var dx = bs[i].XCentroid - star.XCentroid;
                var dy = bs[i].YCentroid - star.YCentroid;
                var d = (dx * dx) + (dy * dy);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                wa.Add(star.HFD);
                wb.Add(bs[best].HFD);
                snr.Add(Math.Min(star.SNR, bs[best].SNR));
            }
        }

        if (wa.Count == 0)
        {
            return (double.NaN, double.NaN, double.NaN, 0, double.NaN);
        }

        var ma = Median(wa);
        var mb = Median(wb);
        return (mb / ma, ma, mb, wa.Count, Median(snr));
    }

    /// <summary>
    /// E7.5's finding, taken back to the pair it came from: <b>which third is the sharp one is decided
    /// FRAME-WIDE and does not have to hold across the frame.</b> E2.10b and E2.10c each read one square
    /// per channel, chosen for its star count, and reported one B/A for the pair; E7.5 then measured the
    /// same two pairs over twelve windows and found the ratio running 0.96 to 1.22 on the Statue and
    /// reversing to 0.81 in an Orion corner, so a pair number is an average over a field that can change
    /// sign. This tiles the whole common region and reports B/A per window per channel, in the SAME
    /// statistics the pair probe uses (the deployed estimator's HFD-based FWHM, and PsfProfileFit's core
    /// width), so the two readings are comparable rather than merely consistent. No oracle: the question
    /// is which frame is sharper WHERE, and every recovery number downstream inherits the answer.
    /// </summary>
    [Fact]
    public async Task ReportHowTheSplitVariesAcrossTheField()
    {
        using var pair = Load(out var skip);
        Assert.SkipWhen(pair is null, skip);
        var ct = TestContext.Current.CancellationToken;
        var channels = pair!.Sharp.Image.Shape.ChannelCount;
        var side = int.TryParse(Environment.GetEnvironmentVariable(WindowVar), out var w) && w >= 128 ? w : DefaultWindow;

        output.WriteLine($"sharp     {pair.Sharp.Path} ({pair.Sharp.Width} x {pair.Sharp.Height}, origin {pair.Sharp.OriginX}, {pair.Sharp.OriginY})");
        output.WriteLine($"soft      {pair.Soft.Path} ({pair.Soft.Width} x {pair.Soft.Height}, origin {pair.Soft.OriginX}, {pair.Soft.OriginY})");
        output.WriteLine($"windows   {side} px, tiled over the region both masters cover, the far column and row flush against its edge; "
            + $"a window over {MaxUncoveredFraction:P0} uncovered in either master is dropped ({WindowVar} sets the side)");
        output.WriteLine($"widths    hfd: the deployed estimator's median FWHM over each frame's OWN detections (what E2.10b/c's A and B columns are); "
            + $"m B/A: the median HFD ratio over the stars BOTH frames detect, matched inside {MatchTolerancePx} px, which is the "
            + $"apples-to-apples one; fit: PsfProfileFit core width, the estimator the kernel comes from");
        output.WriteLine("");

        for (var c = 0; c < channels; c++)
        {
            var regions = TileCommonRegion(pair, c, side).ToList();
            if (regions.Count == 0)
            {
                output.WriteLine($"{c,2} (no covered window of {side} px; skipped)");
                continue;
            }

            output.WriteLine($"ch {c}: {regions.Count} windows");
            output.WriteLine($"   {"sharp x,y",12} {"A hfd",6} {"B hfd",6} {"B/A",6} {"A n",5} {"B n",5} "
                + $"{"m B/A",6} {"m n",5} {"m snr",6} {"fit A",6} {"fit B",6} {"fit B/A",7} {"est-c",6}");
            var ratios = new List<double>();
            var matchedRatios = new List<double>();
            var fitRatios = new List<double>();
            foreach (var r in regions)
            {
                var truth = Cut(pair.Sharp.Image, c, r.SharpX, r.SharpY, r.Side, r.Side);
                var observed = Cut(pair.Soft.Image, c, r.SoftX, r.SoftY, r.Side, r.Side);
                var (aHfd, aStars) = await MeasuredFwhmAsync(truth, r.Side, ct);
                var (bHfd, bStars) = await MeasuredFwhmAsync(observed, r.Side, ct);
                var (fitA, _) = await FitStarProfileAsync(truth, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
                var (fitB, _) = await FitStarProfileAsync(observed, r.Side, r.Side, PsfProfileFit.StarSelection.SignalFloor, EstimatorSnrMin, EstimatorMaxStars, ct);
                var ratio = bHfd / aHfd;
                if (double.IsFinite(ratio))
                {
                    ratios.Add(ratio);
                }

                var truthWrap = Wrap(truth, r.Side, r.Side);
                var softWrap = Wrap(observed, r.Side, r.Side);
                (double RatioHfd, double MedianA, double MedianB, int Matched, double MedianSnr) matched;
                try
                {
                    var starsA = await truthWrap.FindStarsAsync(channel: 0, snrMin: EstimatorSnrMin, maxStars: EstimatorMaxStars, cancellationToken: ct);
                    var starsB = await softWrap.FindStarsAsync(channel: 0, snrMin: EstimatorSnrMin, maxStars: EstimatorMaxStars, cancellationToken: ct);
                    matched = MatchedWidths(starsA, starsB, MatchTolerancePx);
                }
                finally
                {
                    truthWrap.Release();
                    softWrap.Release();
                }

                if (double.IsFinite(matched.RatioHfd))
                {
                    matchedRatios.Add(matched.RatioHfd);
                }

                var fitText = "     .      .       .      .";
                if (fitA is { } fa && fitB is { } fb)
                {
                    var estC = MoffatComposition.DifferenceFwhm(fa.Fwhm, fa.MoffatBeta, fb.Fwhm, SyntheticKernelBeta);
                    fitRatios.Add(fb.Fwhm / fa.Fwhm);
                    fitText = $"{fa.Fwhm,6:F2} {fb.Fwhm,6:F2} {fb.Fwhm / fa.Fwhm,7:F3} {estC,6:F2}";
                }

                output.WriteLine($"   {$"{r.SharpX},{r.SharpY}",12} {aHfd,6:F2} {bHfd,6:F2} {ratio,6:F3} {aStars,5} {bStars,5} "
                    + $"{matched.RatioHfd,6:F3} {matched.Matched,5} {matched.MedianSnr,6:F0} {fitText}"
                    + (matched.RatioHfd < 1.0 ? "   INVERTED" : ratio < 1.0 ? "   (own-star only)" : ""));
            }

            if (ratios.Count > 0)
            {
                ratios.Sort();
                output.WriteLine($"ch {c}: own-star B/A over {ratios.Count} windows {ratios[0]:F3} to {ratios[^1]:F3}, "
                    + $"median {ratios[ratios.Count / 2]:F3}; {ratios.Count(v => v < 1.0)} under 1");
            }

            if (matchedRatios.Count > 0)
            {
                matchedRatios.Sort();
                var inverted = matchedRatios.Count(v => v < 1.0);
                output.WriteLine($"ch {c}: MATCHED-star B/A over {matchedRatios.Count} windows {matchedRatios[0]:F3} to "
                    + $"{matchedRatios[^1]:F3}, median {matchedRatios[matchedRatios.Count / 2]:F3}; "
                    + $"{inverted} window(s) INVERTED (the soft master is the sharper one there)");
            }

            if (fitRatios.Count > 0)
            {
                fitRatios.Sort();
                output.WriteLine($"ch {c}: fitted B/A over {fitRatios.Count} windows {fitRatios[0]:F3} to {fitRatios[^1]:F3}, "
                    + $"median {fitRatios[fitRatios.Count / 2]:F3}; {fitRatios.Count(v => v < 1.0)} inverted");
            }

            output.WriteLine("");
        }

        output.WriteLine("read: one B/A for a pair is an AVERAGE over this field. A window at or under 1.000 has no blur to remove, "
            + "and a deconvolution there can only invent; a pair whose windows straddle 1.0 cannot be scored, trained on or "
            + "validated against as one number.");
    }

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

        // The soft crop with every channel, normalised to a peak of 1 across the channels: a TianWen
        // master is unit-referred by convention with star peaks well over 1 (49 on this pair), and the
        // deconvolver's range check reads the peak.
        var cuts = new float[channels][];
        var max = 0f;
        for (var c = 0; c < channels; c++)
        {
            cuts[c] = Cut(pair.Soft.Image, c, r.SoftX, r.SoftY, r.Side, r.Side);
            foreach (var v in cuts[c])
            {
                if (v > max) max = v;
            }
        }

        var inv = max > 0f ? 1f / max : 1f;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            planes[c] = new float[r.Side, r.Side];
            for (var y = 0; y < r.Side; y++)
            {
                for (var x = 0; x < r.Side; x++)
                {
                    planes[c][y, x] = cuts[c][(y * r.Side) + x] * inv;
                }
            }
        }

        var unit = new Image(planes, BitDepth.Float32, 1f, 0f, 0f,
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
