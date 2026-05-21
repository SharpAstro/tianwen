using System;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// Bivariate polynomial machinery for the FITS SIP convention
/// (Shupe et al. 2005, "The SIP Convention").
/// <para>
/// A SIP polynomial is a bivariate sum
/// <c>f(u, v) = Σ a[i, j] · uⁱ · vʲ</c> over <c>i + j ∈ [1, order]</c>.
/// The (0, 0) term is conventionally absent — it is absorbed by the
/// reference pixel (CRPIX) — and orders 0 are excluded from FITS header
/// emission for the same reason.
/// </para>
/// <para>
/// Coefficient arrays are stored as <c>double[order + 1, order + 1]</c>
/// with the upper-right corner (entries where <c>i + j &gt; order</c>)
/// and the constant (<c>i = j = 0</c>) left at zero so the (i, j) layout
/// is identical to the FITS card naming (<c>A_2_1</c> sits at index
/// <c>[2, 1]</c>).
/// </para>
/// </summary>
internal static class SipPolynomial
{
    /// <summary>
    /// Maximum SIP order this code supports. Order 4 is overkill for any
    /// realistic optical system; the design matrix has 14 basis terms at
    /// order 4 and would need &gt;50 inliers for stable fit.
    /// </summary>
    public const int MaxOrder = 4;

    /// <summary>
    /// Evaluate <c>f(u, v) = Σ coeffs[i, j] · uⁱ · vʲ</c> for
    /// <c>i + j ∈ [1, order]</c>, where <c>order</c> is inferred from the
    /// square <paramref name="coeffs"/> array.
    /// </summary>
    /// <param name="u">First coordinate (typically <c>x − CRPIX1</c>).</param>
    /// <param name="v">Second coordinate (typically <c>y − CRPIX2</c>).</param>
    /// <param name="coeffs">
    /// Coefficient matrix of shape <c>[order + 1, order + 1]</c>. Entries
    /// at <c>(0, 0)</c> and at indices where <c>i + j &gt; order</c> are
    /// ignored.
    /// </param>
    public static double Apply(double u, double v, double[,] coeffs)
    {
        var order = coeffs.GetLength(0) - 1;
        // Stack-allocate the power-of-u and power-of-v ladders. order is
        // bounded by MaxOrder so this stays tiny (max 5 doubles each).
        Span<double> uPow = stackalloc double[order + 1];
        Span<double> vPow = stackalloc double[order + 1];
        uPow[0] = 1.0;
        vPow[0] = 1.0;
        for (var k = 1; k <= order; k++)
        {
            uPow[k] = uPow[k - 1] * u;
            vPow[k] = vPow[k - 1] * v;
        }

        double sum = 0;
        for (var i = 0; i <= order; i++)
        {
            for (var j = 0; j <= order - i; j++)
            {
                // Skip the constant — it is absorbed into CRPIX per SIP convention.
                if ((i | j) == 0) continue;
                sum += coeffs[i, j] * uPow[i] * vPow[j];
            }
        }
        return sum;
    }

    /// <summary>
    /// Number of distinct (i, j) basis terms for a SIP polynomial of the
    /// given order, excluding the constant term (i + j = 0).
    /// </summary>
    /// <remarks>
    /// For order n this is <c>(n + 1)(n + 2)/2 − 1</c>:
    /// order 1 → 2 (u, v), order 2 → 5, order 3 → 9, order 4 → 14.
    /// </remarks>
    public static int BasisCount(int order) => (order + 1) * (order + 2) / 2 - 1;

    /// <summary>
    /// Least-squares fit of a single SIP polynomial against the supplied
    /// observation pairs.
    /// </summary>
    /// <param name="u">u-coordinates of each observation.</param>
    /// <param name="v">v-coordinates (same length as <paramref name="u"/>).</param>
    /// <param name="targets">
    /// Target values to fit (same length as <paramref name="u"/>). For
    /// forward SIP these are <c>u_true − u_observed</c>; for inverse
    /// SIP they are <c>u_observed − u_post_forward</c>.
    /// </param>
    /// <param name="order">Polynomial order (1 ≤ order ≤ MaxOrder).</param>
    /// <returns>
    /// Coefficient matrix of shape <c>[order + 1, order + 1]</c>, or
    /// <c>null</c> if the design matrix was rank-deficient (caller should
    /// fall back to a lower order or to the linear-only WCS).
    /// </returns>
    public static double[,]? Fit(
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        ReadOnlySpan<double> targets,
        int order)
    {
        if (order is < 1 or > MaxOrder)
            throw new ArgumentOutOfRangeException(nameof(order), order, $"order must be in [1, {MaxOrder}]");
        if (u.Length != v.Length || u.Length != targets.Length)
            throw new ArgumentException("u, v, targets must have equal length");

        var rows = u.Length;
        var cols = BasisCount(order);
        if (rows < cols) return null;

        var design = new double[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            FillBasisRow(design, r, u[r], v[r], order);
        }

        var solved = PolynomialLeastSquares.Solve(design, targets);
        if (solved is null) return null;

        // Unflatten the coefficient vector back into the [order+1, order+1]
        // grid following the same (i, j) iteration order used by
        // FillBasisRow so the layout round-trips cleanly with Apply.
        var coeffs = new double[order + 1, order + 1];
        var k = 0;
        for (var i = 0; i <= order; i++)
        {
            for (var j = 0; j <= order - i; j++)
            {
                if ((i | j) == 0) continue;
                coeffs[i, j] = solved[k++];
            }
        }
        return coeffs;
    }

    /// <summary>
    /// Populate one row of the design matrix at (u, v). Internal helper —
    /// kept out of the hot Apply path because it allocates uPow/vPow per
    /// call; for production evaluation use <see cref="Apply"/> which
    /// stack-allocates.
    /// </summary>
    private static void FillBasisRow(double[,] design, int row, double u, double v, int order)
    {
        Span<double> uPow = stackalloc double[order + 1];
        Span<double> vPow = stackalloc double[order + 1];
        uPow[0] = 1.0;
        vPow[0] = 1.0;
        for (var k = 1; k <= order; k++)
        {
            uPow[k] = uPow[k - 1] * u;
            vPow[k] = vPow[k - 1] * v;
        }

        var c = 0;
        for (var i = 0; i <= order; i++)
        {
            for (var j = 0; j <= order - i; j++)
            {
                if ((i | j) == 0) continue;
                design[row, c++] = uPow[i] * vPow[j];
            }
        }
    }
}
