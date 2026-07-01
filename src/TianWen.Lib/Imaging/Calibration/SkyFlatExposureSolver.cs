using System;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>What a twilight sky-flat capture loop should do next after measuring a frame.</summary>
public enum SkyFlatAction
{
    /// <summary>The frame is within tolerance: keep it, then meter the next at <see cref="SkyFlatDecision.NextExposure"/>.</summary>
    Capture,

    /// <summary>Off-target but recoverable within the exposure bounds: discard and re-meter at <see cref="SkyFlatDecision.NextExposure"/>.</summary>
    Adjust,

    /// <summary>Pinned at an exposure bound, but the sky is still ramping <em>toward</em> the target: discard and wait for the sky to change.</summary>
    Wait,

    /// <summary>Pinned at an exposure bound and the sky is ramping <em>away</em> from the target: this filter's twilight window has closed.</summary>
    Stop
}

/// <summary>The sky-flat solver's verdict for one metered frame.</summary>
/// <param name="Action">Capture / Adjust / Wait / Stop.</param>
/// <param name="NextExposure">For <see cref="SkyFlatAction.Capture"/> the exposure to meter the <em>next</em> frame at
/// (re-centred against the drifting sky); for <see cref="SkyFlatAction.Adjust"/> the next exposure to meter at; for
/// <see cref="SkyFlatAction.Wait"/> / <see cref="SkyFlatAction.Stop"/> the (unchanged) current exposure.</param>
/// <param name="Reason">Populated on <see cref="SkyFlatAction.Wait"/> / <see cref="SkyFlatAction.Stop"/>.</param>
public readonly record struct SkyFlatDecision(SkyFlatAction Action, TimeSpan NextExposure, string? Reason = null);

/// <summary>
/// Pure per-frame exposure convergence for <em>twilight sky-flats</em>. Unlike a static calibrator panel
/// (see <see cref="FlatExposureSolver"/>, which converges once then shoots at a fixed exposure), the
/// twilight sky brightness drifts, so every frame is re-metered and the exposure re-centred against the
/// linear panel model. This solver wraps <see cref="FlatExposureSolver"/> and layers the twilight-direction
/// awareness on top: when the routine runs out of exposure headroom it distinguishes "wait, the sky is
/// still ramping toward the target" (dawn too-dim / dusk too-bright) from "stop, the window has closed"
/// (dawn too-bright / dusk too-dim). Kept pure so the convergence + wait/stop logic is unit-testable
/// independently of any camera/mount I/O.
/// </summary>
public static class SkyFlatExposureSolver
{
    /// <param name="period">Which twilight is being shot; sets the ramp direction.</param>
    /// <param name="measuredFraction">Measured flat level in [0, 1] (median ADU / sensor ceiling) at <paramref name="currentExposure"/>.</param>
    /// <param name="currentExposure">The exposure that produced <paramref name="measuredFraction"/>.</param>
    /// <param name="targetFraction">Desired level in [0, 1] (e.g. 0.5 = half full well).</param>
    /// <param name="tolerance">Half-width of the acceptance band around <paramref name="targetFraction"/>.</param>
    /// <param name="minExposure">Shortest exposure the routine will use.</param>
    /// <param name="maxExposure">Longest exposure the routine will use.</param>
    public static SkyFlatDecision Decide(
        TwilightPeriod period,
        double measuredFraction,
        TimeSpan currentExposure,
        double targetFraction,
        double tolerance,
        TimeSpan minExposure,
        TimeSpan maxExposure)
    {
        // attempt=0, maxAttempts=2 so the underlying solver never reports "out of brackets" (that guard
        // is for the panel path's fixed bracket budget); it classifies purely into Capture / Adjust /
        // Fail(pinned-at-bound), which is what the sky loop needs to interpret.
        var decision = FlatExposureSolver.Solve(
            measuredFraction, currentExposure, targetFraction, tolerance, minExposure, maxExposure,
            attempt: 0, maxAttempts: 2);

        switch (decision.Action)
        {
            case FlatExposureAction.Capture:
                // Re-centre the next frame's exposure against the drifting sky under the linear model.
                var safeMeasured = Math.Max(measuredFraction, 1e-4);
                var next = TimeSpan.FromSeconds(currentExposure.TotalSeconds * (targetFraction / safeMeasured));
                var clamped = next < minExposure ? minExposure : next > maxExposure ? maxExposure : next;
                return new SkyFlatDecision(SkyFlatAction.Capture, clamped);

            case FlatExposureAction.Adjust:
                return new SkyFlatDecision(SkyFlatAction.Adjust, decision.NextExposure);

            default:
                // Fail = pinned at an exposure bound. tooBright => pinned at min; tooDim => pinned at max.
                // The sky is still moving toward the target when dawn is too dim (it is brightening) or
                // dusk is too bright (it is darkening); otherwise this filter's window has closed.
                var tooBright = measuredFraction > targetFraction;
                var willImprove = period == TwilightPeriod.Dawn ? !tooBright : tooBright;
                return willImprove
                    ? new SkyFlatDecision(SkyFlatAction.Wait, currentExposure, decision.Reason)
                    : new SkyFlatDecision(SkyFlatAction.Stop, currentExposure, decision.Reason);
        }
    }
}
