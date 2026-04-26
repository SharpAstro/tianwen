using System;

namespace TianWen.Lib.Astrometry.Focus;

/// <summary>
/// Infers focuser backlash from the verification exposure that AutoFocus already takes
/// after moving to the fitted best position. Piggybacks on the existing scan: if the
/// final move overshot enough to take up slack, mechanical position == commanded and
/// verifyHfd matches the hyperbola minimum; if overshoot was too small, mechanical
/// position lags by (B - O) and verifyHfd lands on the V-curve at that offset.
/// Inverting the hyperbola gives the lag, and B = currentOvershoot + lag.
/// </summary>
public static class BacklashEstimator
{
    /// <summary>
    /// Fractional HFD excess (relative to hyperbola minimum) at which confidence saturates
    /// at ~63% (1 - 1/e). Picked to roughly match typical seeing-induced HFD variance.
    /// </summary>
    private const double SeeingNoisePct = 0.10;

    /// <summary>
    /// Below this normalized excess we treat the verification HFD as indistinguishable
    /// from the predicted minimum and return no inference.
    /// </summary>
    private const double NoiseFloorPct = 0.02;

    /// <summary>
    /// Infers backlash from a verification exposure taken at the commanded best-focus position.
    /// </summary>
    /// <param name="solution">Hyperbola fit from the V-curve scan.</param>
    /// <param name="bestPos">Commanded encoder position after the final move.</param>
    /// <param name="verifyHfd">Median HFD measured at <paramref name="bestPos"/>.</param>
    /// <param name="currentOvershoot">Steps of overshoot used in the final BacklashCompensation move.
    /// Pass 0 if no overshoot was performed (final move was in the preferred direction) — in that
    /// case there's no backlash signal in the verification exposure.</param>
    /// <param name="focusDir">Focuser direction; used to pick the correct hyperbola root for the
    /// inferred mechanical position (mechanical lags in the preferred direction when overshoot is too small).</param>
    /// <returns>
    /// <c>BInferred</c>: estimated backlash in steps, or <c>null</c> when there's no signal
    /// (verifyHfd is at or below the predicted minimum, within noise floor).
    /// <c>Confidence</c>: 0..1 saturating with HFD excess; low confidence samples can be down-weighted
    /// or skipped by the caller.
    /// <c>InferredMechanicalPos</c>: the mechanical position the verifyHfd corresponds to (encoder + lag);
    /// useful for diagnostic logging.
    /// </returns>
    public static (int? BInferred, float Confidence, double InferredMechanicalPos) InferFromVerification(
        FocusSolution solution,
        int bestPos,
        double verifyHfd,
        int currentOvershoot,
        FocusDirection focusDir)
    {
        // No overshoot was performed → no signal (the final move was direct, in the preferred direction)
        if (currentOvershoot <= 0)
        {
            return (null, 0f, bestPos);
        }

        // Fit must be valid
        if (solution.A <= 0 || solution.B <= 0 || double.IsNaN(verifyHfd) || verifyHfd <= 0)
        {
            return (null, 0f, bestPos);
        }

        // Predicted HFD at the commanded position (== solution.A if bestPos == BestFocus exactly,
        // slightly more otherwise due to rounding to int encoder steps).
        var hPred = Hyperbola.CalculateValueAtPosition(bestPos, solution.BestFocus, solution.A, solution.B);

        // Normalised excess — how much worse verifyHfd is than predicted, scaled by the minimum HFD.
        // Below the noise floor we declare "no signal" (overshoot was at least equal to backlash, or
        // verifyHfd was a noise-favourable sample we shouldn't read into).
        var normalisedExcess = (verifyHfd - hPred) / solution.A;
        if (normalisedExcess < NoiseFloorPct)
        {
            return (null, 0f, bestPos);
        }

        // Invert the hyperbola: distance from the true minimum at which HFD == verifyHfd.
        // Two roots (symmetric); the mechanical position lies in the preferred direction
        // relative to bestPos because the unfinished return-move from the overshoot left
        // the gear engaged on the non-preferred side.
        var distFromMinimum = Hyperbola.StepsToFocus((float)verifyHfd, solution.A, solution.B);
        var inferredMechanicalPos = solution.BestFocus + focusDir.PreferredSign * distFromMinimum;

        // Lag is mechanical-vs-commanded offset; B = currentOvershoot + lag.
        var lag = Math.Abs(inferredMechanicalPos - bestPos);
        var bInferred = (int)Math.Round(currentOvershoot + lag);

        // Confidence: 1 - exp(-x / σ), x = normalised excess, σ = SeeingNoisePct.
        // Saturates near 1 for large excess, ~0 near noise floor.
        var confidence = (float)(1.0 - Math.Exp(-normalisedExcess / SeeingNoisePct));

        return (bInferred, confidence, inferredMechanicalPos);
    }

    /// <summary>
    /// Updates an exponentially weighted moving average estimate of backlash with a new sample.
    /// Caller is responsible for clamping the result to a reasonable range and for incrementing
    /// the per-focuser sample count alongside.
    /// </summary>
    /// <param name="currentEstimate">Existing EWMA, or 0 for the very first sample.</param>
    /// <param name="newSample">Newly inferred backlash from <see cref="InferFromVerification"/>.</param>
    /// <param name="alpha">Smoothing factor in (0, 1]. Default 0.3 = newest sample weighted 30%,
    /// older history decays geometrically (~3% influence after 10 runs).</param>
    /// <param name="sampleCount">Number of samples already absorbed into <paramref name="currentEstimate"/>.
    /// First sample replaces the estimate outright (no warm-up bias).</param>
    public static int UpdateEwma(int currentEstimate, int newSample, int sampleCount, double alpha = 0.3)
    {
        if (sampleCount <= 0 || currentEstimate <= 0)
        {
            return newSample;
        }

        var updated = alpha * newSample + (1.0 - alpha) * currentEstimate;
        return (int)Math.Round(updated);
    }
}
