using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>What the auto-exposure routine should do next after measuring a metering flat.</summary>
public enum FlatExposureAction
{
    /// <summary>The current exposure lands the flat level within tolerance: shoot the flats at it.</summary>
    Capture,

    /// <summary>The level is off-target but recoverable: re-meter at <see cref="FlatExposureDecision.NextExposure"/>.</summary>
    Adjust,

    /// <summary>The target cannot be reached (panel too bright/dim at the exposure bounds, or out of brackets).</summary>
    Fail
}

/// <summary>The solver's verdict for one metering iteration.</summary>
/// <param name="Action">Capture / Adjust / Fail.</param>
/// <param name="NextExposure">For <see cref="FlatExposureAction.Capture"/> the exposure to shoot at; for
/// <see cref="FlatExposureAction.Adjust"/> the next exposure to meter at; for <see cref="FlatExposureAction.Fail"/>
/// the (unchanged) current exposure.</param>
/// <param name="Reason">Populated only on <see cref="FlatExposureAction.Fail"/>.</param>
public readonly record struct FlatExposureDecision(FlatExposureAction Action, TimeSpan NextExposure, string? Reason = null);

/// <summary>
/// Pure auto-exposure convergence for flat-frame acquisition. Given the measured mean/median ADU
/// level (as a fraction of the sensor ceiling) produced by <paramref name="currentExposure"/>, it
/// brackets the exposure toward a target fraction (e.g. ~0.5 of full well) under a linear panel model
/// (flat-panel illumination is proportional to integration time below saturation). Kept pure so the
/// convergence is unit-testable independently of any camera/cover I/O.
/// </summary>
public static class FlatExposureSolver
{
    /// <param name="measuredFraction">Measured flat level in [0, 1] (median ADU / sensor ceiling) at <paramref name="currentExposure"/>.</param>
    /// <param name="currentExposure">The exposure that produced <paramref name="measuredFraction"/>.</param>
    /// <param name="targetFraction">Desired level in [0, 1] (e.g. 0.5 = half full well).</param>
    /// <param name="tolerance">Half-width of the acceptance band around <paramref name="targetFraction"/>.</param>
    /// <param name="minExposure">Shortest exposure the camera/routine will use.</param>
    /// <param name="maxExposure">Longest exposure the routine will use before giving up.</param>
    /// <param name="attempt">0-based metering attempt index.</param>
    /// <param name="maxAttempts">Total metering attempts allowed before failing.</param>
    public static FlatExposureDecision Solve(
        double measuredFraction,
        TimeSpan currentExposure,
        double targetFraction,
        double tolerance,
        TimeSpan minExposure,
        TimeSpan maxExposure,
        int attempt,
        int maxAttempts)
    {
        // Already within the acceptance band -> shoot the flats at this exposure.
        if (Math.Abs(measuredFraction - targetFraction) <= tolerance)
        {
            return new FlatExposureDecision(FlatExposureAction.Capture, currentExposure);
        }

        // Out of brackets: this was the last allowed metering attempt and it is still off-target.
        if (attempt >= maxAttempts - 1)
        {
            return new FlatExposureDecision(FlatExposureAction.Fail, currentExposure,
                $"Flat exposure did not converge to {targetFraction:P0} +/- {tolerance:P0} within {maxAttempts} brackets " +
                $"(last level {measuredFraction:P1} at {currentExposure.TotalSeconds:F3}s).");
        }

        // Linear panel model: level is proportional to exposure, so scale toward the target.
        // Guard a near-zero measurement so we don't divide by ~0 and overshoot to infinity.
        var safeMeasured = Math.Max(measuredFraction, 1e-4);
        var scale = targetFraction / safeMeasured;
        var next = TimeSpan.FromSeconds(currentExposure.TotalSeconds * scale);

        // Clamp into the exposure bounds.
        var clamped = next;
        if (clamped < minExposure) clamped = minExposure;
        if (clamped > maxExposure) clamped = maxExposure;

        // Clamped back onto the current exposure -> we are pinned at a bound and cannot reach the
        // target by changing exposure alone (the panel is too bright/dim for these bounds).
        if (clamped == currentExposure)
        {
            if (measuredFraction > targetFraction)
            {
                return new FlatExposureDecision(FlatExposureAction.Fail, currentExposure,
                    $"Flat too bright: at min exposure {minExposure.TotalSeconds:F3}s the level {measuredFraction:P1} " +
                    $"still exceeds the target {targetFraction:P0}. Lower the calibrator brightness.");
            }

            return new FlatExposureDecision(FlatExposureAction.Fail, currentExposure,
                $"Flat too dim: at max exposure {maxExposure.TotalSeconds:F3}s the level {measuredFraction:P1} " +
                $"is still below the target {targetFraction:P0}. Raise the calibrator brightness.");
        }

        return new FlatExposureDecision(FlatExposureAction.Adjust, clamped);
    }
}
