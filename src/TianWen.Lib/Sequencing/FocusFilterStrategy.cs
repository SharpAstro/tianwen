namespace TianWen.Lib.Sequencing;

/// <summary>
/// Controls which filter is used during auto-focus.
/// </summary>
public enum FocusFilterStrategy
{
    /// <summary>
    /// Automatic: uses luminance for pure mirror designs and astrographs (CA-free),
    /// uses luminance with offsets for refractive designs with known offsets,
    /// falls back to focusing on the scheduled filter when refractive + no offsets defined.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always focus on the luminance filter, regardless of optical design or offset availability.
    /// </summary>
    UseLuminance,

    /// <summary>
    /// Always focus on whatever filter the imaging loop is currently using.
    /// </summary>
    UseScheduledFilter
}
