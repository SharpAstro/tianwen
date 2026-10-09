using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One way of placing every frame of a capture: each frame's disk position, x and y in pixels from an origin of the track's own
/// (NaN where it has none). Two tracks of the same frames differ by a constant and by their errors, since the disk's motion is
/// the same for both.
/// </summary>
public sealed record RegistrationTrack(string Name, double[] X, double[] Y)
{
    /// <summary>How many frames it places.</summary>
    public int Placed
    {
        get
        {
            var n = 0;
            for (var f = 0; f < X.Length; f++)
            {
                if (double.IsFinite(X[f]) && double.IsFinite(Y[f]))
                {
                    n++;
                }
            }
            return n;
        }
    }
}

/// <summary>
/// Registrations of one capture set side by side with no truth (docs/plans/planetary-restoration.md, R5 part 3): the variance
/// of any two tracks' difference is the sum of their errors' (the motion cancels), and three tracks give each one's own error
/// by the three-cornered hat (<see cref="ThreeCorneredHat"/>). A synthetic capture's recorded motion scores the same tracks
/// against a truth, which is how the hat is checked before a real capture's reading is believed.
/// </summary>
public static class RegistrationComparison
{
    /// <summary>
    /// Each <paramref name="stride"/>-th frame's disk centre by the limb fit (<see cref="PlanetaryLimbFit"/>, a forward model of
    /// the limb, independent of the texture a correlation reads), NaN on the frames between and where a fit fails. Every fit
    /// starts from the fit of the stack of those frames, moved by <paramref name="global"/>'s shift of the frame, which must
    /// be measured against frame <paramref name="referenceIndex"/>'s geometry: a cold fit of one frame takes most of a minute.
    /// </summary>
    public static async Task<RegistrationTrack?> LimbAsync(IPlanetaryFrameStream stream, RegistrationTrack global, int referenceIndex,
        LimbFitOptions options, int stride, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        var n = stream.FrameCount;
        var sampled = new List<int>();
        for (var f = 0; f < n; f += stride)
        {
            if (double.IsFinite(global.X[f]) && double.IsFinite(global.Y[f]))
            {
                sampled.Add(f);
            }
        }

        // The stack's disk, on the reference frame's geometry: where every frame's fit starts, moved by the frame's shift.
        var mean = await new LuckyImagingStacker().StackPlanesAsync(stream, [.. sampled], referenceIndex, whiten: false, cancellationToken).ConfigureAwait(false);
        LimbFit like;
        try
        {
            if (PlanetaryLimbFit.Fit(mean, options) is not { } fit)
            {
                return null;
            }
            like = fit;
        }
        finally
        {
            mean.Release();
        }

        var (x, y) = (new double[n], new double[n]);
        Array.Fill(x, double.NaN);
        Array.Fill(y, double.NaN);
        await PlanetaryCaptureStatistics.ForEachFrameAsync(stream, [.. sampled], () => 0, (_, index, frame) =>
        {
            var plane = frame.GetChannelSpan(0).ToArray();
            if (PlanetaryLimbFit.Fit(plane, frame.Width, frame.Height, like.CenterX + global.X[index], like.CenterY + global.Y[index], like, options) is { } fit)
            {
                (x[index], y[index]) = (fit.CenterX, fit.CenterY);
            }
        }, cancellationToken).ConfigureAwait(false);
        return new RegistrationTrack("limb fit", x, y);
    }

    /// <summary>
    /// The variance of two tracks' difference per axis, over the frames both place (<see cref="ThreeCorneredHat.DifferenceVariance"/>):
    /// the sum of their error variances, when their errors are independent.
    /// </summary>
    public static (double X, double Y) DifferenceVariance(RegistrationTrack a, RegistrationTrack b)
        => (ThreeCorneredHat.DifferenceVariance(a.X, b.X), ThreeCorneredHat.DifferenceVariance(a.Y, b.Y));

    /// <summary>
    /// <see cref="DifferenceVariance(RegistrationTrack, RegistrationTrack)"/> of the difference's FAST part: each frame's difference
    /// less the mean of the other frames' differences within <paramref name="halfWindow"/> frames either side, scaled by
    /// 1 / sqrt(1 + 1/m) for the m frames that mean took, so a white error reads its own variance. What two tracks disagree on
    /// slowly is left out: a rotation the texture shows and the limb does not (0.53 px a minute at a 100 px Jupiter's centre),
    /// or a drift in either's bias. What is left is each frame's own error.
    /// </summary>
    public static (double X, double Y) FastDifferenceVariance(RegistrationTrack a, RegistrationTrack b, int halfWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(halfWindow, 1);
        return (Fast(a.X, b.X, halfWindow), Fast(a.Y, b.Y, halfWindow));

        static double Fast(double[] a, double[] b, int halfWindow)
        {
            var n = a.Length;
            var (sum, count) = (new double[n + 1], new int[n + 1]);
            for (var f = 0; f < n; f++)
            {
                var d = a[f] - b[f];
                var finite = double.IsFinite(d);
                sum[f + 1] = sum[f] + (finite ? d : 0);
                count[f + 1] = count[f] + (finite ? 1 : 0);
            }
            var residuals = new double[n];
            Array.Fill(residuals, double.NaN);
            for (var f = 0; f < n; f++)
            {
                var d = a[f] - b[f];
                if (!double.IsFinite(d))
                {
                    continue;
                }
                var (lo, hi) = (Math.Max(0, f - halfWindow), Math.Min(n, f + halfWindow + 1));
                var others = count[hi] - count[lo] - 1;
                if (others < 1)
                {
                    continue;
                }
                var mean = (sum[hi] - sum[lo] - d) / others;
                residuals[f] = (d - mean) / Math.Sqrt(1 + (1.0 / others));
            }
            return ThreeCorneredHat.DifferenceVariance(residuals, new double[n]);
        }
    }

    /// <summary>
    /// Each of three tracks' error variance per axis by the three-cornered hat, over the frames all three place (so each
    /// pair's difference is taken over the same frames). Two tracks that share an error are credited with too little and the
    /// third with too much, silently (<see cref="ThreeCorneredHat"/>); only opposed errors show, as a negative variance.
    /// </summary>
    public static ImmutableArray<(double X, double Y)> Hat(RegistrationTrack a, RegistrationTrack b, RegistrationTrack c)
    {
        var (ma, mb, mc) = (Common(a, b, c), Common(b, a, c), Common(c, a, b));
        var (ab, ac, bc) = (DifferenceVariance(ma, mb), DifferenceVariance(ma, mc), DifferenceVariance(mb, mc));
        var hx = ThreeCorneredHat.Solve(ab.X, ac.X, bc.X);
        var hy = ThreeCorneredHat.Solve(ab.Y, ac.Y, bc.Y);
        return [(hx.A, hy.A), (hx.B, hy.B), (hx.C, hy.C)];
    }

    // The track with every frame the other two do not both place set to NaN.
    private static RegistrationTrack Common(RegistrationTrack track, RegistrationTrack other1, RegistrationTrack other2)
    {
        double[] x = [.. track.X], y = [.. track.Y];
        for (var f = 0; f < x.Length; f++)
        {
            if (!double.IsFinite(other1.X[f]) || !double.IsFinite(other1.Y[f]) || !double.IsFinite(other2.X[f]) || !double.IsFinite(other2.Y[f])
                || !double.IsFinite(x[f]) || !double.IsFinite(y[f]))
            {
                (x[f], y[f]) = (double.NaN, double.NaN);
            }
        }
        return track with { X = x, Y = y };
    }
}
