using System;
using System.Collections.Generic;
using System.Linq;

namespace TianWen.Lib.Imaging;

/// <summary>One clear-sky observation for <see cref="ExpectedSkyModel.Fit"/>: where the frame pointed, and its sky.</summary>
/// <param name="Covariates">The frame's pointing and Moon (<see cref="SkyCovariates.From"/>).</param>
/// <param name="Level">The sky level in the frame's counts (<see cref="SkySurface.Level"/>).</param>
/// <param name="GradientStrength">The gradient strength in the frame's counts (<see cref="SkySurface.GradientStrength"/>).</param>
/// <param name="ZeroPoint">The frame's photometric zero point (<see cref="ColorCalibration.PhotometricZeroPoint"/>) in the same counts, NaN where it has no WCS or too few matches.</param>
public readonly record struct SkySample(SkyCovariates Covariates, double Level, double GradientStrength, double ZeroPoint = double.NaN);

/// <summary>How far a frame's sky sits above the expected one, in units of the model's own clear scatter.</summary>
/// <param name="LevelSigma">The level's log excess over the expectation, in sigma.</param>
/// <param name="GradientSigma">The gradient strength's log excess, in sigma.</param>
public readonly record struct SkyExcess(double LevelSigma, double GradientSigma)
{
    /// <summary>The two excesses summed: a veil lifts both, and clear scatter in the two is independent.</summary>
    public double Sum => LevelSigma + GradientSigma;
}

/// <summary>
/// The sky a patch of the sky normally shows for one site, camera and filter (#1974 section 1): the log of the sky level and
/// of the gradient strength, each a least-squares fit on the airmass X (<c>1, X, X^2</c>), the meridian side
/// (<c>west * (X - 1)</c>), the Moon (illumination times a falloff in separation while it is up, and its altitude) and the
/// hours from local solar midnight. A statistical model, not a learned one: pure, and serialisable as JSON.
/// </summary>
/// <remarks>
/// <para>The fit clips its worst residuals and refits until the kept set is stable, so a minority of veiled frames does not set
/// it. A term whose covariate does not vary in the input (every frame on one side of the meridian, the Moon never up) is left
/// out, and <see cref="Terms"/> says which were fitted. The scatter of each half is the robust sd of the kept residuals.</para>
/// <para>The coefficient arrays hold one value per entry of <see cref="Terms"/>, in that order.</para>
/// </remarks>
public sealed record ExpectedSkyModel
{
    /// <summary>The format version of the serialised model.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Every term the model can carry, in its fixed order.</summary>
    public static IReadOnlyList<string> AllTerms { get; } = ["1", "X", "X2", "west*(X-1)", "moon*falloff", "moonAlt", "midnight"];

    private const double MoonFalloffDeg = 40.0;
    private const double ClipSigma = 3.0;
    private const int MaxRounds = 12;
    private const int MinFrames = 12;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The fitted terms, a subset of <see cref="AllTerms"/>.</summary>
    public string[] Terms { get; init; } = [];

    /// <summary>The log level's coefficients, one per term.</summary>
    public double[] LevelCoefficients { get; init; } = [];

    /// <summary>The log gradient strength's coefficients, one per term.</summary>
    public double[] GradientCoefficients { get; init; } = [];

    /// <summary>The robust sd of the clear frames' log level about the fit.</summary>
    public double LevelScatter { get; init; }

    /// <summary>The robust sd of the clear frames' log gradient strength about the fit.</summary>
    public double GradientScatter { get; init; }

    /// <summary>The frames the fit kept after clipping.</summary>
    public int FrameCount { get; init; }

    /// <summary>The frames offered to the fit that were usable (finite covariates, positive level and gradient).</summary>
    public int OfferedCount { get; init; }

    /// <summary>
    /// The clear zero point extrapolated to no air, in magnitudes (<c>zp = ZeroPoint0 - Extinction * X</c>), NaN where no frame had
    /// a WCS (and in a version 1 file, which reads with no zero point).
    /// </summary>
    public double ZeroPoint0 { get; init; } = double.NaN;

