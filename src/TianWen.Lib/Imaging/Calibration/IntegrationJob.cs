using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace TianWen.Lib.Imaging.Calibration;

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
public sealed record IntegrationJob(
    Func<CancellationToken, IAsyncEnumerable<Image>> WarpedFrames,
    int ExpectedFrameCount,
    IntegrationOptions Options,
    string StagingDir,
    Rectangle StatsRect,
    IReadOnlyList<Rectangle>? FrameFootprints = null);
