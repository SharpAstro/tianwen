namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Options for a planetary lucky-imaging stack.</summary>
public sealed record PlanetaryStackOptions
{
    /// <summary>Fraction of frames to keep, best-graded first (lucky imaging's "keep the sharpest N%").</summary>
    public double KeepFraction { get; init; } = 0.25;

    /// <summary>The sharpness metric. Laplacian variance by default.</summary>
    public IFrameQualityEstimator QualityEstimator { get; init; } = new LaplacianEnergyEstimator();

    /// <summary>
    /// Phase-correlation tile edge for global alignment. <c>0</c> (default) auto-sizes to the next power
    /// of two that covers the reference disk bounding box, clamped to [64, 512].
    /// </summary>
    public int AlignTileSize { get; init; }

    /// <summary>Spacing (px) of the alignment-point grid cells -- at most one AP per cell.</summary>
    public int AlignmentPointSpacing { get; init; } = 24;

    /// <summary>Maximum number of alignment points to track.</summary>
    public int MaxAlignmentPoints { get; init; } = 64;

    /// <summary>Power-of-two patch edge phase-correlated per alignment point.</summary>
    public int AlignmentPatchSize { get; init; } = 32;

    /// <summary>Displacement-mesh node spacing (px). Smaller = finer distortion correction, more cost.</summary>
    public float MeshNodeSpacing { get; init; } = 24f;

    /// <summary>
    /// Per-AP "best-of" weighting: when true (default) each output pixel is weighted by how locally sharp
    /// each frame was at the feature landing there (<see cref="FrameSharpnessMap"/>), the lucky-imaging
    /// edge. When false, frames are folded in with their global quality weight only.
    /// </summary>
    public bool PerPointQualityWeighting { get; init; } = true;

    /// <summary>
    /// Gate the per-AP best-of weighting by a signal-confidence mask (default true): on the bright disk
    /// body the local-sharpness weighting applies in full; in faint regions (halo / sky) it falls back to
    /// an unbiased mean. Without this, the local-sharpness weight amplifies a real-but-subtle planetary halo
    /// into a bright ring (it preferentially picks the frames where the faint region was brightest). Only
    /// relevant when <see cref="PerPointQualityWeighting"/> is set.
    /// </summary>
    public bool PerPointSignalGate { get; init; } = true;

    /// <summary>
    /// Optional multi-scale wavelet sharpening applied to the final linear master (Phase 7). <c>null</c>
    /// (default) returns the raw integrated master untouched; otherwise the master is sharpened after the
    /// CFA merge + demosaic, so a split-CFA stack sharpens the demosaiced RGB, not the sub-planes.
    /// </summary>
    public WaveletSharpenOptions? Sharpen { get; init; }
}

/// <summary>
/// The product of a planetary stack: the integrated master plus diagnostics. The master carries the
/// reference frame's <see cref="ImageMeta"/> -- for a split-CFA stream it is the four stacked CFA
/// sub-planes (demosaiced in Phase 6); for mono / RGB it is the integrated image directly.
/// </summary>
public sealed record PlanetaryStackResult(Image Master, int ReferenceIndex, int FramesUsed, int FramesGraded);
