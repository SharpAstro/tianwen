namespace TianWen.Lib.Imaging.ColorCalibration;

/// <summary>
/// What a colour calibration measured, in a form a UI can show without knowing which algorithm ran.
///
/// <para>The multipliers alone are not reportable. A white balance is only meaningful next to the
/// method that produced it and the reference it was measured against: a photometric fit over 104
/// stars against the average-spiral-galaxy SED and a sky-background grey-world guess are both
/// "R = 0.46", and they carry completely different authority. The same triple against a different
/// white reference is a different answer (Sa / Sb / Sc span 4 % in R and 6 % in B), so a number
/// shown on its own invites exactly the comparison it cannot support.</para>
///
/// <para>Purely descriptive: nothing reads this back to drive the pipeline. The multipliers that
/// render live on the document as the calibration triple; this is the provenance beside them.</para>
/// </summary>
/// <param name="Method">Short algorithm name, e.g. <c>SPCC</c> or <c>Sky background</c>.</param>
/// <param name="R">Red multiplier.</param>
/// <param name="G">Green multiplier. Always 1 by construction -- green is the reference channel.</param>
/// <param name="B">Blue multiplier.</param>
/// <param name="StarCount">
/// Stars that survived to the fit, or 0 for a method that uses no stars. This is the survivor count,
/// not the match count: it is the number the fit actually stood on.
/// </param>
/// <param name="WhiteReference">
/// The spectrum declared white, or <c>null</c> for a method that has no such notion (a grey-world /
/// sky-background estimate declares the SKY white, which is a different kind of claim).
/// </param>
public readonly record struct ColorCalibrationSummary(
    string Method,
    float R,
    float G,
    float B,
    int StarCount,
    string? WhiteReference)
{
    /// <summary>
    /// One line, sized for a tooltip: method, the full triple, and what backs it.
    /// <para>
    /// All three multipliers, not the R/B pair the old status line showed. G is always 1, but
    /// leaving it out means the reader has to know that to read the other two as ratios -- and the
    /// triple is what a PixInsight user has in front of them to compare against.
    /// </para>
    /// </summary>
    public string Describe()
    {
        var backing = (StarCount, WhiteReference) switch
        {
            ( > 0, { Length: > 0 } wr) => $" -- {StarCount} stars, {wr}",
            ( > 0, _) => $" -- {StarCount} stars",
            (_, { Length: > 0 } wr) => $" -- {wr}",
            _ => "",
        };

        return $"{Method} R={R:F3} G={G:F3} B={B:F3}{backing}";
    }
}
