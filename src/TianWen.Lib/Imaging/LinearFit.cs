using System;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A mono master put on another's scale before they are combined, by the straight line <c>reference = a + b target</c>
/// fitted over the pixels both hold: PixInsight's LinearFit, the step a mono RGB workflow takes before ChannelCombination.
/// Channels that leave the camera on different scales (each filter's own throughput, exposure and sky) then start a
/// combination on one, which is what puts a common divisor, a luminance and a colour balance on equal terms.
/// </summary>
/// <remarks>
/// <para><b>The fit is PixInsight's.</b> <c>pcl::LinearFit</c> minimises the mean absolute deviation
/// (<see cref="LeastAbsoluteDeviation"/>), and the LinearFit process samples only pixels inside a reject window on both
/// images, by default above 0 and below 0.92 of the normalised range, so saturated star cores, which no line can fit,
/// carry no weight. These masters are in ADU rather than PixInsight's [0, 1], so the window is taken as a fraction of
/// each image's peak.</para>
/// <para><b>Every pixel is background or nebula almost everywhere</b>, so the fit is set by the sky's level and the
/// nebula's spread more than by the stars. A narrowband line master is never fitted to a broadband one: its sky and its
/// emission are not the broadband channel's, and the continuum scale (<see cref="ContinuumSubtractor"/>) is what relates
/// them.</para>
/// </remarks>
public static class LinearFit
{
    /// <summary>PixInsight LinearFit's default upper reject, as a fraction of each image's peak.</summary>
    public const double DefaultRejectHigh = 0.92;

    /// <summary>PixInsight LinearFit's default lower reject: a pixel at or below it is out.</summary>
    public const double DefaultRejectLow = 0.0;

    /// <summary>At most this many pixels carry the fit, taken at an even stride; each search step takes a median over
    /// them, and two million fix a line through millions of pixels no worse than all of them.</summary>
    public const int MaxSamples = 1 << 21;

    /// <summary>The line taking the target onto the reference (<c>reference = Offset + Slope target</c>), the pixels it was
    /// fitted over and its mean absolute deviation there, in the reference's units.</summary>
    public readonly record struct Fit(double Offset, double Slope, long Pixels, double MeanAbsoluteDeviation);

    /// <summary>
    /// The line taking <paramref name="target"/> (channel 0) onto <paramref name="reference"/> (channel 0), over the pixels
    /// finite in both and inside the reject window of both, given as fractions of each image's peak. NaN when fewer than
    /// three pixels qualify. The two must be on one grid (<see cref="MasterAlignment"/>).
    /// </summary>
    public static Fit Measure(Image target, Image reference, double rejectLow = DefaultRejectLow, double rejectHigh = DefaultRejectHigh)
    {
        if (target.Width != reference.Width || target.Height != reference.Height)
        {
            throw new ArgumentException("the target and the reference must be on one grid (tianwen image align)", nameof(reference));
        }
        var t = target.GetChannelSpan(0);
        var r = reference.GetChannelSpan(0);
        var (tLow, tHigh) = Window(t, rejectLow, rejectHigh);
        var (rLow, rHigh) = Window(r, rejectLow, rejectHigh);

        long qualifying = 0;
        for (var i = 0; i < t.Length; i++)
        {
            if (Inside(t[i], tLow, tHigh) && Inside(r[i], rLow, rHigh))
            {
                qualifying++;
            }
        }
        if (qualifying < 3)
        {
            return new Fit(double.NaN, double.NaN, qualifying, double.NaN);
        }

        var stride = (int)Math.Max(1, (qualifying + MaxSamples - 1) / MaxSamples);
        var count = (int)((qualifying + stride - 1) / stride);
        var xs = new float[count];
        var ys = new float[count];
        long seen = 0;
        var n = 0;
        for (var i = 0; i < t.Length && n < count; i++)
        {
            if (Inside(t[i], tLow, tHigh) && Inside(r[i], rLow, rHigh))
            {
                if (seen % stride == 0)
                {
                    xs[n] = t[i];
                    ys[n] = r[i];
                    n++;
                }
                seen++;
            }
        }
        if (n < count)
        {
            Array.Resize(ref xs, n);
            Array.Resize(ref ys, n);
        }

        // Start from the ratio of the two spreads about their medians: the slope a pure scale would have.
        var (_, tMad) = StatisticsHelper.MedianAndMad((float[])xs.Clone());
        var (_, rMad) = StatisticsHelper.MedianAndMad((float[])ys.Clone());
        var start = tMad > 0 ? (double)rMad / tMad : 1.0;
        var line = LeastAbsoluteDeviation.Fit(xs, ys, start);
        return new Fit(line.Offset, line.Slope, qualifying, line.MeanAbsoluteDeviation);
    }

    /// <summary><paramref name="target"/> through <paramref name="fit"/>, every channel, absent pixels kept absent.</summary>
    public static Image Apply(Image target, Fit fit) => target.Affine(fit.Slope, fit.Offset);

    private static bool Inside(float v, double low, double high) => float.IsFinite(v) && v > low && v < high;

    // The reject window in the image's own units: fractions of its brightest finite value.
    private static (double Low, double High) Window(ReadOnlySpan<float> values, double rejectLow, double rejectHigh)
    {
        var peak = float.NegativeInfinity;
        foreach (var v in values)
        {
            if (float.IsFinite(v) && v > peak)
            {
                peak = v;
            }
        }
        return (rejectLow * peak, rejectHigh * peak);
    }
}
