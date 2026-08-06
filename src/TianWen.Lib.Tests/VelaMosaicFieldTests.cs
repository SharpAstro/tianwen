using Shouldly;
using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Regressions over the frozen Vela mosaic star lists (see <see cref="VelaMosaicStarLists"/>):
    /// 24 real panel pointings, 96 frames, 78k catalog stars, no FITS.
    ///
    /// <para>These are the tests the synthetic <c>PairRansacLockTests</c> cannot be: a synthetic
    /// field is built from a transform the test already knows, so it can only check that the
    /// algorithm inverts arithmetic it was handed. Every failure the hardening actually had to
    /// survive came from properties of a real sky -- ~4,000 catalog stars in a 5-degree frame, a
    /// bright end scrambled by saturation, mount hints wrong by up to 40 arcmin, a meridian flip
    /// mid-mosaic, and heavy overlap between neighbouring panels.</para>
    ///
    /// <para>Three of the four bugs found while building this data set would have passed a
    /// synthetic suite: the acceptance gate's 1.27 px origin bias, the SIP fit's reference-pixel
    /// mismatch, and the seed's anchor pool being diluted by undetectable off-frame stars.</para>
    /// </summary>
    [Collection("Astrometry")]
    public class VelaMosaicFieldTests(ITestOutputHelper output)
    {
        /// <summary>The pixel bar the whole mosaic is held to. The sensor is square and the
        /// solutions are per-frame, so this is directly comparable across panels.</summary>
        private const double SubPixelRmsBar = 1.0;

        /// <summary>Cross-match window. 3 px at ~6"/px is 18", wide enough to admit a genuine
        /// pair under any residual the solutions have and narrow enough that the chance rate
        /// stays ~0.5% at this field density.</summary>
        private const float MatchTolerancePx = 3f;

        private static VelaMosaicManifest Manifest => VelaMosaicStarLists.Manifest;

        /// <summary>Catalog indices whose stars land inside a frame under its own solution.</summary>
        private static HashSet<int> InFrameIndices(VelaPanel panel, VelaFrame frame)
        {
            var set = new HashSet<int>();
            foreach (var (_, _, index) in VelaProjection.ProjectInFrameIndexed(
                Manifest.Catalog, frame.Wcs, panel.Width, panel.Height))
            {
                set.Add(index);
            }
            return set;
        }

        /// <summary>
        /// Maps every detected star of <paramref name="from"/> into the pixel frame of
        /// <paramref name="to"/> through the two frozen solutions (pixel -> sky -> pixel).
        /// </summary>
        private static Vector2[] MapDetected(VelaFrame from, VelaFrame to)
        {
            var mapped = new List<Vector2>(from.Detected.Length);
            foreach (var d in from.Detected)
            {
                if (from.Wcs.PixelToSky(d.X, d.Y) is { } sky && to.Wcs.SkyToPixel(sky.RA, sky.Dec) is { } px)
                {
                    mapped.Add(new Vector2((float)px.X, (float)px.Y));
                }
            }
            return mapped.ToArray();
        }

        public static TheoryData<string> PanelIds()
        {
            var data = new TheoryData<string>();
            foreach (var p in Manifest.Panels)
            {
                data.Add(p.Id);
            }
            return data;
        }

        /// <summary>
        /// The frozen oracle's own quality bar, asserted so a bad re-export cannot quietly
        /// weaken every test that trusts it. This is also the "sub-pixel everywhere" claim in its
        /// simplest form: each frame's own solution places the catalog on its detected stars to
        /// well under a pixel.
        /// </summary>
        [Fact]
        public void EveryFrozenFrameSolvesToSubPixelRms()
        {
            var worst = 0.0;
            var worstName = "";
            foreach (var panel in Manifest.Panels)
            {
                panel.Frames.Length.ShouldBeGreaterThan(0, $"panel {panel.Id} has no frames");
                foreach (var frame in panel.Frames)
                {
                    frame.VerifyRmsPx.ShouldBeLessThan(SubPixelRmsBar,
                        $"{panel.Id}/{frame.Name} was frozen at {frame.VerifyRmsPx:F3} px");
                    frame.VerifyMatches.ShouldBeGreaterThan(500,
                        $"{panel.Id}/{frame.Name} matched only {frame.VerifyMatches} catalog stars");
                    frame.Wcs.HasCDMatrix.ShouldBeTrue($"{panel.Id}/{frame.Name} has no CD matrix");
                    if (frame.VerifyRmsPx > worst)
                    {
                        worst = frame.VerifyRmsPx;
                        worstName = $"{panel.Id}/{frame.Name}";
                    }
                }
            }

            output.WriteLine($"{Manifest.Panels.Length} panels, worst frame {worstName} at {worst:F3} px " +
                $"({worst * Manifest.Panels[0].PixelScaleArcsec:F2}\")");
        }

        /// <summary>
        /// THE regression for the dense-field hardening, on the real fields that exposed it: from
        /// nothing but the mount's own header pointing, the geometric seed must lock, and the
        /// transform it recovers must agree with the frozen solution across the WHOLE frame.
        ///
        /// <para>Driving <see cref="CatalogPlateSolver.TrySeedPairLock"/> means this covers the
        /// real anchor-pool policy, the rank-sum hypothesis ordering and the hypothesis cap. All
        /// three had to change to get every panel seeding: the pool because off-frame anchors
        /// starved the staged gates, the ordering because a lexicographic scan spent its whole
        /// budget on pairs sharing one bad bright star, and the cap because 400k stopped at 41%
        /// coverage.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(PanelIds))]
        public void SeedsFromHeaderHintAndAgreesWithTheFrozenSolution(string panelId)
        {
            var panel = Manifest.Panel(panelId);
            var catalog = Manifest.CatalogTuples();
            var dim = panel.Dim;
            var pixelScaleRad = double.DegreesToRadians(dim.PixelScale / 3600.0);
            var cx = dim.Width / 2.0;
            var cy = dim.Height / 2.0;

            foreach (var frame in panel.Frames)
            {
                var det = frame.DetectedPoints();

                // Both parities, exactly as the solver tries them.
                PairRansacLock.LockResult? best = null;
                var bestXSign = 0.0;
                foreach (var xSign in new[] { -1.0, 1.0 })
                {
                    if (CatalogPlateSolver.TrySeedPairLock(catalog, det, frame.Hint, pixelScaleRad,
                            cx, cy, dim, xSign, scaleTolerance: 0.03f) is { } locked
                        && (best is null || locked.Hits > best.Value.Hits))
                    {
                        best = locked;
                        bestXSign = xSign;
                    }
                }

                best.ShouldNotBeNull($"{panelId}/{frame.Name} must seed from its header hint " +
                    $"(off by {Separation(frame.Hint, frame.Wcs) * 60:F1} arcmin)");
                var lockResult = best.Value;
                // Held to the algorithm's own criterion (consensus far above the Poisson chance
                // rate), not to a majority of the census: on a hint that is tens of arcmin off,
                // the winning pool is the MARGINED one, whose anchors are partly off-frame and
                // therefore undetectable by construction -- panel 15's late frame legitimately
                // locks at 59/160 and panel 20.2 at 76/160. Demanding a majority there would be
                // asserting a property of the pool, not of the lock.
                lockResult.Hits.ShouldBeGreaterThan((int)(5 * lockResult.ExpectedChanceHits),
                    $"{panelId}/{frame.Name} consensus {lockResult.Hits}/{lockResult.Census} is not clear of " +
                    $"chance ({lockResult.ExpectedChanceHits:F1})");
                lockResult.Hits.ShouldBeGreaterThan(30,
                    $"{panelId}/{frame.Name} seeded on only {lockResult.Hits} anchors");

                // The seed maps HINT-projected catalog pixels onto detected pixels. Composing it
                // with the hint projection must therefore reproduce the frozen solution, which is
                // the only statement that matters: a lock with the right hit count but the wrong
                // geometry would still ruin the solve.
                var hintWcs = VelaProjection.HintWcs(frame.Hint, dim, bestXSign);
                var checkedStars = 0;
                var worstErr = 0.0;
                for (var i = 0; i < Manifest.Catalog.Length && checkedStars < 400; i++)
                {
                    var c = Manifest.Catalog[i];
                    if (hintWcs.SkyToPixel(c.RA, c.Dec) is not { } hintPx
                        || frame.Wcs.SkyToPixel(c.RA, c.Dec) is not { } truePx)
                    {
                        continue;
                    }
                    var seeded = Vector2.Transform(new Vector2((float)hintPx.X, (float)hintPx.Y), lockResult.Transform);
                    if (truePx.X < 0 || truePx.X > dim.Width - 1 || truePx.Y < 0 || truePx.Y > dim.Height - 1)
                    {
                        continue;
                    }
                    checkedStars++;
                    worstErr = Math.Max(worstErr, Vector2.Distance(seeded, new Vector2((float)truePx.X, (float)truePx.Y)));
                }

                checkedStars.ShouldBeGreaterThan(50, $"{panelId}/{frame.Name}: too few in-frame stars to verify the seed");
                // A 2-point similarity plus a least-squares affine refit cannot represent the
                // field's real distortion (which is what SIP is for), so this bar is "the seed
                // lands the iteration in the right basin", not "the seed is the solution".
                worstErr.ShouldBeLessThan(30.0,
                    $"{panelId}/{frame.Name}: seed disagrees with the frozen solution by {worstErr:F1} px");

                output.WriteLine($"{panelId}/{frame.Name}: seeded xSign={bestXSign:+0;-0} " +
                    $"{lockResult.Hits}/{lockResult.Census} hits (chance {lockResult.ExpectedChanceHits:F1}) " +
                    $"in {lockResult.Hypotheses} hypotheses, worst-of-{checkedStars} seed error {worstErr:F1} px");
            }
        }

        /// <summary>
        /// Pins WHY the seed tries two anchor-pool policies, so neither can be dropped as
        /// redundant. Both cases are real and both occur in this one mosaic:
        /// <list type="bullet">
        ///   <item>an accurate hint needs the STRICT pool -- with the matching loop's 0.1 margin,
        ///   ~31% of anchors sit outside the frame where they cannot be detected, and because the
        ///   pool is the brightest N of whatever is kept they displace genuine in-frame stars and
        ///   starve the staged gates;</item>
        ///   <item>a hint tens of arcmin off needs the MARGINED pool, because real stars project
        ///   outside the frame under it.</item>
        /// </list>
        /// </summary>
        [Fact]
        public void BothAnchorPoolPoliciesAreNecessary()
        {
            var catalog = Manifest.CatalogTuples();
            var strictOnly = new List<string>();
            var marginOnly = new List<string>();

            foreach (var panel in Manifest.Panels)
            {
                var frame = panel.Frames[0];
                var dim = panel.Dim;
                var pixelScaleRad = double.DegreesToRadians(dim.PixelScale / 3600.0);
                var det = frame.DetectedPoints();

                var strict = false;
                var margined = false;
                foreach (var xSign in new[] { -1.0, 1.0 })
                {
                    var hintWcs = VelaProjection.HintWcs(frame.Hint, dim, xSign);
                    foreach (var margin in new[] { 0.0, 0.1 })
                    {
                        var pool = VelaProjection.ProjectInFrame(Manifest.Catalog, hintWcs, panel.Width, panel.Height, margin);
                        if (PairRansacLock.TryLock(pool, det, det, panel.Width, panel.Height,
                                scaleTolerance: 0.03f, out _) is not null)
                        {
                            if (margin == 0.0)
                            {
                                strict = true;
                            }
                            else
                            {
                                margined = true;
                            }
                        }
                    }
                }

                if (strict && !margined)
                {
                    strictOnly.Add(panel.Id);
                }
                else if (margined && !strict)
                {
                    marginOnly.Add($"{panel.Id} (hint off by {Separation(frame.Hint, frame.Wcs) * 60:F0} arcmin)");
                }
            }

            output.WriteLine($"strict-pool only: {string.Join(", ", strictOnly)}");
            output.WriteLine($"margined-pool only: {string.Join(", ", marginOnly)}");

            strictOnly.Count.ShouldBeGreaterThan(0,
                "panels that only seed from a strictly in-frame pool are why the seed tries it first");
            marginOnly.Count.ShouldBeGreaterThan(0,
                "panels that only seed from the margined pool are why it remains as a fallback");
        }

        /// <summary>
        /// The failure this whole effort started from, at real density: a DENSE field that has
        /// nothing to do with the frame must not lock. Panel A's detected stars are offered panel
        /// B's sky (projected through B's own solution, so it fills the frame at full density),
        /// for panels whose footprints do not intersect. The old proximity matcher answered 1,434
        /// "matches" to exactly this question.
        /// </summary>
        [Fact]
        public void UnrelatedDenseFieldsMustNotLock()
        {
            var footprints = new Dictionary<string, HashSet<int>>();
            foreach (var panel in Manifest.Panels)
            {
                footprints[panel.Id] = InFrameIndices(panel, panel.Frames[0]);
            }

            var tested = 0;
            foreach (var a in Manifest.Panels)
            {
                foreach (var b in Manifest.Panels)
                {
                    if (ReferenceEquals(a, b) || footprints[a.Id].Overlaps(footprints[b.Id]))
                    {
                        continue;
                    }

                    // B's field at full in-frame density, as B's own camera saw it.
                    var bField = VelaProjection.ProjectInFrame(Manifest.Catalog, b.Frames[0].Wcs, b.Width, b.Height);
                    if (bField.Length < 100)
                    {
                        continue;
                    }

                    var aDet = a.Frames[0].DetectedPoints();
                    var result = PairRansacLock.TryLock(bField, aDet, aDet, a.Width, a.Height,
                        scaleTolerance: 0.03f, out var diagnostics);

                    result.ShouldBeNull($"{a.Id} detected stars must not lock onto {b.Id}'s unrelated field " +
                        $"({bField.Length} catalog stars in frame): {diagnostics}");
                    tested++;

                    if (tested <= 5)
                    {
                        output.WriteLine($"{a.Id} vs {b.Id}: correctly no lock -- {diagnostics}");
                    }
                }
            }

            tested.ShouldBeGreaterThan(20, "expected many non-overlapping panel pairs in a 24-panel mosaic");
            output.WriteLine($"{tested} non-overlapping panel pairs, none locked.");
        }

        /// <summary>
        /// The sky-level counterpart of the above: panels that do not overlap must share no stars
        /// at all. If this ever finds matches, the footprint arithmetic the overlap tests rest on
        /// is wrong, and their sub-pixel agreement would be measuring nothing.
        /// </summary>
        [Fact]
        public void NonOverlappingPanelsShareNoStars()
        {
            var footprints = new Dictionary<string, HashSet<int>>();
            foreach (var panel in Manifest.Panels)
            {
                footprints[panel.Id] = InFrameIndices(panel, panel.Frames[0]);
            }

            var pairs = 0;
            foreach (var a in Manifest.Panels)
            {
                foreach (var b in Manifest.Panels)
                {
                    if (a.Id.CompareTo(b.Id) >= 0 || footprints[a.Id].Overlaps(footprints[b.Id]))
                    {
                        continue;
                    }

                    var mapped = MapDetected(a.Frames[0], b.Frames[0]);
                    var (matches, _, _) = VelaProjection.MutualMatchStats(
                        mapped, b.Frames[0].DetectedPoints(), b.Width, b.Height, MatchTolerancePx);

                    // A handful of chance coincidences is possible where a mapped star lands on
                    // the far frame at all; a shared star population is not.
                    matches.ShouldBeLessThan(20,
                        $"{a.Id} and {b.Id} share no catalog stars, so their detected stars must not cross-match ({matches})");
                    pairs++;
                }
            }

            output.WriteLine($"{pairs} disjoint panel pairs, no shared star populations.");
        }

        /// <summary>
        /// Where two panels DO overlap, the same physical stars must land on the same sky, to
        /// under a pixel. This is the mosaic's own consistency: it is what makes the panels
        /// stackable into one image, and it holds the pair of independent solutions to the same
        /// bar as each solution's own residual.
        /// </summary>
        [Fact]
        public void OverlappingPanelsAgreeToSubPixel()
        {
            var footprints = new Dictionary<string, HashSet<int>>();
            foreach (var panel in Manifest.Panels)
            {
                footprints[panel.Id] = InFrameIndices(panel, panel.Frames[0]);
            }

            var pairs = 0;
            var worst = 0.0;
            var worstPair = "";
            foreach (var a in Manifest.Panels)
            {
                foreach (var b in Manifest.Panels)
                {
                    if (a.Id.CompareTo(b.Id) >= 0)
                    {
                        continue;
                    }

                    var shared = new HashSet<int>(footprints[a.Id]);
                    shared.IntersectWith(footprints[b.Id]);
                    if (shared.Count < 200)
                    {
                        continue;   // touching corners only -- not enough to measure
                    }

                    var mapped = MapDetected(a.Frames[0], b.Frames[0]);
                    var (matches, rms, median) = VelaProjection.MutualMatchStats(
                        mapped, b.Frames[0].DetectedPoints(), b.Width, b.Height, MatchTolerancePx);

                    // Detections are ~2.5x sparser than the catalog at this depth, so a narrow
                    // overlap sliver yields few shared DETECTIONS even with 200 shared catalog
                    // stars. Below 30 there is nothing to measure; skip rather than assert on
                    // noise, and let the pair count at the end guard against skipping everything.
                    if (matches < 30)
                    {
                        output.WriteLine($"{a.Id} vs {b.Id}: {shared.Count} shared catalog stars but only " +
                            $"{matches} shared detections -- sliver overlap, not measured");
                        continue;
                    }

                    rms.ShouldBeLessThan(SubPixelRmsBar,
                        $"{a.Id} vs {b.Id}: {matches} shared detections disagree by {rms:F3} px rms (median {median:F3})");

                    if (rms > worst)
                    {
                        worst = rms;
                        worstPair = $"{a.Id} vs {b.Id}";
                    }
                    pairs++;
                }
            }

            pairs.ShouldBeGreaterThan(10, "expected many overlapping panel pairs in a 24-panel mosaic");
            output.WriteLine($"{pairs} overlapping panel pairs agree; worst {worstPair} at {worst:F3} px " +
                $"({worst * Manifest.Panels[0].PixelScaleArcsec:F2}\")");
        }

        /// <summary>
        /// Three-panel overlap: where three footprints share a region, the same stars must agree
        /// across all three, which is a strictly stronger statement than pairwise agreement (three
        /// solutions cannot all be consistent with each other and individually wrong in the same
        /// place). Reported with the region's star count so a shrinking overlap cannot silently
        /// turn this into a vacuous pass.
        /// </summary>
        [Fact]
        public void TripleOverlapRegionsAgreeToSubPixel()
        {
            var footprints = new Dictionary<string, HashSet<int>>();
            foreach (var panel in Manifest.Panels)
            {
                footprints[panel.Id] = InFrameIndices(panel, panel.Frames[0]);
            }

            var triples = 0;
            var worst = 0.0;
            var worstTriple = "";
            var panels = Manifest.Panels;
            for (var i = 0; i < panels.Length; i++)
            {
                for (var j = i + 1; j < panels.Length; j++)
                {
                    var ij = new HashSet<int>(footprints[panels[i].Id]);
                    ij.IntersectWith(footprints[panels[j].Id]);
                    if (ij.Count < 200)
                    {
                        continue;
                    }

                    for (var k = j + 1; k < panels.Length; k++)
                    {
                        var ijk = new HashSet<int>(ij);
                        ijk.IntersectWith(footprints[panels[k].Id]);
                        if (ijk.Count < 200)
                        {
                            continue;
                        }

                        // Restrict to the shared region: project only the shared catalog stars
                        // through each solution and require the three to place them together.
                        foreach (var (p, q) in new[] { (i, j), (j, k), (i, k) })
                        {
                            var (matches, rms, _) = SharedRegionAgreement(panels[p], panels[q], ijk);
                            matches.ShouldBeGreaterThan(20,
                                $"{panels[i].Id}/{panels[j].Id}/{panels[k].Id}: shared region has {ijk.Count} stars " +
                                $"but {panels[p].Id} vs {panels[q].Id} matched {matches}");
                            rms.ShouldBeLessThan(SubPixelRmsBar,
                                $"triple {panels[i].Id}/{panels[j].Id}/{panels[k].Id}: {panels[p].Id} vs {panels[q].Id} " +
                                $"disagree by {rms:F3} px over the shared region");
                            if (rms > worst)
                            {
                                worst = rms;
                                worstTriple = $"{panels[i].Id}/{panels[j].Id}/{panels[k].Id} ({panels[p].Id} vs {panels[q].Id})";
                            }
                        }

                        triples++;
                        if (triples <= 5)
                        {
                            output.WriteLine($"triple {panels[i].Id}/{panels[j].Id}/{panels[k].Id}: " +
                                $"{ijk.Count} catalog stars in the common region");
                        }
                    }
                }
            }

            triples.ShouldBeGreaterThan(0, "expected at least one three-panel overlap in a 5-degree-square mosaic");
            output.WriteLine($"{triples} three-panel overlaps agree; worst {worstTriple} at {worst:F3} px");
        }

        /// <summary>
        /// Agreement between two panels over the stars of a given shared region, measured between
        /// their DETECTED stars -- two independent exposures resolved by two independent solutions.
        ///
        /// <para>It deliberately does NOT compare each panel's catalog projections: for a shared
        /// star both sides reduce to <c>SkyToPixel_B(star)</c>, so that comparison measures the
        /// WCS round-trip and nothing about the solutions. It read 0.001 px, which is how the
        /// tautology announced itself.</para>
        /// </summary>
        private static (int Matches, double RmsPx, double MedianPx) SharedRegionAgreement(
            VelaPanel a, VelaPanel b, HashSet<int> sharedIndices)
        {
            var frameA = a.Frames[0];
            var frameB = b.Frames[0];

            // Where the shared stars sit in each frame, per that frame's own solution. These
            // define the region; the measurement below uses detections, not these.
            var regionInA = new List<Vector2>();
            foreach (var (x, y, index) in VelaProjection.ProjectInFrameIndexed(
                Manifest.Catalog, frameA.Wcs, a.Width, a.Height))
            {
                if (sharedIndices.Contains(index))
                {
                    regionInA.Add(new Vector2(x, y));
                }
            }
            if (regionInA.Count == 0)
            {
                return (0, double.NaN, double.NaN);
            }

            // A's detections lying on a shared star, mapped into B's pixel frame.
            var regionGrid = new PairRansacLock.PointGrid(
                regionInA.ToArray(), a.Width, a.Height, MatchTolerancePx);
            var mappedFromA = new List<Vector2>();
            foreach (var d in frameA.Detected)
            {
                if (regionGrid.HasWithin(d.X, d.Y)
                    && frameA.Wcs.PixelToSky(d.X, d.Y) is { } sky
                    && frameB.Wcs.SkyToPixel(sky.RA, sky.Dec) is { } px)
                {
                    mappedFromA.Add(new Vector2((float)px.X, (float)px.Y));
                }
            }

            // ...matched against what B's own camera detected there.
            return VelaProjection.MutualMatchStats(
                mappedFromA.ToArray(), frameB.DetectedPoints(), b.Width, b.Height, MatchTolerancePx);
        }

        /// <summary>
        /// Drizzle's precondition, measured on the real dither pattern: consecutive subs of one
        /// panel must be offset from each other (so the sampling grid moves) and those offsets
        /// must have varied SUB-PIXEL phases (so the offsets add information rather than repeating
        /// the same grid). A dither that lands on whole pixels, or on the same fractional offset
        /// every time, gives drizzle nothing to reconstruct.
        /// </summary>
        [Theory]
        [MemberData(nameof(PanelIds))]
        public void DitherBetweenSubsIsSubPixelDiverse(string panelId)
        {
            var panel = Manifest.Panel(panelId);
            if (panel.Frames.Length < 2)
            {
                Assert.Skip($"panel {panelId} has a single frozen frame");
                return;
            }

            var reference = panel.Frames[0];
            var centreSky = reference.Wcs.PixelToSky(panel.Width / 2.0, panel.Height / 2.0);
            centreSky.ShouldNotBeNull($"{panelId}: reference frame has no usable WCS");

            var phases = new List<(double X, double Y)>();
            var maxShift = 0.0;
            for (var i = 1; i < panel.Frames.Length; i++)
            {
                var frame = panel.Frames[i];

                // Where the reference frame's centre lands in this frame: the dither vector.
                var landed = frame.Wcs.SkyToPixel(centreSky.Value.RA, centreSky.Value.Dec);
                landed.ShouldNotBeNull($"{panelId}/{frame.Name}: reference centre falls outside this frame");

                var dx = landed.Value.X - panel.Width / 2.0;
                var dy = landed.Value.Y - panel.Height / 2.0;
                var shift = Math.Sqrt(dx * dx + dy * dy);
                maxShift = Math.Max(maxShift, shift);

                // The mapped detected stars must actually land on this frame's stars -- otherwise
                // the "dither" measured above is just two disagreeing solutions.
                var mapped = MapDetected(reference, frame);
                var (matches, rms, median) = VelaProjection.MutualMatchStats(
                    mapped, frame.DetectedPoints(), panel.Width, panel.Height, MatchTolerancePx);
                matches.ShouldBeGreaterThan(300,
                    $"{panelId}: only {matches} stars carry between frames 0 and {i}");
                rms.ShouldBeLessThan(SubPixelRmsBar,
                    $"{panelId}: frames 0 and {i} register to {rms:F3} px rms, so the dither vector is not trustworthy");

                var phaseX = dx - Math.Floor(dx);
                var phaseY = dy - Math.Floor(dy);
                phases.Add((phaseX, phaseY));

                output.WriteLine($"{panelId}: frame 0 -> {i} dither {shift:F1} px " +
                    $"({shift * panel.PixelScaleArcsec:F1}\"), sub-pixel phase ({phaseX:F2}, {phaseY:F2}), " +
                    $"{matches} stars register at {rms:F3} px rms (median {median:F3})");
            }

            // Dither is a property of the SET, not of every consecutive pair: the sequence dithers
            // every few subs, so back-to-back frames (0 and 1 here) legitimately sit within a
            // fraction of a pixel -- panel 3's are 0.03 px apart. What drizzle needs is that the
            // frames it integrates do not all share one sampling grid.
            maxShift.ShouldBeGreaterThan(1.0,
                $"{panelId}: every sub lands within {maxShift:F2} px of the first -- the set carries no dither");

            // Sub-pixel diversity: at least two frames must sit on distinguishable phases, or the
            // extra subs re-sample the same grid and add no reconstructable detail.
            var spread = 0.0;
            for (var i = 0; i < phases.Count; i++)
            {
                for (var j = i + 1; j < phases.Count; j++)
                {
                    var dxp = Math.Abs(phases[i].X - phases[j].X);
                    var dyp = Math.Abs(phases[i].Y - phases[j].Y);
                    spread = Math.Max(spread, Math.Max(Math.Min(dxp, 1 - dxp), Math.Min(dyp, 1 - dyp)));
                }
            }

            if (phases.Count > 1)
            {
                spread.ShouldBeGreaterThan(0.05,
                    $"{panelId}: every sub lands on the same sub-pixel phase, so drizzle has no extra sampling");
                output.WriteLine($"{panelId}: max dither {maxShift:F1} px, sub-pixel phase spread {spread:F3} px " +
                    $"across {phases.Count + 1} subs");
            }
        }

        private static double Separation(WCS a, WCS b)
        {
            var ra1 = a.CenterRA * (Math.PI / 12.0);
            var ra2 = b.CenterRA * (Math.PI / 12.0);
            var (sinD1, cosD1) = Math.SinCos(double.DegreesToRadians(a.CenterDec));
            var (sinD2, cosD2) = Math.SinCos(double.DegreesToRadians(b.CenterDec));
            var cos = sinD1 * sinD2 + cosD1 * cosD2 * Math.Cos(ra1 - ra2);
            return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1.0, 1.0)));
        }
    }
}
