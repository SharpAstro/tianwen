using System;

namespace TianWen.Lib.Stat;

/// <summary>
/// Each of three estimators' own error variance from the variances of their pairwise differences, with no truth: the
/// three-cornered hat of clock-stability work (Gray and Allan 1974). Three estimators A, B and C of one quantity, whose errors
/// are independent of one another, differ by their errors alone, since the quantity cancels in every difference; so
/// var(A - B) = a + b, var(A - C) = a + c and var(B - C) = b + c, which three equations solve for a, b and c.
/// </summary>
/// <remarks>
/// <para><b>Independence is the whole assumption, and its usual failure is silent.</b> Two estimators that share an error
/// agree with each other better than their errors warrant: the hat credits both with too little and charges the shared part to
/// the third, and nothing in the answer says so (<c>RegistrationComparisonTests</c>). Only errors that OPPOSE, two estimators
/// erring by the same amount in opposite senses, drive a variance below zero, which is returned as it is rather than clipped.
/// So a reading is believed only once the same three estimators have been checked against a truth.</para>
/// <para>Planetary registration (docs/plans/planetary-restoration.md, R5 part 3): the disk's real motion, which the seeing
/// sets and no single frame's truth tells, cancels in the differences of any two registrations of the same frames, so the hat
/// ranks AutoStakkert's track, a cross-correlation and a limb fit by their own errors. It is checked on a synthetic capture,
/// whose true motion is recorded, before it is believed on a real one.</para>
/// </remarks>
public static class ThreeCorneredHat
{
    /// <summary>The error variances of A, B and C from var(A - B), var(A - C) and var(B - C).</summary>
    public static (double A, double B, double C) Solve(double varianceAB, double varianceAC, double varianceBC)
        => ((varianceAB + varianceAC - varianceBC) / 2, (varianceAB + varianceBC - varianceAC) / 2, (varianceAC + varianceBC - varianceAB) / 2);

    /// <summary>
    /// A robust variance of the difference of two series, over the places both are finite: the square of 1.4826 times the
    /// median absolute deviation of <c>a - b</c> from its median, so a constant offset between the two (each measured from its
    /// own origin) and the odd failed estimate do not count. NaN when fewer than three places are finite in both.
    /// </summary>
    public static double DifferenceVariance(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(a.Length, b.Length);
        var differences = new double[a.Length];
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            if (double.IsFinite(d))
            {
                differences[n++] = d;
            }
        }
        if (n < 3)
        {
            return double.NaN;
        }

        var (_, mad) = StatisticsHelper.MedianAndMad(differences.AsSpan(0, n));
        var sigma = 1.4826 * mad;
        return sigma * sigma;
    }
}
