namespace TianWen.AI.Imaging;

/// <summary>
/// Constants shared across the AI4 NAFNet ONNX enhancers (star removal, stellar
/// sharpening, non-stellar conditional-PSF deconvolution, denoise). These mirror
/// the input-distribution assumptions baked into the trained models -- if a
/// future model retrains at a different operating point, declare a separate
/// constants class for it rather than reassigning these.
/// </summary>
internal static class AiNafnetInputs
{
    /// <summary>
    /// Target median for the MTF pre-stretch applied to every AI4 NAFNet ONNX
    /// input. Each enhancer calls
    /// <see cref="TianWen.Lib.Imaging.Image.MtfStretch"/> with this value before
    /// inference and <see cref="TianWen.Lib.Imaging.Image.MtfUnstretch"/> after,
    /// keeping the data inside the training distribution. SetiAstroSuite Pro's
    /// <c>stretch_image_mono</c> / <c>stretch_image_unlinked_rgb</c> use the
    /// same default.
    /// </summary>
    /// <remarks>
    /// Declared <c>static readonly</c> rather than <c>const</c> on purpose:
    /// <c>const</c> would IL-bake this literal into every caller's compiled
    /// site, so consumers in other assemblies built against an old TianWen.AI.Imaging
    /// would keep the stale value until rebuilt. <c>static readonly</c> reads
    /// from the field at runtime, so a single rebuild of this assembly
    /// propagates the change everywhere.
    /// </remarks>
    public static readonly double TargetMedian = 0.25;

    /// <summary>
    /// Inner border (in pixels) discarded when stitching tiled inference chunks
    /// back together. Matches SetiAstroSuite Pro's
    /// <c>stitch_chunks_ignore_border(border_size=16)</c> -- chunks overlap and
    /// only the central (chunk - 2 * StitchBorderPx) region is written to the
    /// output for non-edge chunks, hiding the boundary artefacts NAFNet
    /// produces near tile edges.
    /// </summary>
    public static readonly int StitchBorderPx = 16;
}
