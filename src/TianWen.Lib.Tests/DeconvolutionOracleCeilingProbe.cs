using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Deconvolution;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// P2 / H1: the ORACLE ceiling. Blur a real master by a known Moffat, hand Richardson-Lucy that exact
/// kernel, and record how much of the injected blur comes back and what ringing it costs. This is the
/// number every trained arm is scored against, and an arm that appears to beat it is fabricating
/// detail rather than recovering it, because the oracle is given the one thing inference never has.
/// </summary>
/// <remarks>
/// <para>Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir holding
/// <c>session-masters/</c>. <c>TIANWEN_ORACLE_MASTERS</c> caps the master count (default 6) and
/// <c>TIANWEN_ORACLE_ITERS</c> the RL iterations (default 30).</para>
///
/// <para><b>Both arms run, and the contrast is the result.</b> Noise-free is the absolute ceiling of
/// the inverse problem; blur-then-noise at the frame's own depth is the ceiling of the problem the
/// model is actually given, since <c>DatasetDegradationExporter</c>'s blur mode adds noise after the
/// blur. Reporting only the first would overstate what any net could reach; reporting only the second
/// would leave the cost of the noise unattributed.</para>
///
/// <para><b>Ringing is measured where it happens, AGAINST ITS OWN NULL.</b> Deconvolution's artefact is
/// an undershoot in the annulus just outside a bright star, so the statistic is the deepest excursion
/// below the local background there, in units of the clean frame's background MAD. A whole-frame
/// residual cannot see it: the annulus is a few percent of the pixels and a global statistic averages
/// it away. But the minimum of about a hundred noise samples sits roughly 2.5 sigma below the mean
/// BEFORE anything is deconvolved, so the raw statistic reads "ringing" on an untouched frame, and did:
/// 1.0 MAD on half the stars of a 0.5 px blur, which is a 0.5 px blur's way of saying nothing happened.
/// The same statistic is therefore measured on the TRUTH crop and reported beside every arm, and only
/// the EXCESS over that baseline is deconvolution's doing.</para>
///
/// <para><b>The star count travels with the recovery number, because the failure mode is
/// fabrication.</b> Richardson-Lucy on noisy data sharpens noise into point sources and a detector
/// reads those as narrow stars, so a recovery figure can pass 100 percent by inventing a population
/// rather than restoring one. A row whose recovery exceeds 100 percent while its star count climbs is
/// exactly that, which is why the count is in the table and not in a footnote.</para>
///
/// <para><b>The stars are found once, on the truth.</b> Detecting on each deconvolved frame would
/// change the population per arm, so a ringing count would partly measure which stars survived
/// detection. Positions come from the clean crop and every arm is sampled at those same positions.</para>
/// </remarks>
public class DeconvolutionOracleCeilingProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string MastersVar = "TIANWEN_ORACLE_MASTERS";
    private const string ItersVar = "TIANWEN_ORACLE_ITERS";

    /// <summary>Crop side in pixels. Large enough to hold a few hundred stars and small enough that a
    /// serial convolution over the whole sweep finishes; taken from the frame centre, which is also
    /// where the canvas ring of a stacked master cannot reach.</summary>
    private const int Crop = 512;

    /// <summary>Added FWHM in pixels, composing in quadrature with whatever the master already has.
    /// Spans the exporter's own draw range (0.5 to 4.0 px).</summary>
    private static readonly double[] InjectedFwhm = [0.5, 1.0, 2.0, 3.0, 4.0];

    /// <summary>Fixed so the sweep has ONE axis. The (FWHM, beta) relation is E0's business and varying
    /// both here would leave a recovery difference unattributable to either.</summary>
    private const double Beta = 4.0;

    private static float Median(List<float> v)
    {
        if (v.Count == 0) return float.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>Background MAD of a plane: median of |v - median|, which stars are too sparse to move.</summary>
    private static (float Median, float Mad) BackgroundStats(float[] plane)
    {
        var copy = new List<float>(plane.Length);
        foreach (var v in plane)
        {
            if (float.IsFinite(v)) copy.Add(v);
        }

        var med = Median(copy);
        var dev = new List<float>(copy.Count);
        foreach (var v in copy)
        {
            dev.Add(Math.Abs(v - med));
        }

        return (med, Median(dev));
    }

    private static float[] CropCentre(Image image, int channel, int side)
    {
        var (_, width, height) = image.Shape;
        var x0 = (width - side) / 2;
        var y0 = (height - side) / 2;
        var src = image.GetChannelSpan(channel);
        var plane = new float[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var v = src[((y0 + y) * width) + x0 + x];
                plane[(y * side) + x] = float.IsFinite(v) ? v : 0f;
            }
        }

        return plane;
    }

    private static Image Wrap(float[] plane, int side)
    {
        var data = new float[side, side];
        var max = 0f;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var v = plane[(y * side) + x];
                data[y, x] = v;
                if (v > max) max = v;
            }
        }

        return new Image([data], BitDepth.Float32, max <= 0f ? 1f : max, 0f, 0f, new ImageMeta());
    }

    /// <summary>
    /// Median FWHM the deployed estimator reads off a plane, or NaN when it found nothing. Deliberately
    /// the estimator and not a profile fit, so this table and H5's are in the same units.
    /// </summary>
    private static async Task<(float Fwhm, int Stars)> MeasuredFwhmAsync(float[] plane, int side, System.Threading.CancellationToken ct)
    {
        var image = Wrap(plane, side);
        try
        {
            var m = await new HfdPsfEstimator().MeasureRadiusPxAsync(image, ct);
            return (m.Stars == 0 ? float.NaN : m.RadiusPx * 2f, m.Stars);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>
    /// The deepest undershoot below local background in the annulus around each star, in MAD units, and
    /// the fraction of stars past 1 MAD.
    /// </summary>
    private static (float MedianUndershoot, double FractionOverOneMad) Ringing(
        float[] plane, int side, IReadOnlyCollection<ImagedStar> stars, float background, float mad)
    {
        if (mad <= 0f || stars.Count == 0)
        {
            return (float.NaN, double.NaN);
        }

        var depths = new List<float>(stars.Count);
        var over = 0;
        foreach (var star in stars)
        {
            var inner = Math.Max(2.0, star.StarFWHM * 1.2);
            var outer = Math.Max(inner + 2.0, star.StarFWHM * 2.5);
            var r = (int)Math.Ceiling(outer);
            var cx = (int)Math.Round(star.XCentroid);
            var cy = (int)Math.Round(star.YCentroid);
            if (cx - r < 0 || cy - r < 0 || cx + r >= side || cy + r >= side)
            {
                continue;
            }

            var min = float.PositiveInfinity;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    var d = Math.Sqrt((dx * dx) + (dy * dy));
                    if (d < inner || d > outer)
                    {
                        continue;
                    }

                    var v = plane[((cy + dy) * side) + cx + dx];
                    if (v < min) min = v;
                }
            }

            if (!float.IsFinite(min))
            {
                continue;
            }

            var depth = (background - min) / mad;
            depths.Add(depth);
            if (depth > 1f) over++;
        }

        return depths.Count == 0
            ? (float.NaN, double.NaN)
            : (Median(depths), (double)over / depths.Count);
    }

    /// <summary>Iteration counts the sweep below reads off ONE run each, via the checkpoint handler.</summary>
    private static readonly int[] IterationCheckpoints = [5, 10, 20, 30, 60, 120];

    /// <summary>
    /// The ceiling table fixes Richardson-Lucy at 30 iterations, and on noisy data the iteration count
    /// IS the regularisation parameter, so that number is a free parameter the ceiling silently depends
    /// on. This walks it and reports where each blur regime's residual stops improving and its ringing
    /// starts, which is the only thing that says whether 30 was a reasonable place to stand.
    /// </summary>
    /// <remarks>
    /// One RL run per (master, channel, blur, noise), checkpointed: the iteration is a trajectory, so
    /// re-running it once per candidate count would repeat every earlier iteration and cost about five
    /// times as much for the same numbers.
    /// </remarks>
    [Fact]
    public async Task ReportWhereTheIterationCountStopsHelping()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");

        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var ct = TestContext.Current.CancellationToken;
        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable(MastersVar), out var mm) ? mm : 3;
        var all = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.SkipWhen(all.Length == 0, "no masters");

        var step = Math.Max(1, all.Length / Math.Max(1, maxMasters));
        var masters = all.Where((_, i) => i % step == 0).Take(maxMasters).ToArray();

        // The regime where iterations can matter: a 0.5 px blur is recovered at any count.
        double[] injected = [2.0, 3.0];
        var maxIterations = IterationCheckpoints[^1];

        output.WriteLine($"masters   {masters.Length} of {all.Length}; injected {string.Join(", ", injected)} px; "
            + $"checkpoints {string.Join(", ", IterationCheckpoints)} read off one {maxIterations}-iteration run each");
        output.WriteLine("");

        var residual = new Dictionary<(int Iter, bool Noisy), List<double>>();
        var excessBy = new Dictionary<(int Iter, bool Noisy), List<double>>();
        var starBy = new Dictionary<(int Iter, bool Noisy), List<double>>();

        foreach (var masterPath in masters)
        {
            var name = Path.GetFileNameWithoutExtension(masterPath);
            if (!Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                continue;
            }

            int channels;
            var crops = new List<float[]>();
            try
            {
                var (chan, width, height) = master.Shape;
                if (width < Crop || height < Crop)
                {
                    continue;
                }

                channels = Math.Min(3, chan);
                for (var c = 0; c < channels; c++)
                {
                    crops.Add(CropCentre(master, c, Crop));
                }
            }
            finally
            {
                master.Release();
            }

            for (var c = 0; c < channels; c++)
            {
                var truth = crops[c];
                var (bg, mad) = BackgroundStats(truth);
                if (!(mad > 0f))
                {
                    continue;
                }

                var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, Crop, ct);
                var truthImage = Wrap(truth, Crop);
                StarList stars;
                try
                {
                    stars = await truthImage.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct);
                }
                finally
                {
                    truthImage.Release();
                }

                if (!float.IsFinite(truthFwhm) || stars.Count < 20)
                {
                    continue;
                }

                foreach (var inj in injected)
                {
                    var psf = PsfKernel.Moffat(inj, Beta);
                    var blurred = psf.Convolve(truth, Crop, Crop);

                    foreach (var noisy in new[] { false, true })
                    {
                        var observed = blurred;
                        if (noisy)
                        {
                            var rng = new Random(HashCode.Combine(name, c, inj));
                            observed = new float[blurred.Length];
                            for (var i = 0; i < blurred.Length; i++)
                            {
                                var u1 = 1.0 - rng.NextDouble();
                                var u2 = rng.NextDouble();
                                var g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                                observed[i] = (float)(blurred[i] + (g * mad));
                            }
                        }

                        var (_, nullOver) = Ringing(observed, Crop, stars, bg, mad);
                        var snapshots = new Dictionary<int, float[]>();
                        RichardsonLucy.Deconvolve(observed, Crop, Crop, psf, maxIterations, (iter, est) =>
                        {
                            if (Array.IndexOf(IterationCheckpoints, iter) >= 0)
                            {
                                snapshots[iter] = est.ToArray();
                            }
                        });

                        foreach (var iter in IterationCheckpoints)
                        {
                            if (!snapshots.TryGetValue(iter, out var est))
                            {
                                continue;
                            }

                            var (recFwhm, recStars) = await MeasuredFwhmAsync(est, Crop, ct);
                            var (_, overOne) = Ringing(est, Crop, stars, bg, mad);
                            var key = (iter, noisy);
                            if (float.IsFinite(recFwhm))
                            {
                                (residual.TryGetValue(key, out var rl) ? rl : residual[key] = []).Add(recFwhm - truthFwhm);
                            }

                            if (double.IsFinite(overOne) && double.IsFinite(nullOver))
                            {
                                (excessBy.TryGetValue(key, out var el) ? el : excessBy[key] = []).Add(overOne - nullOver);
                            }

                            if (truthStars > 0)
                            {
                                (starBy.TryGetValue(key, out var sl) ? sl : starBy[key] = []).Add((double)recStars / truthStars);
                            }
                        }
                    }
                }
            }
        }

        Assert.SkipWhen(residual.Count == 0, "nothing measured");

        output.WriteLine($"{"iterations",11} {"noise",6} {"n",4} {"residual px p50",16} {"ring excess p50",16} {"stars kept p50",15}");
        foreach (var iter in IterationCheckpoints)
        {
            foreach (var noisy in new[] { false, true })
            {
                var key = (iter, noisy);
                if (!residual.TryGetValue(key, out var res) || res.Count == 0)
                {
                    continue;
                }

                res.Sort();
                var ex = excessBy.TryGetValue(key, out var e) ? e : [];
                ex.Sort();
                var st = starBy.TryGetValue(key, out var s) ? s : [];
                st.Sort();
                output.WriteLine($"{iter,11} {(noisy ? "yes" : "no"),6} {res.Count,4} {res[res.Count / 2],16:F2} "
                    + $"{(ex.Count == 0 ? double.NaN : ex[ex.Count / 2]).ToString("P0", CultureInfo.InvariantCulture),16} "
                    + $"{(st.Count == 0 ? double.NaN : st[st.Count / 2]),15:F2}");
            }
        }

        output.WriteLine("");
        output.WriteLine("Richardson-Lucy does not converge on noisy data, it fits the noise, so the residual");
        output.WriteLine("and the ringing move in opposite directions and the useful count is where the first");
        output.WriteLine("stops improving rather than where the second becomes tolerable.");
    }

    [Fact]
    public async Task ReportHowMuchOfAKnownBlurAnOracleRecovers()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");

        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var ct = TestContext.Current.CancellationToken;
        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable(MastersVar), out var mm) ? mm : 6;
        var iterations = int.TryParse(Environment.GetEnvironmentVariable(ItersVar), out var it) ? it : 30;

        var all = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.SkipWhen(all.Length == 0, "no masters");

        // Spread the sample across the archive rather than taking the first N, which would take one
        // camera's alphabetical block and report its optics as the ceiling.
        var step = Math.Max(1, all.Length / Math.Max(1, maxMasters));
        var masters = all.Where((_, i) => i % step == 0).Take(maxMasters).ToArray();

        output.WriteLine($"masters   {masters.Length} of {all.Length} (every {step}th, cap {MastersVar}={maxMasters})");
        output.WriteLine($"crop      {Crop}x{Crop} from the frame centre; RL {iterations} iterations with the EXACT kernel; Moffat beta {Beta}");
        output.WriteLine($"psf01     encoded over [0.5, 4.0] px radius, E1's pick");
        output.WriteLine("");
        output.WriteLine($"{"master",-30} {"ch",2} {"inj",5} {"psf01",6} {"truth",6} {"blur",6} {"rec",6} {"recov%",7} "
            + $"{"ring",6} {"null",6} {"excess",7} {"stars",6} {"vs truth",9} {"noise",5}");

        // Keyed by psf01 BIN, which is the axis H1 asks for and the one that handles a frame whose own
        // FWHM is already 7 px: injecting 1 px there is a 1.4 percent change and does not belong in the
        // same bucket as injecting 1 px into a 1.5 px frame.
        var recByBin = new Dictionary<(int Bin, bool Noisy), List<double>>();
        var excessByBin = new Dictionary<(int Bin, bool Noisy), List<double>>();
        var starRatioByBin = new Dictionary<(int Bin, bool Noisy), List<double>>();

        foreach (var masterPath in masters)
        {
            var name = Path.GetFileNameWithoutExtension(masterPath);
            var shortName = name[..Math.Min(34, name.Length)];

            if (!Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-34} (unreadable)");
                continue;
            }

            int channels;
            var crops = new List<float[]>();
            try
            {
                var (chan, width, height) = master.Shape;
                if (width < Crop || height < Crop)
                {
                    output.WriteLine($"{shortName,-34} (smaller than the crop: {width}x{height})");
                    continue;
                }

                channels = Math.Min(3, chan);
                for (var c = 0; c < channels; c++)
                {
                    crops.Add(CropCentre(master, c, Crop));
                }
            }
            finally
            {
                master.Release();
            }

            for (var c = 0; c < channels; c++)
            {
                var truth = crops[c];
                var (bg, mad) = BackgroundStats(truth);
                if (!(mad > 0f))
                {
                    output.WriteLine($"{shortName,-34} {c,2} (flat crop, no MAD)");
                    continue;
                }

                var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, Crop, ct);
                var truthImage = Wrap(truth, Crop);
                StarList stars;
                try
                {
                    stars = await truthImage.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct);
                }
                finally
                {
                    truthImage.Release();
                }

                if (!float.IsFinite(truthFwhm) || stars.Count < 20)
                {
                    output.WriteLine($"{shortName,-30} {c,2} (only {stars.Count} stars; skipped)");
                    continue;
                }

                foreach (var injected in InjectedFwhm)
                {
                    var psf = PsfKernel.Moffat(injected, Beta);
                    var blurred = psf.Convolve(truth, Crop, Crop);

                    foreach (var noisy in new[] { false, true })
                    {
                        var observed = blurred;
                        if (noisy)
                        {
                            // The frame's OWN background MAD as the added sigma: the exporter adds noise
                            // after the blur at the master's depth, so the oracle must face the same.
                            // Seeded per (master, channel, injected) so the table is reproducible.
                            var rng = new Random(HashCode.Combine(name, c, injected));
                            observed = new float[blurred.Length];
                            for (var i = 0; i < blurred.Length; i++)
                            {
                                var u1 = 1.0 - rng.NextDouble();
                                var u2 = rng.NextDouble();
                                var g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                                observed[i] = (float)(blurred[i] + (g * mad));
                            }
                        }

                        var result = RichardsonLucy.Deconvolve(observed, Crop, Crop, psf, iterations);

                        // The null for the ringing statistic, measured on whatever this arm's INPUT is:
                        // noise-free truth for the noise-free arm, the same noise realisation for the
                        // noisy one, so the baseline carries the arm's own noise and the excess is only
                        // what the iteration added.
                        var (nullRing, nullOver) = Ringing(observed, Crop, stars, bg, mad);
                        var (blurFwhm, _) = await MeasuredFwhmAsync(observed, Crop, ct);
                        var (recFwhm, recStars) = await MeasuredFwhmAsync(result.Estimate, Crop, ct);
                        var recovered = float.IsFinite(blurFwhm) && float.IsFinite(recFwhm) && blurFwhm > truthFwhm
                            ? (blurFwhm - recFwhm) / (blurFwhm - truthFwhm)
                            : double.NaN;
                        var (ring, overOne) = Ringing(result.Estimate, Crop, stars, bg, mad);
                        var excess = overOne - nullOver;
                        var starRatio = truthStars > 0 ? (double)recStars / truthStars : double.NaN;
                        var psf01 = HfdPsfEstimator.EncodeRadiusToPsf01(blurFwhm * 0.5f, 0.5f, 4.0f);
                        var bin = float.IsFinite(psf01) ? (int)Math.Clamp(psf01 * 5f, 0, 4) : -1;

                        if (bin >= 0)
                        {
                            var key = (bin, noisy);
                            if (double.IsFinite(recovered))
                            {
                                (recByBin.TryGetValue(key, out var list) ? list : recByBin[key] = []).Add(recovered);
                            }

                            if (double.IsFinite(excess))
                            {
                                (excessByBin.TryGetValue(key, out var el) ? el : excessByBin[key] = []).Add(excess);
                            }

                            if (double.IsFinite(starRatio))
                            {
                                (starRatioByBin.TryGetValue(key, out var sl) ? sl : starRatioByBin[key] = []).Add(starRatio);
                            }
                        }

                        output.WriteLine($"{shortName,-30} {c,2} {injected,5:F1} {psf01,6:F3} {truthFwhm,6:F2} {blurFwhm,6:F2} "
                            + $"{recFwhm,6:F2} {recovered,7:P0} {ring,6:F2} {nullRing,6:F2} {excess,7:P0} {recStars,6} "
                            + $"{starRatio,9:F2} {(noisy ? "yes" : "no"),5}");
                    }
                }
            }
        }

        Assert.SkipWhen(recByBin.Count == 0, "nothing measured");

        output.WriteLine("");
        output.WriteLine($"{"psf01 bin",12} {"noise",6} {"n",4} {"recovered p50",14} {"ring excess p50",16} {"stars vs truth p50",19}");
        for (var bin = 0; bin < 5; bin++)
        {
            foreach (var noisy in new[] { false, true })
            {
                var key = (bin, noisy);
                if (!recByBin.TryGetValue(key, out var rec) || rec.Count == 0)
                {
                    continue;
                }

                rec.Sort();
                var excess = excessByBin.TryGetValue(key, out var e) ? e : [];
                excess.Sort();
                var stars = starRatioByBin.TryGetValue(key, out var s) ? s : [];
                stars.Sort();
                var excessMedian = excess.Count == 0 ? double.NaN : excess[excess.Count / 2];
                var starMedian = stars.Count == 0 ? double.NaN : stars[stars.Count / 2];
                output.WriteLine($"{$"{bin * 0.2:F1}-{(bin + 1) * 0.2:F1}",12} {(noisy ? "yes" : "no"),6} {rec.Count,4} "
                    + $"{rec[rec.Count / 2].ToString("P0", CultureInfo.InvariantCulture),14} "
                    + $"{excessMedian.ToString("P0", CultureInfo.InvariantCulture),16} "
                    + $"{starMedian.ToString("F2", CultureInfo.InvariantCulture),19}");
            }
        }

        output.WriteLine("");
        output.WriteLine("The noise-free rows are the ceiling of the inverse problem; the noisy rows are the");
        output.WriteLine("ceiling of the problem a trained net is actually given. Read the three columns");
        output.WriteLine("together: a recovery over 100 percent whose star count has climbed is fabrication,");
        output.WriteLine("and ring EXCESS is over the same statistic measured on this arm's own input, so a");
        output.WriteLine("row near zero there did not ring however deep its raw annulus minimum looked.");
    }
}
