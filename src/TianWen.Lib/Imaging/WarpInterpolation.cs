namespace TianWen.Lib.Imaging;

/// <summary>
/// The resampling kernel <see cref="Image.WarpToReferenceGridAsync(System.Numerics.Matrix3x2, int, int, WarpInterpolation, System.Threading.CancellationToken)"/>
/// samples a source frame with when placing it on the reference grid.
/// </summary>
/// <remarks>
/// <para>Measured on the Orion 2025-10-15 night (docs/plans/deconvolver-training.md, E2.10a, "the third
/// finding placed", and R1): a stacked master is the mean of its warped frames to 0.3 percent, and the
/// frames themselves widen from their subs' 2.15 px FWHM to 2.4 to 2.7 wherever the registration shift
/// is fractional, while frames at an integer shift keep 2.15. That is the bilinear kernel's own
/// variance, phase times one minus phase per axis: 1.18 px of FWHM in quadrature at half phase, 0.96
/// averaged over phases, about half a 2 px star's width in quadrature, on every master of every
/// session. A stack cannot be sharper than the kernel that placed its frames.</para>
/// </remarks>
public enum WarpInterpolation
{
    /// <summary>Two taps an axis, a triangle of unit base; the default and the behaviour of every
    /// master built before R1. Cheapest, never rings, and blurs by up to a pixel of FWHM in quadrature.</summary>
    Bilinear,

    /// <summary>Six taps an axis, the sinc kernel windowed to a = 3, weights normalised per sample
    /// (a tap outside the source or on a NaN drops out with its weight). Keeps a 2 px star's width to
    /// within a few percent at any phase; rings faintly around bright stars, which the probes measure.</summary>
    Lanczos3,
}
