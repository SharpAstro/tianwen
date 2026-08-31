using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// Deterministic two-star similarity lock between projected catalog stars and detected image
/// stars, used by <see cref="CatalogPlateSolver"/> to seed its proximity refinement with a
/// geometrically verified initial transform (translation + rotation + uniform scale).
///
/// <para><b>Why not quads:</b> ASTAP-style quad matching (<see cref="Imaging.StarReferenceTable"/>)
/// requires both lists to share nearest-neighbour structure, and the catalog / detected
/// populations differ too much for that even under top-K brightness selection; probed
/// empirically on a dense Vela field (5,080 in-frame catalog stars, 1,580 detected): no quad
/// lock at any K from 50 to 500, either parity. A quad needs the same 4 stars with the same
/// 3-nearest-neighbour relations on both sides; a pair hypothesis needs only 2 common stars
/// anywhere in the anchor sets, and lets global consensus do the verifying.</para>
///
/// <para><b>Algorithm:</b> enumerate bright detected star pairs (brightness-ordered, so likely
/// hypotheses come first) against catalog pairs of compatible separation (binary search over a
/// pre-sorted pair-separation index). Each candidate pairing determines a similarity transform
/// via the complex ratio of the pair vectors: chirality-preserving by construction, so mirror
/// parity stays cleanly separated in the caller's per-<c>xSign</c> attempts. Hypotheses are
/// staged against a uniform grid hash of ALL detected stars (cheap 8-star probe, then 32, then
/// full census), then <b>refined onto their own inliers before being judged</b>
/// (<see cref="TryRefine"/>), and accepted only when the refined transform's hit count beats
/// the Poisson expectation of chance alignment by <see cref="ChanceSafetyFactor"/>; the
/// statistic that distinguishes a genuine lock from the dense-field nearest-neighbour-noise
/// regime where proximity matching silently fails.</para>
/// </summary>
internal static class PairRansacLock
{
    /// <summary>Detected-star anchors used to form hypothesis pairs. Kept small because the
    /// pair count is quadratic; brightness ordering makes the true hypothesis appear early.</summary>
    private const int MaxDetectedAnchors = 48;

    /// <summary>Catalog-star anchors (and verification census size). Deeper than the detected
    /// side so a bright detected star's counterpart survives saturation-scrambled flux ranks.</summary>
    private const int MaxCatalogAnchors = 160;

    /// <summary>Hypothesis pairs shorter than this fraction of min(width, height) are skipped:
    /// rotation derived from a short baseline is too noisy to verify at the corners.</summary>
    private const float MinBaselineFraction = 0.2f;

    /// <summary>Stage gates: probe hit floors at 8 and 32 catalog stars. Chance passes the
    /// first gate at ~4e-5 per hypothesis, so full verification runs only on real candidates.</summary>
    private const int Stage1Count = 8, Stage1MinHits = 3, Stage2Count = 32, Stage2MinHits = 8;

    /// <summary>
    /// Staging probes at this multiple of the verification radius, because a hypothesis cannot
    /// be judged at the radius its refined successor is judged at.
    /// <para>A hypothesis is derived from ONE pair, so its rotation error is the pair's centroid
    /// error divided by its baseline, and that angular error is amplified by the distance from
    /// the pair to wherever the hit is being counted. The baseline floor is
    /// <see cref="MinBaselineFraction"/> of the short side and the far corner is half the
    /// diagonal away, so the worst-case amplification is
    /// <c>0.5 * sqrt(w^2 + h^2) / (0.2 * min(w, h))</c> -- 3.5x on a square frame, 4.2x on 4:3.
    /// A per-star centroid error inside the verification radius therefore lands OUTSIDE it at
    /// the corners, and the true hypothesis dies at Stage 1 having been measured against a
    /// tolerance only its own refinement could meet.</para>
    /// <para>Measured on LDN 1089 (835 px winning baseline, ~1.9 px centroid error, 8.6 px
    /// corner displacement): at a 4 px radius the best hypothesis scored 12 of 160 against a
    /// threshold of 24, and at 12 px it scored 38. Widening the ACCEPT radius does not fix that
    /// -- chance grows as r^2, so the threshold outran the gain (24 -> 100.5). Widening only the
    /// CAPTURE radius does: the accept decision stays at the tight radius, where chance is
    /// unchanged, and the extra reach is spent on giving refinement something to work with.
    /// Horsehead, whose corner displacement is 1.8 px, locks either way.</para>
    /// </summary>
    private const float CaptureRadiusFactor = 3f;

