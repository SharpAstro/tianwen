using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Threading;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// A single raw light frame ready to be consumed tile-by-tile by
/// <see cref="TilePipelinedStrategy"/>. The strategy is responsible for
/// loading the FITS file, calibrating, debayering, and warping into the
/// output canvas; <see cref="TransformToCanvas"/> already accounts for the
/// per-group canvas-shift translation.
/// </summary>
/// <param name="Path">Absolute path to the raw light FITS file.</param>
/// <param name="TransformToCanvas">Affine that takes a (source x, y) and
/// maps to (canvas x, y). Composed by the orchestrator as
/// <c>transformToReference * canvasShift</c>.</param>
public sealed record RawLightSource(string Path, Matrix3x2 TransformToCanvas);

/// <summary>
/// Everything an <see cref="IIntegrationStrategy.RunAsync"/> implementation
/// needs to turn N registered + warped frames into a master.
/// </summary>
/// <param name="WarpedFrames">Async producer that yields each warped frame
/// in turn. Producer pattern so staged strategies can consume + stage + drop
/// each frame without holding all N in RAM. In-RAM strategies collect into
/// a list. Frames must already be warped to the union-BB canvas
/// (<see cref="IntegrationProbe.CanvasWidth"/> × <see cref="IntegrationProbe.CanvasHeight"/>);
/// the strategy is not responsible for warping.</param>
/// <param name="ExpectedFrameCount">Hint for buffer pre-sizing; the actual
/// stream may yield fewer frames if some fail downstream.</param>
/// <param name="Options">Rejector + combiner + normalization knobs, same as
/// the existing <see cref="IntegrationOptions"/>.</param>
/// <param name="StagingDir">Directory the staged strategies may write to.
/// Created if absent; cleanup is the strategy's responsibility unless the
/// caller wants to inspect intermediate files.</param>
/// <param name="StatsRect">Per-frame normalisation stats are taken over this
/// rectangle (intersection of all warped frame footprints on the canvas).
/// Pass <see cref="Rectangle.Empty"/> to fall back to whole-frame stats.</param>
/// <param name="FrameFootprints">Per-frame non-NaN bounding boxes on the
/// canvas, indexed in the same order as <see cref="WarpedFrames"/> yields.
/// Used by <see cref="FootprintStagedStrategy"/> to write only the footprint
/// sub-region of each frame to disk; ignored by strategies that stage the
/// full canvas. <c>null</c> = fall back to full-canvas staging.</param>
/// <param name="RawLightSources">Per-frame raw FITS path + transform-to-canvas.
/// Used by <see cref="TilePipelinedStrategy"/> to bypass the WarpedFrames
/// producer entirely -- the strategy loads, calibrates, debayers, and warps
/// per-tile from the raw FITS. <c>null</c> means the strategy can't run
/// (will throw NotImplementedException). Provided in the same frame order
/// as <see cref="WarpedFrames"/> yields, so per-frame stats lookups stay
/// consistent across strategies.</param>
/// <param name="Calibrator">Required by <see cref="TilePipelinedStrategy"/>
/// (or any future strategy that calibrates raw input). Other strategies
/// consume already-calibrated frames from the producer and ignore this.</param>
/// <param name="DebayerAlgorithm">Which debayer algorithm
/// <see cref="TilePipelinedStrategy"/> applies on the raw input. Other
/// strategies receive already-debayered frames from the producer and
/// ignore this.</param>
/// <param name="CanvasWidth">Output canvas width. Filled in by the
/// orchestrator from the same union-BB computation that drives
/// <see cref="WarpedFrames"/>. Used by <see cref="TilePipelinedStrategy"/>
/// to size its output buffer + tile grid; other strategies infer this
/// from the warped frames they consume and may leave it as 0.</param>
/// <param name="CanvasHeight">See <see cref="CanvasWidth"/>.</param>
public sealed record IntegrationJob(
    Func<CancellationToken, IAsyncEnumerable<Image>> WarpedFrames,
    int ExpectedFrameCount,
    IntegrationOptions Options,
    string StagingDir,
    Rectangle StatsRect,
    IReadOnlyList<Rectangle>? FrameFootprints = null,
    IReadOnlyList<RawLightSource>? RawLightSources = null,
    Calibrator? Calibrator = null,
    DebayerAlgorithm DebayerAlgorithm = DebayerAlgorithm.VNG,
    int CanvasWidth = 0,
    int CanvasHeight = 0,
    // Optional structured progress sink. Strategies report semantic events
    // (e.g. LoadingFrames 200/244) at natural checkpoints; the consumer
    // (test orchestrator today, GUI later) formats / throttles / computes
    // ETA. Status only, NOT a general logging channel -- use ILogger via
    // strategy constructor injection for warnings/debug/diagnostics.
    IProgress<IntegrationProgress>? Progress = null);

/// <summary>
/// Coarse-grained pipeline phase reported by integration strategies. Phases
/// model the order user-visible work happens in, not internal strategy passes:
/// a strategy may visit the same phase multiple times (TilePipelined revisits
/// <see cref="LoadingFrames"/> on pass-2 cache misses) and may skip phases
/// it doesn't need.
/// </summary>
public enum IntegrationPhase
{
    /// <summary>Decoding raw FITS + calibration + debayer + per-frame stats.
    /// CompletedItems = frames decoded so far; TotalItems = frame count.</summary>
    LoadingFrames,

    /// <summary>Affine warp from source grid into the canvas grid.
    /// CompletedItems = frames or strips warped (strategy-specific);
    /// TotalItems = total to warp.</summary>
    Warping,

    /// <summary>Per-frame intensity normalization to the reference.
    /// CompletedItems = frames normalized; TotalItems = frame count.</summary>
    Normalizing,

    /// <summary>Per-pixel rejection + combine into the master (potentially
    /// per-strip for tile-pipelined strategies). CompletedItems = strips
    /// integrated or chunks combined; TotalItems = total units.</summary>
    Integrating,

    /// <summary>Final master + rejection-map assembly. Usually a single
    /// event with CompletedItems = TotalItems = 1 just before return.</summary>
    Finalizing,
}

/// <summary>
/// One progress event from an integration strategy. Pure data; the consumer
/// is responsible for translating to log / UI / ETA inference. Elapsed is
/// wall-clock since the strategy started so the consumer can compute simple
/// linear-projection ETAs without each strategy carrying its own stopwatch.
/// </summary>
public sealed record IntegrationProgress(
    IntegrationPhase Phase,
    int CompletedItems,
    int TotalItems,
    TimeSpan Elapsed);