    /// <summary>The extinction in magnitudes per airmass the zero point falls by (<see cref="ZeroPoint0"/>).</summary>
    public double Extinction { get; init; } = double.NaN;

    /// <summary>The robust sd of the clear frames' zero point about <c>ZeroPoint0 - Extinction * X</c>, in magnitudes.</summary>
    public double ZeroPointScatter { get; init; } = double.NaN;

    /// <summary>The frames the zero-point fit kept after clipping; 0 when there is no zero point.</summary>
    public int ZeroPointCount { get; init; }

    /// <summary>What the fitted frames covered, so a frame outside it is not read against the model (a guess); see <see cref="Covers"/>.</summary>
    public SkyCoverage Coverage { get; init; } = new();

    /// <summary>Whether the model carries a clear zero point.</summary>
    public bool HasZeroPoint => ZeroPointCount > 0 && double.IsFinite(ZeroPoint0) && double.IsFinite(Extinction);

    /// <summary>The fewest frames with a zero point the zero-point line is fitted from.</summary>
    public const int MinZeroPointFrames = 12;

    /// <summary>An airmass spread under which the slope is not fitted and the extinction stays the default (<c>Session.AtmosphericExtinctionMagPerAirmass</c>).</summary>
    private const double MinAirmassSpanForSlope = 0.15;

    /// <summary>
    /// Fits the model to <paramref name="samples"/>, which should be mostly clear frames of one rig. <see langword="null"/>
    /// when fewer than a dozen are usable.
    /// </summary>
    public static ExpectedSkyModel? Fit(IReadOnlyList<SkySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var features = new List<double[]>(samples.Count);
        var yLevel = new List<double>(samples.Count);
        var yGradient = new List<double>(samples.Count);
        var zeroPoints = new List<(double Airmass, double ZeroPoint)>();
        var covered = new List<SkyCovariates>(samples.Count);
        foreach (var s in samples)
        {
            if (!(s.Level > 0) || !(s.GradientStrength > 0) || !double.IsFinite(s.Level) || !double.IsFinite(s.GradientStrength))
            {
                continue;
            }
            var f = new double[AllTerms.Count];
            if (!TryFeatures(s.Covariates, f))
            {
                continue;
            }
            features.Add(f);
            covered.Add(s.Covariates);
            yLevel.Add(Math.Log(s.Level));
            yGradient.Add(Math.Log(s.GradientStrength));
            if (double.IsFinite(s.ZeroPoint))
            {
                zeroPoints.Add((s.Covariates.Airmass, s.ZeroPoint));
            }
        }
        var n = features.Count;
        if (n < MinFrames)
        {
            return null;
        }

        // A term stays out when its column does not vary: one side of the meridian, no Moon, a single airmass.
        var used = new List<int> { 0 };
        for (var t = 1; t < AllTerms.Count; t++)
        {
            if (Variance(features, t) > 1e-6)
            {
                used.Add(t);
            }
        }
        // X2 beside a nearly constant X is collinear with it; keep it only when the airmass has some spread.
        if (used.Contains(2) && !used.Contains(1))
        {
            used.Remove(2);
        }

        // A veil over the end of a night (a rising or setting target's last hour) is a high-leverage run that a flexible least-squares
        // fit bends toward and so hides from its own clip (34 veiled frames of 300, +7 % level and +86 % gradient, kept 23 of them),
        // so the clip starts from the best of many elemental fits judged by their scatter, which a minority cannot lower.
        var keep = RobustStart(features, yLevel, yGradient, used);
        double[] cl = [], cg = [];
        var sdL = 0.0;
        var sdG = 0.0;
        var kept = n;
        for (var round = 0; round < MaxRounds; round++)
        {
            cl = Solve(features, yLevel, keep, used);
            cg = Solve(features, yGradient, keep, used);
            // The scatter comes from EVERY row's residuals through the MAD, which a minority of outliers does not move.
            var rl = Residuals(features, yLevel, used, cl);
            var rg = Residuals(features, yGradient, used, cg);
            sdL = RobustSd(rl, keep, onlyKept: true);
            sdG = RobustSd(rg, keep, onlyKept: true);
            var changed = false;
            kept = 0;
            for (var i = 0; i < n; i++)
            {
                var ok = Math.Abs(rl[i]) <= ClipSigma * sdL && Math.Abs(rg[i]) <= ClipSigma * sdG;
                changed |= ok != keep[i];
                keep[i] = ok;
                if (ok)
                {
                    kept++;
                }
            }
            if (!changed || kept < MinFrames)
            {
                break;
            }
        }
        if (kept < MinFrames)
        {
            Array.Fill(keep, true);
            kept = n;
            cl = Solve(features, yLevel, keep, used);
            cg = Solve(features, yGradient, keep, used);
        }
        sdL = RobustSd(Residuals(features, yLevel, used, cl), keep, onlyKept: true);
        sdG = RobustSd(Residuals(features, yGradient, used, cg), keep, onlyKept: true);

        return new ExpectedSkyModel
        {
            Terms = [.. used.ConvertAll(t => AllTerms[t])],
            LevelCoefficients = cl,
            GradientCoefficients = cg,
            LevelScatter = sdL,
            GradientScatter = sdG,
            FrameCount = kept,
            OfferedCount = n,
            Coverage = SkyCoverage.Of(covered),
        }.WithZeroPoint(zeroPoints);
    }

