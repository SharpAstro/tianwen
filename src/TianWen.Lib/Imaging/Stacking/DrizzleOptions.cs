namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-run knobs for <see cref="DrizzleStrategy"/> (Bayer drizzle integration).
/// Phase 1 ships scale=10 only; <see cref="OutputScale"/> is encoded as a
/// **deci-int** up front so the surface is forward-compatible with Phase 2's
/// 1.5x / 2.0x oversampling without breaking the options record.
/// </summary>
/// <param name="Pixfrac">Linear fraction of a source pixel each "drop" covers
/// on the output grid, in [0, 1]. <c>1.0</c> (default) means each input
/// pixel's drop is a full unit square -- at <see cref="OutputScale"/>=10 this
/// degenerates to forward-bilinear distribution, the safest "transparent"
/// drizzle setting. Lower values (typical 0.6-0.8 for production drizzle)
/// give sharper output but need more frames to keep coverage from going
/// patchy.</param>
/// <param name="OutputScale">Output grid scale in units of 1/10 (deci-int):
/// <list type="bullet">
///   <item><c>10</c> = 1.0x (same grid as the reference frame; Phase 1).</item>
///   <item><c>15</c> = 1.5x (Phase 2).</item>
///   <item><c>20</c> = 2.0x (classical sub-Bayer resolution recovery; Phase 2).</item>
/// </list>
/// Encoding the scale as deci-int (rather than a float) keeps the canvas
/// dimensions in integer arithmetic and lets us validate "must be one of
/// {10, 15, 20}" without float epsilon games. Phase 1 throws
/// <see cref="System.NotSupportedException"/> for any value != 10.</param>
/// <param name="MinFrameCount">Minimum matched-frame count before the
/// strategy is allowed to run. Drizzle's per-cell coverage relies on many
/// sub-pixel-dithered drops to fill the output grid; at low N the master
/// ends up with sparse zero-weight cells (NaN holes) in R and B (each only
/// 25% of input pixels under RGGB). Production default of 60 is the
/// conservative floor; tests override to 4 to exercise the code path on
/// small synthetic fixtures.</param>
public sealed record DrizzleOptions(
    float Pixfrac = 1.0f,
    int OutputScale = 10,
    int MinFrameCount = 60)
{
    /// <summary>The only <see cref="OutputScale"/> value supported in Phase 1.
    /// Exists so the strategy + tests have a name to grep on when Phase 2
    /// lifts the restriction.</summary>
    public const int OutputScalePhase1 = 10;
}
