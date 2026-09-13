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
    /// within a few percent at any phase. Rings on a DELTA: a mono 2 px star does not ring measurably
    /// (0.04 percent of the peak, the noise), but a DEBAYERED OSC plane samples that star on a 2 px
    /// pitch, so per colour plane it is a spike with 6 percent at the neighbours, and the negative
    /// lobes dig a ring of 13 percent of the peak two pixels out on every fractional-phase sub
    /// (measured on the synthetic RGGB fixture, 2026-09-12). Kept unclamped for R1's measurements and
    /// for mono data; the default is the clamped twin.</summary>
    Lanczos3,

    /// <summary>The default since 7.1. <see cref="Lanczos3"/> with PixInsight's clamping rule (PCL
    /// <c>LanczosInterpolation</c>, what StarAlignment's "Lanczos-3" is): per sample the negative-lobe
    /// contribution is compared with the positive one, attenuated smoothly once it exceeds the
    /// threshold and dropped once it reaches it. The threshold is 0.7, not PixInsight's 0.3, and it
    /// was measured rather than inherited (<c>Image.LanczosClampingThreshold</c> carries the sweep):
    /// at 0.3 the clamp lifts the skirt of every smooth star by 0.73 px of second-moment width in
    /// quadrature, at 0.7 it adds nothing, and the ring a per-plane spike draws is bounded to 0.8
    /// percent of the peak either way (5.9 unclamped; 13 on the debayered fixture's subs). This is
    /// what makes Lanczos usable on debayered OSC frames, where every star is a spike per plane. Not
    /// a knob, deliberately: a kernel that means one thing can be named in a bake's provenance.</summary>
    Lanczos3Clamped,
}