    /// <summary>
    /// Refinement rounds per promoted candidate. Each is a capture-and-refit (ICP) pass, and the
    /// transform is already within a capture radius of the truth when the first one runs, so it
    /// converges in two; the third is the margin.
    /// </summary>
    private const int RefineIterations = 3;

    /// <summary>
    /// Correspondences a refit needs. An affine has 6 degrees of freedom, so at 3 points it
    /// interpolates rather than fits and any noise becomes signal.
    /// </summary>
    private const int MinRefineCorrespondences = 6;

    /// <summary>
    /// Consensus fraction that ends the scan. Plate solving has exactly ONE true transform and
    /// every hypothesis is verified by a full census (not a noisy sample), so the first
    /// hypothesis clearing the chance-safe accept threshold is the lock; the LSQ refit then
    /// re-captures inliers over the full radius, so a marginally off-centre winning pair lands
    /// on the same final affine a higher-consensus duplicate would. Adaptive-stop lesson from
    /// PDF.Lib.Diff's PointSetAligner (the drawboard sibling of this algorithm): make the stop
    /// a LOOP-BOUND condition, not a break inside the improvement branch, which only re-enters
    /// on a new best and therefore never fires once consensus stops growing.
    /// </summary>
    private const float EarlyExitFraction = 0.25f;

    /// <summary>Accepted hit count must exceed this multiple of the expected chance hits.</summary>
    private const double ChanceSafetyFactor = 5.0;

    /// <summary>Absolute floor on accepted hits, independent of the chance estimate.</summary>
    private const int MinAcceptHits = 10;

    /// <summary>
    /// Accepted hits must also reach this fraction of the matchable census
    /// (min(catalog anchors, detected stars)). The Poisson chance model assumes an
    /// independent star field, but real fields cluster -- an open cluster lets a
    /// clump-onto-clump hypothesis pile up hits far beyond the independent-field
    /// expectation, and across a 400k-hypothesis exhaustive scan the maximum of those
    /// correlated draws was observed reaching 14 on a parity with no true solution.
    /// A genuine lock carries the majority of the bright census; 15% is far below any
    /// observed real lock and far above the clustered-chance pile-up.
    /// </summary>
    private const float ConsensusFloorFraction = 0.15f;

    /// <summary>
    /// Runaway backstop on the hypothesis scan. Sized so a 48-detected x 160-catalog anchor
    /// field can be searched EXHAUSTIVELY: each detected pair costs the width of its
    /// scale-compatible catalog window (~800 hypotheses at a 3% scale tolerance over 12,720
    /// catalog pairs), and there are 1,128 detected pairs, so full coverage needs ~900k.
    /// The previous 400k stopped at 37-41% of pairs on real Vela frames and found no seed on
    /// fields that do have one -- reported as "capped" rather than "nothing correlates" only
    /// because <see cref="LockDiagnostics"/> now separates the two. This costs nothing on a
    /// field that locks (Vela panels seeded at 235-567 hypotheses and exit early); it is paid
    /// only where the alternative was failing the solve.
    /// </summary>
    private const int MaxHypotheses = 1_000_000;

    internal readonly record struct LockResult(Matrix3x2 Transform, int Hits, int Census, double ExpectedChanceHits, int Hypotheses);

