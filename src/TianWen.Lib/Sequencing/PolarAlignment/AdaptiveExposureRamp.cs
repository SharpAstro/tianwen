using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.PlateSolve;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Per-rung progress emitted by <see cref="AdaptiveExposureRamp.ProbeAsync"/> so
    /// the GUI / TUI can surface "Probing 200ms (rung 3/8)" instead of a static
    /// "Probing exposure..." while ASTAP chews through 5-10 seconds per attempt.
    /// </summary>
    /// <param name="Exposure">Exposure being tried right now.</param>
    /// <param name="RungIndex">Zero-based index in the ramp.</param>
    /// <param name="RungCount">Total rungs in the ramp.</param>
    public readonly record struct ProbeProgress(TimeSpan Exposure, int RungIndex, int RungCount);

    /// <summary>
    /// Adaptive exposure ramp for the polar-alignment routine: tries each
    /// exposure in <see cref="DefaultRamp"/> until a plate solve succeeds with
    /// at least <paramref name="minStarsMatched"/> matched stars, then locks
    /// that exposure for the rest of the routine.
    ///
    /// Stops at the longest configured exposure with a clear "no solve" result
    /// instead of climbing past 5 s — if a 5 s wide-FOV frame can't solve, longer
    /// exposures are not the cure (focus / dew / light pollution / clouds).
    /// </summary>
    internal static class AdaptiveExposureRamp
    {
        /// <summary>
        /// Default ramp: 100ms, 150, 200, 250, 500, 1000, 2000, 5000.
        /// Configurable via <see cref="SessionConfiguration.PolarAlignmentExposureRamp"/>.
        /// Eight rungs span ~1.6 dex in exposure, which covers the practical range
        /// from a fast f/4 mini-guider on a dark site (100ms solves) up to an f/10
        /// SCT on a light-polluted suburban rig (5s).
        /// </summary>
        public static readonly ImmutableArray<TimeSpan> DefaultRamp = ImmutableArray.Create(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(2000),
            TimeSpan.FromMilliseconds(5000));

        /// <summary>
        /// Walk the ramp until one rung produces a plate solve with at least
        /// <paramref name="minStarsMatched"/> matched stars; that rung locks
        /// the exposure for both Phase A (axis recovery) and Phase B (live
        /// refining). Single-threshold model -- the previous two-tier
        /// strict/relaxed split existed when synth flux didn't scale with
        /// aperture and short rungs starved the matcher; with the
        /// aperture-scaled catalog branch we get plenty of stars at the
        /// shortest rung that solves at all, so picking that one rung for
        /// both phases is simpler and just as good.
        /// </summary>
        /// <param name="source">Capture source that produces FITS+WCS for one exposure.</param>
        /// <param name="solver">Plate solver to invoke (typically the active <see cref="IPlateSolverFactory"/>).</param>
        /// <param name="ramp">Exposure ramp to try, shortest first. Pass <see cref="DefaultRamp"/>
        /// or a profile-specific override.</param>
        /// <param name="minStarsMatched">The single matched-star threshold the
        /// rung must clear to "win" the ramp. Phase A and Phase B both run at
        /// the winning rung's exposure.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="progress">Optional progress reporter for the side panel.</param>
        /// <param name="logger">Optional logger.</param>
        public static async ValueTask<CaptureAndSolveResult> ProbeAsync(
            ICaptureSource source,
            IPlateSolver solver,
            ImmutableArray<TimeSpan> ramp,
            int minStarsMatched,
            CancellationToken ct,
            IProgress<ProbeProgress>? progress = null,
            ILogger? logger = null)
        {
            CaptureAndSolveResult last = default;
            for (var i = 0; i < ramp.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var exposure = ramp[i];
                progress?.Report(new ProbeProgress(exposure, i, ramp.Length));

                // Per-rung deadline so a single under-exposed frame can't strand
                // the ramp on rung 1/8 chasing noise inside the plate solver.
                // Budget = 5x exposure + 5s overhead, capped at 30s -- generous
                // enough that a 5s rung with a real catalog match completes,
                // tight enough that a 100ms rung with no stars times out within
                // ~6s and the next rung gets its turn. The user-supplied token
                // still cancels the whole routine; we only short-circuit the
                // current rung here.
                var rungBudget = exposure * 5 + TimeSpan.FromSeconds(5);
                if (rungBudget > TimeSpan.FromSeconds(30))
                {
                    rungBudget = TimeSpan.FromSeconds(30);
                }
                using var rungCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                rungCts.CancelAfter(rungBudget);

                logger?.LogInformation(
                    "PolarAlignment probe rung {RungIndex}/{RungCount} START exposure={ExposureMs:F0}ms budget={BudgetMs:F0}ms",
                    i + 1, ramp.Length, exposure.TotalMilliseconds, rungBudget.TotalMilliseconds);

                var rungStart = Stopwatch.GetTimestamp();
                bool timedOut = false;
                try
                {
                    last = await source.CaptureAndSolveAsync(exposure, solver, ct: rungCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Rung timeout -- record a synthetic failure and move on.
                    timedOut = true;
                    last = new CaptureAndSolveResult(false, null, default, 0, exposure, null,
                        FailureReason: $"Rung {exposure.TotalMilliseconds:F0}ms timed out after {rungBudget.TotalSeconds:F0}s");
                }
                var rungElapsed = Stopwatch.GetElapsedTime(rungStart);

                // Distinguish "completed past budget" from "timed out" -- if the
                // capture-source / plate solver ignores the cancellation token,
                // elapsed > budget but timedOut == false; that's the diagnostic
                // signal that some downstream layer is swallowing the token.
                logger?.LogInformation(
                    "PolarAlignment probe rung {RungIndex}/{RungCount} DONE exposure={ExposureMs:F0}ms elapsed={ElapsedMs:F0}ms budget={BudgetMs:F0}ms timedOut={TimedOut} budgetOverrun={Overrun} success={Success} stars={StarsMatched} reason={Reason}",
                    i + 1, ramp.Length, exposure.TotalMilliseconds, rungElapsed.TotalMilliseconds,
                    rungBudget.TotalMilliseconds, timedOut,
                    !timedOut && rungElapsed > rungBudget,
                    last.Success, last.StarsMatched, last.FailureReason ?? "");

                if (last.Success && last.StarsMatched >= minStarsMatched)
                {
                    return last;
                }
            }
            return last;
        }
    }
}
