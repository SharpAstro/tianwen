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
    /// <summary>The result of one pick: who won, why, what every
    /// candidate had to say, and which sink backing the master canvas
    /// should use under the current memory pressure.</summary>
    public sealed record Selection(
        IIntegrationStrategy Chosen,
        ImmutableArray<Candidate> Considered,
        RankingPolicy RankedBy,
        string Notes,
        SinkKind Sink);

    /// <summary>Master canvas occupies <c>&gt; <see cref="MmapPreferredCanvasRamFraction"/></c>
    /// of the available RAM budget -> prefer <see cref="SinkKind.MemoryMappedFits"/>.
    /// Leaves headroom for staged-frame caches + strategy scratch + the
    /// reject map. 0.25 chosen so a 3008² × 3 RGB canvas (~108 MB) on a
    /// 6 GB-free host stays in-RAM (1.8%), while a 16K mosaic (3.2 GB) on
    /// the same host flips to mmap (53%).</summary>
    public const double MmapPreferredCanvasRamFraction = 0.25;

    /// <summary>Master canvas occupies <c>&gt; <see cref="MmapMandatoryCanvasRamFraction"/></c>
    /// of the available RAM budget -> <see cref="SinkKind.MemoryMappedFits"/>
    /// is mandatory; in-RAM would bust the budget under any realistic
    /// strategy working set.</summary>
    public const double MmapMandatoryCanvasRamFraction = 0.80;

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
        // Sink decision is orthogonal to strategy ranking: it depends only on
        // the canvas-to-available-RAM ratio. Computed once up front so both the
        // user-override return path and the ranked-winner path stamp the same
        // value. The mandatory threshold acts as a hard ceiling -- any sink
        // pick above it would force GC paging during the integrator hot loop.
        var sinkKind = PickSinkKind(probe);
        // Default policy: Balanced (50/50 fidelity/speed). FidelityFirst
        // (SpeedWeight=0) made the selector indifferent to wall time, so a
        // 0.06-fidelity bump would justify arbitrary slowdown. Empirical:
        // SoL 244-frame run picked TilePipelined (fidelity 0.98) over
        // Float16Staged (0.92) and ran for 20+ min before the user cancelled,
        // vs Float16Staged's 5.5 min. Balanced still favours fidelity within
        // the same speed bucket but lets normalized speed break ties.
        policy ??= RankingPolicy.Balanced;

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
                    Notes: $"user override --strategy {pref}" + (match.Fit.CanRun ? "" : " (warning: fit reports CanRun=false)"),
                    Sink: sinkKind);
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

        // Memory-pressure soft penalty: candidates that need more RAM than is
        // currently free (i.e. would force eviction or paging) lose ranking
        // points proportional to the over-commit. The hard gate already runs
        // against physical RAM (AvailableRamBytes) so a slow staging
        // strategy can still win when the host is mid-other-work and a
        // RAM-heavy strategy would page. The penalty saturates at 50% of
        // score so a much-higher-fidelity strategy can still come out on top.
        // Falls back to "no penalty" when FreeRamBytes wasn't populated (0)
        // so synthesised probes in tests don't get unexpectedly nudged.
        //
        // The penalty applies to the strategy's *floor*, not its *target*:
        // an adaptive strategy (TilePipelined) reports a high target (the
        // optimistic cache-everything ceiling) and a much lower floor (strips
        // + in-flight + output). Penalising on target made it lose to
        // staged-to-disk alternatives even when its floor fit comfortably and
        // the staged alternative then spent 100+ s on staging IO that the
        // tile-pipelined path would have skipped entirely. Floor is what the
        // strategy MUST consume, so that's what the GC pressure heuristic
        // should weigh.
        var freeRam = probe.FreeRamBytes;
        double MemoryPressurePenalty(long floorRam)
        {
            if (freeRam <= 0 || floorRam <= freeRam) return 0.0;
            // Penalty scales against free RAM, not physical: a strategy whose
            // floor needs 2x free RAM gets penalised regardless of whether the
            // host has 8 GB or 64 GB installed, because in both cases the GC
            // would need to grow + evict + page to satisfy it. Coefficient
            // 0.25 chosen so a 3x-over-free floor hits the cap (50%) and
            // a 1.4x slip lands at a gentle 10% penalty -- enough to favour
            // a staged strategy when free RAM is tight, not enough to override
            // a much-higher-fidelity winner on a roomy box.
            var ratio = (double)floorRam / freeRam;
            return Math.Min(0.5, (ratio - 1.0) * 0.25);
        }

        // Excess-eta penalty: a strategy whose cost model says it's >1.5x the
        // cheapest survivor gets a multiplicative score reduction even when
        // the speed term in policy.Score already accounted for it. Defensive
        // against under-calibrated cost models that under-report a slow path
        // (e.g. TilePipelined's strip-pipelined re-decode on a cache-thrashing
        // mosaic) but get within a normalised-speed-range hair of the winner.
        // The penalty saturates at 50% so a much-higher-fidelity strategy can
        // still win on a 2-3x slip, just not a 5x+ one.
        double EtaExcessPenalty(double etaMs)
        {
            if (minMs <= 0) return 0.0;
            var ratio = etaMs / minMs;
            if (ratio <= 1.5) return 0.0; // tolerate <=50% slop -- within cost-model noise
            // 1.5x  -> 0%
            // 2.0x  -> 15%
            // 3.0x  -> 45%
            // 3.17x -> cap 50%
            return Math.Min(0.5, (ratio - 1.5) * 0.30);
        }

        // For each candidate compute the score. Non-survivors stay at 0 so
        // log readers can see who was gated out.
        var ranked = evaluated
            .Select(c =>
            {
                if (!c.Fit.CanRun) return new Candidate(c.Strategy, c.Fit, 0);
                var speedNorm = 1.0 - (c.Fit.EstimatedDuration.TotalMilliseconds - minMs) / range;
                var baseScore = policy.Score(c.Strategy.FidelityScore, speedNorm);
                var memPenalty = MemoryPressurePenalty(c.Fit.FloorRamBytes);
                var etaPenalty = EtaExcessPenalty(c.Fit.EstimatedDuration.TotalMilliseconds);
                // Penalties compose multiplicatively so they can't sum past
                // 100% even at their individual 50% caps.
                var score = baseScore * (1.0 - memPenalty) * (1.0 - etaPenalty);
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
            Notes: $"score={winner.Score:F3} (fidelity={winner.Strategy.FidelityScore:F2}, eta={winner.Fit.EstimatedDuration.TotalSeconds:F0}s)",
            Sink: sinkKind);
    }

    /// <summary>Decide between in-RAM and mmap backing for the master canvas
    /// based on how much of the available RAM budget the canvas alone would
    /// occupy. Exposed for tests; production callers should rely on the
    /// <see cref="Selection.Sink"/> value returned by <see cref="Pick"/>.</summary>
    public static SinkKind PickSinkKind(IntegrationProbe probe)
    {
        if (probe.AvailableRamBytes <= 0) return SinkKind.InRamArray; // synthesised probes (tests)
        var ratio = (double)probe.CanvasBytes / probe.AvailableRamBytes;
        return ratio >= MmapPreferredCanvasRamFraction
            ? SinkKind.MemoryMappedFits
            : SinkKind.InRamArray;
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
