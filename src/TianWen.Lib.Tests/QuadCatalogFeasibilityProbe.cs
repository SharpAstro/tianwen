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
        /// Picks the guard the recovered scale must pass before it is trusted, from data rather
        /// than by guess.
        ///
        /// <para>A tight window around a WRONG scale locks nothing, so narrowing the seed to +/-1%
        /// is only safe when we can tell a good recovery from a bad one WITHOUT knowing the answer.
        /// The measurement above found 23 of 24 panels within 1% -- so the whole design rests on
        /// whether that 24th panel is IDENTIFIABLE at the time, and the only signals available then
        /// are the candidate count and the spread of the candidate ratios. This reports both against
        /// the error, per panel, so the threshold is read off the separation instead of invented.
        /// </para>
        ///
        /// <para>Spread is the more promising of the two on principle: a contaminated candidate set
        /// is contaminated by CHANCE ratio matches, which scatter, while a set carrying real shared
        /// quads agrees tightly. A count cannot distinguish fifty agreeing candidates from fifty
        /// scattered ones. But principle is not evidence, so both are printed.</para>
        /// </summary>
        [Fact]
        public void ReportWhichSignalIdentifiesABadScaleRecovery()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_QUAD_FEASIBILITY")),
                "Set TIANWEN_QUAD_FEASIBILITY=1 to run the phase-C feasibility probe");

            var manifest = VelaMosaicStarLists.Manifest;
            const float Tol = 0.004f;

            // Truth is NOT the nominal scale times the injected error. The manifest's
            // PixelScaleArcsec is the panel's DECLARED scale (what the header said); the frozen
            // solution's own CD matrix says what the optics actually deliver, and the two differ by
            // the same sub-percent class this whole plan is about. Comparing against the declared
            // one charges the recovery for the header's error, which is the error it exists to find.
            output.WriteLine("panel    cands   median  truth    err%     IQR/med   MAD/med  hdr-vs-solved%");
            var goodMinCands = int.MaxValue;
            var goodMaxSpread = 0.0;
            var badCands = new List<int>();
            var badSpread = new List<double>();

            foreach (var panel in manifest.Panels)
            {
                var frame = panel.Frames[0];
                var det = frame.DetectedPoints(TopK);

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

                var imageQuads = BuildQuads(det);
                var catQuads = BuildQuads(cat);

                var ratios = new List<float>();
                for (var i = 0; i < imageQuads.Count; i++)
                {
                    var q = imageQuads[i];
                    for (var j = 0; j < catQuads.Count; j++)
                    {
                        var c = catQuads[j];
                        if (MathF.Abs(q.Dist2 - c.Dist2) <= Tol
                            && MathF.Abs(q.Dist3 - c.Dist3) <= Tol
                            && MathF.Abs(q.Dist4 - c.Dist4) <= Tol
                            && MathF.Abs(q.Dist5 - c.Dist5) <= Tol
                            && MathF.Abs(q.Dist6 - c.Dist6) <= Tol
                            && c.Dist1 > 0)
                        {
                            ratios.Add(q.Dist1 / c.Dist1);
                        }
                    }
                }

                if (ratios.Count == 0)
                {
                    output.WriteLine($"{panel.Id,-8} {0,5}   -- no candidates --");
                    badCands.Add(0);
                    badSpread.Add(double.PositiveInfinity);
                    continue;
                }

                ratios.Sort();
                var median = Pct(ratios, 0.5);

                // The catalog was projected at (declared x injected error) arcsec/px and the image
                // sits at whatever the optics actually deliver, so the ratio a correct recovery
                // returns is that quotient -- not the injected factor alone.
                var solvedScale = frame.Wcs.PixelScaleArcsec;
                var truth = panel.PixelScaleArcsec * ScaleErrorFactor / solvedScale;
                var headerVsSolved = 100.0 * (panel.PixelScaleArcsec - solvedScale) / solvedScale;
                var errPct = 100.0 * Math.Abs(median - truth) / truth;
                var iqr = (Pct(ratios, 0.75) - Pct(ratios, 0.25)) / median;

                // MAD about the median, normalised -- the robust scatter of the candidate set.
                var devs = new List<float>(ratios.Count);
                foreach (var r in ratios)
                {
                    devs.Add(MathF.Abs(r - median));
                }
                devs.Sort();
                var mad = Pct(devs, 0.5) / median;

                output.WriteLine(
                    $"{panel.Id,-8} {ratios.Count,5}   {median:F4}  {truth:F4}  {errPct,6:F3}   {iqr,7:F4}   {mad,7:F4}  {headerVsSolved,8:F3}");

                if (errPct <= 1.0)
                {
                    goodMinCands = Math.Min(goodMinCands, ratios.Count);
                    goodMaxSpread = Math.Max(goodMaxSpread, mad);
                }
                else
                {
                    badCands.Add(ratios.Count);
                    badSpread.Add(mad);
                }
            }

            output.WriteLine("");
            output.WriteLine($"within 1%: min candidates {goodMinCands}, max MAD/median {goodMaxSpread:F4}");
            output.WriteLine($"outside 1%: candidates [{string.Join(", ", badCands)}], " +
                $"MAD/median [{string.Join(", ", badSpread.ConvertAll(d => d.ToString("F4")))}]");
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

            // Swept below 1% deliberately. PairRansacLock admits a catalog pair on
            // [dDet/(1+tol) - 3, dDet/(1-tol) + 3] px, and that +/-3 px is ABSOLUTE, so there is a
            // floor -- but MEASURED it is far below where reasoning from the minimum baseline puts
            // it. At the 601 px baseline MinBaselineFraction enforces, 3 px is 0.5% and the absolute
            // term does dominate; the pair POPULATION is mostly much longer baselines (up to the
            // ~4,000 px diagonal, where the fractional half-width at 0.5% is 20 px against the same
            // 3 px), so the fraction still rules for most pairs and counts keep falling to 0.1%.
            // The floor binds the shortest baselines only.
            // A window is a width AND a centre, and the centre is the thing under test. "declared"
            // projects at the header's own scale, which the guard measurement shows is ~0.29% off;
            // "recovered" projects at the solved scale, standing in for what quad recovery returns
            // (accurate to better than 0.07%, so the substitution is honest to well inside the
            // narrowest window swept). Sweeping width against the wrong centre measures the centre.
            foreach (var centre in new[] { "declared", "recovered" })
            foreach (var scaleTol in new[] { 0.05f, 0.02f, 0.01f, 0.005f, 0.0025f, 0.001f })
            {
                long hypotheses = 0;
                var locks = 0;
                var attempts = 0;

                foreach (var panel in manifest.Panels)
                {
                    var frame = panel.Frames[0];
                    var det = frame.DetectedPoints();
                    var dim = centre == "declared"
                        ? panel.Dim
                        : new ImageDim(frame.Wcs.PixelScaleArcsec, panel.Width, panel.Height);

                    foreach (var xSign in new[] { -1.0, 1.0 })
                    {
                        var hintWcs = VelaProjection.HintWcs(frame.Hint, dim, xSign);
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
                    $"{centre,-9} centre, window +/-{100 * scaleTol,5:F3}%: {hypotheses,12:N0} hypotheses over " +
                    $"{attempts} parity attempts ({(attempts > 0 ? hypotheses / (double)attempts : 0),9:N0}/attempt), " +
                    $"{locks,2} locked");
            }
        }

        /// <summary>
        /// C0, the phase-C gate: is the 2.6% shared-quad rate a POPULATION problem or a QUAD
        /// CONSTRUCTION problem? Nothing below phase C is worth building if it is the latter.
        /// </summary>
        /// <remarks>
        /// <para>Both sides already take top-K, so the star COUNTS were never the issue. What the
        /// baseline also reports is that barely 63% of the BRIGHTEST detections have a catalog
        /// counterpart at all, and a quad needs all four of its members shared before the neighbour
        /// ranking even gets a say -- 0.63^4 is 16% before any attrition. So the question is which of
        /// those two the rate is measuring.</para>
        /// <para>Three arms. <b>Baseline</b> is the shipped comparison. <b>Ceiling</b> builds both quad
        /// lists from ONLY the mutually matched stars, which is not achievable at solve time (it reads
        /// the answer) and is not meant to be: it is the control that says whether the builder itself
        /// is stable, and a ceiling far below 100% kills the phase outright. <b>Catalog sweep</b> is
        /// the actionable one -- at solve time the detection list is what it is and the CATALOG cut is
        /// the free variable, so this asks whether any cut aligns the two populations.</para>
        /// </remarks>
        [Fact(Timeout = 900_000)]
        public void ReportWhetherSharedQuadsAreAPopulationOrAConstructionProblem()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_QUAD_FEASIBILITY")),
                "Set TIANWEN_QUAD_FEASIBILITY=1 to run the phase-C feasibility probe");

            var manifest = VelaMosaicStarLists.Manifest;

            foreach (var kDet in new[] { 200, 500 })
            {
                output.WriteLine($"--- image top-K = {kDet}");

                int imgQuads = 0, catQuads = 0, shared = 0, detMatched = 0, detTotal = 0, catMatched = 0, catTotal = 0;
                int ceilImgQuads = 0, ceilShared = 0, panels = 0;
                foreach (var panel in manifest.Panels)
                {
                    var frame = panel.Frames[0];
                    var det = frame.DetectedPoints(kDet);
                    var cat = ProjectTopK(manifest, panel, frame, kDet);
                    if (det.Length < 50 || cat.Length < 50)
                    {
                        continue;
                    }

                    panels++;
                    detTotal += det.Length;
                    catTotal += cat.Length;
                    detMatched += CountWithin(det, cat, CentreTolerancePx);
                    catMatched += CountWithin(cat, det, CentreTolerancePx);

                    var iq = BuildQuads(det);
                    var cq = BuildQuads(cat);
                    imgQuads += iq.Count;
                    catQuads += cq.Count;
                    shared += CountCoincidentQuads(iq, cq);

                    // The ceiling: the same stars on both sides, image coords against catalog coords.
                    var (dPts, cPts) = MutualMatches(det, cat, CentreTolerancePx);
                    if (dPts.Length >= 50)
                    {
                        var ciq = BuildQuads(dPts);
                        ceilImgQuads += ciq.Count;
                        ceilShared += CountCoincidentQuads(ciq, BuildQuads(cPts));
                    }
                }

                output.WriteLine(
                    $"  baseline : {imgQuads,5} img quads vs {catQuads,5} cat quads, shared {shared,5} "
                    + $"({(imgQuads > 0 ? 100.0 * shared / imgQuads : 0),5:F1}%); "
                    + $"stars det->cat {(detTotal > 0 ? 100.0 * detMatched / detTotal : 0),5:F1}%, "
                    + $"cat->det {(catTotal > 0 ? 100.0 * catMatched / catTotal : 0),5:F1}% over {panels} panels");
                output.WriteLine(
                    $"  CEILING  : matched-population quads {ceilImgQuads,5}, shared {ceilShared,5} "
                    + $"({(ceilImgQuads > 0 ? 100.0 * ceilShared / ceilImgQuads : 0),5:F1}%)  <- construction is sound iff this is high");

                foreach (var mult in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
                {
                    int mImg = 0, mShared = 0, mDetMatched = 0, mDetTotal = 0;
                    foreach (var panel in manifest.Panels)
                    {
                        var frame = panel.Frames[0];
                        var det = frame.DetectedPoints(kDet);
                        var cat = ProjectTopK(manifest, panel, frame, (int)(kDet * mult));
                        if (det.Length < 50 || cat.Length < 50)
                        {
                            continue;
                        }

                        mDetTotal += det.Length;
                        mDetMatched += CountWithin(det, cat, CentreTolerancePx);
                        var iq = BuildQuads(det);
                        mImg += iq.Count;
                        mShared += CountCoincidentQuads(iq, BuildQuads(cat));
                    }

                    output.WriteLine(
                        $"  cat x{mult,-4}: shared {(mImg > 0 ? 100.0 * mShared / mImg : 0),5:F1}%  "
                        + $"(stars det->cat {(mDetTotal > 0 ? 100.0 * mDetMatched / mDetTotal : 0),5:F1}%)");
                }
            }
        }

        private static Vector2[] ProjectTopK(
            VelaMosaicManifest manifest, VelaPanel panel,
            VelaFrame frame, int k)
        {
            var all = VelaProjection.ProjectInFrameIndexed(manifest.Catalog, frame.Wcs, panel.Width, panel.Height);
            var take = Math.Min(k, all.Count);
            var pts = new Vector2[take];
            for (var i = 0; i < take; i++)
            {
                pts[i] = new Vector2(all[i].X, all[i].Y);
            }

            return pts;
        }

        /// <summary>Quads whose CENTRES coincide, which needs no descriptor tolerance.</summary>
        private static int CountCoincidentQuads(StarQuadList a, StarQuadList b)
        {
            var n = 0;
            for (var i = 0; i < a.Count; i++)
            {
                var q = a[i];
                for (var j = 0; j < b.Count; j++)
                {
                    var c = b[j];
                    var dx = q.X - c.X;
                    var dy = q.Y - c.Y;
                    if (dx * dx + dy * dy < CentreTolerancePx * CentreTolerancePx)
                    {
                        n++;
                        break;
                    }
                }
            }

            return n;
        }

        /// <summary>
        /// Greedy nearest pairing between the two lists. Used only for the CEILING arm, which is
        /// deliberately not achievable at solve time -- it reads the answer.
        /// </summary>
        private static (Vector2[] A, Vector2[] B) MutualMatches(Vector2[] a, Vector2[] b, float tolerance)
        {
            var outA = new List<Vector2>(a.Length);
            var outB = new List<Vector2>(a.Length);
            var taken = new bool[b.Length];
            foreach (var p in a)
            {
                var best = -1;
                var bestD = tolerance * tolerance;
                for (var j = 0; j < b.Length; j++)
                {
                    if (taken[j])
                    {
                        continue;
                    }

                    var dx = p.X - b[j].X;
                    var dy = p.Y - b[j].Y;
                    var d = dx * dx + dy * dy;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = j;
                    }
                }

                if (best >= 0)
                {
                    taken[best] = true;
                    outA.Add(p);
                    outB.Add(b[best]);
                }
            }

            return (outA.ToArray(), outB.ToArray());
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
