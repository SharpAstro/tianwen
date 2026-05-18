using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Identifies an <see cref="IIntegrationStrategy"/> implementation. Used by the
/// CLI flag (<c>--strategy ...</c>), by logs, and by the selector's
/// <c>preferred</c> override. Ordering here is informational only -- the
/// selector ranks via <see cref="IIntegrationStrategy.FidelityScore"/> /
/// <see cref="RankingPolicy"/>, not via enum order.
/// </summary>
public enum IntegrationStrategyKind
{
    /// <summary>Hold all N frames in RAM as <see cref="Image"/> instances; no
    /// disk. Highest fidelity, smallest RAM headroom.</summary>
    InRamAllFrames,

    /// <summary>PLAN-stacking Phase 8: re-read raw lights tile-by-tile, calibrate
    /// + warp + normalize + stack in-memory per tile. No staging on disk; OS
    /// page cache absorbs the repeated raw reads.</summary>
    TilePipelined,

    /// <summary>Stage each frame's non-NaN footprint AABB as float32 to disk
    /// (smaller than the full warped canvas), then tile-integrate from staging.
    /// Drops disk pressure 5-50% vs full-canvas staging depending on mount
    /// motion.</summary>
    FootprintStaged,

    /// <summary>Stage full warped canvas as float16 -- half the disk bytes of
    /// float32. Tiny CPU overhead on read for the f16-&gt;f32 unpack.</summary>
    Float16Staged,

    /// <summary>Split frames into K chunks, integrate each to a partial master,
    /// combine partials with weighted mean. Memory falls to one chunk's worth
    /// but per-chunk rejection diverges from across-all-frames rejection --
    /// fidelity warning logged.</summary>
    ChunkedTwoPass,

    /// <summary>Frame-at-a-time Welford online accumulator (PLAN-stacking
    /// Phase 14 <c>LiveStacker</c>). Selected only when the probe sets
    /// <see cref="IntegrationProbe.LiveStacking"/>; not interchangeable with
    /// batch strategies because rejection is per-frame relative to the
    /// running mean, not across all frames.</summary>
    LiveAccumulator,
}

/// <summary>
/// One concrete integration strategy. The selector calls
/// <see cref="Evaluate"/> on every registered strategy for a given probe,
/// gates by <see cref="StrategyFit.CanRun"/>, then ranks survivors by
/// <see cref="FidelityScore"/> + estimated speed under the active
/// <see cref="RankingPolicy"/>.
/// </summary>
/// <remarks>
/// <para>v1 contract is read-only: strategies report their requirements but
/// don't run yet. The execution method
/// (<c>RunAsync(IntegrationJob, ...)</c>) will be added once the
/// <c>IntegrationJob</c> contract crystallises -- it has to carry raw light
/// paths, transforms, calibrator, canvas geometry, intersection AABB, and
/// the <see cref="IntegrationOptions"/>. Doing that as a follow-up keeps
/// the scaffolding decoupled from the in-flux call site in
/// <c>StackingEndToEndManualTest</c>.</para>
/// </remarks>
public interface IIntegrationStrategy
{
    /// <summary>Identifies the strategy for CLI / log / override use.</summary>
    IntegrationStrategyKind Kind { get; }

    /// <summary>Quality of the result this strategy produces, on [0, 1]. 1.00 =
    /// reference (full-precision per-pixel rejection across all N frames at
    /// once). Lower scores reflect either reduced precision (float16 staging)
    /// or reduced rejection fidelity (chunked partials).</summary>
    double FidelityScore { get; }

    /// <summary>True when this strategy can drive the per-frame live preview
    /// path (Welford online or equivalent). False for batch-only strategies
    /// that need every frame up front. Selector filters on this when
    /// <see cref="IntegrationProbe.LiveStacking"/> is set.</summary>
    bool SupportsLiveStacking { get; }

    /// <summary>Verdict for the given probe under the given budget. Always
    /// returns -- never throws on a probe that simply doesn't fit; surfaces
    /// the reason via <see cref="StrategyFit.Rationale"/> instead.</summary>
    StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget);

    /// <summary>
    /// Execute the integration. Consumes <see cref="IntegrationJob.WarpedFrames"/>
    /// and produces an <see cref="IntegrationResult"/>. Implementations that
    /// can't run yet throw <see cref="System.NotImplementedException"/> with a
    /// message describing what's missing -- the selector lets callers see this
    /// at probe time via the <see cref="IntegrationStrategyKind"/>.
    /// </summary>
    ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct);
}
