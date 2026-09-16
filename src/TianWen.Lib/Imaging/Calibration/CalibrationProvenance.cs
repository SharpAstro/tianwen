namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Which calibration masters a frame was actually calibrated with, named by the resolver's own group
/// slugs. Null members mean that kind of master was not applied.
/// </summary>
/// <remarks>
/// <para>The slug is the same string the master cache names its file with, so each value joins
/// directly to a file under a bake's <c>masters/</c> directory without any further lookup. It is the
/// master's IDENTITY rather than a path on purpose: a path is specific to one output directory and
/// stops being true the moment a dataset is moved or rebuilt elsewhere, while the slug keeps meaning
/// the same master.</para>
/// <para>Recorded per session in the PSF store, which is what makes "which sessions used this flat"
/// a grep rather than a re-bake.</para>
/// </remarks>
/// <param name="Dark">Slug of the applied dark master, null when none was.</param>
/// <param name="Flat">Slug of the applied flat master, null when none was.</param>
/// <param name="Bias">Slug of the applied bias master, null when none was.</param>
/// <param name="DarkBias">Slug of the bias used to SCALE the dark, null when the dark was applied
/// unscaled. Distinct from <paramref name="Bias"/>: this one never touches the light, it only
/// separates the dark's offset from its thermal signal.</param>
public sealed record CalibrationProvenance(
    string? Dark = null,
    string? Flat = null,
    string? Bias = null,
    string? DarkBias = null)
{
    /// <summary>True when nothing was recorded, so an empty record need not be stored.</summary>
    public bool IsEmpty => Dark is null && Flat is null && Bias is null && DarkBias is null;
}
