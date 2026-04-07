using System;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// What triggered the plate solve, used for filtering and display in session UI and the hosting API.
/// </summary>
public enum PlateSolveContext
{
    /// <summary>Pointing correction after a slew (e.g. calibration slew sync).</summary>
    MountSync,

    /// <summary>Iterative centering loop converging on a target.</summary>
    Centering,

    /// <summary>Guider camera solve during the rough-focus check.</summary>
    GuiderFocus,
}

/// <summary>
/// Snapshot of a single plate solve attempt, stored for the session history panel and hosting API.
/// Failures are recorded with <see cref="Solution"/> == <c>null</c>.
/// </summary>
/// <param name="Timestamp">UTC time the solve completed (or failed).</param>
/// <param name="Context">What triggered this solve.</param>
/// <param name="OtaName">
///     Name of the OTA whose camera produced the image, or the guider display name for
///     <see cref="PlateSolveContext.GuiderFocus"/>.
/// </param>
/// <param name="Succeeded">Whether the solve produced a usable WCS solution.</param>
/// <param name="Solution">The WCS solution, or <c>null</c> on failure.</param>
/// <param name="Elapsed">Wall-clock solve duration.</param>
/// <param name="DetectedStars">Stars detected in the image (0 when not reported by solver).</param>
/// <param name="MatchedStars">Stars matched to catalog (0 when not reported by solver).</param>
public readonly record struct PlateSolveRecord(
    DateTimeOffset Timestamp,
    PlateSolveContext Context,
    string OtaName,
    bool Succeeded,
    WCS? Solution,
    TimeSpan Elapsed,
    int DetectedStars = 0,
    int MatchedStars = 0);