    /// <summary>
    /// Why a lock attempt ended where it did. A bare <c>null</c> cannot distinguish "the
    /// hypothesis space was exhausted and nothing correlated" from "the cap stopped the scan
    /// before the true hypothesis was reached", and those call for opposite fixes -- so the
    /// scan reports both. <see cref="DetectedPairsTried"/> against
    /// <see cref="DetectedPairsTotal"/> is the coverage figure: well under 1.0 with
    /// <see cref="HypothesisCapHit"/> set means the answer is "not searched", not "not there".
    /// </summary>
    internal readonly record struct LockDiagnostics(
        int CatalogAnchors,
        int DetectedAnchors,
        int Hypotheses,
        bool HypothesisCapHit,
        int DetectedPairsTried,
        int DetectedPairsTotal,
        int BestHits,
        double AcceptThreshold,
        double ExpectedChanceHits,
        int Refinements,
        /// <summary>The scan was abandoned because the other parity locked first, so "no lock" here is not evidence there is none.</summary>
        bool Cancelled = false)
    {
        /// <summary>Fraction of bright detected pairs the scan actually got to.</summary>
        internal double Coverage => DetectedPairsTotal == 0 ? 0 : (double)DetectedPairsTried / DetectedPairsTotal;

        public override string ToString() =>
            $"{BestHits} best hits vs threshold {AcceptThreshold:F1} (chance {ExpectedChanceHits:F1}) over " +
            $"{Hypotheses} hypotheses{(HypothesisCapHit ? " (CAP HIT)" : "")}, " +
            $"{Refinements} refined, " +
            $"{DetectedPairsTried}/{DetectedPairsTotal} bright detected pairs tried ({Coverage:P0}), " +
            $"{CatalogAnchors} catalog / {DetectedAnchors} detected anchors";
    }

