using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the comet compose itself: the one operand-order slip the whole derivation chain cannot
    /// catch, and the epoch the body's absolute position is evaluated at.
    /// </summary>
    public class CometComposeTests
    {
        private static readonly DateTimeOffset RefStart = new(2025, 10, 18, 10, 15, 30, TimeSpan.Zero);

        private static ImageMeta MetaAt(DateTimeOffset start, double exposureSeconds = 30) => new()
        {
            Instrument = "synth",
            ExposureStartTime = start,
            ExposureDuration = TimeSpan.FromSeconds(exposureSeconds),
            FrameType = FrameType.Light,
        };

        private static void ShouldBeCloseTo(Vector2 actual, Vector2 expected, float tolerance)
        {
            actual.X.ShouldBe(expected.X, tolerance);
            actual.Y.ShouldBe(expected.Y, tolerance);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.35f)]
        [InlineData(-1.2f)]
        public void TheTargetLandsOnOnePixelAndTheStarsDoNot(float rotationRad)
        {
            // Frame i is dithered and rotated against the reference. The star solution undoes that;
            // the compose then also undoes the body's drift since the reference epoch.
            var starSolution = Matrix3x2.CreateRotation(rotationRad) * Matrix3x2.CreateTranslation(88.6f, -41.2f);
            var rate = new Vector2(-241.7f, -41.4f);
            const double dtHours = 0.75;

            // Where the body IS at t_i, on the reference's star grid: it moved rate*dt since t_ref.
            var bodyAtRef = new Vector2(1451.2f, 1501.0f);
            var bodyAtTi = bodyAtRef + rate * (float)dtHours;
            // And therefore where it sits in frame i's own pixels.
            Matrix3x2.Invert(starSolution, out var refToFrame).ShouldBeTrue();
            var bodyInFrame = Vector2.Transform(bodyAtTi, refToFrame);

            var compose = CometCompose.ToCometGrid(starSolution, rate, dtHours);

            // The compose puts the body back where it was at the reference epoch...
            ShouldBeCloseTo(Vector2.Transform(bodyInFrame, compose), bodyAtRef, 1e-2f);

            // ...and a fixed star, which the star solution alone would have pinned, is displaced by
            // exactly the drift. That is the trailing the comet layer accepts.
            var starInFrame = new Vector2(400f, 900f);
            var starOnStarGrid = Vector2.Transform(starInFrame, starSolution);
            ShouldBeCloseTo(Vector2.Transform(starInFrame, compose), starOnStarGrid - rate * (float)dtHours, 1e-2f);
        }

        [Fact]
        public void ReversingTheOperandOrderIsWrongWheneverTheFrameIsRotated()
        {
            // The check above would pass for either order at zero rotation, so this is the case that
            // separates them: a translation composed BEFORE a rotation gets rotated with the frame.
            var starSolution = Matrix3x2.CreateRotation(0.35f) * Matrix3x2.CreateTranslation(88.6f, -41.2f);
            var rate = new Vector2(-241.7f, -41.4f);
            const double dtHours = 0.75;
            var correct = CometCompose.ToCometGrid(starSolution, rate, dtHours);
            var reversed = Matrix3x2.CreateTranslation(-rate.X * (float)dtHours, -rate.Y * (float)dtHours) * starSolution;

            var probe = new Vector2(1000f, 1000f);
            var gap = Vector2.Distance(Vector2.Transform(probe, correct), Vector2.Transform(probe, reversed));
            gap.ShouldBeGreaterThan(10f);
        }

        [Fact]
        public void DriftHoursIsThisFrameMinusTheReference()
        {
            var reference = MetaAt(RefStart);
            CometCompose.DriftHours(MetaAt(RefStart.AddMinutes(90)), reference).ShouldBe(1.5, 1e-9);
            CometCompose.DriftHours(MetaAt(RefStart.AddMinutes(-30)), reference).ShouldBe(-0.5, 1e-9);
            CometCompose.DriftHours(reference, reference).ShouldBe(0.0);
        }

        [Fact]
        public void TheBodyIsEvaluatedAtTheReferenceMidExposureNotItsStart()
        {
            // Anchor an hour before the reference frame; a 30 s exposure puts the mid-exposure a
            // further 15 s along. At 245 px/h that is a whole pixel, and the model is sampled
            // bilinearly precisely because half a pixel subtracts a dipole.
            var fit = new CometRate(
                PxPerHour: new Vector2(-241.7f, -41.4f),
                MaxResidualPx: 0.1,
                SampleCount: 13,
                AnchorPx: new Vector2(1485.5f, 1506.9f),
                AnchorEpoch: RefStart.AddHours(-1));
            var reference = MetaAt(RefStart, exposureSeconds: 30);

            var expectedHours = 1.0 + 15.0 / 3600.0;
            var expected = fit.AnchorPx + fit.PxPerHour * (float)expectedHours;
            ShouldBeCloseTo(CometCompose.BodyOnGrid(fit, reference), expected, 1e-3f);

            // And it is NOT the start-of-exposure answer.
            var atStart = fit.AnchorPx + fit.PxPerHour * 1.0f;
            Vector2.Distance(CometCompose.BodyOnGrid(fit, reference), atStart).ShouldBeGreaterThan(0.9f);
        }
    }
}
