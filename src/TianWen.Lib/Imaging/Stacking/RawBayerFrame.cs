using System.Numerics;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// One raw CFA frame yielded by the drizzle producer in
/// <see cref="StackingPipeline"/>. The image is the calibrated 1-channel
/// Bayer plane (no debayer applied) -- the strategy is responsible for
/// dispatching pixel colour via
/// <see cref="SensorType.GetBayerPatternMatrix"/> on
/// <paramref name="RawCfa"/>'s <c>ImageMeta</c>. The transform is the
/// composed source-to-canvas affine (per-frame registration result × the
/// group's canvas shift), so the strategy applies it directly without
/// inversion.
/// </summary>
/// <remarks>
/// Distinct from the standard <see cref="IntegrationJob.WarpedFrames"/>
/// producer which yields debayered + warped 3-channel <see cref="Image"/>s.
/// Only <see cref="DrizzleStrategy"/> consumes <see cref="RawBayerFrame"/>;
/// other strategies should never see it (the
/// <see cref="IntegrationJob.RawBayerFrames"/> producer stays null when a
/// non-drizzle strategy runs).
/// </remarks>
public sealed record RawBayerFrame(
    Image RawCfa,
    Matrix3x2 TransformToCanvas);
