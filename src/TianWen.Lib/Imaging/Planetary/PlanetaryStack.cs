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

    /// <summary>
    /// Whether the global aligner and the alignment points register by phase correlation (whitened, every frequency weighted
    /// alike, the default) or by a plain cross-correlation. On a single 8-bit frame the finest frequencies are noise, and
    /// whitening hands the peak to it: a 16 px patch at 2022-09-03's level is placed to 1.1 px RMS whitened, 0.35 px plain
    /// (<c>AlignmentPointMatchingTests</c>; docs/plans/planetary-restoration.md, R5).
    /// </summary>
    public bool WhitenedCorrelation { get; init; } = true;

    /// <summary>
    /// Pool each alignment point's warp over the frames either side of a frame, a Gaussian of this many frames in capture order
    /// (0, the default, takes each frame's own match). A warp stays coherent for a few frames while one frame's match is as
    /// uncertain as the warp is large (docs/plans/planetary-restoration.md, R5 part 2). Every frame of the capture is matched
    /// first, selected or not; the alignment-point mesh path only, not drizzle.
    /// </summary>
    public double WarpPoolFrames { get; init; }

    /// <summary>
    /// Put the stack on each alignment point's median geometry over the capture's frames, not the reference frame's: each
    /// point's median warp is taken out of every frame's, so a feature lands where it lies on average, not where the
    /// reference's own warp put it (the user's centroid idea, R5 part 2). The alignment-point mesh path only.
    /// </summary>
    public bool MedianGeometry { get; init; }

    /// <summary>Maximum number of alignment points to track.</summary>
    public int MaxAlignmentPoints { get; init; } = 64;

    /// <summary>Power-of-two patch edge phase-correlated per alignment point.</summary>
    public int AlignmentPatchSize { get; init; } = 32;

    /// <summary>Displacement-mesh node spacing (px). Smaller = finer distortion correction, more cost.</summary>
    public float MeshNodeSpacing { get; init; } = 24f;

    /// <summary>
    /// How far an alignment point's displacement reaches into the mesh, px: the Gaussian that blends the points' residuals
    /// (<see cref="DisplacementMesh.Build"/>). A warp that varies over less than this cannot be followed, however well each
    /// point is matched (R5 part 2: 2022-09-03's warp is correlated over 9 px or less).
    /// </summary>
    public float MeshInfluence { get; init; } = 48f;

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

    /// <summary>
    /// Optional Bayer drizzle (Phase 6): forward-scatter each raw CFA sample onto an upscaled output grid
    /// instead of bilinearly mesh-warping and demosaicing. Avoids the interpolation softening that limits
    /// the mesh path, and recovers sub-Bayer resolution when upscaled. <c>null</c> (default) uses the mesh /
    /// translate integrator. Only meaningful for a Bayer (split-CFA) source. Alignment defaults to the per-AP
    /// displacement mesh (see <see cref="PlanetaryDrizzleOptions.AlignmentPointMesh"/>) so each raw sample is
    /// scattered through the local de-warp, not just a whole-disk translation.
    /// </summary>
    public PlanetaryDrizzleOptions? Drizzle { get; init; }
}

/// <summary>
/// Bayer-drizzle knobs for a planetary stack. <paramref name="Scale"/> is the output grid upscale relative
/// to the native mosaic (1.0 = same grid, 1.5 / 2.0 = sub-Bayer resolution recovery, matching the classic
/// "Drizzle1.5"). <paramref name="Pixfrac"/> is the linear drop size in (0, 1]; smaller is sharper but needs
/// more frames for full coverage. <paramref name="AlignmentPointMesh"/> (default true) forward-scatters each
/// raw sample through the per-AP displacement mesh (the same local de-warp the mesh integrator uses) instead
/// of a single whole-disk translation -- combines drizzle's sub-Bayer resolution with the seeing-distortion
/// correction; set false for whole-disk-only drizzle (cheaper, A/B baseline).
/// </summary>
public sealed record PlanetaryDrizzleOptions(float Scale = 1.5f, float Pixfrac = 1.0f, bool AlignmentPointMesh = true);

/// <summary>
/// The product of a planetary stack: the integrated master plus diagnostics. The master carries the
/// reference frame's <see cref="ImageMeta"/> -- for a split-CFA stream it is the four stacked CFA
/// sub-planes (demosaiced in Phase 6); for mono / RGB it is the integrated image directly.
/// </summary>
public sealed record PlanetaryStackResult(Image Master, int ReferenceIndex, int FramesUsed, int FramesGraded);
