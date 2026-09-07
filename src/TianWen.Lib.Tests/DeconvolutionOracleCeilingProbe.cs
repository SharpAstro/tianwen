using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
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
/// <c>session-masters/</c>. <c>TIANWEN_ORACLE_MASTERS</c> caps the master count (default 6),
/// <c>TIANWEN_ORACLE_ITERS</c> the RL iterations (default 30; the tabled numbers are 60), and
/// <c>TIANWEN_ORACLE_KERNEL</c> names which kernels the iteration is handed, as a comma list of
/// <c>exact</c>, <c>estimated</c> and <c>estimated-shape</c> (default <c>exact</c>, which is E1).</para>
///
/// <para><b>Both noise arms run, and the contrast is the result.</b> Noise-free is the absolute ceiling
/// of the inverse problem; blur-then-noise at the frame's own depth is the ceiling of the problem the
/// model is actually given, since <c>DatasetDegradationExporter</c>'s blur mode adds noise after the
/// blur. Reporting only the first would overstate what any net could reach; reporting only the second
/// would leave the cost of the noise unattributed.</para>
///
/// <para><b>The ESTIMATED kernels are E1b (H11), and they close the gap the exact arm leaves open.</b>
/// At deployment nothing knows the kernel, but a star is a point, so a frame's star profile IS its
/// PSF, and the question is how much of the exact-kernel ceiling survives when the kernel has to be
/// read off the frame. <see cref="PsfProfileFit"/> is run on the observed crop's own detections (the
/// deployment condition: no truth in hand) and on the clean crop; the difference width is
/// <c>sqrt(obs^2 - clean^2)</c>, exact for Gaussians and an approximation for a Moffat that is part of
/// what is measured. Arm <c>estimated</c> takes that width with the shape (beta) exact; arm
/// <c>estimated-shape</c> takes the observed profile's beta as well. Every listed arm runs on the SAME
/// observed crop, so the comparison against <c>exact</c> is paired row by row, and the estimate's own
/// error (estimated over true width, estimated over true beta) is printed beside the recovery so a
/// ceiling loss can be attributed to the width or to the shape. Where the crop's stars cannot support
/// a fit the estimator falls back to the WHOLE degraded frame and the row says so.</para>
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
///
/// <para><b>The summary bins by BLUR RATIO (blurred over truth), not by psf01.</b> E1 binned by psf01
/// and found it the wrong axis: a frame whose own FWHM is 7 px takes a 1 px injection as a 1.4 percent
/// change and does not belong in the same bucket as a 1.5 px frame taking the same injection, and the
/// recovery FRACTION rises again in the top psf01 bin because its denominator grows with the blur.
/// The plan's tables were re-aggregated by ratio from the rows with a script; the probe now prints
/// that aggregation itself so a re-run needs no second step.</para>
/// </remarks>
public class DeconvolutionOracleCeilingProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string MastersVar = "TIANWEN_ORACLE_MASTERS";
    private const string ItersVar = "TIANWEN_ORACLE_ITERS";
    private const string KernelVar = "TIANWEN_ORACLE_KERNEL";

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

    /// <summary>Detection settings the estimator fits from: <c>DatasetPsfNoiseReport</c>'s, so a profile
    /// fitted here is the same measurement E0 took on every session master.</summary>
    private const float EstimatorSnrMin = 5f;
    private const int EstimatorMaxStars = 3000;

    /// <summary>The narrowest kernel the estimated arms will build. An estimate whose difference width
    /// comes out imaginary (observed no wider than clean, which noise can do at a 0.5 px injection) is
    /// the estimator saying "no blur", and the honest consequence is a near-identity kernel that
    /// recovers nothing, not a skipped row that would drop the failure from the aggregate.</summary>
    private const double MinEstimatedFwhm = 0.1;

    /// <summary>Which kernel Richardson-Lucy is handed.</summary>
    private enum KernelSource
    {
        /// <summary>The Moffat that made the blur. E1's oracle.</summary>
        Exact,

        /// <summary>Width read off the frame, shape exact. E1b arm i.</summary>
        Estimated,

        /// <summary>Width and shape both read off the frame. E1b arm ii.</summary>
        EstimatedShape,

        /// <summary>Width by Moffat COMPOSITION of the two fitted profiles rather than a quadrature of
        /// their FWHMs, shape exact. E1d.</summary>
        EstimatedComposed,
    }

    private static string ArmLabel(KernelSource source) => source switch
    {
        KernelSource.Exact => "exact",
        KernelSource.Estimated => "est-w",
        KernelSource.EstimatedShape => "est-wb",
        KernelSource.EstimatedComposed => "est-c",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static KernelSource[] ParseKernelSources(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [KernelSource.Exact];
        }

        var sources = new List<KernelSource>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var source = token.ToLowerInvariant() switch
            {
                "exact" => KernelSource.Exact,
                "estimated" => KernelSource.Estimated,
                "estimated-shape" => KernelSource.EstimatedShape,
                "estimated-composed" => KernelSource.EstimatedComposed,
                _ => throw new ArgumentException($"{KernelVar}: unknown kernel source '{token}' (exact, estimated, estimated-shape, estimated-composed)"),
            };
            if (!sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        return [.. sources];
    }

    /// <summary>One frame's PSF as the estimator reads it, and where it had to read it.</summary>
    /// <param name="Fwhm">Stacked-profile FWHM in pixels.</param>
    /// <param name="Beta">Moffat exponent of the stacked profile.</param>
    /// <param name="FromWholeFrame">True when the crop could not support a fit and the whole frame was
    /// blurred and fitted instead.</param>
    private readonly record struct ProfileEstimate(double Fwhm, double Beta, bool FromWholeFrame);

    /// <summary>One arm of one row, kept so the summary can pair the estimated arms against the exact one.
    /// A row the estimator REFUSED (no fit on the crop or the whole frame) is kept with
    /// <paramref name="NoEstimate"/> set and every measurement NaN, so the refusal is counted in its
    /// bin instead of silently thinning the aggregate towards the rows the estimator found easy.</summary>
    private sealed record ArmRow(
        string Master,
        int Channel,
        double Injected,
        bool Noisy,
        KernelSource Arm,
        double BlurRatio,
        double RecOverTruth,
        double ResidualPx,
        double RingExcess,
        double StarRatio,
        double EstWidthRatio,
        double EstBetaRatio,
        bool FromWholeFrame,
        bool NoEstimate = false);

    private static readonly string[] RatioBinLabels = ["<1.1x", "1.1-1.3x", "1.3-1.6x", "1.6-2.0x", "2.0-3.0x", "3.0x+"];

    /// <summary>The plan's bands, on blurred over truth.</summary>
    private static int RatioBin(double ratio)
        => ratio < 1.1 ? 0 : ratio < 1.3 ? 1 : ratio < 1.6 ? 2 : ratio < 2.0 ? 3 : ratio < 3.0 ? 4 : 5;

    private static float Median(List<float> v)
    {
        if (v.Count == 0) return float.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    private static double Percentile(List<double> sorted, double p)
        => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

    /// <summary>
    /// A seed that is the same in every process. <c>HashCode.Combine(name, ...)</c> was used before and
    /// is NOT: string hashing is randomised per process in .NET, so two runs drew different noise while
    /// the comment beside it promised a reproducible table. Within one run the arms share the
    /// realisation either way; across runs only this makes a row comparable.
    /// </summary>
    private static int StableSeed(string name, int channel, double injected)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in name)
            {
                h = (h ^ ch) * 16777619u;
            }

            h = (h ^ (uint)channel) * 16777619u;
            h = (h ^ (uint)Math.Round(injected * 100.0)) * 16777619u;
            return (int)h;
        }
    }

    private static float[] AddNoise(float[] plane, float sigma, Random rng)
    {
        var noisy = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            var g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            noisy[i] = (float)(plane[i] + (g * sigma));
        }

        return noisy;
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

    /// <summary>The whole channel, NaN zeroed as <see cref="CropCentre"/> does, for the estimator's
    /// whole-frame fallback. A TianWen master's canvas ring is zero where no frame covered it anyway.</summary>
    private static float[] FullPlane(Image image, int channel)
    {
        var (_, width, height) = image.Shape;
        var src = image.GetChannelSpan(channel);
        var plane = new float[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            var v = src[i];
            plane[i] = float.IsFinite(v) ? v : 0f;
        }

        return plane;
    }

    private static Image Wrap(float[] plane, int width, int height)
    {
        var data = new float[height, width];
        var max = 0f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = plane[(y * width) + x];
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
    private static async Task<(float Fwhm, int Stars)> MeasuredFwhmAsync(float[] plane, int side, CancellationToken ct)
    {
        var image = Wrap(plane, side, side);
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
    /// The frame's PSF shape as <see cref="PsfProfileFit"/> reads it from its OWN detections, which is
    /// what a deployed estimator has: no truth, no star list handed in. The fit is null when the plane
    /// cannot support one, and the diagnostics say which check refused and what it saw.
    /// </summary>
    private static async Task<(PsfProfileFit.Result? Fit, PsfProfileFit.Diagnostics Diagnostics)> FitProfileAsync(
        float[] plane, int width, int height, CancellationToken ct)
    {
        var image = Wrap(plane, width, height);
        try
        {
            var stars = await image.FindStarsAsync(channel: 0, snrMin: EstimatorSnrMin, maxStars: EstimatorMaxStars, cancellationToken: ct);
            var fit = PsfProfileFit.Measure(image, 0, stars, out var diagnostics);
            return (fit, diagnostics);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>One line of refusal detail: the check that fired and the counts it tested.</summary>
    private static string Describe(PsfProfileFit.Diagnostics d)
        => $"{d.Refusal} (stars {d.StarsOffered}, band {d.InBrightnessBand}, stacked {d.Stacked}, bins {d.FitBins}"
            + (double.IsFinite(d.MoffatLogRms) ? $", rms {d.MoffatLogRms:F2}" : "") + ")";

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
                var truthImage = Wrap(truth, Crop, Crop);
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
                        var observed = noisy ? AddNoise(blurred, mad, new Random(StableSeed(name, c, inj))) : blurred;

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
        var arms = ParseKernelSources(Environment.GetEnvironmentVariable(KernelVar));
        var estimating = arms.Any(a => a != KernelSource.Exact);
        var exactIndex = Array.IndexOf(arms, KernelSource.Exact);

        var all = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.SkipWhen(all.Length == 0, "no masters");

        // Spread the sample across the archive rather than taking the first N, which would take one
        // camera's alphabetical block and report its optics as the ceiling.
        var step = Math.Max(1, all.Length / Math.Max(1, maxMasters));
        var masters = all.Where((_, i) => i % step == 0).Take(maxMasters).ToArray();

        output.WriteLine($"masters   {masters.Length} of {all.Length} (every {step}th, cap {MastersVar}={maxMasters})");
        output.WriteLine($"crop      {Crop}x{Crop} from the frame centre; RL {iterations} iterations; injected Moffat beta {Beta}");
        output.WriteLine($"kernels   {string.Join(", ", arms.Select(ArmLabel))} ({KernelVar}); "
            + "exact = the injected Moffat, est-w = width from the frame with beta exact, est-wb = width and beta from the frame");
        if (estimating)
        {
            output.WriteLine($"estimator PsfProfileFit on the frame's own detections (snr >= {EstimatorSnrMin}, <= {EstimatorMaxStars} stars): "
                + "observed crop and clean crop, difference width sqrt(obs^2 - clean^2); 'frame' = crop could not be fitted, whole blurred frame used");
        }

        output.WriteLine($"psf01     encoded over [0.5, 4.0] px radius, E1's pick");
        output.WriteLine("");
        output.WriteLine($"{"master",-30} {"ch",2} {"inj",5} {"noise",5} {"arm",-6} {"psf01",6} {"truth",6} {"blur",6} {"b/t",5} "
            + $"{"rec",6} {"r/t",5} {"recov%",7} {"ring",6} {"null",6} {"excess",7} {"stars",6} {"vs truth",8} "
            + $"{"estW/t",7} {"estB/t",7} {"fit",5}");

        var rows = new List<ArmRow>();
        var noEstimate = 0;
        // Every whole-frame refusal, once per (master, channel, noise, side): the clean side refuses once
        // per channel and the observed side once per row, and the tally below is by check and by master.
        var refusals = new List<(string Master, int Channel, bool Noisy, string Side, PsfProfileFit.Refusal Refusal)>();

        foreach (var masterPath in masters)
        {
            var name = Path.GetFileNameWithoutExtension(masterPath);
            var shortName = name[..Math.Min(30, name.Length)];

            if (!Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-30} (unreadable)");
                continue;
            }

            int channels;
            int fullWidth;
            int fullHeight;
            var crops = new List<float[]>();
            // The whole channel is kept only when an estimated arm may need to fall back to it; the
            // exact arm never does, and a 3-channel master is a few hundred MB of planes.
            var fullPlanes = new List<float[]>();
            try
            {
                var (chan, width, height) = master.Shape;
                fullWidth = width;
                fullHeight = height;
                if (width < Crop || height < Crop)
                {
                    output.WriteLine($"{shortName,-30} (smaller than the crop: {width}x{height})");
                    continue;
                }

                channels = Math.Min(3, chan);
                for (var c = 0; c < channels; c++)
                {
                    crops.Add(CropCentre(master, c, Crop));
                    if (estimating)
                    {
                        fullPlanes.Add(FullPlane(master, c));
                    }
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
                    output.WriteLine($"{shortName,-30} {c,2} (flat crop, no MAD)");
                    continue;
                }

                var (truthFwhm, truthStars) = await MeasuredFwhmAsync(truth, Crop, ct);
                var truthImage = Wrap(truth, Crop, Crop);
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

                // The clean side of the difference, fitted once per channel: on the crop, else on the
                // whole frame, else this channel has no estimate at all and every estimated arm says so.
                ProfileEstimate? clean = null;
                var cleanRefusal = "";
                if (estimating)
                {
                    var (cropFit, cropDiag) = await FitProfileAsync(truth, Crop, Crop, ct);
                    if (cropFit is { } cf)
                    {
                        clean = new ProfileEstimate(cf.Fwhm, cf.MoffatBeta, false);
                    }
                    else
                    {
                        var (frameFit, frameDiag) = await FitProfileAsync(fullPlanes[c], fullWidth, fullHeight, ct);
                        if (frameFit is { } ff)
                        {
                            clean = new ProfileEstimate(ff.Fwhm, ff.MoffatBeta, true);
                        }
                        else
                        {
                            cleanRefusal = $"clean crop {Describe(cropDiag)}; clean frame {Describe(frameDiag)}";
                            refusals.Add((name, c, false, "clean", frameDiag.Refusal));
                        }
                    }
                }

                foreach (var injected in InjectedFwhm)
                {
                    var exactPsf = PsfKernel.Moffat(injected, Beta);
                    var blurred = exactPsf.Convolve(truth, Crop, Crop);
                    // The whole degraded frame, made only if a crop fit fails, and then once per
                    // injection: the convolution is the expensive half of the fallback.
                    float[]? blurredFull = null;

                    foreach (var noisy in new[] { false, true })
                    {
                        // The frame's OWN background MAD as the added sigma: the exporter adds noise
                        // after the blur at the master's depth, so the oracle must face the same.
                        // Seeded per (master, channel, injected) so the table is reproducible, and
                        // shared by every arm so the comparison between them is paired.
                        var seed = StableSeed(name, c, injected);
                        var observed = noisy ? AddNoise(blurred, mad, new Random(seed)) : blurred;

                        // The estimate, from the observed frame alone, as deployment would have to.
                        ProfileEstimate? observedFit = null;
                        var observedRefusal = cleanRefusal;
                        if (estimating && clean is not null)
                        {
                            var (cropFit, cropDiag) = await FitProfileAsync(observed, Crop, Crop, ct);
                            if (cropFit is { } of)
                            {
                                observedFit = new ProfileEstimate(of.Fwhm, of.MoffatBeta, false);
                            }
                            else
                            {
                                blurredFull ??= exactPsf.Convolve(fullPlanes[c], fullWidth, fullHeight);
                                var observedFull = noisy ? AddNoise(blurredFull, mad, new Random(seed ^ 0x5bd1e995)) : blurredFull;
                                var (frameFit, frameDiag) = await FitProfileAsync(observedFull, fullWidth, fullHeight, ct);
                                if (frameFit is { } ff)
                                {
                                    observedFit = new ProfileEstimate(ff.Fwhm, ff.MoffatBeta, true);
                                }
                                else
                                {
                                    observedRefusal = $"observed crop {Describe(cropDiag)}; observed frame {Describe(frameDiag)}";
                                    refusals.Add((name, c, noisy, "observed", frameDiag.Refusal));
                                }
                            }
                        }

                        // Two width estimates from the same two fits: the quadrature the plan first
                        // wrote (E1b arms i and ii), and the Moffat composition that inverts what the
                        // convolution actually does to a half-maximum crossing (E1d). Both floor at a
                        // near-delta when the observed profile is no wider than the clean one.
                        var estWidth = double.NaN;
                        var composedWidth = double.NaN;
                        var estBeta = double.NaN;
                        var fromWholeFrame = false;
                        if (observedFit is { } ofit && clean is { } cfit)
                        {
                            var diff2 = (ofit.Fwhm * ofit.Fwhm) - (cfit.Fwhm * cfit.Fwhm);
                            estWidth = diff2 > 0 ? Math.Max(MinEstimatedFwhm, Math.Sqrt(diff2)) : MinEstimatedFwhm;
                            var composed = MoffatComposition.DifferenceFwhm(cfit.Fwhm, cfit.Beta, ofit.Fwhm, Beta);
                            composedWidth = double.IsFinite(composed) ? Math.Max(MinEstimatedFwhm, composed) : MinEstimatedFwhm;
                            estBeta = ofit.Beta;
                            fromWholeFrame = ofit.FromWholeFrame || cfit.FromWholeFrame;
                        }

                        double WidthOf(KernelSource arm) => arm == KernelSource.EstimatedComposed ? composedWidth : estWidth;

                        var kernels = new PsfKernel?[arms.Length];
                        for (var a = 0; a < arms.Length; a++)
                        {
                            kernels[a] = arms[a] switch
                            {
                                KernelSource.Exact => exactPsf,
                                KernelSource.Estimated => double.IsFinite(estWidth) ? PsfKernel.Moffat(estWidth, Beta) : null,
                                KernelSource.EstimatedShape => double.IsFinite(estWidth) && double.IsFinite(estBeta)
                                    ? PsfKernel.Moffat(estWidth, estBeta)
                                    : null,
                                KernelSource.EstimatedComposed => double.IsFinite(composedWidth) ? PsfKernel.Moffat(composedWidth, Beta) : null,
                                _ => throw new ArgumentOutOfRangeException(nameof(arms)),
                            };
                        }

                        // The arms share one observed frame and differ only in the kernel, and the
                        // iteration is a serial direct convolution, so they run side by side.
                        var estimates = new float[]?[arms.Length];
                        Parallel.For(0, arms.Length, a =>
                        {
                            if (kernels[a] is { } k)
                            {
                                estimates[a] = RichardsonLucy.Deconvolve(observed, Crop, Crop, k, iterations).Estimate;
                            }
                        });

                        // The null for the ringing statistic, measured on whatever this row's INPUT is:
                        // noise-free truth for the noise-free arm, the same noise realisation for the
                        // noisy one, so the baseline carries the row's own noise and the excess is only
                        // what the iteration added.
                        var (nullRing, nullOver) = Ringing(observed, Crop, stars, bg, mad);
                        var (blurFwhm, _) = await MeasuredFwhmAsync(observed, Crop, ct);
                        var blurRatio = float.IsFinite(blurFwhm) ? blurFwhm / (double)truthFwhm : double.NaN;
                        var psf01 = HfdPsfEstimator.EncodeRadiusToPsf01(blurFwhm * 0.5f, 0.5f, 4.0f);
                        var estBetaRatio = estBeta / Beta;

                        for (var a = 0; a < arms.Length; a++)
                        {
                            var arm = arms[a];
                            var prefix = $"{shortName,-30} {c,2} {injected,5:F1} {(noisy ? "yes" : "no"),5} {ArmLabel(arm),-6} {psf01,6:F3} "
                                + $"{truthFwhm,6:F2} {blurFwhm,6:F2} {blurRatio,5:F2}";
                            if (estimates[a] is not { } est)
                            {
                                noEstimate++;
                                if (double.IsFinite(blurRatio))
                                {
                                    rows.Add(new ArmRow(name, c, injected, noisy, arm, blurRatio, double.NaN, double.NaN,
                                        double.NaN, double.NaN, double.NaN, double.NaN, false, NoEstimate: true));
                                }

                                output.WriteLine($"{prefix} (no estimate: {observedRefusal})");
                                continue;
                            }

                            var (recFwhm, recStars) = await MeasuredFwhmAsync(est, Crop, ct);
                            var recovered = float.IsFinite(blurFwhm) && float.IsFinite(recFwhm) && blurFwhm > truthFwhm
                                ? (blurFwhm - recFwhm) / (blurFwhm - truthFwhm)
                                : double.NaN;
                            var (ring, overOne) = Ringing(est, Crop, stars, bg, mad);
                            var excess = overOne - nullOver;
                            var starRatio = truthStars > 0 ? (double)recStars / truthStars : double.NaN;
                            var recOverTruth = float.IsFinite(recFwhm) ? recFwhm / (double)truthFwhm : double.NaN;
                            var estimatedArm = arm != KernelSource.Exact;
                            var estWidthRatio = WidthOf(arm) / injected;

                            if (double.IsFinite(blurRatio))
                            {
                                rows.Add(new ArmRow(name, c, injected, noisy, arm, blurRatio, recOverTruth,
                                    float.IsFinite(recFwhm) ? recFwhm - truthFwhm : double.NaN, excess, starRatio,
                                    estimatedArm ? estWidthRatio : double.NaN,
                                    estimatedArm ? estBetaRatio : double.NaN,
                                    estimatedArm && fromWholeFrame));
                            }

                            output.WriteLine($"{prefix} {recFwhm,6:F2} {recOverTruth,5:F2} {recovered,7:P0} {ring,6:F2} {nullRing,6:F2} "
                                + $"{excess,7:P0} {recStars,6} {starRatio,8:F2} "
                                + (estimatedArm
                                    ? $"{estWidthRatio,7:F2} {estBetaRatio,7:F2} {(fromWholeFrame ? "frame" : "crop"),5}"
                                    : $"{"-",7} {"-",7} {"-",5}"));
                        }
                    }
                }
            }
        }

        Assert.SkipWhen(rows.Count == 0, "nothing measured");

        // Paired against the exact arm on the same (master, channel, injection, noise) row, which is
        // the only comparison the estimate's cost can be read from: the rows differ by a factor of five
        // in blur and a bin median of each arm alone would mix that in.
        var exactByRow = rows.Where(r => r.Arm == KernelSource.Exact)
            .ToDictionary(r => (r.Master, r.Channel, r.Injected, r.Noisy), r => r);

        output.WriteLine("");
        output.WriteLine($"{"blurred/truth",13} {"noise",5} {"arm",-6} {"n",4} {"rec/truth",9} {"resid px",8} {"ring exc",8} {"stars",6} "
            + $"{"d(r/t) p50",10} {"d(r/t) p90",10} {"estW/t",7} {"estB/t",7} {"frame",5} {"no-fit",6}");
        for (var bin = 0; bin < RatioBinLabels.Length; bin++)
        {
            foreach (var noisy in new[] { false, true })
            {
                foreach (var arm in arms)
                {
                    var cell = rows.Where(r => r.Arm == arm && r.Noisy == noisy && RatioBin(r.BlurRatio) == bin).ToList();
                    if (cell.Count == 0)
                    {
                        continue;
                    }

                    var recOverTruth = cell.Where(r => double.IsFinite(r.RecOverTruth)).Select(r => r.RecOverTruth).ToList();
                    var residual = cell.Where(r => double.IsFinite(r.ResidualPx)).Select(r => r.ResidualPx).ToList();
                    var excess = cell.Where(r => double.IsFinite(r.RingExcess)).Select(r => r.RingExcess).ToList();
                    var starRatio = cell.Where(r => double.IsFinite(r.StarRatio)).Select(r => r.StarRatio).ToList();
                    var deltas = new List<double>();
                    if (arm != KernelSource.Exact && exactIndex >= 0)
                    {
                        foreach (var r in cell)
                        {
                            if (exactByRow.TryGetValue((r.Master, r.Channel, r.Injected, r.Noisy), out var ex)
                                && double.IsFinite(r.RecOverTruth) && double.IsFinite(ex.RecOverTruth))
                            {
                                deltas.Add(r.RecOverTruth - ex.RecOverTruth);
                            }
                        }
                    }

                    deltas.Sort();
                    var estW = cell.Where(r => double.IsFinite(r.EstWidthRatio)).Select(r => r.EstWidthRatio).ToList();
                    var estB = cell.Where(r => double.IsFinite(r.EstBetaRatio)).Select(r => r.EstBetaRatio).ToList();
                    var frameCount = cell.Count(r => r.FromWholeFrame);
                    var refused = cell.Count(r => r.NoEstimate);

                    output.WriteLine($"{RatioBinLabels[bin],13} {(noisy ? "yes" : "no"),5} {ArmLabel(arm),-6} {cell.Count,4} "
                        + $"{Median(recOverTruth),9:F2} {Median(residual),8:F2} "
                        + $"{Median(excess).ToString("P0", CultureInfo.InvariantCulture),8} {Median(starRatio),6:F2} "
                        + (arm == KernelSource.Exact
                            ? $"{"-",10} {"-",10} {"-",7} {"-",7} {"-",5} {"-",6}"
                            : $"{(deltas.Count == 0 ? double.NaN : deltas[deltas.Count / 2]),10:+0.00;-0.00} "
                              + $"{Percentile(deltas, 0.9),10:+0.00;-0.00} {Median(estW),7:F2} {Median(estB),7:F2} {frameCount,5} {refused,6}"));
                }
            }
        }

        if (estimating)
        {
            output.WriteLine("");
            output.WriteLine($"arm rows the estimator refused (counted in n and no-fit above, measured in none of the other columns): {noEstimate}");

            // Which check refused, on the WHOLE-FRAME attempt (the crop never fits and is not the
            // question), by noise arm and then by master. The clean side's refusal is per channel and
            // removes every row of that channel from both estimated arms, so it is listed separately.
            output.WriteLine("");
            output.WriteLine($"{"whole-frame refusal",-20} {"side",-9} {"noise",5} {"n",4}");
            foreach (var group in refusals
                .GroupBy(r => (r.Side, r.Noisy, r.Refusal))
                .OrderBy(g => g.Key.Side).ThenBy(g => g.Key.Noisy).ThenByDescending(g => g.Count()))
            {
                output.WriteLine($"{group.Key.Refusal,-20} {group.Key.Side,-9} {(group.Key.Noisy ? "yes" : "no"),5} {group.Count(),4}");
            }

            output.WriteLine("");
            output.WriteLine($"{"master",-30} {"ch",2} {"refusals",8}  by check");
            foreach (var group in refusals.GroupBy(r => (r.Master, r.Channel)).OrderByDescending(g => g.Count()))
            {
                var byCheck = string.Join(", ", group.GroupBy(r => r.Refusal).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
                output.WriteLine($"{group.Key.Master[..Math.Min(30, group.Key.Master.Length)],-30} {group.Key.Channel,2} {group.Count(),8}  {byCheck}");
            }
        }

        output.WriteLine("");
        output.WriteLine("The noise-free rows are the ceiling of the inverse problem; the noisy rows are the");
        output.WriteLine("ceiling of the problem a trained net is actually given. Read the three columns");
        output.WriteLine("together: a recovery over 100 percent whose star count has climbed is fabrication,");
        output.WriteLine("and ring EXCESS is over the same statistic measured on this row's own input, so a");
        output.WriteLine("row near zero there did not ring however deep its raw annulus minimum looked.");
        if (estimating)
        {
            output.WriteLine("d(r/t) is an estimated arm's rec/truth minus the exact arm's on the SAME row; estW/t and");
            output.WriteLine("estB/t are the estimate over the injected width and beta, so a loss reads as width or shape.");
        }
    }
}
