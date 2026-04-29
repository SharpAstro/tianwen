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

        // Sidereal angular velocity in J2000 (rad/s). Used to advance the
        // refined WCS centre forward from seed time to current time so it
        // matches what a real plate solver would return at the live capture
        // moment. Without this, downstream consumers calling
        // <c>SiderealNormalise(wcsRaw, t_now, t_ref)</c> on the
        // IncrementalSolver's output get a double-correction: the refined WCS
        // is already implicitly anchored at seed-time J2000 (because anchors'
        // sky RA/Dec are frozen at seed), so sidereal-back-rotating from "now"
        // introduces 7+'-magnitude bias at typical Phase B durations.
        private const double SiderealRateRadPerSec = 2.0 * Math.PI / 86164.0905;

        /// <summary>
        /// Half-extent of the ROI box used for centroid refinement. 5 gives an
        /// 11x11-pixel window -- comfortably wider than typical HFD * 2 even on
        /// poor seeing, and still sub-millisecond per anchor.
        /// </summary>
        public int RoiHalfSize { get; init; } = 5;

        /// <summary>SNR floor for keeping an anchor through Refine. Below this the anchor is dropped.</summary>
        public float SnrThreshold { get; init; } = 5f;

        /// <summary>Lower bound on surviving anchors. Below this Refine returns null and forces a fallback.</summary>
        public int MinAnchors { get; init; } = 10;

        /// <summary>RMS residual ceiling (in pixels) for an accepted refine. Above this Refine returns null.</summary>
        public double MaxRmsResidualPx { get; init; } = 2.0;

        /// <summary>Anchor cap at seed time -- 50 keeps the per-frame ROI work under ~5 ms even on a low-end CPU.</summary>
        public int MaxAnchors { get; init; } = 50;

        private WCS? _wcs;
        private List<Anchor> _anchors = [];

        /// <summary>True iff a successful <see cref="SeedAsync"/> has happened and the solver can <see cref="Refine"/>.</summary>
        public bool IsSeeded => _wcs is { } w && w.HasCDMatrix && _anchors.Count >= MinAnchors;

        /// <summary>Anchor count from the most recent seed (diagnostics).</summary>
        public int AnchorCount => _anchors.Count;

        /// <summary>The current WCS estimate -- updated on each successful Refine, null until first seed.</summary>
        public WCS? CurrentWcs => _wcs;

        /// <summary>
        /// Reset the internal state. Call after a slew / meridian flip /
        /// derotator move where the anchor list can no longer be trusted.
        /// </summary>
        public void Reset()
        {
            _wcs = null;
            _anchors.Clear();
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
            // CatalogPlateSolver pays. A 60MP polar-align frame at 0.97"/px
            // runs FindStarsAsync ~4x faster when binned 2x to 1.94"/px, and
            // we don't need sub-arcsec centroid accuracy for ROI-anchor
            // priors -- the per-frame Refine re-centroids in a small box at
            // native resolution. Centroids come back in binned space; scale
            // them to original-image coords before projecting through the WCS.
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

            var stars = await detectionImage.FindStarsAsync(0, snrMin: SnrThreshold, maxStars: MaxAnchors, minStars: MinAnchors, logger: _logger, cancellationToken: ct).ConfigureAwait(false);

            // (factor*x + factor/2 - 0.5) puts the binned-pixel centre back to
            // the centre of its original block. Mirrors CatalogPlateSolver's
            // rescale step.
            var halfBlock = detectionScale > 1 ? detectionScale / 2.0f - 0.5f : 0f;

            var anchors = new List<Anchor>(stars.Count);
            foreach (var s in stars)
            {
                var nativeX = detectionScale > 1 ? s.XCentroid * detectionScale + halfBlock : s.XCentroid;
                var nativeY = detectionScale > 1 ? s.YCentroid * detectionScale + halfBlock : s.YCentroid;
                // FindStars uses 0-based pixel coords; WCS is 1-based (FITS
                // convention). Add 1 for the projection, store the 0-based
                // pixel coord for the anchor.
                var sky = wcs.PixelToSky(nativeX + 1.0, nativeY + 1.0);
                if (sky is { } pos)
                {
                    anchors.Add(new Anchor(nativeX, nativeY, pos.RA, pos.Dec, s.Flux));
                }
            }

            if (anchors.Count < MinAnchors)
            {
                _logger?.LogDebug("IncrementalSolver: seed yielded only {Count} anchors (min {Min}) -- staying on full-solve path", anchors.Count, MinAnchors);
                Reset();
                return anchors.Count;
            }

            _wcs = wcs;
            _anchors = anchors;
            // Stamp seed time so subsequent Refine() calls can sidereal-
            // forward-rotate the WCS centre from seed-time J2000 (which is
            // what the anchor list represents) to capture-time J2000 (which
            // is what a real plate solver would emit). Falls back to
            // DateTimeOffset.MinValue when no time provider is wired in --
            // safe because the forward rotation is only applied when the
            // provider is non-null.
            _seedUtc = _timeProvider?.GetUtcNow() ?? default;
            _logger?.LogDebug("IncrementalSolver: seeded with {Count} anchors (detection scale {Scale}x)", anchors.Count, detectionScale);
            return anchors.Count;
        }

        /// <summary>
        /// Run one differential refinement step. ROI-centroids each anchor in
        /// <paramref name="image"/>, fits an affine from old to new positions,
        /// applies the affine to the seed WCS to recover the current frame's
        /// WCS. Returns null on insufficient anchors, singular fit, or RMS
        /// residual above <see cref="MaxRmsResidualPx"/> -- caller should fall
        /// back to a full hinted solve and reseed.
        /// </summary>
        public PlateSolveResult? Refine(Image image, CancellationToken ct = default)
        {
            if (_wcs is not { } prevWcs || _anchors.Count < MinAnchors)
            {
                return null;
            }

            var sw = Stopwatch.StartNew();

            // ROI-centroid each anchor in the new frame. The anchor's previous
            // pixel coord is the prior; the field shifts only by the user's
            // knob nudge (sub-pixel to a few pixels), so a small box is enough.
            var oldVecs = new List<Vector2>(_anchors.Count);
            var newVecs = new List<Vector2>(_anchors.Count);
            var survivingIndexes = new List<int>(_anchors.Count);
            for (int i = 0; i < _anchors.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var a = _anchors[i];
                if (TryRoiCentroid(image, a.ImgX, a.ImgY, RoiHalfSize, SnrThreshold, out var newX, out var newY))
                {
                    oldVecs.Add(new Vector2((float)a.ImgX, (float)a.ImgY));
                    newVecs.Add(new Vector2((float)newX, (float)newY));
                    survivingIndexes.Add(i);
                }
            }

            if (survivingIndexes.Count < MinAnchors)
            {
                _logger?.LogDebug("IncrementalSolver: only {Count} anchors survived ROI gate (min {Min})", survivingIndexes.Count, MinAnchors);
                return null;
            }

            // Fit affine M: pixel_new = Vector2.Transform(pixel_old, M).
            var fit = Matrix3x2.FitAffineTransform(CollectionsMarshal.AsSpan(oldVecs), CollectionsMarshal.AsSpan(newVecs));
            if (fit is not { } M || !Matrix3x2.Invert(M, out _))
            {
                _logger?.LogDebug("IncrementalSolver: affine fit singular");
                return null;
            }

            // RMS residual sanity gate. Above MaxRmsResidualPx the field has
            // moved more than a small knob-nudge would explain (e.g. the user
            // bumped the rig hard, or a cloud blanked half the anchors and the
            // few that survived are noise). Force a fallback.
            double sumSqResidual = 0;
            for (int i = 0; i < oldVecs.Count; i++)
            {
                var transformed = Vector2.Transform(oldVecs[i], M);
                var dx = transformed.X - newVecs[i].X;
                var dy = transformed.Y - newVecs[i].Y;
                sumSqResidual += dx * dx + dy * dy;
            }
            var rms = Math.Sqrt(sumSqResidual / oldVecs.Count);
            if (rms > MaxRmsResidualPx)
            {
                _logger?.LogDebug("IncrementalSolver: RMS residual {Rms:F2} px exceeds ceiling {Ceiling:F2} px -- falling back", rms, MaxRmsResidualPx);
                return null;
            }

            // Update WCS: CRPix_new = M(CRPix_old). CD_new = CD_old * L_col^-1
            // where L_col is the linear part of M in column-vector convention.
            // See class doc for the derivation. Then canonicalise so CRPix is
            // back at the frame centre and CenterRA/CenterDec is the sky at
            // that pixel -- matches what CatalogPlateSolver emits, so
            // downstream consumers reading wcs.CenterRA / wcs.CenterDec (e.g.
            // PolarAlignmentSession's frame-centre unit vector derivation)
            // see consistent values across the full-solve and incremental
            // paths.
            var shifted = ApplyAffineToWcs(prevWcs, M);
            var newWcs = CanonicaliseToFrameCentre(shifted, image.Width, image.Height);

            // Update anchors: replace each surviving anchor's image-pixel coord
            // with the just-observed centroid so the next Refine works from
            // the latest positions. J2000 RA/Dec is unchanged; the catalog
            // doesn't move between frames.
            for (int i = 0; i < survivingIndexes.Count; i++)
            {
                var idx = survivingIndexes[i];
                var n = newVecs[i];
                _anchors[idx] = _anchors[idx] with { ImgX = n.X, ImgY = n.Y };
            }
            _wcs = newWcs;

            return new PlateSolveResult(newWcs, sw.Elapsed)
            {
                MatchedStars = survivingIndexes.Count,
                DetectedStars = survivingIndexes.Count,
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

        /// <summary>
        /// Background-subtracted weighted centroid in a (2*halfSize+1)^2 box
        /// around (cx, cy). Returns false if the box would step outside the
        /// image, if the peak SNR is below <paramref name="snrFloor"/>, or if
        /// the weighted-sum denominator is non-positive (saturated all-bg
        /// region).
        /// </summary>
        private static bool TryRoiCentroid(Image image, double cx, double cy, int halfSize, float snrFloor, out double newCx, out double newCy)
        {
            newCx = 0;
            newCy = 0;

            int x0 = (int)Math.Round(cx) - halfSize;
            int y0 = (int)Math.Round(cy) - halfSize;
            int x1 = x0 + 2 * halfSize;
            int y1 = y0 + 2 * halfSize;
            if (x0 < 0 || y0 < 0 || x1 >= image.Width || y1 >= image.Height)
            {
                return false;
            }

            int n = (2 * halfSize + 1) * (2 * halfSize + 1);
            Span<float> pixels = stackalloc float[n];
            int idx = 0;
            float maxVal = float.MinValue;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var v = image[0, y, x];
                    pixels[idx++] = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            // Background = 25th percentile of the box. Stable against a single
            // bright peak and one or two bad pixels; cheap on a small ROI.
            Span<float> sorted = stackalloc float[n];
            pixels.CopyTo(sorted);
            sorted.Sort();
            float bg = sorted[n / 4];

            // Noise = MAD around the background, scaled by 1.4826 to approximate
            // a Gaussian sigma. Cheap and robust for sub-arcsec PSFs where the
            // ROI tail is dominated by sky photon noise.
            Span<float> dev = stackalloc float[n];
            for (int i = 0; i < n; i++)
            {
                dev[i] = MathF.Abs(sorted[i] - bg);
            }
            dev.Sort();
            float mad = dev[n / 2];
            float noise = MathF.Max(mad * 1.4826f, 1e-3f);

            float snr = (maxVal - bg) / noise;
            if (snr < snrFloor)
            {
                return false;
            }

            // Weighted centroid using bg-subtracted pixels. Negative values
            // (bg fluctuations) are clipped to zero so they don't pull the
            // centroid off the peak.
            double sumWX = 0, sumWY = 0, sumW = 0;
            idx = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var v = pixels[idx++] - bg;
                    if (v <= 0) continue;
                    sumWX += v * x;
                    sumWY += v * y;
                    sumW += v;
                }
            }
            if (sumW <= 0) return false;

            newCx = sumWX / sumW;
            newCy = sumWY / sumW;
            return true;
        }

        /// <summary>
        /// One anchor: image pixel coord (0-based, matches FindStars), J2000
        /// (RA, Dec) projected through the seed WCS, and seed-time flux for
        /// future weighted-fit support. RA/Dec stay constant -- only the
        /// pixel coords are updated as the field shifts.
        /// </summary>
        private readonly record struct Anchor(double ImgX, double ImgY, double RaHours, double DecDeg, float Flux);
    }
}