    /// <summary>
    /// The clear zero point <c>zp = zp0 - k X</c> from the frames that had one (#1974 section 2): the slope by Theil-Sen over the
    /// airmass (or the default extinction when the airmass barely varies), the intercept by the median, then a clip at three robust
    /// sigma and a least-squares refit of what is kept, which a minority of dim frames does not move. The model is returned as it
    /// was where fewer than <see cref="MinZeroPointFrames"/> frames had one.
    /// </summary>
    private ExpectedSkyModel WithZeroPoint(List<(double Airmass, double ZeroPoint)> points)
    {
        if (points.Count < MinZeroPointFrames)
        {
            return this;
        }
        var ordered = points.OrderBy(static p => p.Airmass).ToList();
        var span = ordered[^1].Airmass - ordered[0].Airmass;
        var k = TianWen.Lib.Sequencing.Session.AtmosphericExtinctionMagPerAirmass;
        if (span >= MinAirmassSpanForSlope)
        {
            // Over every pair when there are few frames and over a stride of them when there are many.
            var step = Math.Max(1, ordered.Count / 200);
            var slopes = new List<double>();
            for (var i = 0; i < ordered.Count; i += step)
            {
                for (var j = i + step; j < ordered.Count; j += step)
                {
                    var dx = ordered[j].Airmass - ordered[i].Airmass;
                    if (dx > 1e-3)
                    {
                        slopes.Add((ordered[j].ZeroPoint - ordered[i].ZeroPoint) / dx);
                    }
                }
            }
            if (slopes.Count > 0)
            {
                k = -Median(slopes);
            }
        }
        var zp0 = Median(ordered.ConvertAll(p => p.ZeroPoint + k * p.Airmass));
        var sd = SdAbout(ordered, zp0, k);
        var kept = ordered;
        for (var round = 0; round < 3 && sd > 0; round++)
        {
            var round0 = zp0;
            var round1 = k;
            var clipped = ordered.Where(p => Math.Abs(p.ZeroPoint - (round0 - round1 * p.Airmass)) <= ClipSigma * sd).ToList();
            if (clipped.Count < MinZeroPointFrames)
            {
                break;
            }
            kept = clipped;
            // A least-squares line of the kept points, when the airmass spreads enough to carry a slope.
            var meanX = kept.Average(static p => p.Airmass);
            var meanY = kept.Average(static p => p.ZeroPoint);
            var sxx = kept.Sum(p => (p.Airmass - meanX) * (p.Airmass - meanX));
            if (span >= MinAirmassSpanForSlope && sxx > 1e-9)
            {
                k = -kept.Sum(p => (p.Airmass - meanX) * (p.ZeroPoint - meanY)) / sxx;
            }
            zp0 = meanY + k * meanX;
            sd = SdAbout(kept, zp0, k);
        }
        return this with { ZeroPoint0 = zp0, Extinction = k, ZeroPointScatter = sd, ZeroPointCount = kept.Count };
    }

