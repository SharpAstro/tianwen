using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>
    /// What the fit did on one fitted plane. For a CFA mosaic there are four of these (the photosite
    /// planes R, G1, G2, B, in that order) against one image channel.
    /// </summary>
    /// <param name="Plane">Index of the fitted plane.</param>
    /// <param name="Iterations">Fit iterations run (each is a refit on the surviving pixels).</param>
    /// <param name="Converged">Whether the kept fraction settled within the tolerance before the iteration cap.</param>
    /// <param name="KeptFraction">Fraction of the VALID working pixels the final fit was made on (valid = finite and
    /// not excluded by the caller).</param>
    /// <param name="ExcludedFraction">Fraction of ALL working pixels the fit could never use: caller exclusion
    /// polygons plus blocks with no finite data.</param>
    /// <param name="ResidualSigma">Robust sigma (1.4826 x MAD) of the residual over the kept pixels, image units.</param>
    /// <param name="ResidualRms">RMS of the residual over the kept pixels, image units.</param>
    /// <param name="Level">The level added back (median of the model), image units; 0 when the level is not preserved.</param>
    /// <param name="Coefficients">The polynomial stage's coefficients in <see cref="BackgroundPolynomial"/> term order
    /// (<c>1, x, y, x^2, xy, y^2, ...</c> over <c>[-1, 1]</c> normalised working-grid coordinates, x = column, y = row),
    /// image units. The fitted degree is <see cref="BackgroundPolynomial.DegreeOf"/> of the length, which is lower
    /// than the requested one after a rank-deficiency fallback; empty when nothing was fitted. With
    /// <see cref="BackgroundExtractionOptions.SurfaceRefinement"/> on, the model also carries the inpainted surface,
    /// which these do not describe.</param>
    public sealed record ChannelFitDiagnostics(
        int Plane, int Iterations, bool Converged, float KeptFraction, float ExcludedFraction, float ResidualSigma, float ResidualRms, float Level,
        ImmutableArray<double> Coefficients)
    {
        // An ImmutableArray compares by reference, which would make two identical fits unequal records;
        // the coefficients are part of the value, so compare them as a sequence.
        public bool Equals(ChannelFitDiagnostics? other) =>
            other is not null
            && Plane == other.Plane && Iterations == other.Iterations && Converged == other.Converged
            && KeptFraction.Equals(other.KeptFraction) && ExcludedFraction.Equals(other.ExcludedFraction)
            && ResidualSigma.Equals(other.ResidualSigma) && ResidualRms.Equals(other.ResidualRms) && Level.Equals(other.Level)
            && Coefficients.AsSpan().SequenceEqual(other.Coefficients.AsSpan());

        public override int GetHashCode() =>
            HashCode.Combine(Plane, Iterations, Converged, KeptFraction, ResidualSigma, ResidualRms, Level, Coefficients.IsDefault ? 0 : Coefficients.Length);
    }

    /// <summary>
    /// The cleaned frame, the background model it was cleaned with, and per-plane fit diagnostics.
    /// The caller owns both images and releases each when done.
    /// </summary>
    /// <param name="Cleaned">The corrected frame: gradient shape removed, sky level preserved, same shape and
    /// metadata as the source. A NaN source pixel stays NaN.</param>
    /// <param name="Background">The fitted model at source resolution, finite everywhere, in the source's own
    /// units. It is the same interop surface a GraXpert or Siril background export is: subtracting it from the
    /// source and adding its median back reproduces <paramref name="Cleaned"/>.</param>
    /// <param name="Planes">One entry per fitted plane.</param>
    public sealed record BackgroundExtractionResult(Image Cleaned, Image Background, ImmutableArray<ChannelFitDiagnostics> Planes)
    {
        /// <summary>RMS of the kept residuals over all planes, image units. The one-number diagnostic.</summary>
        public float ResidualRms
        {
            get
            {
                if (Planes.IsDefaultOrEmpty)
                {
                    return 0f;
                }
                var sumSq = 0.0;
                foreach (var p in Planes)
                {
                    sumSq += (double)p.ResidualRms * p.ResidualRms;
                }
                return (float)Math.Sqrt(sumSq / Planes.Length);
            }
        }
    }
}
