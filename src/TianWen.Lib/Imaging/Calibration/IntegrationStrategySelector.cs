using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Two-phase picker over <see cref="IIntegrationStrategy"/>:
/// <list type="number">
///   <item>Gate: each strategy's <see cref="IIntegrationStrategy.Evaluate"/>
///         marks itself <see cref="StrategyFit.CanRun"/> = false when it
///         doesn't fit under the budget, AND the selector drops strategies
///         whose <see cref="IIntegrationStrategy.SupportsLiveStacking"/>
///         doesn't match the probe's live-stacking flag.</item>
///   <item>Rank: survivors get a weighted score from
///         <see cref="RankingPolicy.Score"/> over their
///         <see cref="IIntegrationStrategy.FidelityScore"/> and a 0..1
///         normalised speed (1 = fastest survivor, 0 = slowest).</item>
/// </list>
/// The full considered-list is returned alongside the winner so callers can
/// log every row (`[strategy] X NO/YES ...`).
/// </summary>
public static class IntegrationStrategySelector
{
    /// <summary>The result of one pick: who won, why, and what every
    /// candidate had to say.</summary>
    public sealed record Selection(
        IIntegrationStrategy Chosen,
        ImmutableArray<Candidate> Considered,
        RankingPolicy RankedBy,
        string Notes);

    /// <summary>Per-candidate row: the strategy, its fit verdict, and its
    /// final ranked score (only meaningful when <see cref="StrategyFit.CanRun"/>
    /// is true; <c>0</c> otherwise).</summary>
    public sealed record Candidate(
        IIntegrationStrategy Strategy,
        StrategyFit Fit,
        double Score);

    /// <summary>
    /// Pick the highest-scoring strategy that fits.
    /// </summary>
    /// <param name="probe">Snapshot of the job + host.</param>
    /// <param name="budget">RAM / disk safety factors (default 60% / 80%).</param>
    /// <param name="policy">How fidelity vs estimated speed blends into the
    /// score (default <see cref="RankingPolicy.FidelityFirst"/> -- closest
    /// to the pre-ranking behaviour).</param>
    /// <param name="preferred">User override. Wins even on a non-fitting
    /// verdict; caller is responsible for refusing or warning if
    /// <see cref="StrategyFit.CanRun"/> is false on the chosen candidate.</param>
    /// <param name="pool">Strategies to consider. Defaults to one instance of
    /// each built-in strategy.</param>
    /// <exception cref="InvalidOperationException">No strategy fit and no
    /// override was supplied.</exception>
    public static Selection Pick(
        IntegrationProbe probe,
        ResourceBudget? budget = null,
        RankingPolicy? policy = null,
        IntegrationStrategyKind? preferred = null,
        IEnumerable<IIntegrationStrategy>? pool = null)
    {
        budget ??= new ResourceBudget();
        policy ??= RankingPolicy.FidelityFirst;

        // Drop strategies whose live-stacking capability doesn't match the
        // probe up front -- they're not "rejected" so much as "wrong tool",
        // and including them in the log would just add noise.
        var filtered = (pool ?? DefaultStrategies())
            .Where(s => s.SupportsLiveStacking == probe.LiveStacking)
            .ToList();

        if (filtered.Count == 0)
        {
            throw new InvalidOperationException(
                $"No strategies in the pool match LiveStacking={probe.LiveStacking}. " +
                $"Check the configured strategy set.");
        }

        var evaluated = filtered
            .Select(s => (Strategy: s, Fit: s.Evaluate(probe, budget)))
            .ToList();

        // User override: pick it, log the considered list as-is. Score
        // column is zero because we didn't rank.
        if (preferred is { } pref)
        {
            var match = evaluated.FirstOrDefault(c => c.Strategy.Kind == pref);
            if (match.Strategy is not null)
            {
                var withZeroScores = evaluated
                    .Select(c => new Candidate(c.Strategy, c.Fit, 0))
                    .ToImmutableArray();
                return new Selection(
                    Chosen: match.Strategy,
                    Considered: withZeroScores,
                    RankedBy: policy,
                    Notes: $"user override --strategy {pref}" + (match.Fit.CanRun ? "" : " (warning: fit reports CanRun=false)"));
            }
            // Preferred wasn't in the pool -- fall through to ranking.
        }

        var survivors = evaluated.Where(c => c.Fit.CanRun).ToList();
        if (survivors.Count == 0)
        {
            var rationaleList = string.Join("; ", evaluated.Select(c => $"{c.Strategy.Kind}={c.Fit.Rationale}"));
            throw new InvalidOperationException(
                $"No integration strategy fits probe (N={probe.FrameCount}, canvas={probe.CanvasWidth}x{probe.CanvasHeight}, " +
                $"ramFree={probe.AvailableRamBytes / 1e9:F1}GB, diskFree={probe.AvailableDiskBytes / 1e9:F1}GB): {rationaleList}");
        }

        var minMs = survivors.Min(c => c.Fit.EstimatedDuration.TotalMilliseconds);
        var maxMs = survivors.Max(c => c.Fit.EstimatedDuration.TotalMilliseconds);
        var range = Math.Max(1.0, maxMs - minMs);

        // For each candidate compute the score. Non-survivors stay at 0 so
        // log readers can see who was gated out.
        var ranked = evaluated
            .Select(c =>
            {
                if (!c.Fit.CanRun) return new Candidate(c.Strategy, c.Fit, 0);
                var speedNorm = 1.0 - (c.Fit.EstimatedDuration.TotalMilliseconds - minMs) / range;
                var score = policy.Score(c.Strategy.FidelityScore, speedNorm);
                return new Candidate(c.Strategy, c.Fit, score);
            })
            .ToImmutableArray();

        var winner = ranked
            .Where(c => c.Fit.CanRun)
            .OrderByDescending(c => c.Score)
            .First();

        return new Selection(
            Chosen: winner.Strategy,
            Considered: ranked,
            RankedBy: policy,
            Notes: $"score={winner.Score:F3} (fidelity={winner.Strategy.FidelityScore:F2}, eta={winner.Fit.EstimatedDuration.TotalSeconds:F0}s)");
    }

    /// <summary>The built-in strategy set. One instance per kind, default
    /// cost model. Override for tests or per-host calibration.</summary>
    public static IEnumerable<IIntegrationStrategy> DefaultStrategies() => new IIntegrationStrategy[]
    {
        new InRamAllFramesStrategy(),
        new TilePipelinedStrategy(),
        new FootprintStagedStrategy(),
        new Float16StagedStrategy(),
        new ChunkedTwoPassStrategy(),
        new LiveAccumulatorStrategy(),
    };
}
