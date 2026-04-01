using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A single guider error sample for RA/Dec, used to populate the live session guide graph.
/// </summary>
/// <param name="Timestamp">When this sample was recorded.</param>
/// <param name="RaError">RA error in arcseconds.</param>
/// <param name="DecError">Dec error in arcseconds.</param>
/// <param name="RaCorrectionMs">RA correction pulse in ms (positive = West). 0 = no correction.</param>
/// <param name="DecCorrectionMs">Dec correction pulse in ms (positive = North). 0 = no correction.</param>
/// <param name="IsDither">True if a dither event started at this sample.</param>
/// <param name="IsSettling">True if the guider is settling (post-dither or post-calibration).</param>
public readonly record struct GuideErrorSample(
    DateTimeOffset Timestamp,
    double RaError,
    double DecError,
    double RaCorrectionMs = 0,
    double DecCorrectionMs = 0,
    bool IsDither = false,
    bool IsSettling = false);
