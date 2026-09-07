using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// <c>soft/master_*.fits</c> (the FULL masters, not the autocrops: the two crops differ, the canvases do
/// not). The shipped SAS graph's arm needs the GPU and runs only under <c>TIANWEN_E210_SAS=1</c>.
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

    private sealed record Pair(Image Sharp, Image Soft, string SharpPath, string SoftPath) : IDisposable
    {
        public void Dispose()
        {
            Sharp.Release();
            Soft.Release();
        }
    }

    private static string? FindMaster(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "master_*.fits")
                .Where(p => !p.Contains("_autocrop", StringComparison.OrdinalIgnoreCase)
                         && !p.Contains(".rejection", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;

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

        if (!Image.TryReadFitsFile(sharpPath, out var sharp) || sharp is null)
        {
            skip = $"unreadable {sharpPath}";
            return null;
        }

        if (!Image.TryReadFitsFile(softPath, out var soft) || soft is null)
        {
            sharp.Release();
            skip = $"unreadable {softPath}";
            return null;
        }

        if (sharp.Shape != soft.Shape)
        {
            var shapes = $"{sharp.Shape} against {soft.Shape}";
            sharp.Release();
            soft.Release();
            skip = $"the two masters are not on one canvas ({shapes}); stack both from the same manifest";
            return null;
        }

        skip = string.Empty;
        return new Pair(sharp, soft, sharpPath, softPath);
    }

    /// <summary>
    /// A centred square inside the region BOTH masters cover (finite and non-zero in each), capped for
    /// the serial oracle. A same-reference pair covers nearly the whole canvas, so the square sits well
    /// inside the two footprints' intersection.
    /// </summary>
    private static (int X0, int Y0, int Side)? CommonSquare(Image a, Image b, int channel)
    {
        var (_, width, height) = a.Shape;
        var sa = a.GetChannelSpan(channel);
        var sb = b.GetChannelSpan(channel);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var va = sa[i];
                var vb = sb[i];
                if (float.IsFinite(va) && float.IsFinite(vb) && va != 0f && vb != 0f)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        var boxW = maxX - minX + 1;
        var boxH = maxY - minY + 1;
        var side = Math.Min(MaxSide, Math.Min(boxW, boxH));
        return (minX + ((boxW - side) / 2), minY + ((boxH - side) / 2), side);
    }

    [Fact]
    public async Task ReportWhatTheOracleRecoversOnARealSeeingSplit()
    {
        using var pair = Load(out var skip);
        Assert.SkipWhen(pair is null, skip);
        var ct = TestContext.Current.CancellationToken;
        var (channels, width, height) = pair!.Sharp.Shape;

        output.WriteLine($"sharp     {pair.SharpPath}");
        output.WriteLine($"soft      {pair.SoftPath}");
        output.WriteLine($"canvas    {width} x {height}, {channels} channel(s); crop: a centred square of the common covered region, at most {MaxSide} px");
        output.WriteLine($"estimator PsfProfileFit on each crop's own detections (snr >= {EstimatorSnrMin}, <= {EstimatorMaxStars} stars, SignalFloor); "
            + $"kernel width by Moffat composition; est-c takes beta {SyntheticKernelBeta}, est-cb the soft frame's fitted beta");
        output.WriteLine($"oracle    Richardson-Lucy, {Iterations} iterations, read at {string.Join(", ", Checkpoints)}; widths are the deployed estimator's median FWHM in px");
        output.WriteLine("");
        output.WriteLine($"{"ch",2} {"arm",-7} {"kernel",7} {"beta",5} {"iter",4} {"rec",6} {"rec/A",6} {"recov%",7} {"stars",6} {"vs A",6} {"ring",6} {"null",6} {"excess",7}");

        for (var c = 0; c < channels; c++)
        {
            var region = CommonSquare(pair.Sharp, pair.Soft, c);
            if (region is not { } r || r.Side < MinSide)
            {
                output.WriteLine($"{c,2} (common covered region under {MinSide} px; skipped)");
                continue;
            }

            var truth = Cut(pair.Sharp, c, r.X0, r.Y0, r.Side, r.Side);
            var observed = Cut(pair.Soft, c, r.X0, r.Y0, r.Side, r.Side);
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

            output.WriteLine($"{c,2} crop {r.Side} px at ({r.X0}, {r.Y0}); A (sharp) {truthFwhm:F2} px over {truthStars} stars, B (soft) {blurFwhm:F2} px over {blurStars}, "
                + $"B/A {blurFwhm / truthFwhm:F3}; {stars.Count} truth stars at snr 20; B's own ring null {nullRing:F2} MAD, {nullOver:P0} over one");
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
        var (channels, _, _) = pair!.Sharp.Shape;
        var region = CommonSquare(pair.Sharp, pair.Soft, 0);
        Assert.SkipWhen(region is not { } || region.Value.Side < MinSide, "common covered region too small");
        var (x0, y0, side) = region!.Value;

        // The soft crop with every channel, in the deconvolver's unit range (the rescale REWRAPS; use its result).
        var planes = new float[channels][,];
        var max = 0f;
        for (var c = 0; c < channels; c++)
        {
            var cut = Cut(pair.Soft, c, x0, y0, side, side);
            planes[c] = new float[side, side];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var v = cut[(y * side) + x];
                    planes[c][y, x] = v;
                    if (v > max) max = v;
                }
            }
        }

        var input = new Image(planes, BitDepth.Float32, max <= 0f ? 1f : max, 0f, 0f,
            new ImageMeta { SensorType = channels == 1 ? SensorType.Monochrome : SensorType.Color });
        var unit = input.ScaleFloatValuesToUnitInPlace();
        using var deconvolver = new OnnxNonStellarDeconvolver(resolver, new HfdPsfEstimator(), chunkSize: 256, overlap: 64);

        output.WriteLine($"sharp     {pair.SharpPath}");
        output.WriteLine($"soft      {pair.SoftPath}");
        output.WriteLine($"graph     {OnnxNonStellarDeconvolver.Model}, whole-image psf01 over the shipped range; crop {side} px at ({x0}, {y0})");
        output.WriteLine("");
        output.WriteLine($"{"ch",2} {"A fwhm",6} {"A n",5} {"B fwhm",6} {"B n",5} {"B/A",6} {"out",6} {"out/A",6} {"recov%",7} {"out n",6} {"vs A",6} {"ring",6} {"null",6} {"excess",7} {"s",5}");

        var started = DateTime.UtcNow;
        var result = await deconvolver.EnhanceAsync(unit, ct);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        try
        {
            for (var c = 0; c < channels; c++)
            {
                var truth = Cut(pair.Sharp, c, x0, y0, side, side);
                var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, side, ct);
                StarList stars;
                var truthImage = Wrap(truth, side, side);
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
                var (blurFwhm, blurStars) = await MeasuredFwhmAsync(observedUnit, side, ct);
                var (nullRing, nullOver) = Ringing(observedUnit, side, stars, bgIn, madIn);
                var outPlane = FullPlane(result, c);
                var (bgOut, madOut) = BackgroundStats(outPlane);
                var (outFwhm, outStars) = await MeasuredFwhmAsync(outPlane, side, ct);
                var (ring, overOne) = Ringing(outPlane, side, stars, bgOut, madOut);
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