    /// <summary>
    /// Attempts the lock. <paramref name="catalogBright"/> and <paramref name="detectedBright"/>
    /// must be brightest-first; <paramref name="detectedAll"/> is the full detected field the
    /// consensus is measured against. Returns the refined affine mapping catalog-projected
    /// pixels to detected pixels, or <c>null</c> when no hypothesis beats chance --
    /// <paramref name="diagnostics"/> then says which.
    /// </summary>
    internal static LockResult? TryLock(
        ReadOnlySpan<Vector2> catalogBright,
        ReadOnlySpan<Vector2> detectedBright,
        ReadOnlySpan<Vector2> detectedAll,
        int width,
        int height,
        float scaleTolerance,
        out LockDiagnostics diagnostics,
        float verifyRadiusPx = 4f,
        CancellationToken cancellationToken = default)
    {
        var cancelled = false;
        var nCat = Math.Min(MaxCatalogAnchors, catalogBright.Length);
        var nDet = Math.Min(MaxDetectedAnchors, detectedBright.Length);
        diagnostics = new LockDiagnostics(nCat, nDet, 0, false, 0, Math.Max(0, nDet * (nDet - 1) / 2), 0, 0, 0, 0);
        if (nCat < Stage1Count || nDet < 2 || detectedAll.Length < Stage1MinHits)
        {
            return null;
        }

        // Two radii, two jobs (see CaptureRadiusFactor): staging and refinement CAPTURE at the
        // wide one, the accept decision VERIFIES at the tight one. Chance is a property of the
        // radius a hit is accepted at, so widening the capture radius costs candidates to refine,
        // never a looser bar to clear.
        var verifyGrid = new PointGrid(detectedAll, width, height, verifyRadiusPx);
        var captureGrid = new PointGrid(detectedAll, width, height, CaptureRadiusFactor * verifyRadiusPx);
        var verifyRadiusSq = verifyRadiusPx * verifyRadiusPx;

        // Chance model: a transformed catalog star lands within verifyRadius of SOME detected
        // star with probability lambda * pi * r^2 (Poisson field). The accept threshold derives
        // from this, so density can never fake a lock.
        var lambda = detectedAll.Length / ((double)width * height);
        var expectedChance = nCat * lambda * Math.PI * verifyRadiusSq;
        var consensusFloor = ConsensusFloorFraction * Math.Min(nCat, detectedAll.Length);
        var acceptThreshold = Math.Max(Math.Max(MinAcceptHits, ChanceSafetyFactor * expectedChance), consensusFloor);

        // Promotion floor for refinement. The same Poisson model at the capture radius says how
        // many hits chance alone supplies there, and a candidate that cannot beat its own noise
        // is not worth refining; Stage2MinHits floors it on a sparse field where that number is
        // near zero. This gate is a COST control, not a correctness one -- everything promoted
        // still has to clear acceptThreshold at the verify radius afterwards.
        var expectedCaptureChance = expectedChance * CaptureRadiusFactor * CaptureRadiusFactor;
        var refineFloor = Math.Max(Stage2MinHits, (int)Math.Ceiling(expectedCaptureChance));

        // Pair-separation index over the catalog anchors, sorted ascending so each detected
        // pair's scale-compatible window is a binary search + linear walk.
        var pairCount = nCat * (nCat - 1) / 2;
        var pairs = new (float Sep, short A, short B)[pairCount];
        var p = 0;
        for (var a = 0; a < nCat; a++)
        {
            for (var b = a + 1; b < nCat; b++)
            {
                var dx = catalogBright[b].X - catalogBright[a].X;
                var dy = catalogBright[b].Y - catalogBright[a].Y;
                pairs[p] = (MathF.Sqrt(dx * dx + dy * dy), (short)a, (short)b);
                p++;
            }
        }
        Array.Sort(pairs, static (x, y) => x.Sep.CompareTo(y.Sep));

        var minBaseline = MinBaselineFraction * Math.Min(width, height);
        var scaleLoSq = (1f - scaleTolerance) * (1f - scaleTolerance);
        var scaleHiSq = (1f + scaleTolerance) * (1f + scaleTolerance);

        var bestHits = 0;
        Matrix3x2 bestM = default;
        var hypotheses = 0;
        var detectedPairsTried = 0;
        var refinements = 0;
        var capHit = false;

        // Refit scratch, allocated once: refinement runs on a small fraction of hypotheses but
        // that is still hundreds of calls, and it is on the solve's critical path.
        var refineSrc = new Vector2[nCat];
        var refineDst = new Vector2[nCat];
        var earlyExitHits = Math.Max((int)Math.Ceiling(acceptThreshold), (int)(EarlyExitFraction * nCat));

        // Detected pairs in RANK-SUM order -- (0,1), (0,2), (1,2), (0,3), (1,3), (2,3), ...
        // rather than the lexicographic (0,1)..(0,47), (1,2).. this used to run. Each pair
        // costs the full width of its scale-compatible catalog window (~800 hypotheses on a
        // 160-anchor field), so lexicographic order spends ~40,000 hypotheses on pairs that
        // all share detected star 0 -- and if that one star is a blend, a hot-pixel cluster
        // or anything else with no catalog counterpart, every one of them is wasted. A
        // handful of such stars at the bright end burns the whole budget before the scan
        // reaches an honest pair. Rank-sum spreads the budget over the bright end instead,
        // so no single bad detection can monopolise it. Measured on Vela panels 17 and 18:
        // lexicographic reached 37-41% of pairs at the cap and found NO seed on either.
        var detPairs = new (short I, short J)[nDet * (nDet - 1) / 2];
        var dp = 0;
        for (var i = 0; i < nDet; i++)
        {
            for (var j = i + 1; j < nDet; j++)
            {
                detPairs[dp++] = ((short)i, (short)j);
            }
        }
        Array.Sort(detPairs, static (x, y) =>
        {
            var c = (x.I + x.J).CompareTo(y.I + y.J);
            return c != 0 ? c : x.I.CompareTo(y.I);
        });

        foreach (var (i, j) in detPairs)
        {
            if (bestHits >= earlyExitHits)
            {
                break;
            }

            detectedPairsTried++;
            var ddx = detectedBright[j].X - detectedBright[i].X;
            var ddy = detectedBright[j].Y - detectedBright[i].Y;
            var dDet = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dDet < minBaseline)
            {
                continue;
            }

            // Catalog pairs whose separation is scale-compatible (+/- centroid slack).
            var lo = dDet / (1f + scaleTolerance) - 3f;
            var hi = dDet / (1f - scaleTolerance) + 3f;
            var k = LowerBound(pairs, lo);
            for (; k < pairs.Length && pairs[k].Sep <= hi && bestHits < earlyExitHits; k++)
            {
                var catA = catalogBright[pairs[k].A];
                var catB = catalogBright[pairs[k].B];

                // Both correspondence assignments of the candidate pair.
                for (var flip = 0; flip < 2; flip++)
                {
                    var detI = flip == 0 ? detectedBright[i] : detectedBright[j];
                    var detJ = flip == 0 ? detectedBright[j] : detectedBright[i];

                    if (++hypotheses > MaxHypotheses)
                    {
                        capHit = true;
                        goto scanDone;
                    }

                    // Give up when the OTHER parity has already locked. Checked every 4096
                    // hypotheses rather than every one: this is the innermost loop of the whole
                    // solve, and a token read here would be measurable where the abandoned work
                    // it saves is ~250k hypotheses. Leaves by the same path as the cap, so the
                    // diagnostics still describe how far the scan actually got -- and says
                    // CANCELLED, because "no lock" and "never finished looking" are different
                    // facts and only one of them justifies not retrying.
                    if ((hypotheses & 0xFFF) == 0 && cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        goto scanDone;
                    }

                    // Similarity linear part from the complex ratio (detJ-detI)/(catB-catA):
                    // rotation + uniform scale, always chirality-preserving (det = re^2+im^2 > 0).
                    var dcx = catB.X - catA.X;
                    var dcy = catB.Y - catA.Y;
                    var vdx = detJ.X - detI.X;
                    var vdy = detJ.Y - detI.Y;
                    var den = dcx * dcx + dcy * dcy;
                    // Collapsed catalog basis gives NaN scale, and NaN PASSES a
                    // `< lo || > hi` range gate (both comparisons are false) -- reject
                    // it explicitly, per the same guard in PointSetAligner.SimilarityFrom2.
                    if (den < 1e-6f)
                    {
                        continue;
                    }
                    var re = (vdx * dcx + vdy * dcy) / den;
                    var im = (vdy * dcx - vdx * dcy) / den;
                    var scaleSq = re * re + im * im;
                    if (scaleSq < scaleLoSq || scaleSq > scaleHiSq)
                    {
                        continue;
                    }

                    var tx = detI.X - (catA.X * re - catA.Y * im);
                    var ty = detI.Y - (catA.X * im + catA.Y * re);

                    var m = new Matrix3x2(re, im, -im, re, tx, ty);

                    // Staged consensus at the CAPTURE radius: cheap probes first so chance
                    // hypotheses die in nanoseconds; only near-certain candidates pay the full
                    // census, and only those pay refinement.
                    var captured = CountHits(catalogBright, 0, Stage1Count, m, captureGrid);
                    if (captured < Stage1MinHits)
                    {
                        continue;
                    }
                    captured += CountHits(catalogBright, Stage1Count, Math.Min(Stage2Count, nCat), m, captureGrid);
                    if (captured < Stage2MinHits)
                    {
                        continue;
                    }
                    captured += CountHits(catalogBright, Math.Min(Stage2Count, nCat), nCat, m, captureGrid);
                    if (captured < refineFloor)
                    {
                        continue;
                    }

                    // Refine BEFORE judging. Everything up to here is a 2-point estimate, whose
                    // error at the frame corners is several times its error at the pair; the
                    // refit turns it into an N-point one, and only then is it worth measuring
                    // against a tolerance the whole frame has to meet.
                    refinements++;
                    if (!TryRefine(catalogBright, nCat, captureGrid, refineSrc, refineDst, scaleLoSq, scaleHiSq, ref m))
                    {
                        continue;
                    }

                    var hits = CountHits(catalogBright, 0, nCat, m, verifyGrid);
                    if (hits > bestHits)
                    {
                        bestHits = hits;
                        bestM = m;
                        if (bestHits >= earlyExitHits)
                        {
                            break;
                        }
                    }
                }
            }
        }

