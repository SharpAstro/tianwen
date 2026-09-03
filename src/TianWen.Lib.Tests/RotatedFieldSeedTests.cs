using Shouldly;
using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The seed's anchor pool must not assume the camera is north-up.
    /// </summary>
    /// <remarks>
    /// <para>The bug this pins kept LDN 1089 -- a real 3:2 frame at an 88 degree camera angle --
    /// from solving at all, while every existing regression stayed green, because
    /// <c>VelaMosaicFieldTests</c> builds its pools with the test project's own
    /// <c>VelaProjection.ProjectInFrame</c> and so never exercised
    /// <see cref="CatalogPlateSolver.ProjectCatalogStars"/>'s in-frame policy, and every panel in
    /// that mosaic is close to north-up regardless.</para>
    /// <para>The field here is synthetic in exactly one respect -- the camera angle -- over the
    /// REAL frozen Vela catalogue, which is the point: the defect is geometric (which region of
    /// sky the pool selects), not photometric, so what matters is real star density and brightness
    /// distribution, both of which the frozen catalogue supplies.</para>
    /// </remarks>
    public class RotatedFieldSeedTests(ITestOutputHelper output)
    {
        // A deliberately non-square frame: on a square one the rectangle and the disc very nearly
        // agree, and the bug is invisible. 3:2 is what most sensors are.
        private const int W = 6248, H = 4176;
        private const double PixelScaleArcsec = 1.38;
        private const double CameraAngleDeg = 88.0;

        [Fact]
        public void ARotatedFieldSeedsFromTheDiscPoolAndNotFromTheRectangle()
        {
            var catalog = VelaMosaicStarLists.Manifest.CatalogTuples();
            catalog.Sort(static (a, b) => a.VMag.CompareTo(b.VMag));

            var panel = VelaMosaicStarLists.Manifest.Panels[0];
            var origin = new WCS(panel.Frames[0].Wcs.CenterRA, panel.Frames[0].Wcs.CenterDec);
            var dim = new ImageDim(PixelScaleArcsec, W, H);
            var pixelScaleRad = double.DegreesToRadians(PixelScaleArcsec / 3600.0);
            double cx = W / 2.0, cy = H / 2.0;

            // The detected field: every catalogue star the ROTATED camera actually sees. Built by
            // rotating the north-up projection about the frame centre and keeping what lands on
            // the sensor -- which is precisely the set the rectangle policy gets wrong.
            var rot = double.DegreesToRadians(CameraAngleDeg);
            var truth = new Matrix3x2(
                (float)Math.Cos(rot), (float)Math.Sin(rot),
                (float)-Math.Sin(rot), (float)Math.Cos(rot),
                0f, 0f);
            truth.M31 = (float)(cx - (cx * truth.M11 + cy * truth.M21));
            truth.M32 = (float)(cy - (cx * truth.M12 + cy * truth.M22));

            var everything = CatalogPlateSolver.ProjectCatalogStars(
                catalog, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0, marginFraction: 1.0f);
            var detected = new List<Vector2>();
            foreach (var s in everything)
            {
                var t = Vector2.Transform(new Vector2(s.Pixel.XCentroid, s.Pixel.YCentroid), truth);
                if (t.X >= 0 && t.Y >= 0 && t.X < W && t.Y < H)
                {
                    detected.Add(t);
                }
            }
            var detPts = detected.ToArray();
            output.WriteLine($"{CameraAngleDeg} deg camera angle on a {W}x{H} frame: " +
                $"{catalog.Count} catalogue stars, {detPts.Length} of them on the sensor");
            detPts.Length.ShouldBeGreaterThan(200, "the synthetic field must be dense enough to be worth locking");

            var disc = Anchors(catalog, origin, pixelScaleRad, cx, cy, dim, rotationInvariant: true);
            var rect = Anchors(catalog, origin, pixelScaleRad, cx, cy, dim, rotationInvariant: false);

            // How many anchors each policy picks that the camera cannot see. This is the defect
            // itself, independent of whether any particular scan finds a lock.
            output.WriteLine($"  disc pool      {disc.Length,4} anchors, {OffSensor(disc, truth),4} of them off the sensor");
            output.WriteLine($"  rectangle pool {rect.Length,4} anchors, {OffSensor(rect, truth),4} of them off the sensor");
            OffSensor(disc, truth).ShouldBe(0, "no disc anchor may leave the sensor under ANY camera angle -- that is what the disc IS");
            OffSensor(rect, truth).ShouldBeGreaterThan(rect.Length / 10,
                "the rectangle is the CONTROL: if it stops picking off-sensor anchors here, this test " +
                "has stopped discriminating and the disc assertion above proves nothing");

            var ct = TestContext.Current.CancellationToken;
            var discLock = PairRansacLock.TryLock(disc, detPts, detPts, W, H, 0.03f, out var discDiag, cancellationToken: ct);
            var rectLock = PairRansacLock.TryLock(rect, detPts, detPts, W, H, 0.03f, out var rectDiag, cancellationToken: ct);
            output.WriteLine($"  disc:      {(discLock is null ? "NO LOCK" : $"locked {discLock.Value.Hits}/{discLock.Value.Census}")} -- {discDiag}");
            output.WriteLine($"  rectangle: {(rectLock is null ? "NO LOCK" : $"locked {rectLock.Value.Hits}/{rectLock.Value.Census}")} -- {rectDiag}");

            // Sanity check, NOT the discriminator: these detections are noiseless and complete, so
            // the rectangle survives its 44 dead anchors and locks too (116/160 when this was
            // written). The real frame is what separates them -- on LDN 1089 only 35% of the
            // rectangle's 20 brightest anchors had a detection at all, which is what starved
            // Stage 1. The OffSensor pair above is what fails if the disc policy regresses.
            discLock.ShouldNotBeNull($"a rotated field must seed from the rotation-invariant pool; {discDiag}");

            // And the recovered transform must be the one we rotated by, across the whole frame.
            foreach (var corner in new[] { new Vector2(0, 0), new Vector2(W, 0), new Vector2(0, H), new Vector2(W, H) })
            {
                Vector2.Distance(Vector2.Transform(corner, truth), Vector2.Transform(corner, discLock.Value.Transform))
                    .ShouldBeLessThan(2.0f, $"corner {corner} disagrees with the {CameraAngleDeg} deg truth");
            }
        }

        private static Vector2[] Anchors(
            List<(double RA, double Dec, double VMag)> catalog, WCS origin, double pixelScaleRad,
            double cx, double cy, ImageDim dim, bool rotationInvariant)
        {
            var pool = CatalogPlateSolver.ProjectCatalogStars(
                catalog, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0,
                marginFraction: 0f, rotationInvariant: rotationInvariant);
            var n = Math.Min(160, pool.Count);
            var pts = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                pts[i] = new Vector2(pool[i].Pixel.XCentroid, pool[i].Pixel.YCentroid);
            }
            return pts;
        }

        private static int OffSensor(Vector2[] anchors, in Matrix3x2 truth)
        {
            var off = 0;
            foreach (var a in anchors)
            {
                var t = Vector2.Transform(a, truth);
                if (t.X < 0 || t.Y < 0 || t.X >= W || t.Y >= H)
                {
                    off++;
                }
            }
            return off;
        }

        /// <summary>
        /// The similarity fit the in-scan refinement uses. Four degrees of freedom, so unlike
        /// <see cref="Matrix3x2Helper.FitAffineTransform"/> it cannot answer with a mirror -- which
        /// is what keeps <c>PairRansacLock</c>'s per-parity separation intact while it refines.
        /// </summary>
        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(88.0, 1.0)]
        [InlineData(-33.0, 1.04)]
        [InlineData(180.0, 0.97)]
        public void FitSimilarityTransformRecoversRotationScaleAndTranslation(double rotDeg, double scale)
        {
            var rot = double.DegreesToRadians(rotDeg);
            var expected = new Matrix3x2(
                (float)(scale * Math.Cos(rot)), (float)(scale * Math.Sin(rot)),
                (float)(-scale * Math.Sin(rot)), (float)(scale * Math.Cos(rot)),
                17f, -42f);

            var rng = new Random(11);
            var src = new Vector2[40];
            var dst = new Vector2[40];
            for (var i = 0; i < src.Length; i++)
            {
                src[i] = new Vector2(rng.Next(0, 4000), rng.Next(0, 3000));
                dst[i] = Vector2.Transform(src[i], expected);
            }

            var fit = Matrix3x2.FitSimilarityTransform(src, dst);
            fit.ShouldNotBeNull();
            foreach (var probe in new[] { new Vector2(0, 0), new Vector2(4000, 0), new Vector2(0, 3000), new Vector2(4000, 3000) })
            {
                Vector2.Distance(Vector2.Transform(probe, expected), Vector2.Transform(probe, fit.Value))
                    .ShouldBeLessThan(0.01f);
            }
        }

        [Fact]
        public void FitSimilarityTransformNeverAnswersWithAMirror()
        {
            // A perfectly mirrored correspondence has no similarity that explains it. The fit must
            // still return a chirality-PRESERVING matrix (positive determinant) and simply be
            // wrong, rather than silently flipping parity.
            var rng = new Random(5);
            var src = new Vector2[40];
            var dst = new Vector2[40];
            for (var i = 0; i < src.Length; i++)
            {
                src[i] = new Vector2(rng.Next(0, 4000), rng.Next(0, 3000));
                dst[i] = new Vector2(4000 - src[i].X, src[i].Y);
            }

            var fit = Matrix3x2.FitSimilarityTransform(src, dst);
            fit.ShouldNotBeNull();
            (fit.Value.M11 * fit.Value.M22 - fit.Value.M12 * fit.Value.M21)
                .ShouldBeGreaterThanOrEqualTo(0f, "a similarity fit must never flip chirality");
        }

        [Fact]
        public void FitSimilarityTransformRefusesDegenerateInput()
        {
            Matrix3x2.FitSimilarityTransform([new Vector2(1, 1)], [new Vector2(2, 2)]).ShouldBeNull();

            // Every source point identical: nothing determines a rotation or a scale.
            var same = new[] { new Vector2(7, 7), new Vector2(7, 7), new Vector2(7, 7) };
            var moved = new[] { new Vector2(1, 2), new Vector2(3, 4), new Vector2(5, 6) };
            Matrix3x2.FitSimilarityTransform(same, moved).ShouldBeNull();
        }
    }
}
