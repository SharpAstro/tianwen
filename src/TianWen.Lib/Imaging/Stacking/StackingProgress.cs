using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-group result yielded by <see cref="StackingPipeline.RunAsync"/>.
/// Streams as the pipeline finishes each light group so a CLI / TUI can
/// render results progressively. <see cref="SkipReason"/> is non-empty
/// when the group was scanned but not integrated (e.g. fewer than two
/// frames matched the reference).
/// </summary>
/// <param name="GroupSlug">The <see cref="LightGroupKey.Slug"/> for the
/// light group (e.g. <c>"Skull_Nebula_120s_g100_o25_-5C_RGGB"</c>).</param>
/// <param name="FramesAttempted">Light frames the group started with.</param>
/// <param name="FramesMatched">Light frames that registered cleanly
/// against the reference and contributed to the master.</param>
/// <param name="Result">Integration output (master + rejection map +
/// stats). Null when <see cref="SkipReason"/> is set.</param>
/// <param name="MasterFitsPath">Absolute path to <c>master_&lt;slug&gt;.fits</c>.
/// Null when skipped.</param>
/// <param name="PreviewPngPath">Absolute path to <c>master_&lt;slug&gt;.png</c>
/// (display-encoded preview with bg-neut + WB baked into the stretch).
/// Null when skipped.</param>
/// <param name="Elapsed">Wall clock for this group, from start of
/// reference-pick to end of post-processing.</param>
/// <param name="SkipReason">Empty when the group integrated; otherwise
/// a human-readable reason ("no reference frame", "fewer than 2 matched",
/// "no calibration master for shape").</param>
public sealed record GroupResult(
    string GroupSlug,
    int FramesAttempted,
    int FramesMatched,
    IntegrationResult? Result,
    string? MasterFitsPath,
    string? PreviewPngPath,
    TimeSpan Elapsed,
    string SkipReason = "");

/// <summary>
/// Coarse phase markers used by <see cref="StackingPipeline.RunAsync"/>
/// when reporting structured progress. Phase semantics:
/// <list type="bullet">
///   <item><c>Scanning</c>: walking <see cref="StackingOptions.DataRoot"/>
///     for FITS, no per-item progress.</item>
///   <item><c>BuildingMasters</c>: per-master-group cal build (bias/dark/flat).
///     <c>CompletedItems</c> = groups done, <c>TotalItems</c> = total.</item>
///   <item><c>Registering</c>: per-frame star detection + quad match.
///     <c>GroupSlug</c> set to the current light group.</item>
///   <item><c>Integrating</c>: the chosen strategy's pass over the matched
///     frames. <c>IntegrationProgress</c> carries the strategy's own
///     per-frame / per-strip tick when available.</item>
///   <item><c>PostProcessing</c>: bg-neut, plate-solve, SPCC WB, FITS +
///     preview write. No per-item progress.</item>
/// </list>
/// </summary>
public enum StackingPhase
{
    Scanning,
    BuildingMasters,
    Registering,
    Integrating,
    PostProcessing,
}

/// <summary>
/// Structured progress tick. Stages with no item-level progress leave
/// <see cref="TotalItems"/> = 0 and report only the phase + group slug.
/// </summary>
/// <param name="Phase">Coarse stage marker -- see <see cref="StackingPhase"/>.</param>
/// <param name="GroupSlug">Light group currently being processed.
/// Empty for cross-group phases (<c>Scanning</c>, <c>BuildingMasters</c>).</param>
/// <param name="CompletedItems">Items finished at this tick.</param>
/// <param name="TotalItems">Total items in this phase (0 if not item-paced).</param>
/// <param name="Integration">Strategy's own structured progress, forwarded
/// when the pipeline is inside <see cref="StackingPhase.Integrating"/>.
/// Null for other phases.</param>
/// <param name="Scan">One-shot scan summary, set on the SECOND
/// <see cref="StackingPhase.Scanning"/> tick (after the DataRoot walk
/// completes). Null on the initial "scanning..." tick and every other phase.</param>
public sealed record StackingProgress(
    StackingPhase Phase,
    string GroupSlug,
    int CompletedItems,
    int TotalItems,
    IntegrationProgress? Integration = null,
    ScanSummary? Scan = null);

/// <summary>
/// One-shot scan summary, reported once via <see cref="StackingPhase.Scanning"/>
/// after <see cref="StackingOptions.DataRoot"/> is fully walked. Lets a CLI / TUI
/// surface what the scan DROPPED -- silently re-ingesting a TianWen product (a
/// stale master, or a sharpen / enhance output that inherited the master's
/// <c>SWCREATE</c>) as a fresh light is a footgun, and a silent skip reads as
/// "there was nothing there to skip". Reported in addition to the initial bare
/// "scanning..." tick, not instead of it.
/// </summary>
/// <param name="FramesScanned">FITS kept after filtering, across ALL frame types
/// (lights + calibration + any integrations kept via IncludeIntegrations).</param>
/// <param name="ProductsSkipped">TianWen products dropped: STACK_N masters plus
/// SWCREATE-stamped derived outputs (sharpen / enhance). Zero unless such files
/// sat alongside the inputs; always 0 when IncludeIntegrations is set.</param>
/// <param name="RejectionMapsSkipped">Per-pixel <c>.rejection.fits</c> maps dropped.</param>
/// <param name="ProductsKept">TianWen products kept as input under
/// IncludeIntegrations (two-stage mosaic re-stacking).</param>
public sealed record ScanSummary(
    int FramesScanned,
    int ProductsSkipped,
    int RejectionMapsSkipped,
    int ProductsKept);