    private static double SdAbout(List<(double Airmass, double ZeroPoint)> points, double zp0, double k)
    {
        var residuals = points.ConvertAll(p => p.ZeroPoint - (zp0 - k * p.Airmass));
        var median = Median(residuals);
        return 1.4826 * Median(residuals.ConvertAll(r => Math.Abs(r - median)));
    }

    /// <summary>
    /// How much of a clear night's light a frame transmits (#1974 section 2): <c>10^(-0.4 (expected - zp))</c>, where the expected
    /// zero point is the clear one at the frame's airmass. Below 1 the frame is dimmer than the rig's clear sky would make it. NaN
    /// where the model has no zero point or the frame has none or no airmass.
    /// </summary>
    public double Transparency(in SkyCovariates covariates, double zeroPoint)
    {
        if (!HasZeroPoint || !double.IsFinite(zeroPoint) || !double.IsFinite(covariates.Airmass))
        {
            return double.NaN;
        }
        var expected = ZeroPoint0 - Extinction * covariates.Airmass;
        return Math.Pow(10.0, -0.4 * (expected - zeroPoint));
    }

    /// <summary>The log level and log gradient strength the model expects at <paramref name="covariates"/>; NaN where a fitted term cannot be read.</summary>
    public (double LogLevel, double LogGradient) Expected(in SkyCovariates covariates)
    {
        var f = new double[AllTerms.Count];
        if (!TryFeatures(covariates, f, tolerateMissing: Terms))
        {
            return (double.NaN, double.NaN);
        }
        var l = 0.0;
        var g = 0.0;
        for (var k = 0; k < Terms.Length; k++)
        {
            var x = f[IndexOf(Terms[k])];
            l += LevelCoefficients[k] * x;
            g += GradientCoefficients[k] * x;
        }
        return (l, g);
    }

    /// <summary>
    /// How far a frame with these <paramref name="covariates"/>, <paramref name="level"/> and
    /// <paramref name="gradientStrength"/> sits above the expected sky, in units of the clear scatter of each half. NaN where
    /// the frame or the model cannot say.
    /// </summary>
    public SkyExcess Excess(in SkyCovariates covariates, double level, double gradientStrength)
    {
        var (l, g) = Expected(covariates);
        if (!Covers(covariates))
        {
            return new SkyExcess(double.NaN, double.NaN);
        }
        if (!(level > 0) || !(gradientStrength > 0) || !(LevelScatter > 0) || !(GradientScatter > 0))
        {
            return new SkyExcess(double.NaN, double.NaN);
        }
        return new SkyExcess((Math.Log(level) - l) / LevelScatter, (Math.Log(gradientStrength) - g) / GradientScatter);
    }

    /// <summary>
    /// Whether a frame with these <paramref name="covariates"/> lies inside what the fitted frames covered: its meridian side, the
    /// Moon up or down, its airmass and its hours from midnight. A term the fit left out (no Moon in the clear nights) says nothing
    /// about a Moon-lit frame, so such a frame is not read at all; the verdict falls back to the night's own fit or to no term.
    /// A model with no recorded coverage (an older file) covers everything.
    /// </summary>
    public bool Covers(in SkyCovariates covariates) => Coverage.Contains(covariates);