    scanDone:
        diagnostics = new LockDiagnostics(
            nCat, nDet, hypotheses, capHit, detectedPairsTried, nDet * (nDet - 1) / 2,
            bestHits, acceptThreshold, expectedChance, refinements, cancelled);

        if (bestHits < acceptThreshold)
        {
            return null;
        }

        // Final refit, now over the TIGHT-radius inliers: refinement captured at the wide radius,
        // so this drops whatever it swept in that the accepted transform does not actually
        // explain. Falls back to the winning hypothesis when degenerate.
        var srcPts = new List<Vector2>(bestHits);
        var dstPts = new List<Vector2>(bestHits);
        for (var c = 0; c < nCat; c++)
        {
            var q = catalogBright[c];
            var txp = q.X * bestM.M11 + q.Y * bestM.M21 + bestM.M31;
            var typ = q.X * bestM.M12 + q.Y * bestM.M22 + bestM.M32;
            if (verifyGrid.TryNearest(txp, typ, out var nearest))
            {
                srcPts.Add(q);
                dstPts.Add(nearest);
            }
        }
        var refined = Matrix3x2.FitAffineTransform(
            CollectionsMarshal.AsSpan(srcPts),
            CollectionsMarshal.AsSpan(dstPts));

        return new LockResult(refined ?? bestM, bestHits, nCat, expectedChance, hypotheses);
    }

    private static int CountHits(
        ReadOnlySpan<Vector2> catalogBright, int from, int to,
        in Matrix3x2 m,
        in PointGrid grid)
    {
        var hits = 0;
        for (var c = from; c < to; c++)
        {
            var q = catalogBright[c];
            var x = q.X * m.M11 + q.Y * m.M21 + m.M31;
            var y = q.X * m.M12 + q.Y * m.M22 + m.M32;
            if (grid.HasWithin(x, y))
            {
                hits++;
            }
        }
        return hits;
    }

    /// <summary>
    /// Turns a 2-point hypothesis into an N-point one: captures the nearest detected star to
    /// each projected catalog star and refits over those correspondences, repeatedly. The
    /// starting transform is already within a capture radius of the truth (that is what promoted
    /// it), so this is the converging end of ICP, not a search.
    /// <para><b>The refit is a SIMILARITY, not the affine the accepted winner is finally
    /// upgraded to</b>, and that is what keeps the chance model honest. Refinement optimises the
    /// very statistic the transform is then judged on, so its hits are no longer an independent
    /// draw from the Poisson field <see cref="ChanceSafetyFactor"/> assumes -- the freer the
    /// model, the further it can chase a dense field's own clustering. A 6-DOF affine has enough
    /// of that freedom to matter: fitted in here it walked two UNRELATED Vela panels (P14
    /// detected against P06's 3,774-star catalog) to exactly 24 hits -- the accept threshold,
    /// dead on -- over 12,884 refinements, a false lock. Four degrees of freedom cannot shear or
    /// stretch onto a random field: all 272 non-overlapping panel pairs stay clear of the
    /// threshold, and the five the test samples fall from 18-21 back to 14-17. The affine upgrade
    /// still happens, once, on the accepted winner, where nothing is selected on its
    /// outcome.</para>
    /// </summary>
    /// <returns>
    /// <c>false</c> when no round produced a usable fit, leaving <paramref name="m"/> untouched;
    /// a later round failing keeps what the earlier ones achieved.
    /// </returns>
    private static bool TryRefine(
        ReadOnlySpan<Vector2> catalogBright,
        int nCat,
        in PointGrid captureGrid,
        Vector2[] src,
        Vector2[] dst,
        float scaleLoSq,
        float scaleHiSq,
        ref Matrix3x2 m)
    {
        var refined = false;
        for (var iter = 0; iter < RefineIterations; iter++)
        {
            var n = 0;
            for (var c = 0; c < nCat; c++)
            {
                var q = catalogBright[c];
                var x = q.X * m.M11 + q.Y * m.M21 + m.M31;
                var y = q.X * m.M12 + q.Y * m.M22 + m.M32;
                if (captureGrid.TryNearest(x, y, out var nearest))
                {
                    src[n] = q;
                    dst[n] = nearest;
                    n++;
                }
            }
            if (n < MinRefineCorrespondences
                || Matrix3x2.FitSimilarityTransform(src.AsSpan(0, n), dst.AsSpan(0, n)) is not { } fit)
            {
                return refined;
            }

            // Scale is the one thing a similarity refit can still run away with, and it is the
            // one the hypothesis was admitted on. Its determinant IS the scale squared, so the
            // gate is the same window the 2-point hypothesis passed.
            var scaleSq = fit.M11 * fit.M22 - fit.M12 * fit.M21;
            if (scaleSq < scaleLoSq || scaleSq > scaleHiSq)
            {
                return refined;
            }

            m = fit;
            refined = true;
        }
        return refined;
    }

    private static int LowerBound((float Sep, short A, short B)[] sorted, float value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].Sep < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// Uniform grid hash over a point set in CSR (counting-sort) layout: zero per-query
    /// allocation, O(1) membership probes. The query radius is fixed at construction and the
    /// cell size is twice that radius, so a probe only ever touches the 2x2 cell block around
    /// the query point.
    /// </summary>
    internal readonly struct PointGrid
    {
        private readonly int[] _cellStart;
        private readonly int[] _entryIdx;
        private readonly Vector2[] _points;
        private readonly float _radius;
        private readonly float _radiusSq;
        private readonly float _invCell;
        private readonly int _gridW, _gridH;

        internal PointGrid(ReadOnlySpan<Vector2> points, int width, int height, float queryRadius)
        {
            _radius = queryRadius;
            _radiusSq = queryRadius * queryRadius;
            _invCell = 1f / (2f * queryRadius);
            _gridW = Math.Max(1, (int)(width * _invCell) + 2);
            _gridH = Math.Max(1, (int)(height * _invCell) + 2);
            _points = points.ToArray();

            var cells = _gridW * _gridH;
            _cellStart = new int[cells + 1];
            var cellOf = new int[_points.Length];
            for (var i = 0; i < _points.Length; i++)
            {
                var cx = Math.Clamp((int)(_points[i].X * _invCell), 0, _gridW - 1);
                var cy = Math.Clamp((int)(_points[i].Y * _invCell), 0, _gridH - 1);
                cellOf[i] = cy * _gridW + cx;
                _cellStart[cellOf[i] + 1]++;
            }
            for (var c = 0; c < cells; c++)
            {
                _cellStart[c + 1] += _cellStart[c];
            }
            _entryIdx = new int[_points.Length];
            var cursor = new int[cells];
            for (var i = 0; i < _points.Length; i++)
            {
                var c = cellOf[i];
                _entryIdx[_cellStart[c] + cursor[c]] = i;
                cursor[c]++;
            }
        }

        internal bool HasWithin(float x, float y)
        {
            var cx0 = Math.Clamp((int)((x - _radius) * _invCell), 0, _gridW - 1);
            var cx1 = Math.Clamp((int)((x + _radius) * _invCell), 0, _gridW - 1);
            var cy0 = Math.Clamp((int)((y - _radius) * _invCell), 0, _gridH - 1);
            var cy1 = Math.Clamp((int)((y + _radius) * _invCell), 0, _gridH - 1);
            for (var cy = cy0; cy <= cy1; cy++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var c = cy * _gridW + cx;
                    for (var e = _cellStart[c]; e < _cellStart[c + 1]; e++)
                    {
                        var q = _points[_entryIdx[e]];
                        var dx = q.X - x;
                        var dy = q.Y - y;
                        if (dx * dx + dy * dy <= _radiusSq)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        internal bool TryNearest(float x, float y, out Vector2 nearest)
        {
            var cx0 = Math.Clamp((int)((x - _radius) * _invCell), 0, _gridW - 1);
            var cx1 = Math.Clamp((int)((x + _radius) * _invCell), 0, _gridW - 1);
            var cy0 = Math.Clamp((int)((y - _radius) * _invCell), 0, _gridH - 1);
            var cy1 = Math.Clamp((int)((y + _radius) * _invCell), 0, _gridH - 1);
            var bestSq = _radiusSq;
            var found = false;
            nearest = default;
            for (var cy = cy0; cy <= cy1; cy++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var c = cy * _gridW + cx;
                    for (var e = _cellStart[c]; e < _cellStart[c + 1]; e++)
                    {
                        var q = _points[_entryIdx[e]];
                        var dx = q.X - x;
                        var dy = q.Y - y;
                        var dSq = dx * dx + dy * dy;
                        if (dSq <= bestSq)
                        {
                            bestSq = dSq;
                            nearest = q;
                            found = true;
                        }
                    }
                }
            }
            return found;
        }
    }
}
