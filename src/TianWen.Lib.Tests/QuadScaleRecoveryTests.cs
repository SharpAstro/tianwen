using Shouldly;
using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="QuadScaleRecovery"/> against the frozen Vela mosaic: 24 real panel pointings
    /// at real density, with the catalog projected through the HEADER HINT (pointing wrong by up to
    /// 40 arcmin, unrotated where the real fields are rotated) and the pixel scale deliberately
    /// wrong by the marketed-versus-actual focal length this feature exists for.
    ///
    /// <para>Two properties matter equally and are tested separately, because a recovery that is
    /// usually right is worthless without a way to tell when it is not: the scale must come back
    /// ACCURATE where it comes back at all, and it must be REFUSED where it would be wrong. The
    /// second is what lets the solver narrow its seed window to a fifth of the old one.</para>
    /// </summary>
    [Collection("Astrometry")]
    public class QuadScaleRecoveryTests(ITestOutputHelper output)
    {
        /// <summary>
        /// The marketed-versus-actual focal length error (130 mm lens sold as 135), applied to the
        /// declared scale so the recovery has something real to find.
        /// </summary>
        private const double ScaleErrorFactor = 1.039;

        /// <summary>
        /// Accuracy bar. Measured worst case over the panels that recover is 0.065%; 0.15% is a bar
        /// with margin that still fails long before <c>RecoveredScaleTolerance</c> (0.25%) could
        /// exclude the truth from the seed's window, which is the consequence that matters.
        /// </summary>
        private const double AccuracyBarPercent = 0.15;

        private static (Vector2[] Detected, Vector2[] Projected, double TruthRatio) BuildPanelInputs(
            VelaPanel panel, VelaFrame frame, double scaleErrorFactor)
        {
            var detected = frame.DetectedPoints();
            var wrongDim = new ImageDim(panel.PixelScaleArcsec * scaleErrorFactor, panel.Width, panel.Height);
            var hintWcs = VelaProjection.HintWcs(frame.Hint, wrongDim);
            var projected = VelaProjection.ProjectInFrame(
                VelaMosaicStarLists.Manifest.Catalog, hintWcs, panel.Width, panel.Height);

            // The ratio a correct recovery returns is the projection's assumed scale over the scale
            // the optics actually deliver -- NOT the injected factor alone. The declared scale is
            // itself 0.26-0.31% off the solved one on every panel of this mosaic, and charging the
            // recovery for that is charging it for the error it exists to find.
            var truthRatio = panel.PixelScaleArcsec * scaleErrorFactor / frame.Wcs.PixelScaleArcsec;
            return (detected, projected, truthRatio);
        }

        [Fact]
        public void TheScaleComesBackFromTheStarsWhereverItComesBackAtAll()
        {
            var manifest = VelaMosaicStarLists.Manifest;
            var recovered = 0;
            var declined = new List<string>();
            var worstErrorPercent = 0.0;
            var worstPanel = "";

            foreach (var panel in manifest.Panels)
            {
                var frame = panel.Frames[0];
                var (detected, projected, truthRatio) = BuildPanelInputs(panel, frame, ScaleErrorFactor);

                if (QuadScaleRecovery.TryRecover(detected, projected) is not { } recovery)
                {
                    declined.Add(panel.Id);
                    continue;
                }

                recovered++;
                var errorPercent = 100.0 * Math.Abs(recovery.Ratio - truthRatio) / truthRatio;
                if (errorPercent > worstErrorPercent)
                {
                    worstErrorPercent = errorPercent;
                    worstPanel = panel.Id;
                }

                errorPercent.ShouldBeLessThan(AccuracyBarPercent,
                    $"{panel.Id}: recovered {recovery.Ratio:F5} against truth {truthRatio:F5} " +
                    $"({errorPercent:F3}% off) from {recovery.Candidates} candidates at spread {recovery.RelativeSpread:F4}");
            }

            output.WriteLine($"recovered on {recovered}/{manifest.Panels.Length} panels, " +
                $"worst {worstErrorPercent:F3}% ({worstPanel}); declined: " +
                $"{(declined.Count == 0 ? "none" : string.Join(", ", declined))}");

            // The feature is worthless if it declines everything, so the count is asserted too --
            // otherwise a guard tightened to always-refuse would pass the accuracy test above
            // vacuously, which is the failure mode of testing only one side of a gate.
            recovered.ShouldBeGreaterThanOrEqualTo(manifest.Panels.Length - 2,
                "the recovery must actually fire on real fields, not merely avoid being wrong");
        }

        /// <summary>
        /// The guard's own test: the mosaic contains a panel whose candidate set is contaminated
        /// (92 candidates -- MORE than any panel that recovers correctly -- scattering to a spread of
        /// 0.37 against the 0.0014 of the worst good one, and a median 27% off). It must be refused.
        /// <para>This is the case that decides whether the narrow seed window is safe, and it is also
        /// why the guard is a SPREAD and not a count: keyed on candidate count, this panel would have
        /// been ranked the most trustworthy in the set.</para>
        /// </summary>
        [Fact]
        public void AContaminatedCandidateSetIsRefusedRatherThanAveraged()
        {
            var manifest = VelaMosaicStarLists.Manifest;
            var refusedSomething = false;

            foreach (var panel in manifest.Panels)
            {
                var frame = panel.Frames[0];
                var (detected, projected, truthRatio) = BuildPanelInputs(panel, frame, ScaleErrorFactor);

                // What the raw candidate set says, with no guard applied: this is the number the
                // guard has to overrule.
                var rawMedian = RawCandidateMedian(detected, projected);
                if (rawMedian is not { } raw || Math.Abs(raw - truthRatio) / truthRatio <= 0.01)
                {
                    continue;
                }

                refusedSomething = true;
                QuadScaleRecovery.TryRecover(detected, projected).ShouldBeNull(
                    $"{panel.Id}: the raw candidate median is {raw:F4} against truth {truthRatio:F4}, " +
                    "so a recovery here would hand the seed a window that excludes the answer");
                output.WriteLine($"{panel.Id}: raw median {raw:F4} vs truth {truthRatio:F4} -- correctly refused");
            }

            // Assert the fixture's premise. If the frozen data ever stops containing a bad-recovery
            // case, this test silently stops testing the guard, and the guard is the whole safety
            // argument for the narrowed window.
            refusedSomething.ShouldBeTrue(
                "the frozen mosaic no longer contains a contaminated candidate set, so this test " +
                "is not exercising the guard any more -- find one or the narrow window is unpinned");
        }

        /// <summary>
        /// The real negative case for a SCALE estimator, which is not the one it looks like.
        ///
        /// <para>The obvious test -- one panel's detected stars against a different, non-overlapping
        /// panel's sky, expecting a refusal -- is WRONG, and writing it that way is how this got
        /// found: 8 of 24 such pairs return a confident answer, and every one of them is RIGHT.
        /// Both panels come from the same camera through the same optics, so two unrelated patches of
        /// sky genuinely share a plate scale, and ~1.0 is the true ratio. Positional unrelatedness is
        /// <c>PairRansacLock</c>'s problem (see <c>UnrelatedDenseFieldsMustNotLock</c>); this estimates
        /// one number and must not be judged on a property it never claimed.</para>
        ///
        /// <para>The hazard that actually matters is a CONFIDENT ratio that is not the true one,
        /// because the seed's window is only 0.25% wide around it. So the pairs are run at their
        /// native scale AND with the second field's scale deliberately shifted well outside that
        /// window: if the answer tracks the shift, the estimator is measuring the real relationship;
        /// if it keeps answering ~1.0 because two fields of similar density look alike, it is
        /// reporting chance agreement and the guard has to catch it.</para>
        /// </summary>
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(0.8)]
        public void AConfidentScaleIsTheTrueScaleEvenOnAnUnrelatedField(double relativeScale)
        {
            var manifest = VelaMosaicStarLists.Manifest;
            var tested = 0;
            var answered = 0;
            var wrong = new List<string>();

            for (var i = 0; i < manifest.Panels.Length; i++)
            {
                var a = manifest.Panels[i];
                // Deliberately far apart in the mosaic so the footprints cannot overlap.
                var b = manifest.Panels[(i + (manifest.Panels.Length / 2)) % manifest.Panels.Length];
                if (ReferenceEquals(a, b))
                {
                    continue;
                }

                var detected = a.Frames[0].DetectedPoints();

                // B's own solution, rescaled: the projection's scale is what the recovery must find
                // relative to A's frame, whatever the sky in it.
                var bWcs = b.Frames[0].Wcs;
                var projectedDim = new ImageDim(bWcs.PixelScaleArcsec * relativeScale, b.Width, b.Height);
                var projected = VelaProjection.ProjectInFrame(
                    manifest.Catalog, VelaProjection.HintWcs(bWcs, projectedDim), b.Width, b.Height);
                if (projected.Length < QuadScaleRecovery.MinStars)
                {
                    continue;
                }

                tested++;
                if (QuadScaleRecovery.TryRecover(detected, projected) is not { } recovery)
                {
                    continue;
                }

                answered++;
                var truthRatio = bWcs.PixelScaleArcsec * relativeScale / a.Frames[0].Wcs.PixelScaleArcsec;
                var errorPercent = 100.0 * Math.Abs(recovery.Ratio - truthRatio) / truthRatio;
                if (errorPercent > AccuracyBarPercent)
                {
                    wrong.Add($"{a.Id} vs {b.Id} -> {recovery.Ratio:F4} against truth {truthRatio:F4} " +
                        $"({errorPercent:F2}% off, {recovery.Candidates} candidates, spread {recovery.RelativeSpread:F4})");
                }
            }

            tested.ShouldBeGreaterThan(0, "no unrelated pair was actually compared");
            output.WriteLine($"relative scale {relativeScale:F2}: {tested} unrelated pairs, " +
                $"{answered} answered, {wrong.Count} wrong");
            wrong.ShouldBeEmpty(
                "a confident answer whose ratio is not the true one would hand the seed a 0.25% " +
                "window that excludes the truth: " + string.Join("; ", wrong));
        }

        /// <summary>
        /// The candidate median with NO guard, so a test can state what the guard is overruling.
        /// Mirrors <see cref="QuadScaleRecovery"/>'s scan deliberately rather than calling it: the
        /// point is to see the ungated number.
        /// </summary>
        private static float? RawCandidateMedian(ReadOnlySpan<Vector2> detected, ReadOnlySpan<Vector2> projected)
        {
            var det = Take(detected);
            var proj = Take(projected);
            if (det.Length < QuadScaleRecovery.MinStars || proj.Length < QuadScaleRecovery.MinStars)
            {
                return null;
            }

            var detQuads = new StarQuadList(det);
            var projQuads = new StarQuadList(proj);
            var ratios = new List<float>();
            for (var i = 0; i < detQuads.Count; i++)
            {
                var q = detQuads[i];
                for (var j = 0; j < projQuads.Count; j++)
                {
                    var c = projQuads[j];
                    if (MathF.Abs(q.Dist2 - c.Dist2) <= QuadScaleRecovery.RatioTolerance
                        && MathF.Abs(q.Dist3 - c.Dist3) <= QuadScaleRecovery.RatioTolerance
                        && MathF.Abs(q.Dist4 - c.Dist4) <= QuadScaleRecovery.RatioTolerance
                        && MathF.Abs(q.Dist5 - c.Dist5) <= QuadScaleRecovery.RatioTolerance
                        && MathF.Abs(q.Dist6 - c.Dist6) <= QuadScaleRecovery.RatioTolerance
                        && c.Dist1 > 0)
                    {
                        ratios.Add(q.Dist1 / c.Dist1);
                    }
                }
            }

            if (ratios.Count == 0)
            {
                return null;
            }

            ratios.Sort();
            return ratios[ratios.Count / 2];
        }

        private static Vector2[] Take(ReadOnlySpan<Vector2> points)
        {
            var n = Math.Min(QuadScaleRecovery.MaxStars, points.Length);
            var copy = points[..n].ToArray();
            Array.Sort(copy, static (a, b) => a.X.CompareTo(b.X));
            return copy;
        }
    }
}
