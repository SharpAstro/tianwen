using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve
{
    /// <summary>
    /// Fast differential plate "solver" used during the polar-alignment refining
    /// loop. Drops the catalog query + star-detect + pattern-match steps of a
    /// full <see cref="CatalogPlateSolver"/>, replacing them with ROI centroid
    /// against an anchor list captured at seed time. Targets &lt;20 ms per
    /// frame on a 60 MP polar-align preview where a full hinted solve runs
    /// ~700 ms.
    ///
    /// <para><b>Algorithm</b></para>
    /// <list type="number">
    ///   <item>Seed from a successful full solve: detect stars in the seed
    ///         frame, project each detected centroid through the seed WCS to
    ///         get a J2000 (RA, Dec). Stash the (image-px, sky) pairs.</item>
    ///   <item>Refine on each subsequent frame: ROI centroid each anchor's
    ///         expected position (using the previous frame's pixel coords as
    ///         the prior, since refining is sub-pixel between frames). Drop
    ///         anchors whose ROI peak SNR falls below a threshold.</item>
    ///   <item>Fit an affine M from old-pixel to new-pixel via weighted least
    ///         squares (<see cref="Matrix3x2Helper.FitAffineTransform"/>).</item>
    ///   <item>Apply M to the previous WCS: the new CRPix is the affine
    ///         applied to the old CRPix; the new CD matrix is the old CD
    ///         post-multiplied by the linear part of M^-1.</item>
    ///   <item>Validate via RMS residual. If above
    ///         <see cref="MaxRmsResidualPx"/>, return null and let the
    ///         orchestrator fall back to a full hinted solve.</item>
    /// </list>
    ///
    /// <para><b>Use cases</b></para>
    /// Only valid when the field is essentially locked between frames (polar
    /// alignment knob nudges, focus-tweak preview). Not a general plate solver:
    /// can't recover from a slew, a meridian flip, or a derotator move. The
    /// caller must reseed after any such event.
    /// </summary>
    /// <remarks>
    /// Not <see cref="IPlateSolver"/>: incremental refinement requires explicit
    /// state (anchor list + previous WCS) and can't be exposed as a stateless
    /// solver. The polar-align orchestrator owns the instance.
    /// </remarks>
    internal sealed class IncrementalSolver(ILogger? logger = null, ITimeProvider? timeProvider = null)
    {
        private readonly ILogger? _logger = logger;
        private readonly ITimeProvider? _timeProvider = timeProvider;
        private DateTimeOffset _seedUtc;

        // Frozen-seed star-list alignment state. Each Refine reads the
        // current frame's stars and aligns them to *the seed frame's stars*
        // via quad-pattern matching, NOT to the previous Refine's output.
        // This breaks the drift-accumulation chain that the prior ROI +
        // affine-on-prev-WCS approach had: every refine is now an
        // independent affine fit against the same seed reference, so the
        // worst-case error per frame is the per-frame plate-noise floor
        // (sub-pixel) and there is no compounding bias over time.
        private SortedStarList? _seedSortedStars;
        private WCS? _seedWcs;
        private int _seedDetectionScale = 1;

        /// <summary>SNR floor for star detection in both seed and live frames.</summary>
        public float SnrThreshold { get; init; } = 5f;

        /// <summary>Lower bound on detected stars per frame. Below this the seed/refine returns null and forces a fallback.</summary>
        public int MinAnchors { get; init; } = 10;

        /// <summary>
        /// Star cap for both seed and live detection. Quad matching builds
        /// O(N^4 / 24) quads per list and compares them pairwise across the
        /// two lists, so the cost grows quickly with N -- but we also need
        /// enough stars to have robust quads (the matcher needs >= 6 quad
        /// matches to fit an affine, and most quad pairs near the pole share
        /// distance invariants only loosely). 200 is a good compromise: at
        /// IMX455-class fields we typically detect ~100-150 stars per frame
        /// so we cap rarely, and the per-frame match cost stays under 50 ms.
        /// </summary>
        public int MaxAnchors { get; init; } = 200;

        /// <summary>True iff a successful <see cref="SeedAsync"/> has happened and the solver can <see cref="RefineAsync"/>.</summary>
        public bool IsSeeded => _seedWcs is { HasCDMatrix: true } && _seedSortedStars is { } s && s.Count >= MinAnchors;

        /// <summary>Detected-star count from the most recent seed (diagnostics).</summary>
        public int AnchorCount => _seedSortedStars?.Count ?? 0;

        /// <summary>The seed WCS that all live refines align against -- never mutated.</summary>
        public WCS? CurrentWcs => _seedWcs;

        /// <summary>
        /// Reset the internal state. Call after a slew / meridian flip /
        /// derotator move where the anchor list can no longer be trusted.
        /// </summary>
        public void Reset()
        {
            _seedWcs = null;
            _seedSortedStars?.Dispose();
            _seedSortedStars = null;
        }

        /// <summary>
        /// Seed the solver from a freshly captured frame and its full plate
        /// solution. Detects stars in the frame and projects each centroid
        /// through <paramref name="wcs"/> to get a J2000 (RA, Dec). Anchors
        /// without a CD matrix or behind the tangent plane are skipped.
        /// </summary>
        /// <returns>Number of anchors captured. The orchestrator should treat
        /// fewer than <see cref="MinAnchors"/> as a failed seed and stay on
        /// the full-solve path until conditions improve (e.g. exposure
        /// lengthens, clouds clear).</returns>
        public async ValueTask<int> SeedAsync(Image image, WCS wcs, CancellationToken ct = default)
        {
            if (!wcs.HasCDMatrix)
            {
                _logger?.LogDebug("IncrementalSolver: cannot seed -- WCS has no CD matrix");
                Reset();
                return 0;
            }

            // Detect stars on a downsampled view to match the per-pass cost
            // CatalogPlateSolver pays. Centroids come back in binned space;
            // we keep them in binned coords throughout (seed + every Refine
            // detect at the same scale) so quad-distance invariants are
            // directly comparable. The CD matrix lives in native-pixel units,
            // so when we apply the affine to the seed WCS we scale CRPix
            // back up by detectionScale.
            const double TargetPixelScaleArcsec = 1.5;
            var dim = image.GetImageDim();
            var detectionScale = 1;
            var detectionImage = image;
            if (dim is { PixelScale: > 0 } d && d.PixelScale < TargetPixelScaleArcsec)
            {
                detectionScale = (int)Math.Round(TargetPixelScaleArcsec / d.PixelScale);
                if (detectionScale > 1)
                {
                    detectionImage = image.Downsample(detectionScale);
                }
            }

            var stars = await detectionImage.FindStarsAsync(
                0, snrMin: SnrThreshold,
                maxStars: MaxAnchors, minStars: MinAnchors,
                logger: _logger, cancellationToken: ct).ConfigureAwait(false);

            if (stars.Count < MinAnchors)
            {
                _logger?.LogDebug("IncrementalSolver: seed yielded only {Count} stars (min {Min}) -- staying on full-solve path", stars.Count, MinAnchors);
                Reset();
                return stars.Count;
            }

            // Pre-build the seed quads (deferred until first FindFitAsync
            // call, but worth doing now so the first live Refine doesn't pay
            // a one-shot cost while the user is staring at the gauge).
            var seedSorted = new SortedStarList(stars);
            _ = await seedSorted.FindQuadsAsync(ct).ConfigureAwait(false);

            // Drop any prior seed before swapping in the new one.
            _seedSortedStars?.Dispose();
            _seedSortedStars = seedSorted;
            _seedWcs = wcs;
            _seedDetectionScale = detectionScale;
            // Stamp seed time so callers (PolarAlignmentSession) can correlate
            // the seed reference frame with the live refine timestamps when
            // building the sidereal-normalised reference frame.
            _seedUtc = _timeProvider?.GetUtcNow() ?? default;
            _logger?.LogDebug("IncrementalSolver: seeded with {Count} stars (detection scale {Scale}x)", stars.Count, detectionScale);
            return stars.Count;
        }

        /// <summary>
        /// Run one differential refinement step against the *frozen* seed
        /// reference (NOT against the previous Refine's output). Detects
        /// stars in the live frame, builds quad invariants, matches them
        /// against the seed's pre-built quads via <see cref="StarReferenceTable.FindFit"/>,
        /// fits an affine in pixel space, and composes that affine with the
        /// seed WCS to produce the live WCS. Drift-free by construction:
        /// every Refine independently aligns to the same seed reference, so
        /// per-frame plate-noise sets the precision floor with no
        /// accumulation across many frames.
        /// </summary>
        public async ValueTask<PlateSolveResult?> RefineAsync(Image image, CancellationToken ct = default)
        {
            if (_seedWcs is not { } seedWcs || _seedSortedStars is not { } seedStars)
            {
                return null;
            }

            var sw = Stopwatch.StartNew();

            // Detect stars in the live frame at the SAME downsample factor
            // the seed used. Star-quad invariants only match across frames
            // captured at identical pixel scales, since Dist1 is in absolute
            // pixels. Using the seed's detection scale also keeps the per-
            // frame cost predictable.
            var detectionImage = image;
            if (_seedDetectionScale > 1)
            {
                detectionImage = image.Downsample(_seedDetectionScale);
            }

            var stars = await detectionImage.FindStarsAsync(
                0, snrMin: SnrThreshold,
                maxStars: MaxAnchors, minStars: MinAnchors,
                logger: _logger, cancellationToken: ct).ConfigureAwait(false);

            if (stars.Count < MinAnchors)
            {
                _logger?.LogDebug("IncrementalSolver: live frame yielded only {Count} stars (min {Min}) -- falling back", stars.Count, MinAnchors);
                return null;
            }

            using var liveSorted = new SortedStarList(stars);

            // Quad-pattern matching: ASTAP-style geometric matching of star
            // quads via 6 normalised pairwise distances. Returns the matched
            // pair table (seed positions <- dest, live positions <- source).
            var refTable = await seedStars.FindFitAsync(liveSorted, minimumCount: 6, quadTolerance: 0.008f).ConfigureAwait(false);
            if (refTable is null || refTable.Count < 3)
            {
                _logger?.LogDebug("IncrementalSolver: quad match failed ({Count} pairs) -- falling back", refTable?.Count ?? 0);
                return null;
            }

            // FitAffineTransform returns M mapping seed pixels -> live pixels.
            // ApplyAffineToWcs treats its input as "old -> new", composing
            // with the seed WCS to produce a WCS on the live frame.
            if (refTable.FitAffineTransform() is not { } M || !Matrix3x2.Invert(M, out _))
            {
                _logger?.LogDebug("IncrementalSolver: quad-matched affine singular -- falling back");
                return null;
            }

            // Transition from binned-pixel space (where the affine was fit)
            // to native-pixel space (where the seed WCS lives). The CD matrix
            // and CRPix in seedWcs are native-pixel units; the affine M is
            // binned-pixel. Compose: native_new = scale * M * (native_old /
            // scale) = scaled affine. For our purposes the linear part of M
            // is dimensionless (rotation + uniform scale ~1) so we can apply
            // M directly to the native-scale CRPix, but the translation
            // component must scale by detectionScale. Folding it together
            // with Matrix3x2 stays clean if we represent the scaling around
            // the seed's CRPix.
            var Mscaled = M;
            if (_seedDetectionScale > 1)
            {
                // Translation column (M31, M32) of M is in binned pixels.
                // Scale it back to native pixels. Linear part of M (M11,
                // M12, M21, M22) is dimensionless and stays unchanged: a
                // rotation/scale in binned space is the same rotation/scale
                // in native space.
                Mscaled = new Matrix3x2(
                    M.M11, M.M12,
                    M.M21, M.M22,
                    M.M31 * _seedDetectionScale, M.M32 * _seedDetectionScale);
            }

            var shifted = ApplyAffineToWcs(seedWcs, Mscaled);
            var newWcs = CanonicaliseToFrameCentre(shifted, image.Width, image.Height);

            return new PlateSolveResult(newWcs, sw.Elapsed)
            {
                MatchedStars = refTable.Count,
                DetectedStars = stars.Count,
                Iterations = 1,
            };
        }

        /// <summary>
        /// CRPix_new = Vector2.Transform(CRPix_old, M).
        /// CD_new = CD_old * L^T_inv where L^T = column-vector form of M's
        /// linear part. Working it through with Matrix3x2.Invert(M, out Minv):
        /// Minv has elements (Minv.M11, Minv.M12, Minv.M21, Minv.M22) for the
        /// row-vector inverse. The column-vector inverse is the transpose of
        /// that, i.e. (Minv.M11, Minv.M21, Minv.M12, Minv.M22) read row-major.
        /// </summary>
        private static WCS ApplyAffineToWcs(WCS prev, Matrix3x2 M)
        {
            // Cannot fail: caller already ensured Invert succeeded above.
            Matrix3x2.Invert(M, out var Minv);

            var oldCrPix = new Vector2((float)prev.CRPix1, (float)prev.CRPix2);
            var newCrPix = Vector2.Transform(oldCrPix, M);

            // L_col_inv elements (column-vector convention):
            //   row 0: [Minv.M11, Minv.M21]
            //   row 1: [Minv.M12, Minv.M22]
            // CD_new = CD_old * L_col_inv (matrix product, both 2x2):
            //   CD_new[0,0] = CD1_1 * Minv.M11 + CD1_2 * Minv.M12
            //   CD_new[0,1] = CD1_1 * Minv.M21 + CD1_2 * Minv.M22
            //   CD_new[1,0] = CD2_1 * Minv.M11 + CD2_2 * Minv.M12
            //   CD_new[1,1] = CD2_1 * Minv.M21 + CD2_2 * Minv.M22
            return prev with
            {
                CRPix1 = newCrPix.X,
                CRPix2 = newCrPix.Y,
                CD1_1 = prev.CD1_1 * Minv.M11 + prev.CD1_2 * Minv.M12,
                CD1_2 = prev.CD1_1 * Minv.M21 + prev.CD1_2 * Minv.M22,
                CD2_1 = prev.CD2_1 * Minv.M11 + prev.CD2_2 * Minv.M12,
                CD2_2 = prev.CD2_1 * Minv.M21 + prev.CD2_2 * Minv.M22,
            };
        }

        /// <summary>
        /// Canonicalise a WCS by moving CRPix back to the (1-based) frame
        /// centre and updating CenterRA / CenterDec to the sky position at
        /// that pixel. The CD matrix is left unchanged: locally near the
        /// frame centre the projection is well-approximated as linear, so
        /// the gnomonic re-tangenting error is well below the centroid
        /// noise for sub-pixel CRPix shifts. Downstream consumers can then
        /// read wcs.CenterRA / wcs.CenterDec as the frame-centre sky
        /// without having to know which solver produced the WCS.
        /// </summary>
        private static WCS CanonicaliseToFrameCentre(WCS wcs, int width, int height)
        {
            var fcX = (width + 1) / 2.0;
            var fcY = (height + 1) / 2.0;
            var fcSky = wcs.PixelToSky(fcX, fcY);
            if (fcSky is not { } pos)
            {
                // Should never happen with a valid CD matrix and sane CRPix --
                // if it does, return the un-canonicalised WCS so the caller at
                // least has the CRPix-shifted result to fall back on.
                return wcs;
            }
            return wcs with
            {
                CenterRA = pos.RA,
                CenterDec = pos.Dec,
                CRPix1 = fcX,
                CRPix2 = fcY,
            };
        }

    }
}
