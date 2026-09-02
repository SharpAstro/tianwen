using System;
using System.Collections.Immutable;
using System.Numerics;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>How the fitted background is taken out of the frame.</summary>
    public enum BackgroundCorrection
    {
        /// <summary><c>source - background + level</c>: an additive gradient (light pollution, sky glow).</summary>
        Subtract,

        /// <summary><c>source / background * level</c>: a multiplicative residue (vignetting a flat did not take out).
        /// The right fix is a better flat; this is the escape hatch when there is none.</summary>
        Divide,
    }

    /// <summary>
    /// A region the fit must not look at, in FULL-IMAGE pixel coordinates (a galaxy core, the bright
    /// heart of a nebula the user wants preserved). Rasterised onto the working grid with an even-odd
    /// test at each working pixel's centre.
    /// </summary>
    /// <remarks>
    /// A class rather than a record so it promises nothing about value equality: an
    /// <see cref="ImmutableArray{T}"/> member compares by reference, which would make two polygons with
    /// identical vertices unequal for no reason a caller could see.
    /// </remarks>
    public sealed class ExclusionPolygon
    {
        public ExclusionPolygon(ImmutableArray<Vector2> vertices)
        {
            if (vertices.IsDefault || vertices.Length < 3)
            {
                throw new ArgumentException("An exclusion polygon needs at least three vertices.", nameof(vertices));
            }
            Vertices = vertices;
        }

        /// <summary>Polygon vertices in full-image pixel coordinates, in order, implicitly closed.</summary>
        public ImmutableArray<Vector2> Vertices { get; }

        /// <summary>An axis-aligned rectangle spanning <c>[x0, x1] x [y0, y1]</c> in full-image pixels.</summary>
        public static ExclusionPolygon Rectangle(float x0, float y0, float x1, float y1)
            => new ExclusionPolygon([new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1)]);

        /// <summary>Even-odd point-in-polygon test.</summary>
        public bool Contains(float x, float y)
        {
            var inside = false;
            var n = Vertices.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = Vertices[i];
                var b = Vertices[j];
                if ((a.Y > y) != (b.Y > y))
                {
                    var xAtY = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
                    if (x < xAtY)
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }
    }

    /// <summary>
    /// Parameters of the classical background fit. The defaults are the ones the three reference
    /// implementations agree on (PixInsight GradientCorrection as ported by Siril's
    /// AutoGradientRemoval, Siril's AutoBGE, SAS Pro's abe.py), stated in the plan's review; the
    /// ones the plan marks "to be measured" say so on the property.
    /// </summary>
    /// <remarks>
    /// <para><b>The model is a stiff polynomial with an optional flexible refinement on top.</b> The
    /// polynomial (degree 2 by default) carries the bulk gradient and, because it is evaluated
    /// everywhere, is what the model says inside a region the fit was kept out of. The refinement
    /// (<see cref="SurfaceRefinement"/>) is a masked low-pass inpainting surface fitted to the
    /// polynomial's residual: three separable box blurs and a mask, no samples, no dense solve. It
    /// follows a light-pollution dome the quadratic cannot, and it can also follow a frame-filling
    /// nebula and hollow it out, which is why it is off by default for the unattended pipeline role
    /// and one flag away for everything else. Measure before flipping the default.</para>
    /// <para><b>Every threshold is in noise units</b>: a multiple of the robust sigma
    /// (<c>1.4826 x MAD</c>) of the fit residual over the pixels currently kept. Note that the noise
    /// in question is the WORKING grid's, a block mean of <c>Downsample^2</c> source pixels, so it is
    /// that many times smaller than the frame's per-pixel noise.</para>
    /// <para><b>Convergence is automatic</b> (the option PixInsight's GradientCorrection calls by that
    /// name, and here it is always on) for the polynomial stage: the fit is re-run on the surviving
    /// pixels until the kept fraction moves by less than <see cref="ConvergenceTolerance"/>, capped
    /// at <see cref="MaxIterations"/>. The surface stage runs once on top of the converged polynomial;
    /// <see cref="RobustBackgroundFit"/> says why iterating it fed on itself.</para>
    /// </remarks>
    public sealed record BackgroundExtractionOptions
    {
        public static BackgroundExtractionOptions Default { get; } = new BackgroundExtractionOptions();

        /// <summary>Block-mean downsample factor before fitting (SAS Pro dialog 4; its headless preset 6). A
        /// CFA mosaic is split into its four photosite planes first and fitted at half this factor, so the
        /// working resolution stays the same.</summary>
        public int Downsample { get; init; } = 4;

        /// <summary>Degree of the stiff tensor polynomial <c>x^i y^j, i + j &lt;= degree</c>, on coordinates
        /// normalised to <c>[-1, 1]</c>. 0 is a constant; every reference defaults to 2; capped at 6 (SAS Pro's
        /// cap, needed there because it fits float32 on raw coordinates; here it is a choice).</summary>
        public int PolynomialDegree { get; init; } = 2;

        /// <summary>Add the inpainted low-pass surface on top of the polynomial. Off by default; see the type
        /// remarks for why.</summary>
        public bool SurfaceRefinement { get; init; }

        /// <summary>The surface's model radius as a percentage of the smaller working dimension (default 5:
        /// AutoGradientRemoval's <c>scale</c> 5 of 1 to 10). Features wider than roughly twice this are
        /// background to the surface; narrower ones are structure.</summary>
        public float SurfaceScalePercent { get; init; } = 5f;

        /// <summary>Blur-and-restore passes of the inpainting; each diffuses background about one radius into
        /// the holes, so holes up to about ten radii wide are bridged (default 10).</summary>
        public int SurfaceInpaintPasses { get; init; } = 10;

        /// <summary>Final smoothing of the surface, as a multiple of the model radius (default 1.0, 0 disables).</summary>
        public float SurfaceSmoothness { get; init; } = 1f;

        /// <summary>Bright rejection: a pixel whose residual sits more than this many sigma ABOVE the robust median
        /// leaves the fit (default 2; structure is bright).</summary>
        public float RejectBrightSigma { get; init; } = 2f;

        /// <summary>Dark rejection: more than this many sigma BELOW the median (default 4; dark outliers are mostly
        /// noise, so the asymmetry keeps the sky).</summary>
        public float RejectDarkSigma { get; init; } = 4f;

        /// <summary>Upper bound on fit iterations (default 20).</summary>
        public int MaxIterations { get; init; } = 20;

        /// <summary>The fit has converged when the kept fraction moves by less than this between iterations
        /// (default 1e-4).</summary>
        public float ConvergenceTolerance { get; init; } = 1e-4f;

        /// <summary>Never keep fewer than this fraction of the valid pixels (default 2 percent, and never fewer
        /// than 16 pixels): when rejection would go below it, the pixels closest to the median are kept instead.</summary>
        public float MinKeptFraction { get; init; } = 0.02f;

        /// <summary>Grow the bright-rejected regions so a nebula's dim wings leave the fit too (default on).</summary>
        public bool ProtectStructure { get; init; } = true;

        /// <summary>Polynomial stage: structure seeds are pixels more than this many sigma above the median residual
        /// (default 3). Above a stiff quadratic a light-pollution dome IS structure and should leave that fit.
        /// <b>To be measured</b> on real masters: the reference states this threshold in absolute stretched pixel
        /// units (0.05), which has no meaning on a linear frame, so the default here is a reasoned start, not a
        /// measured one.</summary>
        public float StructureThresholdSigma { get; init; } = 3f;

        /// <summary>Surface stage: structure seeds are pixels more than this many sigma above the median of the
        /// residual against the surface itself (default 10). The surface follows anything wider than a few model
        /// radii, and a smooth feature of scale <c>s</c> leaks about <c>(sigma_blur / s)^2</c> of its amplitude
        /// into that residual: a dome spanning a quarter of the frame leaks a few sigma of block-mean noise, a
        /// nebula core a few radii wide leaks tens. Ten sits between them on the synthetic cases; <b>to be
        /// measured</b> on real masters like its polynomial-stage sibling.</summary>
        public float SurfaceStructureThresholdSigma { get; init; } = 10f;

        /// <summary>How far the seeds grow (default 0.5): the seed map is low-passed with radius
        /// <c>modelRadius x (0.5 + amount)</c> and thresholded at <c>(1 - amount) x 0.5</c>.</summary>
        public float StructureAmount { get; init; } = 0.5f;

        /// <summary>Subtract (additive gradient, the default) or divide (multiplicative residue).</summary>
        public BackgroundCorrection Correction { get; init; } = BackgroundCorrection.Subtract;

        /// <summary>Add the background's median back per channel so the sky LEVEL survives and only its shape
        /// goes (default on). Per channel, so a colour gradient's removal does not double as a background
        /// neutralisation, which is a separate step. Never re-baseline to the fit minimum.</summary>
        public bool PreserveLevel { get; init; } = true;

        /// <summary>Regions the fit must not look at, in full-image pixel coordinates.</summary>
        public ImmutableArray<ExclusionPolygon> Exclusions { get; init; } = [];

        /// <summary>Throws when a value is outside the range the fit is defined for.</summary>
        public void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(Downsample, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(PolynomialDegree);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(PolynomialDegree, 6);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SurfaceScalePercent);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(SurfaceScalePercent, 50f);
            ArgumentOutOfRangeException.ThrowIfLessThan(SurfaceInpaintPasses, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(SurfaceSmoothness);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RejectBrightSigma);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RejectDarkSigma);
            ArgumentOutOfRangeException.ThrowIfLessThan(MaxIterations, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(ConvergenceTolerance);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinKeptFraction);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(MinKeptFraction, 1f);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StructureThresholdSigma);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SurfaceStructureThresholdSigma);
            ArgumentOutOfRangeException.ThrowIfNegative(StructureAmount);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(StructureAmount, 1f);
        }
    }
}
