using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Routes polar-alignment lifecycle from the signal handler into the
    /// orchestrator. Pure plumbing: takes a constructed
    /// <see cref="PolarAlignmentSession"/>, drives Phase A then Phase B,
    /// reflects the orchestrator's results back into <see cref="LiveSessionState"/>
    /// fields the live view's render thread reads each frame.
    ///
    /// All math + I/O lives in <see cref="PolarAlignmentSession"/>; this helper
    /// has no business logic. Mirror of <see cref="EquipmentActions"/> /
    /// <see cref="PlannerActions"/> for the polar-alignment surface.
    /// </summary>
    internal static class PolarAlignmentActions
    {
        /// <summary>
        /// Run the full polar-alignment routine: Phase A (two-frame solve) then
        /// Phase B (live refinement) until the cancellation token fires.
        /// State is updated synchronously at each phase boundary and on each
        /// successful refinement tick. The reverse-axis restore happens via
        /// <see cref="PolarAlignmentSession.DisposeAsync"/> in the caller's
        /// finally block — this helper does not own session disposal.
        /// </summary>
        /// <remarks>
        /// Cancellation: callers pass a linked CTS so they can cancel via the
        /// <see cref="LiveSessionState.PolarAlignmentCts"/> stored alongside the
        /// session. The orchestrator's <see cref="PolarAlignmentSession.RefineAsync"/>
        /// loop honours the token, so a Cancel signal interrupts the loop and
        /// the caller's finally block disposes the session, triggering the
        /// reverse-axis restore.
        /// </remarks>
        internal static async Task RunAsync(
            PolarAlignmentSession session,
            LiveSessionState state,
            ILogger logger,
            CancellationToken ct)
        {
            // --- Phase A ---
            state.PolarPhase = PolarAlignmentPhase.ProbingExposure;
            state.PolarStatusMessage = "Probing exposure...";
            state.NeedsRedraw = true;

            // Per-rung progress: surface "Probing 200ms (rung 3/8)" as the ramp
            // walks so the user knows the routine is making forward motion during
            // the multi-second per-rung ASTAP solve attempts. Without this the
            // panel sits on "Probing exposure..." for ~50s and looks stuck.
            var progress = new Progress<ProbeProgress>(p =>
            {
                state.PolarStatusMessage = $"Probing {p.Exposure.TotalMilliseconds:F0}ms (rung {p.RungIndex + 1}/{p.RungCount})";
                state.NeedsRedraw = true;
            });

            TwoFrameSolveResult phaseA;
            try
            {
                phaseA = await session.SolveAsync(ct, progress);
            }
            catch (OperationCanceledException)
            {
                state.PolarPhase = PolarAlignmentPhase.Idle;
                state.PolarStatusMessage = "Cancelled before Phase A completed";
                state.NeedsRedraw = true;
                throw;
            }

            state.PolarPhaseAResult = phaseA;
            if (!phaseA.Success)
            {
                state.PolarPhase = PolarAlignmentPhase.Failed;
                state.PolarStatusMessage = phaseA.FailureReason ?? "Phase A failed";
                state.NeedsRedraw = true;
                logger.LogWarning("PolarAlignment Phase A failed: {Reason}", phaseA.FailureReason);
                return;
            }

            // --- Phase B ---
            state.PolarPhase = PolarAlignmentPhase.Refining;
            state.PolarStatusMessage = $"Refining at {phaseA.LockedExposure.TotalMilliseconds:F0}ms";
            state.NeedsRedraw = true;

            try
            {
                await foreach (var live in session.RefineAsync(ct))
                {
                    state.LastPolarSolve = live;
                    if (live.IsAligned && live.IsSettled && state.PolarPhase != PolarAlignmentPhase.Aligned)
                    {
                        state.PolarPhase = PolarAlignmentPhase.Aligned;
                        state.PolarStatusMessage = "Aligned within target accuracy - click Done";
                    }
                    else if (!live.IsAligned && state.PolarPhase == PolarAlignmentPhase.Aligned)
                    {
                        // User bumped a knob and broke alignment — fall back to refining.
                        state.PolarPhase = PolarAlignmentPhase.Refining;
                        state.PolarStatusMessage = "Refining...";
                    }
                    state.NeedsRedraw = true;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("PolarAlignment refinement cancelled");
                throw;
            }
        }
    }
}