    private static int IndexOf(string term)
    {
        for (var i = 0; i < AllTerms.Count; i++)
        {
            if (AllTerms[i] == term)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Fills <paramref name="f"/> with every term's value. A Moon covariate that is NaN fails the read only when
    /// <paramref name="tolerateMissing"/> is unset or names a Moon term.
    /// </summary>
    private static bool TryFeatures(in SkyCovariates c, double[] f, string[]? tolerateMissing = null)
    {
        var x = c.Airmass;
        var wantsMidnight = tolerateMissing is null || Array.IndexOf(tolerateMissing, "midnight") >= 0;
        if (!double.IsFinite(x) || (wantsMidnight && !double.IsFinite(c.HoursFromLocalMidnight)))
        {
            return false;
        }
        var wantsMoon = tolerateMissing is null || Array.IndexOf(tolerateMissing, "moon*falloff") >= 0 || Array.IndexOf(tolerateMissing, "moonAlt") >= 0;
        double moon = 0, moonAlt = 0;
        if (double.IsFinite(c.MoonAltitudeDeg))
        {
            if (c.MoonAltitudeDeg > 0)
            {
                if (!double.IsFinite(c.MoonIllumination) || !double.IsFinite(c.MoonSeparationDeg))
                {
                    if (wantsMoon)
                    {
                        return false;
                    }
                }
                else
                {
                    moon = c.MoonIllumination * Math.Exp(-c.MoonSeparationDeg / MoonFalloffDeg);
                    moonAlt = c.MoonAltitudeDeg / 90.0;
                }
            }
        }
        else if (wantsMoon)
        {
            return false;
        }
        f[0] = 1;
        f[1] = x;
        f[2] = x * x;
        f[3] = c.West ? x - 1 : 0;
        f[4] = moon;
        f[5] = moonAlt;
        f[6] = double.IsFinite(c.HoursFromLocalMidnight) ? c.HoursFromLocalMidnight : 0;
        return true;
    }

    private static double Variance(List<double[]> rows, int col)
    {
        double sum = 0, sum2 = 0;
        foreach (var r in rows)
        {
            sum += r[col];
            sum2 += r[col] * r[col];
        }
        var mean = sum / rows.Count;
        return sum2 / rows.Count - mean * mean;
    }

    private const int RobustTrials = 250;
    private const int RobustCandidates = 12;
    private const double StartClipSigma = 2.5;

    /// <summary>
    /// The rows to start the clip from: of <see cref="RobustTrials"/> least-squares fits to <c>4p</c> random rows (a fixed seed, so a fit is
    /// repeatable), the one whose best 85 % of residuals have the smallest rms in both halves together (the product of the two, a least-trimmed-squares criterion over the best 85 % of rows, which a minority of veiled rows cannot lower), and the rows it holds within
    /// <see cref="StartClipSigma"/> of its scatter. Every row when that leaves too few.
    /// </summary>
    private static bool[] RobustStart(List<double[]> features, List<double> yLevel, List<double> yGradient, List<int> used)
    {
        var n = features.Count;
        var p = used.Count;
        var all = new bool[n];
        Array.Fill(all, true);
        if (n < 3 * p)
        {
            return all;
        }
        var random = new Random(0x5EED);
        var subset = new bool[n];
        var size = Math.Min(n / 2, 4 * p);
        // The best few trials by scatter over every row, kept as candidates (score, level coefficients, gradient coefficients).
        var candidates = new List<(double Score, double[] L, double[] G)>(RobustCandidates + 1);
        for (var trial = 0; trial < RobustTrials; trial++)
        {
            Array.Clear(subset);
            for (var k = 0; k < size; k++)
            {
                int row;
                do
                {
                    row = random.Next(n);
                }
                while (subset[row]);
                subset[row] = true;
            }
            var cl = Solve(features, yLevel, subset, used);
            var cg = Solve(features, yGradient, subset, used);
            var score = ScatterProduct(features, yLevel, yGradient, used, cl, cg, all);
            if (double.IsFinite(score) && score > 0 && (candidates.Count < RobustCandidates || score < candidates[^1].Score))
            {
                candidates.Add((score, cl, cg));
                candidates.Sort(static (x, y) => x.Score.CompareTo(y.Score));
                if (candidates.Count > RobustCandidates)
                {
                    candidates.RemoveAt(candidates.Count - 1);
                }
            }
        }
        if (candidates.Count == 0)
        {
            return all;
        }

        // Each candidate is polished by concentration steps (refit on the 70 % of rows closest to the current fit, both halves together)
        // and the polished fit with the least scatter wins: two attractors, one bent toward a veil at the leverage end of the night and
        // one not, sit within a rounding error of each other before the polish, and the scatter tells them apart after it.
        var hKeep = (int)(0.7 * n);
        var trimmed = new bool[n];
        var distance = new double[n];
        var order = new int[n];
        var bestScore = double.PositiveInfinity;
        double[] bestL = [], bestG = [];
        foreach (var (_, startL, startG) in candidates)
        {
            var cl = startL;
            var cg = startG;
            for (var step = 0; step < 12; step++)
            {
                var rl = Residuals(features, yLevel, used, cl);
                var rg = Residuals(features, yGradient, used, cg);
                var sl = RobustSd(rl, all, onlyKept: false);
                var sg = RobustSd(rg, all, onlyKept: false);
                if (!(sl > 0) || !(sg > 0))
                {
                    break;
                }
                for (var i = 0; i < n; i++)
                {
                    distance[i] = rl[i] * rl[i] / (sl * sl) + rg[i] * rg[i] / (sg * sg);
                    order[i] = i;
                }
                Array.Sort(order, (a, b) => distance[a].CompareTo(distance[b]));
                Array.Clear(trimmed);
                for (var k = 0; k < hKeep; k++)
                {
                    trimmed[order[k]] = true;
                }
                cl = Solve(features, yLevel, trimmed, used);
                cg = Solve(features, yGradient, trimmed, used);
            }
            var score = ScatterProduct(features, yLevel, yGradient, used, cl, cg, all);
            if (double.IsFinite(score) && score > 0 && score < bestScore)
            {
                bestScore = score;
                bestL = cl;
                bestG = cg;
            }
        }
        if (bestL.Length == 0)
        {
            return all;
        }
        var resL = Residuals(features, yLevel, used, bestL);
        var resG = Residuals(features, yGradient, used, bestG);
        var sdL = RobustSd(resL, all, onlyKept: false);
        var sdG = RobustSd(resG, all, onlyKept: false);
        var keep = new bool[n];
        var kept = 0;
        for (var i = 0; i < n; i++)
        {
            keep[i] = Math.Abs(resL[i]) <= StartClipSigma * sdL && Math.Abs(resG[i]) <= StartClipSigma * sdG;
            if (keep[i])
            {
                kept++;
            }
        }
        return kept >= MinFrames ? keep : all;
    }

    private static double ScatterProduct(List<double[]> features, List<double> yLevel, List<double> yGradient, List<int> used,
        double[] cl, double[] cg, bool[] all)
        => RobustSd(Residuals(features, yLevel, used, cl), all, onlyKept: false) * RobustSd(Residuals(features, yGradient, used, cg), all, onlyKept: false);

    private static double[] Solve(List<double[]> rows, List<double> y, bool[] keep, List<int> used)
    {
        var p = used.Count;
        var a = new double[p, p + 1];
        for (var i = 0; i < rows.Count; i++)
        {
            if (!keep[i])
            {
                continue;
            }
            for (var r = 0; r < p; r++)
            {
                var fr = rows[i][used[r]];
                for (var c = 0; c < p; c++)
                {
                    a[r, c] += fr * rows[i][used[c]];
                }
                a[r, p] += fr * y[i];
            }
        }
        // A whisper of ridge keeps a near-collinear pair (X and X^2 over a narrow airmass range) solvable.
        for (var r = 0; r < p; r++)
        {
            a[r, r] += 1e-9 * (1 + a[r, r]);
        }
        for (var col = 0; col < p; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < p; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }
            for (var c = 0; c <= p; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }
            for (var r = 0; r < p; r++)
            {
                if (r != col && a[col, col] != 0)
                {
                    var factor = a[r, col] / a[col, col];
                    for (var c = 0; c <= p; c++)
                    {
                        a[r, c] -= factor * a[col, c];
                    }
                }
            }
        }
        var x = new double[p];
        for (var r = 0; r < p; r++)
        {
            x[r] = a[r, r] != 0 ? a[r, p] / a[r, r] : 0;
        }
        return x;
    }

    private static double[] Residuals(List<double[]> rows, List<double> y, List<int> used, double[] coefficients)
    {
        var r = new double[rows.Count];
        for (var i = 0; i < r.Length; i++)
        {
            var pred = 0.0;
            for (var k = 0; k < used.Count; k++)
            {
                pred += coefficients[k] * rows[i][used[k]];
            }
            r[i] = y[i] - pred;
        }
        return r;
    }

    /// <summary>1.4826 times the median absolute deviation of the residuals (every row, or only the kept ones).</summary>
    private static double RobustSd(double[] residuals, bool[] keep, bool onlyKept)
    {
        var list = new List<double>(residuals.Length);
        for (var i = 0; i < residuals.Length; i++)
        {
            if (!onlyKept || keep[i])
            {
                list.Add(residuals[i]);
            }
        }
        var median = Median(list);
        for (var i = 0; i < list.Count; i++)
        {
            list[i] = Math.Abs(list[i] - median);
        }
        return 1.4826 * Median(list);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n == 0 ? double.NaN : n % 2 == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
    }
}

/// <summary>What the frames a model was fitted from covered (#1974): the sides of the meridian, the Moon up and down, the airmass and hours-from-midnight ranges.</summary>
public sealed record SkyCoverage
{
    /// <summary>Airmass slack past the fitted range a frame may still be read at.</summary>
    private const double AirmassSlack = 0.15;

