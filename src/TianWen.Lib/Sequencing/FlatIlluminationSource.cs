namespace TianWen.Lib.Sequencing;

/// <summary>
/// Illumination source for automated flat-frame acquisition. Selects which capture strategy the
/// end-of-session flat block (<see cref="Session.TakeFlatsAsync"/>) runs.
/// </summary>
public enum FlatIlluminationSource
{
    /// <summary>
    /// A controllable calibrator panel / flip-flat (illuminated closed cover or standalone lightbox).
    /// Time-independent: converge the exposure once, then shoot the flats at a fixed exposure.
    /// </summary>
    Calibrator,

    /// <summary>
    /// The twilight sky (dawn and/or dusk). The exposure is re-metered per frame as the sky
    /// brightness ramps, and the pointing is near the anti-solar zenith with tracking off.
    /// </summary>
    TwilightSky
}

/// <summary>
/// Which twilight a sky-flat run targets. This sets the exposure-ramp direction and the
/// termination logic (see <see cref="Imaging.Calibration.SkyFlatExposureSolver"/>).
/// </summary>
public enum TwilightPeriod
{
    /// <summary>
    /// Evening twilight (sky darkening): exposures lengthen frame-to-frame; the window closes for a
    /// filter once the sky is too dark even at the maximum exposure.
    /// </summary>
    Dusk,

    /// <summary>
    /// Morning twilight (sky brightening): exposures shorten frame-to-frame; the window closes for a
    /// filter once the sky is too bright even at the minimum exposure.
    /// </summary>
    Dawn
}
