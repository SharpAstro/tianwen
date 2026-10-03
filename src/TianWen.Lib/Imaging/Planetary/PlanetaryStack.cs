using System;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Options for a planetary lucky-imaging stack. The defaults are the measured best of docs/plans/planetary-restoration.md, R4 to R6
/// (the enhanced pipeline, #1159); <see cref="Legacy"/> is every stack's recipe before it.
/// </summary>
public sealed record PlanetaryStackOptions
{
    /// <summary>
    /// The recipe every planetary stack used before the enhanced pipeline (#1159): the Laplacian keeping a quarter, phase
    /// correlation against the best frame, bilinear resampling. Kept so an old master can be made again and a new one compared.
    /// </summary>
    public static PlanetaryStackOptions Legacy { get; } = new PlanetaryStackOptions
    {
        KeepFraction = 0.25,
        QualityEstimator = new LaplacianEnergyEstimator(),
        WhitenedCorrelation = true,
        Interpolation = WarpInterpolation.Bilinear,
        ReferenceFrames = 0,
    };

    /// <summary>
    /// Fraction of frames to keep, best-graded first (lucky imaging's "keep the sharpest N%"). Half, the default, is the best keep
    /// for a stack that is sharpened afterwards: restoration divides the blur out, and then every frame lowers the noise it lifts
    /// (#1083, a third less error than 5 % through the limb's edge on every twin). A stack left as it is keeps fewer, 5 to 10 %
    /// (R4), since there each frame added blurs it more.
    /// </summary>
    public double KeepFraction { get; init; } = 0.5;

    /// <summary>
    /// The sharpness metric, which grades the frames and weights them. The gradient by default: at 8 bits a frame's finest scale is
    /// its noise, and the Laplacian ranked the twin's frames at +0.19 against their true transfer where the gradient ranks them at
    /// +0.87 (R4).
    /// </summary>
    public IFrameQualityEstimator QualityEstimator { get; init; } = new GradientEnergyEstimator();

    /// <summary>
    /// Phase-correlation tile edge for global alignment. <c>0</c> (default) auto-sizes to the next power
    /// of two that covers the reference disk bounding box, clamped to [64, 512].
    /// </summary>
    public int AlignTileSize { get; init; }

    /// <summary>Spacing (px) of the alignment-point grid cells -- at most one AP per cell.</summary>
    public int AlignmentPointSpacing { get; init; } = 24;

    /// <summary>
    /// Whether the global aligner and the alignment points register by phase correlation (whitened, every frequency weighted
    /// alike) or by a plain cross-correlation, its peak climbed (false, the default). On a single 8-bit frame the finest
    /// frequencies are noise, and whitening hands the peak to it: a 16 px patch at 2022-09-03's level is placed to 1.1 px RMS
    /// whitened, 0.35 px plain (<c>AlignmentPointMatchingTests</c>; docs/plans/planetary-restoration.md, R5).
    /// </summary>
    public bool WhitenedCorrelation { get; init; }

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

    /// <summary>
    /// Match every frame's points against a second reference: the best <see cref="ReferenceFrames"/> frames each folded through the
    /// mesh their points give against the first, so the reference itself is dewarped as far as the points can (#1081's second pass,
    /// docs/plans/planetary-restoration.md, "#1081: dense points, the stack's geometry and kriging, together"). The points stay where
    /// the first reference put them. Not with a de-rotation, whose reference turns with the planet.
    /// </summary>
    public bool RemeasureAgainstStack { get; init; }

    /// <summary>
    /// The kernel each frame is resampled by as it is folded in, global and alignment-point paths alike (clamped Lanczos-3, the
    /// default; bilinear in every stack before the enhanced pipeline). A stack of frames resampled bilinearly at sub-pixel phases
    /// spread evenly is blurred by the kernel's triangle, sinc squared an axis in transfer; Lanczos-3 keeps the transfer to 0.3
    /// cycles a pixel, and band 1's error fell 0.014 to 0.024 (docs/plans/planetary-restoration.md, R5 part 3). Its clamp changes
    /// nothing on a planet, which trips it nowhere. Not drizzle, which scatters instead of resampling.
    /// </summary>
    public WarpInterpolation Interpolation { get; init; } = WarpInterpolation.Lanczos3Clamped;

    /// <summary>
    /// Register every frame, and match every alignment point, against a stack of this many of the best-graded frames (each
    /// aligned to the best frame first; 1,000, the default) instead of the best frame alone (0 or 1). A stack carries a fraction
    /// of one frame's noise and its local warp averaged out, and it is what AutoStakkert registers to: its session file for
    /// 2022-09-03's Red names the best 8,572 of 12,990 frames. On that capture the best frame's registration error variance was
    /// larger than a stack of 1,000's by 0.08 and 0.38 px^2 (docs/plans/planetary-restoration.md, R5 part 3).
    /// </summary>
    public int ReferenceFrames { get; init; } = 1000;

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
    /// A gain on every alignment point's departure from the points' mean residual before the mesh blends them (#1081,
    /// docs/plans/planetary-restoration.md, "#1081: dense points, the stack's geometry and kriging, together"). A point's plain
    /// correlation reads a sixth of a warp that varies over its patch, its windows pulling the peak toward zero, so the blend of its
    /// readings applies too little of it; one, the default, applies the readings as read.
    /// </summary>
    public float MeshGain { get; init; } = 1f;

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

    /// <summary>
    /// Carry every frame through the planet's rotation to one epoch before it is stacked (docs/plans/planetary-restoration.md,
    /// R6 part 2), on every path: null (the default) stacks each frame as it was taken, which over a run of minutes smears
    /// the planet along its rotation, since the limb stays where it is and the belts move. Needs every frame's time.
    /// </summary>
    public PlanetaryDerotationOptions? Derotation { get; init; }

    /// <summary>
    /// The body the capture shows, written to the master's <c>OBJECT</c> card, where a viewer reads that it is a planetary frame
    /// (and opens it linear, as it does a SER: a deep-sky auto-stretch of a 1,500-frame stack's sky, its noise 3e-5, blew it up
    /// thirty thousand times). The de-rotation's planet when null. Nothing in the stack itself depends on it.
    /// </summary>
    public CatalogIndex? Planet { get; init; }
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
public sealed record PlanetaryStackResult(Image Master, int ReferenceIndex, int FramesUsed, int FramesGraded)
{
    /// <summary>The instant a de-rotated stack shows the planet at (<see cref="PlanetaryStackOptions.Derotation"/>), or null for one that was not.</summary>
    public DateTimeOffset? Epoch { get; init; }

    /// <summary>Which way round a de-rotated stack took the planet's north, and why; null for one that was not de-rotated.</summary>
    public PlanetaryNorthDecision? North { get; init; }

    /// <summary>
    /// How far the planet's turn over the capture moved the middle of its disk, px, when a de-rotation was asked for
    /// (<see cref="PlanetaryDerotationOptions.MinimumTurnPx"/> decides from it whether one was done); NaN for a capture without frame
    /// times, null when none was asked for.
    /// </summary>
    public double? TurnPx { get; init; }
}