    /// <summary>Hours-from-midnight slack past the fitted range.</summary>
    private const double HoursSlack = 1.5;

    public bool Recorded { get; init; }
    public bool SawEast { get; init; }
    public bool SawWest { get; init; }
    public bool SawMoonUp { get; init; }
    public bool SawMoonDown { get; init; }
    public double MinAirmass { get; init; }
    public double MaxAirmass { get; init; }
    public double MinHours { get; init; }
    public double MaxHours { get; init; }

    public static SkyCoverage Of(IReadOnlyList<SkyCovariates> frames)
    {
        var c = new SkyCoverage
        {
            Recorded = frames.Count > 0,
            MinAirmass = double.PositiveInfinity, MaxAirmass = double.NegativeInfinity,
            MinHours = double.PositiveInfinity, MaxHours = double.NegativeInfinity,
        };
        bool east = false, west = false, up = false, down = false;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minH = double.PositiveInfinity, maxH = double.NegativeInfinity;
        foreach (var f in frames)
        {
            east |= !f.West;
            west |= f.West;
            if (double.IsFinite(f.MoonAltitudeDeg))
            {
                up |= f.MoonAltitudeDeg > 0;
                down |= f.MoonAltitudeDeg <= 0;
            }
            minX = Math.Min(minX, f.Airmass);
            maxX = Math.Max(maxX, f.Airmass);
            if (double.IsFinite(f.HoursFromLocalMidnight))
            {
                minH = Math.Min(minH, f.HoursFromLocalMidnight);
                maxH = Math.Max(maxH, f.HoursFromLocalMidnight);
            }
        }
        return c with { SawEast = east, SawWest = west, SawMoonUp = up, SawMoonDown = down, MinAirmass = minX, MaxAirmass = maxX, MinHours = minH, MaxHours = maxH };
    }

    public bool Contains(in SkyCovariates c)
    {
        if (!Recorded)
        {
            return true;
        }
        if (c.West ? !SawWest : !SawEast)
        {
            return false;
        }
        if (double.IsFinite(c.MoonAltitudeDeg) && (c.MoonAltitudeDeg > 0 ? !SawMoonUp : !SawMoonDown))
        {
            return false;
        }
        if (double.IsFinite(c.Airmass) && (c.Airmass < MinAirmass - AirmassSlack || c.Airmass > MaxAirmass + AirmassSlack))
        {
            return false;
        }
        return !double.IsFinite(c.HoursFromLocalMidnight) || !double.IsFinite(MinHours)
            || (c.HoursFromLocalMidnight >= MinHours - HoursSlack && c.HoursFromLocalMidnight <= MaxHours + HoursSlack);
    }
}
