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
}

/// <summary>
/// The product of a planetary stack: the integrated master plus diagnostics. The master carries the
/// reference frame's <see cref="ImageMeta"/> -- for a split-CFA stream it is the four stacked CFA
/// sub-planes (demosaiced in Phase 6); for mono / RGB it is the integrated image directly.
/// </summary>
public sealed record PlanetaryStackResult(Image Master, int ReferenceIndex, int FramesUsed, int FramesGraded);
