using System;
using TianWen.Lib.Geometry;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The band that survives <see cref="Image.LargestCoveredRectangle()"/>: real data, no zeros in it,
    /// reached by fewer subs than the interior and so noisier. See docs/plans/viewer-prerelease-fixes.md
    /// P25 for the measurements these constants come from.
    /// </summary>
    /// <remarks>
    /// The frames here are synthetic, deliberately: a real master cannot state its own answer, and the
    /// two cases worth pinning are exactly the two a real file leaves ambiguous -- a band that ends
    /// (trim it) against a frame-wide noise gradient that does not (refuse). The real-file numbers live
    /// in the plan doc beside the harness that produced them.
    /// </remarks>
    public class CoverageEdgeWalkTests
    {
        private const double BaseSigma = 0.002;

        /// <summary>
        /// White noise at <paramref name="sigmaAt"/> x <see cref="BaseSigma"/>, on a flat level. White
        /// because the statistic is a difference of neighbours: correlated noise would measure the
        /// correlation as well as the level, which is the whole difficulty on a real drizzled frame.
        /// </summary>
        private static Image NoiseFrame(int width, int height, Func<int, int, double> sigmaAt, int channels = 1, int seed = 20260908)
        {
            var rng = new Random(seed);
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // Box-Muller, one draw per pixel.
                        var u1 = 1.0 - rng.NextDouble();
                        var u2 = rng.NextDouble();
                        var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                        plane[y, x] = (float)(0.2 + BaseSigma * sigmaAt(x, y) * gauss);
                    }
                }
                planes[c] = plane;
            }

            return new Image([.. planes], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        private static PixelRect Whole(Image image) => new PixelRect(0, 0, image.Width, image.Height);

        /// <summary>A record struct would hand every caller zeros here; the type is a record class for
        /// exactly that reason, and this is the assertion that says so.</summary>
        [Fact]
        public void TheDefaultsAreTheDefaults()
        {
            var fresh = new CoverageEdgeWalkOptions();
            fresh.ShouldBe(CoverageEdgeWalkOptions.Default);
            fresh.BandThickness.ShouldBe(16);
            fresh.TileLength.ShouldBe(64);
            fresh.SettleMargin.ShouldBe(1.15);
        }

        [Fact]
        public void AUniformFrameHasNothingToTrim()
        {
            var image = NoiseFrame(1024, 1024, static (_, _) => 1.0);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.TotalDepth.ShouldBe(0);
            trims.AnyDeclined.ShouldBeFalse();
            trims.Top.EdgeRatio.ShouldBeLessThan(1.15);
            trims.Apply(Whole(image)).ShouldBe(Whole(image));
        }

        /// <summary>The case the feature exists for: a noisier band of known depth on one edge.</summary>
        [Fact]
        public void ABandThatEndsIsTrimmedToWhereItEnds()
        {
            var image = NoiseFrame(1024, 1024, static (_, y) => y < 32 ? 2.5 : 1.0);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.Top.Settled.ShouldBeTrue();
            trims.Top.EdgeRatio.ShouldBeGreaterThan(2.0);
            // The band is 32 px and a sample is 16 px thick, so the first fully-clean sample sits at 32.
            trims.Top.Depth.ShouldBeInRange(28, 40);
            trims.Bottom.Depth.ShouldBe(0);
            trims.Left.Depth.ShouldBe(0);
            trims.Right.Depth.ShouldBe(0);

            var kept = trims.Apply(Whole(image));
            kept.Y.ShouldBe(trims.Top.Depth);
            kept.Height.ShouldBe(1024 - trims.Top.Depth);
            kept.Width.ShouldBe(1024);
        }

        /// <summary>Every edge at once, and with three channels, since the worst channel decides.</summary>
        [Fact]
        public void EveryEdgeIsWalkedIndependently()
        {
            var image = NoiseFrame(1024, 1024, static (x, y) =>
                y < 32 || y >= 1024 - 16 || x < 24 ? 2.5 : 1.0, channels: 3);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.Top.Depth.ShouldBeInRange(28, 40);
            trims.Bottom.Depth.ShouldBeInRange(12, 28);
            trims.Left.Depth.ShouldBeInRange(20, 32);
            trims.Right.Depth.ShouldBe(0);
        }

        /// <summary>
        /// The first of the two protections, and the one that saves the case this whole design turns on:
        /// TianWen's own 10P drizzle master reads 1.66x at its left edge, PEAKS at 2.05x 76 px in, and
        /// only then decays over ~460 px -- all at full coverage, per its own weight map. The edge is
        /// therefore not the noisiest place, which is what "nothing to trim" means; every rule that
        /// matched a level instead (against the interior, against the edge's own deep plateau, or by
        /// slope) trimmed 300+ px of it, where the weight map says 4.
        /// </summary>
        [Fact]
        public void AnEdgeQuieterThanJustInsideIsNotABand()
        {
            // 1.66 at the edge, 2.05 at 76 px, back to 1.0 by 460: the shape read off the real master.
            var image = NoiseFrame(1024, 1024, static (_, y) => y < 76
                ? 1.66 + (0.39 * y / 76.0)
                : y < 460 ? 2.05 - (1.05 * (y - 76) / 384.0) : 1.0);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.Top.EdgeRatio.ShouldBeLessThan(1.15);
            trims.Top.Depth.ShouldBe(0);
            trims.Top.Settled.ShouldBeTrue();
        }

        /// <summary>
        /// The second protection: a band whose end lies past the LOSS CAP is still not trimmed, because
        /// trimming to the cap is the worst of both -- a frame smaller by 5% of every edge that still
        /// shows the band it was cropped to remove.
        ///
        /// <para>What changed is that the walk now SAYS SO. It reports
        /// <see cref="CoverageEdgeOutcome.BeyondCap"/> with the depth it found, which is a different
        /// fact from "the noise never settles at all" and is actionable: raise the cap past
        /// <c>SettleDepth</c> and the band comes off. Both used to arrive as one bare
        /// <c>Settled: false</c>.</para>
        ///
        /// <para>Note what the roomier case no longer has to do. It used to raise
        /// <c>ReferenceFraction</c> to 0.40 BY HAND alongside the cap, because the reference was fixed
        /// while the cap moved and the guard would otherwise fire. The reference now follows the search
        /// window on its own.</para>
        /// </summary>
        [Fact]
        public void ABandDeeperThanTheLossCapIsReportedRatherThanTrimmedOrRefused()
        {
            // 60 px of band against a 48 px cap (5% of 1024, rounded to the step).
            var image = NoiseFrame(1024, 1024, static (_, y) => y < 60 ? 3.0 : 1.0);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.Top.Outcome.ShouldBe(CoverageEdgeOutcome.BeyondCap);
            trims.Top.Depth.ShouldBe(0);
            trims.Top.SettleDepth.ShouldBeInRange(56, 72);   // it knows where the band ends
            trims.Top.Settled.ShouldBeTrue();                // it DID settle, just past the cap
            trims.Apply(Whole(image)).ShouldBe(Whole(image));

            // Raise only the cap. The reference used to need raising with it; it does not now.
            var roomier = CoverageEdgeWalk.Measure(
                image, Whole(image), new CoverageEdgeWalkOptions { MaxTrimFraction = 0.15 });
            roomier.Top.Outcome.ShouldBe(CoverageEdgeOutcome.Trimmed);
            roomier.Top.Depth.ShouldBeInRange(56, 72);
        }

        /// <summary>
        /// A frame too small for three tiles across a band says it COULD NOT MEASURE, not "nothing to
        /// trim", and does not crash.
        ///
        /// <para>It used to answer the latter: the internal <c>Nothing</c> carried
        /// <c>Settled: true, EdgeRatio: 1.0</c>, so "I could not look" and "I looked and the edge is
        /// clean" were the same value. That conflation is what let a mis-set option silently disable
        /// the whole feature on every edge of every master with nothing reporting it.</para>
        /// </summary>
        [Fact]
        public void AFrameTooSmallToMeasureSaysSoRatherThanClaimingItIsClean()
        {
            var image = NoiseFrame(96, 96, static (_, y) => y < 8 ? 4.0 : 1.0);

            var trims = CoverageEdgeWalk.Measure(image, Whole(image));

            trims.TotalDepth.ShouldBe(0);
            trims.Left.Outcome.ShouldBe(CoverageEdgeOutcome.NotMeasurable);
            trims.Top.Outcome.ShouldBe(CoverageEdgeOutcome.NotMeasurable);
            trims.Right.Outcome.ShouldBe(CoverageEdgeOutcome.NotMeasurable);
            trims.Bottom.Outcome.ShouldBe(CoverageEdgeOutcome.NotMeasurable);
            trims.AnyDeclined.ShouldBeTrue();   // it refused to answer, and that is reportable
        }

        [Fact]
        public void TrimsThatWouldEmptyTheRectangleLeaveItAlone()
        {
            var rect = new PixelRect(10, 20, 40, 30);
            var trims = new CoverageEdgeTrims(
                new CoverageEdgeTrim(30, CoverageEdgeOutcome.Trimmed, 2.0, 30),
                new CoverageEdgeTrim(20, CoverageEdgeOutcome.Trimmed, 2.0, 20),
                new CoverageEdgeTrim(30, CoverageEdgeOutcome.Trimmed, 2.0, 30),
                new CoverageEdgeTrim(20, CoverageEdgeOutcome.Trimmed, 2.0, 20));

            trims.Apply(rect).ShouldBe(rect);
        }

        /// <summary>The walk starts from the zero-free rectangle, so it must respect an offset one.</summary>
        [Fact]
        public void TheWalkMeasuresInsideTheRectangleItIsGiven()
        {
            var image = NoiseFrame(1024, 1024, static (_, y) => y < 132 ? 2.5 : 1.0);

            // Told to start 100 px in, only the remaining 32 px of the band are its business.
            var trims = CoverageEdgeWalk.Measure(image, new PixelRect(0, 100, 1024, 924));

            trims.Top.Depth.ShouldBeInRange(28, 40);
        }
    }
}
