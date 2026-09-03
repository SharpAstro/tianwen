using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>
    /// The polynomial basis the classical background fit uses, stated once so the fit and every reader
    /// of its coefficients (<see cref="ChannelFitDiagnostics.Coefficients"/>) agree on the term order
    /// and the coordinate convention.
    /// </summary>
    /// <remarks>
    /// <para>The model is <c>sum_k c[k] x^i y^j</c> with <c>i + j &lt;= degree</c>, over plane
    /// coordinates normalised to <c>[-1, 1]</c> on each axis (<see cref="Normalise"/>: <c>x</c> is the
    /// column, <c>y</c> the row, so <c>+y</c> is DOWN a top-down frame). Terms are enumerated by total
    /// degree, then by descending power of <c>x</c>: <c>1, x, y, x^2, xy, y^2, x^3, ...</c>
    /// (<see cref="Exponents"/>). A degree-2 vector is therefore
    /// <c>[c00, c10, c01, c20, c11, c02]</c>, its gradient at the frame centre is <c>(c10, c01)</c>
    /// per normalised unit and its Hessian is <c>[[2 c20, c11], [c11, 2 c02]]</c>.</para>
    /// <para>The coefficients are in the plane's own image units and describe the WORKING grid the fit
    /// ran on (a block mean of the plane). Because the normalisation maps both grids onto the same
    /// square, direction and shape read the same at either resolution; only a per-pixel slope needs the
    /// grid's width and height to convert.</para>
    /// </remarks>
    public static class BackgroundPolynomial
    {
        private static readonly ImmutableArray<ImmutableArray<(int X, int Y)>> ExponentsByDegree = BuildExponents(maxDegree: 8);

        /// <summary>Number of coefficients a tensor polynomial of <paramref name="degree"/> has.</summary>
        public static int TermCount(int degree) => (degree + 1) * (degree + 2) / 2;

        /// <summary>
        /// The degree a coefficient vector of <paramref name="termCount"/> entries encodes, or -1 when
        /// no degree has that many terms (a rank-deficient fit falls back one degree at a time, so a
        /// reader must ask rather than assume).
        /// </summary>
        public static int DegreeOf(int termCount)
        {
            for (var d = 0; TermCount(d) <= termCount; d++)
            {
                if (TermCount(d) == termCount)
                {
                    return d;
                }
            }
            return -1;
        }

        /// <summary>The <c>(i, j)</c> exponents of <c>x^i y^j</c> for each coefficient of <paramref name="degree"/>, in coefficient order.</summary>
        public static ImmutableArray<(int X, int Y)> Exponents(int degree)
        {
            if (degree < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(degree), degree, "degree must be non-negative");
            }
            return degree < ExponentsByDegree.Length ? ExponentsByDegree[degree] : BuildExponents(degree)[degree];
        }

        /// <summary>Normalised coordinate of pixel <paramref name="index"/> on an axis of <paramref name="extent"/> pixels: -1 at the first, +1 at the last, 0 for a one-pixel axis.</summary>
        public static double Normalise(int index, int extent) => extent > 1 ? index * 2.0 / (extent - 1) - 1.0 : 0.0;

        /// <summary>Evaluates the polynomial at normalised coordinates; an empty vector evaluates to 0.</summary>
        public static double Evaluate(ReadOnlySpan<double> coefficients, double xn, double yn)
        {
            if (coefficients.IsEmpty)
            {
                return 0.0;
            }
            var degree = DegreeOf(coefficients.Length);
            if (degree < 0)
            {
                throw new ArgumentException($"{coefficients.Length} coefficients is not a whole degree", nameof(coefficients));
            }
            var exponents = Exponents(degree);
            var sum = 0.0;
            for (var k = 0; k < coefficients.Length; k++)
            {
                var (i, j) = exponents[k];
                sum += coefficients[k] * Math.Pow(xn, i) * Math.Pow(yn, j);
            }
            return sum;
        }

        private static ImmutableArray<ImmutableArray<(int X, int Y)>> BuildExponents(int maxDegree)
        {
            var byDegree = ImmutableArray.CreateBuilder<ImmutableArray<(int X, int Y)>>(maxDegree + 1);
            for (var d = 0; d <= maxDegree; d++)
            {
                var terms = ImmutableArray.CreateBuilder<(int X, int Y)>(TermCount(d));
                for (var total = 0; total <= d; total++)
                {
                    for (var i = total; i >= 0; i--)
                    {
                        terms.Add((i, total - i));
                    }
                }
                byDegree.Add(terms.MoveToImmutable());
            }
            return byDegree.MoveToImmutable();
        }
    }
}
