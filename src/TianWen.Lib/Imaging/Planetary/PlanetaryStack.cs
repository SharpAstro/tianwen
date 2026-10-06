using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Where alignment points are placed (<see cref="PlanetaryStackOptions.PointPlacement"/>, #1253).</summary>
public enum PlanetaryPointPlacement
{
    /// <summary>A cell's strongest gradient above a fifth of the strongest in the bright disk's box (every stack before #1253).</summary>
    PeakFraction,

    /// <summary>
    /// Over the whole planet, rings included, a cell only where its square lies on it and its strongest gradient stands above the
    /// frame's own noise (<see cref="FeatureDetector.DetectOverPlanet"/>).
    /// </summary>
    OverPlanet,
}

/// <summary>How an alignment point's shift is read (<see cref="PlanetaryStackOptions.PointEstimator"/>).</summary>
public enum PlanetaryPointEstimator
{
    /// <summary>A Hann-windowed cross-correlation, phase-whitened or plain (<see cref="PlanetaryStackOptions.WhitenedCorrelation"/>),
    /// its peak climbed. Both windows sit where the point is, so a smooth texture's reading is pulled toward no shift.</summary>
    Correlation,

    /// <summary>Loefdahl 2010's square difference (<see cref="Stat.SquareDifferenceShift"/>): unwindowed, mean- and plane-subtracted,
    /// against a reference <see cref="AlignmentPointMatcher.SquareDifferenceMargin"/> px larger each side, the minimum placed by a 2-D
    /// quadratic fit.</summary>
    SquareDifference,

    /// <summary>The plain correlation with its cross-spectrum weighted by the maximum-likelihood weight (Knapp and Carter 1976):
    /// <c>S / (S (N1 + N2) + N1 N2)</c>, S the reference's signal power at a frequency, N1 its noise floor, N2 the frame patch's own
    /// (<see cref="Stat.PhaseCorrelation.EstimateWeighted"/>). Against a reference stacked from many frames N1 is small, and the weight
    /// is a constant wherever the reference holds signal.</summary>
    WeightedCorrelation,
}

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
        AlignChannels = false,
        CropToCoverage = false,
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
    /// How each alignment point's shift is read (#1082, docs/plans/planetary-restoration.md, "#1082: an estimator for the points,
    /// against its bound"): a windowed cross-correlation (the default), or a square difference against a reference 3 px larger each
    /// side, which no window pulls toward zero.
    /// </summary>
    public PlanetaryPointEstimator PointEstimator { get; init; }

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

    /// <summary>
    /// Where the alignment points are placed (#1253): by a fraction of the strongest gradient in the bright disk's box (the default), or
    /// over the whole planet by the frame's own noise. The fraction leaves Saturn, whose strongest edges are its rings', 3 to 13 points.
    /// </summary>
    public PlanetaryPointPlacement PointPlacement { get; init; }

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
    /// Align a colour master's planes onto green before the demosaic (<see cref="PlanetaryChannelAlignment"/>), as AutoStakkert's
    /// RGB align does: the atmosphere's dispersion leaves the colours apart in a stack registered by its luminance, a fringe at the
    /// limb (2022-10-09: red to blue 6.4 px). Each colour is read by its own limb fit when <see cref="Planet"/> (or the
    /// de-rotation's) and the master's instant are known, else by correlation. A mono master is untouched. Off in <see cref="Legacy"/>.
    /// </summary>
    public bool AlignChannels { get; init; } = true;

    /// <summary>
    /// Crop the master to the largest rectangle at least <see cref="CoverageCropFraction"/> of the frames' weight reached
    /// (#1300, the default): every frame is moved onto the reference, so the master's edges are reached by fewer of them, or by none, and
    /// hold no signal (the owner: "the edges can be a bit weird in planetary data and never contain actual signal"), while an edge no frame
    /// reached sits below the sky and rings under the sharpening. The planet, its rings and its moons are never cut
    /// (<see cref="PlanetaryDisk.Footprint"/>). Off keeps the frame's full size.
    /// </summary>
    public bool CropToCoverage { get; init; } = true;

    /// <summary>
    /// The share of a stack's full coverage (the central median of its weight, <see cref="Image.LargestCoveredRectangle(Image, double, int)"/>)
    /// the edges of a master must reach to be kept (<see cref="CropToCoverage"/>, #1300): what is kept is within 1/sqrt(0.95) = 1.026 of
    /// the interior's noise, the deep-sky master's rule.
    /// </summary>
    public const double CoverageCropFraction = 0.95;

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
    /// A de-rotation was worth doing (<see cref="TurnPx"/> reached <see cref="PlanetaryDerotationOptions.MinimumTurnPx"/>) but the
    /// capture could not tell the planet's north (its quarters held no frames to compare, or it turned the planet under
    /// <see cref="PlanetaryDerotation.LeastTurnToTellNorthDeg"/>), so the stack was made as taken (#1292): north comes from agreement,
    /// never the limb fit alone.
    /// </summary>
    public bool NorthUnread { get; init; }

    /// <summary>
    /// How far the planet's turn over the capture moved the middle of its disk, px, when a de-rotation was asked for
    /// (<see cref="PlanetaryDerotationOptions.MinimumTurnPx"/> decides from it whether one was done); NaN for a capture without frame
    /// times, null when none was asked for.
    /// </summary>
    public double? TurnPx { get; init; }

    /// <summary>
    /// How a colour master's planes lay against green and whether they were moved onto it (<see cref="PlanetaryStackOptions.AlignChannels"/>);
    /// null for a mono master or when none was asked for.
    /// </summary>
    public PlanetaryChannelAlignmentResult? ChannelAlignment { get; init; }

    /// <summary>
    /// How many frames were left out because their planet was cut, by the frame's edge (#1237) or a straight line inside it (#1291), or
    /// they held none (<see cref="FrameGrader.IsCutOrEmpty"/>): an untracked Dobsonian lets the planet drift out of its field.
    /// </summary>
    public int FramesCut { get; init; }

    /// <summary>
    /// How many frames were left out because the telescope's motion smeared their planet (<see cref="FrameGrader.SmearRatio"/>, #1300): a
    /// scope that moves during a frame (a bump, a nudge, a slew) draws its planet far longer than the run's.
    /// </summary>
    public int FramesSmeared { get; init; }

    /// <summary>
    /// The rectangle of the master's uncropped grid it was cropped to (<see cref="PlanetaryStackOptions.CropToCoverage"/>, #1300), empty
    /// when it kept every pixel.
    /// </summary>
    public PixelRect Cropped { get; init; }

    /// <summary>
    /// How many alignment points the stack followed, 0 for a global one: <see cref="PlanetaryStackOptions.MaxAlignmentPoints"/> caps any grid
    /// silently, and a cell whose gradient is under a fifth of the frame's strongest keeps none, so a denser grid can follow few more (#1195).
    /// </summary>
    public int AlignmentPoints { get; init; }

    /// <summary>How many cells qualified for a point before <see cref="PlanetaryStackOptions.MaxAlignmentPoints"/> capped them; 0 for a global stack.</summary>
    public int AlignmentPointCandidates { get; init; }
}
