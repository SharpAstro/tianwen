using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Decides phase C of <c>docs/plans/plate-solver-performance.md</c> -- ASTAP-style quad
    /// matching against the catalog -- by measuring the one thing it rests on, before any of it is
    /// built.
    ///
    /// <para><b>Why this probe exists: two documents in this repo disagree.</b> The performance
    /// plan says the quad matcher "has never been pointed at the catalog side". The XML doc on
    /// <see cref="StarReferenceTable"/> says it HAS, and that the answer was zero locks at every K
    /// from 50 to 500 in either parity, blaming the population mismatch between a detected field
    /// and a catalog. One of those is stale, and the difference between them is a multi-day
    /// feature.</para>
    ///
    /// <para><b>The experiment is built to be decisive, which means removing every confound in
    /// quad matching's favour.</b> The catalog is projected through the frame's OWN frozen
    /// solution, not a hint -- so the two point sets share a pixel frame exactly: same scale, same
    /// rotation, same translation, same parity, zero hint error. Everything quad matching could
    /// possibly need is therefore granted, and what remains is the only question that matters:
    /// <b>do a detected field and a catalog field contain the same quads at all?</b></para>
    ///
    /// <para>The headline number is <b>tolerance-free on purpose</b>. A quad is identified by its
    /// CENTRE, and under a shared pixel frame a genuinely corresponding quad has the same centre,
    /// so counting image quads that have a catalog quad within a couple of px measures the
    /// population question with no matcher, no tolerance and no threshold in the way. Only then are
    /// the descriptors of the coincident quads compared, which separates the two failure modes that
    /// look identical from outside a matcher:</para>
    /// <list type="bullet">
    ///   <item>centres do not coincide -> the four stars are not the same four stars, and no
    ///   tolerance, K, or matcher rewrite recovers it. Phase C is dead.</item>
    ///   <item>centres coincide but descriptors disagree -> the quads ARE shared and the earlier
    ///   probe was defeated by <see cref="StarQuad.WithinTolerance"/> comparing <c>Dist1</c> in
    ///   absolute px against the same tolerance as the five ratios (a mixed-unit test that only
    ///   works for stacking, as its own doc says). Phase C is alive, and needs a ratio-only
    ///   matcher rather than a reuse of <see cref="StarReferenceTable.FindFit"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Env-gated (<c>TIANWEN_QUAD_FEASIBILITY</c>): it builds quad lists for 24 panels x 4 values
    /// of K, and the <see cref="StarQuadList"/> ctor's duplicate-centre scan is quadratic.
    /// </remarks>
    [Collection("Astrometry")]
    public class QuadCatalogFeasibilityProbe(ITestOutputHelper output)
    {
        /// <summary>
        /// Two quads are the same quad when their centres agree to this. A centre is the mean of
        /// four positions whose individual residuals are already sub-pixel under these solutions,
        /// so 2 px is generous rather than tight -- deliberately, since the whole risk in this
        /// measurement is under-reporting coincidence.
        /// </summary>
        private const float CentreTolerancePx = 2f;

        [Fact]
        public void ReportWhetherADetectedFieldAndACatalogFieldShareQuads()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_QUAD_FEASIBILITY")),
                "Set TIANWEN_QUAD_FEASIBILITY=1 to run the phase-C feasibility probe");

            var manifest = VelaMosaicStarLists.Manifest;
            int[] ks = [100, 200, 300, 500];

            foreach (var k in ks)
            {
                var totalImageQuads = 0;
                var totalCatQuads = 0;
                var totalCoincident = 0;
                var totalStarOverlap = 0;
                var totalDetSampled = 0;
                var ratioErrors = new List<float>();
                var dist1Ratios = new List<float>();
                var panelsWithFindFit = 0;
                var panels = 0;

                foreach (var panel in manifest.Panels)
                {
                    var frame = panel.Frames[0];

                    // Detected: brightest first already, top-K.
                    var det = frame.DetectedPoints(k);

                    // Catalog through the frame's OWN solution, brightest first, top-K.
                    var catAll = VelaProjection.ProjectInFrameIndexed(
                        manifest.Catalog, frame.Wcs, panel.Width, panel.Height);
                    var catTake = Math.Min(k, catAll.Count);
                    var cat = new Vector2[catTake];
                    for (var i = 0; i < catTake; i++)
                    {
                        cat[i] = new Vector2(catAll[i].X, catAll[i].Y);
                    }

                    if (det.Length < 50 || cat.Length < 50)
                    {
                        continue;
                    }

                    panels++;

                    // Star-level overlap bounds the quad-level rate from above: a quad can only be
                    // shared if all four of its members are.
                    totalDetSampled += det.Length;
                    totalStarOverlap += CountWithin(det, cat, CentreTolerancePx);

                    var imageQuads = BuildQuads(det);
                    var catQuads = BuildQuads(cat);
                    totalImageQuads += imageQuads.Count;
                    totalCatQuads += catQuads.Count;

                    // THE headline: coincident quad centres, with no tolerance on the descriptor.
                    for (var i = 0; i < imageQuads.Count; i++)
                    {
                        var q = imageQuads[i];
                        var best = -1;
                        var bestD = CentreTolerancePx;
                        for (var j = 0; j < catQuads.Count; j++)
                        {
                            var c = catQuads[j];
                            var d = MathF.Sqrt((q.X - c.X) * (q.X - c.X) + (q.Y - c.Y) * (q.Y - c.Y));
                            if (d < bestD)
                            {
                                bestD = d;
                                best = j;
                            }
                        }

                        if (best >= 0)
                        {
                            totalCoincident++;
                            var c = catQuads[best];
                            // Worst of the FIVE ratios (Dist2..Dist6). Dist1 is absolute and is
                            // reported separately, as the scale a matcher would recover from it.
                            var err = MathF.Max(
                                MathF.Max(MathF.Abs(q.Dist2 - c.Dist2), MathF.Abs(q.Dist3 - c.Dist3)),
                                MathF.Max(MathF.Abs(q.Dist4 - c.Dist4),
                                    MathF.Max(MathF.Abs(q.Dist5 - c.Dist5), MathF.Abs(q.Dist6 - c.Dist6))));
                            ratioErrors.Add(err);
                            if (c.Dist1 > 0)
                            {
                                dist1Ratios.Add(q.Dist1 / c.Dist1);
                            }
                        }
                    }

                    // What the EXISTING matcher answers, for the record.
                    if (StarReferenceTable.FindFit(imageQuads, catQuads, minimumCount: 3, quadTolerance: 0.008f) is not null)
                    {
                        panelsWithFindFit++;
                    }
                }

                var coincidentPct = totalImageQuads > 0 ? 100.0 * totalCoincident / totalImageQuads : 0;
                var starPct = totalDetSampled > 0 ? 100.0 * totalStarOverlap / totalDetSampled : 0;
                output.WriteLine(
                    $"K={k,3}: {totalImageQuads,5} image quads, {totalCatQuads,5} catalog quads, " +
                    $"stars shared {starPct,5:F1}%, QUADS SHARED {totalCoincident,5} ({coincidentPct,5:F1}%), " +
                    $"existing FindFit locks {panelsWithFindFit}/{panels} panels");

                if (ratioErrors.Count > 0)
                {
                    ratioErrors.Sort();
                    dist1Ratios.Sort();
                    output.WriteLine(
                        "        shared-quad descriptor error (worst of 5 ratios): " +
                        $"median {Pct(ratioErrors, 0.5):F4}, p90 {Pct(ratioErrors, 0.9):F4}, max {ratioErrors[^1]:F4}; " +
                        $"implied scale (Dist1 ratio) median {Pct(dist1Ratios, 0.5):F4} " +
                        $"(p10 {Pct(dist1Ratios, 0.1):F4}, p90 {Pct(dist1Ratios, 0.9):F4})");
                }
            }
        }

        /// <summary>
        /// The half of phase C that only quads can do: recover the plate SCALE with no prior.
        ///
        /// <para>The pair seed needs one (<c>CatalogPlateSolver.MinPairLockScaleTolerance</c>, 5%),
        /// because a pair gives a distance and a distance has units; a quad descriptor is five
        /// ratios, which are scale-free, so a matched quad hands back the scale as the ratio of the
        /// two longest sides. That is the only structural dependency on <c>FOCALLEN</c> left in the
        /// solver, and <c>FOCALLEN</c> is whatever a human typed.</para>
        ///
        /// <para><b>Run under the production condition, not a favourable one.</b> The catalog is
        /// projected through the HEADER HINT -- pointing wrong by up to 40 arcmin across this
        /// mosaic, unrotated where the real fields are rotated -- and with the pixel scale
        /// deliberately wrong by <see cref="ScaleErrorFactor"/>, which is the 3.9% marketed-versus-
        /// actual focal length this plan already measured. The matcher is given NO scale
        /// information: candidates are admitted on the five ratios alone, with
        /// <see cref="StarQuad.Dist1"/> never compared. If the median longest-side ratio over the
        /// surviving candidates comes back at the injected error, the scale is recoverable from the
        /// stars; if contamination by chance candidates swamps it, it is not.</para>
        ///
        /// <para>Rotation is not a confound and that is the point: a distance is invariant under
        /// rotation. So is reflection, which is why this cannot settle the PARITY -- a mirrored
        /// field has identical quad descriptors, so the parity race stays exactly as it is.</para>
        /// </summary>
        [Fact]
        public void ReportWhetherTheScaleIsRecoverableFromQuadsWithNoPrior()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_QUAD_FEASIBILITY")),
                "Set TIANWEN_QUAD_FEASIBILITY=1 to run the phase-C feasibility probe");

            var manifest = VelaMosaicStarLists.Manifest;

            foreach (var tol in new[] { 0.002f, 0.004f, 0.008f })
            {
                var recovered = new List<float>();
                var withinOnePercent = 0;
                var panels = 0;
                long candidates = 0;
                long comparisons = 0;

                foreach (var panel in manifest.Panels)
                {
                    var frame = panel.Frames[0];
                    var det = frame.DetectedPoints(TopK);

                    // The hint, with the scale wrong the way a typed focal length is wrong.
                    var wrongDim = new ImageDim(panel.PixelScaleArcsec * ScaleErrorFactor, panel.Width, panel.Height);
                    var hintWcs = VelaProjection.HintWcs(frame.Hint, wrongDim);
                    var catAll = VelaProjection.ProjectInFrameIndexed(
                        manifest.Catalog, hintWcs, panel.Width, panel.Height);
                    var catTake = Math.Min(TopK, catAll.Count);
                    var cat = new Vector2[catTake];
                    for (var i = 0; i < catTake; i++)
                    {
                        cat[i] = new Vector2(catAll[i].X, catAll[i].Y);
                    }

                    if (det.Length < 50 || cat.Length < 50)
                    {
                        continue;
                    }

                    panels++;
                    var imageQuads = BuildQuads(det);
                    var catQuads = BuildQuads(cat);
                    comparisons += (long)imageQuads.Count * catQuads.Count;

                    // Ratio-only candidate scan: Dist1 is NEVER compared, so no scale is assumed.
                    var ratios = new List<float>();
                    for (var i = 0; i < imageQuads.Count; i++)
                    {
                        var q = imageQuads[i];
                        for (var j = 0; j < catQuads.Count; j++)
                        {
                            var c = catQuads[j];
                            if (MathF.Abs(q.Dist2 - c.Dist2) <= tol
                                && MathF.Abs(q.Dist3 - c.Dist3) <= tol
                                && MathF.Abs(q.Dist4 - c.Dist4) <= tol
                                && MathF.Abs(q.Dist5 - c.Dist5) <= tol
                                && MathF.Abs(q.Dist6 - c.Dist6) <= tol
                                && c.Dist1 > 0)
                            {
                                ratios.Add(q.Dist1 / c.Dist1);
                            }
                        }
                    }

                    candidates += ratios.Count;
                    if (ratios.Count < 4)
                    {
                        continue;
                    }

                    ratios.Sort();
                    var median = Pct(ratios, 0.5);
                    recovered.Add(median);
                    if (MathF.Abs(median - ScaleErrorFactor) / ScaleErrorFactor <= 0.01f)
                    {
                        withinOnePercent++;
                    }
                }

                recovered.Sort();
                output.WriteLine(
                    $"ratio tol {tol:F3}: {comparisons:N0} comparisons over {panels} panels, " +
                    $"{candidates:N0} candidates ({(panels > 0 ? candidates / (double)panels : 0):F0}/panel); " +
                    $"truth {ScaleErrorFactor:F3}, recovered median-of-medians " +
                    $"{(recovered.Count > 0 ? Pct(recovered, 0.5) : float.NaN):F4}, " +
                    $"{withinOnePercent}/{panels} panels within 1%");
            }
        }

        /// <summary>
        /// What a recovered scale would be WORTH to the seed, which is the question that decides
        /// whether the recovery above is worth building.
        ///
        /// <para><c>PairRansacLock</c> admits a catalog pair for a detected pair when its length
        /// lands inside the scale window, so the window's width is a direct multiplier on the
        /// candidate set every detected pair is tried against -- and the hypothesis count is what
        /// phase A established as the solver's unit of wasted work. This walks the frozen mosaic at
        /// the shipped 5% window and at the ~1% a quad-recovered scale would justify, and reports
        /// hypotheses and locks for each. A narrower window that lost locks would be a bad trade at
        /// any speed, so both are reported together.</para>
        /// </summary>
        [Fact]
        public void ReportWhatANarrowerScaleWindowBuysTheSeed()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_QUAD_FEASIBILITY")),
                "Set TIANWEN_QUAD_FEASIBILITY=1 to run the phase-C feasibility probe");

            var manifest = VelaMosaicStarLists.Manifest;

            foreach (var scaleTol in new[] { 0.05f, 0.02f, 0.01f })
            {
                long hypotheses = 0;
                var locks = 0;
                var attempts = 0;

                foreach (var panel in manifest.Panels)
                {
                    var frame = panel.Frames[0];
                    var det = frame.DetectedPoints();

                    foreach (var xSign in new[] { -1.0, 1.0 })
                    {
                        var hintWcs = VelaProjection.HintWcs(frame.Hint, panel.Dim, xSign);
                        var pool = VelaProjection.ProjectInFrame(
                            manifest.Catalog, hintWcs, panel.Width, panel.Height);
                        if (pool.Length < 50)
                        {
                            continue;
                        }

                        attempts++;
                        var result = PairRansacLock.TryLock(pool, det, det, panel.Width, panel.Height,
                            scaleTolerance: scaleTol, out var diag,
                            cancellationToken: TestContext.Current.CancellationToken);
                        hypotheses += diag.Hypotheses;
                        if (result is not null)
                        {
                            locks++;
                        }
                    }
                }

                output.WriteLine(
                    $"scale window +/-{scaleTol:P0}: {hypotheses:N0} hypotheses over {attempts} parity attempts " +
                    $"({(attempts > 0 ? hypotheses / (double)attempts : 0):N0}/attempt), {locks} locked");
            }
        }

        /// <summary>
        /// Star count carried into matching. 500 is what the detector already caps at and what
        /// ASTAP solves this class of field from.
        /// </summary>
        private const int TopK = 500;

        /// <summary>
        /// How wrong the scale prior is made: the 3.9% marketed-versus-actual focal length measured
        /// on a 130 mm lens sold as 135 (see <c>MinPairLockScaleTolerance</c>). A systematic error
        /// rather than a typo, so it recurs, and it is outside the seed's old 3% window.
        /// </summary>
        private const float ScaleErrorFactor = 1.039f;

        private static float Pct(List<float> sorted, double p)
            => sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

        /// <summary>
        /// Builds a quad list from bare positions. <see cref="StarQuadList"/>'s ctor takes
        /// <see cref="ImagedStar"/> and reads only the two centroids, and its three-nearest-neighbour
        /// window is an INDEX range, so the input must be X-sorted (as
        /// <c>SortedStarList.FindQuadsAsync</c> does before calling it) or that search looks in the
        /// wrong part of the frame.
        /// </summary>
        private static StarQuadList BuildQuads(Vector2[] points)
        {
            var stars = new ImagedStar[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                stars[i] = new ImagedStar(0, 0, 0, 0, points[i].X, points[i].Y, 0);
            }
            Array.Sort(stars, (a, b) => a.XCentroid.CompareTo(b.XCentroid));
            return new StarQuadList(stars.AsSpan());
        }

        private static int CountWithin(Vector2[] a, Vector2[] b, float tolerance)
        {
            var hits = 0;
            foreach (var p in a)
            {
                foreach (var q in b)
                {
                    var dx = p.X - q.X;
                    var dy = p.Y - q.Y;
                    if (dx * dx + dy * dy <= tolerance * tolerance)
                    {
                        hits++;
                        break;
                    }
                }
            }
            return hits;
        }
    }
}
