using System;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// Small-system least-squares solver built around the normal equations
/// <c>(AᵀA) x = Aᵀb</c>, factorised via Cholesky.
/// <para>
/// Intended for short, fat design matrices (many rows, &lt;= ~20 unknowns)
/// such as the polynomial coefficient fits used by <see cref="SipPolynomial"/>.
/// At those sizes the AᵀA matrix is tiny and forming it explicitly is
/// cheaper than a QR factorisation, and the symmetric PSD structure lets
/// us use Cholesky which is numerically well-behaved for the well-scaled
/// inputs SIP produces (pixel offsets centred on CRPIX).
/// </para>
/// </summary>
internal static class PolynomialLeastSquares
{
    /// <summary>
    /// Solve <c>min ||A x − b||²</c> for <paramref name="x"/>.
    /// </summary>
    /// <param name="designMatrix">
    /// Design matrix <c>A</c> of shape <c>[rows, cols]</c>. <c>rows</c> must be
    /// at least <c>cols</c> and the columns must be linearly independent.
    /// </param>
    /// <param name="rhs">
    /// Right-hand side vector <c>b</c> of length <c>rows</c>.
    /// </param>
    /// <returns>
    /// Coefficient vector of length <c>cols</c>; <c>null</c> if AᵀA was not
    /// positive-definite (i.e. design matrix is rank-deficient or has too
    /// few rows). Callers should treat <c>null</c> as a fit failure and
    /// fall back to a lower-order model.
    /// </returns>
    public static double[]? Solve(double[,] designMatrix, ReadOnlySpan<double> rhs)
    {
        var rows = designMatrix.GetLength(0);
        var cols = designMatrix.GetLength(1);
        if (rows < cols) return null;
        if (rhs.Length != rows) throw new ArgumentException("rhs length must equal designMatrix row count", nameof(rhs));

        // Column-norm preconditioning: scale each column to unit L2 norm before
        // forming the normal equations. Cholesky on AᵀA squares the design
        // matrix's condition number; for polynomial fits with raw pixel
        // offsets (~10² × ²-px monomials end up ~10⁴ apart in magnitude) this
        // is the difference between a usable fit and a numerically-poisoned
        // one. The unit-norm columns also make the symmetric diagonal-PSD
        // check robust under tiny inputs. We undo the scaling on the way out.
        var scales = new double[cols];
        for (var col = 0; col < cols; col++)
        {
            double sumSq = 0;
            for (var r = 0; r < rows; r++) sumSq += designMatrix[r, col] * designMatrix[r, col];
            var s = Math.Sqrt(sumSq);
            scales[col] = s > 0 ? s : 1.0;
        }

        // Form normal equations against the scaled design: N = ÃᵀÃ, c = Ãᵀb,
        // where Ã[:, k] = A[:, k] / scales[k]. N is symmetric so we only fill
        // the lower triangle and mirror in Cholesky.
        var n = new double[cols, cols];
        var c = new double[cols];
        for (var i = 0; i < cols; i++)
        {
            double ci = 0;
            for (var r = 0; r < rows; r++) ci += designMatrix[r, i] * rhs[r];
            c[i] = ci / scales[i];

            for (var j = 0; j <= i; j++)
            {
                double s = 0;
                for (var r = 0; r < rows; r++) s += designMatrix[r, i] * designMatrix[r, j];
                var sScaled = s / (scales[i] * scales[j]);
                n[i, j] = sScaled;
                n[j, i] = sScaled;
            }
        }

        // In-place Cholesky: N = L Lᵀ, L stored in the lower triangle of n.
        // If the matrix is not strictly positive-definite (any diagonal goes <= 0)
        // we bail out; that is the signature of a rank-deficient design matrix.
        for (var i = 0; i < cols; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = n[i, j];
                for (var k = 0; k < j; k++) sum -= n[i, k] * n[j, k];

                if (i == j)
                {
                    if (sum <= 0) return null;
                    n[i, i] = Math.Sqrt(sum);
                }
                else
                {
                    n[i, j] = sum / n[j, j];
                }
            }
        }

        // Forward substitution: L y = c.
        var y = new double[cols];
        for (var i = 0; i < cols; i++)
        {
            var sum = c[i];
            for (var k = 0; k < i; k++) sum -= n[i, k] * y[k];
            y[i] = sum / n[i, i];
        }

        // Back substitution: Lᵀ x̃ = y, then unscale: x = x̃ / scales.
        var x = new double[cols];
        for (var i = cols - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var k = i + 1; k < cols; k++) sum -= n[k, i] * x[k];
            x[i] = sum / n[i, i];
        }
        for (var i = 0; i < cols; i++) x[i] /= scales[i];

        return x;
    }
}
