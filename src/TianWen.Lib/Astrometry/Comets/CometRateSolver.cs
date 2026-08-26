using System;
using System.Globalization;
using System.Numerics;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// A moving target's apparent motion, expressed the way registration needs it.
/// </summary>
/// <param name="PxPerHour">Canvas pixels per hour, the value
/// <c>StackingOptions.CometRatePxPerHour</c> takes.</param>
/// <param name="MaxResidualPx">Worst departure of any sample from the fitted straight line. This is
/// the number that says whether one linear rate was the right model for this run, and it is reported
/// rather than assumed: the plan measured 0.185 px over 3.5 h for 10P, comfortably under the
/// registration residual, but a faster body or a longer night need not be so kind.</param>
/// <param name="SampleCount">Ephemeris samples the fit consumed.</param>
/// <param name="AnchorPx">Where the body WAS, in the reference frame's pixel basis, at
/// <paramref name="AnchorEpoch"/>. This is the fit's intercept, so it is a least-squares answer over
/// every sample rather than one projected position, and it is what a consumer needs to know where
/// the body is at some other instant rather than merely how fast it moves.
///
/// <para><b>The pixel convention DOES bite here, unlike the rate.</b> A rate is a difference, so any
/// constant offset cancels and the fit is immune to whether <see cref="WCS.SkyToPixel"/> answers
/// 0-based or 1-based. An absolute position has no such protection. The repo rule applies and is not
/// optional: a solver-built WCS answers in detected-centroid coordinates, so this is used AS
/// RETURNED and never adjusted by a pixel. The plausible-looking correction injects a constant bias
/// that no test of the rate can ever see.</para></param>
/// <param name="AnchorEpoch">The instant <paramref name="AnchorPx"/> describes: the first sample's
/// time, which is the fit's own origin. Carried rather than assumed, because "the first sample" is
/// not a fact a caller should have to reconstruct from a track it may no longer hold.</param>
public readonly record struct CometRate(
    Vector2 PxPerHour,
    double MaxResidualPx,
    int SampleCount,
    Vector2 AnchorPx,
    DateTimeOffset AnchorEpoch);

/// <summary>
/// Turns a topocentric ephemeris track into the canvas-space rate the comet compose consumes, by
/// projecting each sky position through the REFERENCE FRAME's WCS and fitting a straight line
/// through the resulting pixel positions against time.
///
/// <para><b>The pixel convention cannot bite here, which is worth stating because it bites
/// elsewhere.</b> <see cref="WCS.SkyToPixel"/> answers in the solver's own centroid coordinates, and
/// whether those are read as 1-based or 0-based is a live hazard in this codebase (a plausible-looking
/// <c>-1</c> once injected a constant sub-pixel bias into the acceptance gate). A RATE is a
/// difference of two positions, so any constant offset -- one pixel, half a pixel, or none --
/// cancels identically. The fit is therefore immune to the question.</para>
///
/// <para><b>Least squares over every sample, not a two-point difference.</b> Two points are enough
/// arithmetically and the plan's hand-derived number was computed that way, but a single pair inherits
/// the full ephemeris rounding of both endpoints and, more importantly, cannot report a residual. The
/// fit gives the same slope for a linear track while also measuring how linear the track actually
/// was, which is the check that says a single rate was the right model.</para>
/// </summary>
public static class CometRateSolver
{
    /// <summary>
    /// Fits the canvas rate from an ephemeris track. Returns <c>null</c> when the WCS cannot project,
    /// when fewer than two samples project successfully, or when every sample shares one instant.
    /// </summary>
    /// <param name="wcs">The reference frame's solved WCS. Canvas space and the reference frame's
    /// pixel space coincide (its own registration solution is the identity), which is why the rate
    /// this produces needs no knowledge of the final canvas origin.</param>
    /// <param name="samples">Topocentric positions, in any order.</param>
    public static CometRate? SolveCanvasRatePxPerHour(WCS wcs, ReadOnlySpan<EphemerisSample> samples)
    {
        if (samples.Length < 2 || !wcs.HasCDMatrix)
        {
            return null;
        }

        Span<double> hours = new double[samples.Length];
        Span<double> xs = new double[samples.Length];
        Span<double> ys = new double[samples.Length];
        var n = 0;
        var epoch = samples[0].TimeUtc;

        foreach (var s in samples)
        {
            // SkyToPixel takes RA in HOURS; the ephemeris states it in degrees.
            if (wcs.SkyToPixel(s.RaDeg / 15.0, s.DecDeg) is not { } px)
            {
                continue;
            }
            hours[n] = (s.TimeUtc - epoch).TotalHours;
            xs[n] = px.X;
            ys[n] = px.Y;
            n++;
        }

        if (n < 2)
        {
            return null;
        }

        var (slopeX, interceptX) = Fit(hours[..n], xs[..n]);
        var (slopeY, interceptY) = Fit(hours[..n], ys[..n]);
        if (double.IsNaN(slopeX) || double.IsNaN(slopeY))
        {
            return null;
        }

        var worst = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dx = xs[i] - (interceptX + slopeX * hours[i]);
            var dy = ys[i] - (interceptY + slopeY * hours[i]);
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        // The intercepts are the fitted position at `epoch`, which the fit already had to compute.
        // Returning them costs nothing and is strictly better than re-projecting one sample: the
        // same least-squares averaging that makes the slope robust makes this robust too.
        return new CometRate(
            new Vector2((float)slopeX, (float)slopeY),
            worst,
            n,
            new Vector2((float)interceptX, (float)interceptY),
            epoch);
    }

    /// <summary>
    /// Parses the offline form of a rate, <c>"dx,dy"</c> in canvas pixels per hour, as the CLI's
    /// <c>--comet-rate</c> takes it. Lives here rather than in the CLI so the hosted API and any
    /// future UI parse it the same way, the same reason <c>EnhanceOptions.TryParse</c> is shared.
    /// </summary>
    public static bool TryParsePxPerHour(ReadOnlySpan<char> text, out Vector2 rate)
    {
        rate = default;
        var comma = text.IndexOf(',');
        if (comma <= 0)
        {
            return false;
        }

        if (!float.TryParse(text[..comma].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
            || !float.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dy)
            || !float.IsFinite(dx) || !float.IsFinite(dy))
        {
            return false;
        }

        rate = new Vector2(dx, dy);
        return true;
    }

    /// <summary>Ordinary least squares of <paramref name="v"/> against <paramref name="t"/>.
    /// Returns NaN slope when every t is identical, which the caller treats as unusable.</summary>
    private static (double Slope, double Intercept) Fit(ReadOnlySpan<double> t, ReadOnlySpan<double> v)
    {
        double meanT = 0, meanV = 0;
        for (var i = 0; i < t.Length; i++)
        {
            meanT += t[i];
            meanV += v[i];
        }
        meanT /= t.Length;
        meanV /= v.Length;

        double num = 0, den = 0;
        for (var i = 0; i < t.Length; i++)
        {
            var dt = t[i] - meanT;
            num += dt * (v[i] - meanV);
            den += dt * dt;
        }

        if (den <= 0)
        {
            return (double.NaN, double.NaN);
        }
        var slope = num / den;
        return (slope, meanV - slope * meanT);
    }
}
