using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="CometMask"/>, the per-frame exclusion that lets one comet run also emit a
    /// clean STAR layer. The interesting failures here are all silent ones: a mask in the wrong basis
    /// still produces a master, and it looks entirely plausible while containing the comet it was
    /// built to remove.
    /// </summary>
    public class CometMaskTests
    {
        private static readonly DateTimeOffset Epoch = new(2025, 10, 18, 10, 15, 30, TimeSpan.Zero);

        /// <summary>C/2025 R2's measured geometry: 245 px/h along the track, 357 px over the session.</summary>
        private static CometMask Swan(float radiusPx = 80f) => new(
            AnchorRefPx: new Vector2(1000f, 1200f),
            RatePxPerHour: new Vector2(240.4f, 48.1f),
            AnchorEpoch: Epoch,
            RadiusPx: radiusPx);

        private static Image Flat(int width, int height, int channels = 1)
        {
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                planes[c] = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        planes[c][y, x] = 1f;
                    }
                }
            }
            var meta = new ImageMeta
            {
                Instrument = "synth",
                ExposureStartTime = Epoch,
                ExposureDuration = TimeSpan.FromSeconds(30),
                FrameType = FrameType.Light,
                SensorType = SensorType.Monochrome,
            };
            return new Image(planes, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        private static int CountNaN(Image img, int channel = 0)
        {
            var n = 0;
            for (var y = 0; y < img.Height; y++)
            {
                for (var x = 0; x < img.Width; x++)
                {
                    if (float.IsNaN(img[channel, y, x])) { n++; }
                }
            }
            return n;
        }

        [Fact]
        public void TheBodyMovesAtTheStatedRateAndIsAtTheAnchorWhenDtIsZero()
        {
            var mask = Swan();
            mask.ReferencePositionAt(Epoch).ShouldBe(new Vector2(1000f, 1200f));

            // The session span: 1.4581 h. 245.2 px/h over it is the 357 px measured two ways (the
            // ephemeris fit, and the star-trail length on the comet-aligned master).
            var end = mask.ReferencePositionAt(Epoch + TimeSpan.FromHours(1.4581));
            (end - mask.AnchorRefPx).Length().ShouldBe(357f, 2f);
        }

        [Fact]
        public void PunchBlanksADiscOfTheRightAreaOnEveryChannel()
        {
            var img = Flat(400, 400, channels: 3);
            var blanked = CometMask.Punch(img, new Vector2(200f, 200f), 50f);

            // pi r^2 = 7854; a rasterised disc lands within a percent or so of it.
            blanked.ShouldBeInRange(7754, 7954);
            CountNaN(img, 0).ShouldBe(blanked);
            CountNaN(img, 2).ShouldBe(blanked);
            float.IsNaN(img[0, 200, 200]).ShouldBeTrue();
            float.IsNaN(img[0, 200, 260]).ShouldBeFalse();     // just outside r=50
        }

        [Fact]
        public void ADiscThatMissesTheFrameBlanksNothingRatherThanThrowing()
        {
            // This is the signature the pipeline watches for. An anchor computed in the wrong basis
            // lands off the sensor, and the star layer is then simply an unmasked one -- a plausible
            // master containing the comet it exists to exclude. Nothing about the pixels says so, so
            // the count has to.
            var img = Flat(200, 200);
            CometMask.Punch(img, new Vector2(-500f, -500f), 80f).ShouldBe(0);
            CountNaN(img).ShouldBe(0);
        }

        [Fact]
        public void APartlyOffFrameDiscBlanksOnlyThePartOnTheFrame()
        {
            var img = Flat(200, 200);
            // Centred on the corner: a quarter of the disc is on the frame.
            var blanked = CometMask.Punch(img, new Vector2(0f, 0f), 40f);
            blanked.ShouldBeInRange(1197, 1317);   // pi r^2 / 4 = 1257
            float.IsNaN(img[0, 0, 0]).ShouldBeTrue();
        }

        [Fact]
        public void ANegativeOrNonFiniteCentreIsRefusedRatherThanRasterised()
        {
            var img = Flat(100, 100);
            CometMask.Punch(img, new Vector2(float.NaN, 50f), 20f).ShouldBe(0);
            CometMask.Punch(img, new Vector2(50f, 50f), 0f).ShouldBe(0);
            CountNaN(img).ShouldBe(0);
        }

        [Fact]
        public void TheSourcePositionInvertsTheFramesOwnRegistrationSolution()
        {
            var mask = Swan();
            var when = Epoch + TimeSpan.FromHours(1.0);
            var expectedRef = mask.ReferencePositionAt(when);

            // A frame whose stars sit 30 px right and 12 px down of the reference's: its own pixels
            // put the body 30/12 the other way.
            var solution = Matrix3x2.CreateTranslation(30f, 12f);
            var src = mask.SourcePositionAt(solution, when);
            src.ShouldNotBeNull();
            src.Value.X.ShouldBe(expectedRef.X - 30f, 1e-3f);
            src.Value.Y.ShouldBe(expectedRef.Y - 12f, 1e-3f);
        }

        [Fact]
        public void TheSourcePositionSurvivesRotationAndScale()
        {
            var mask = Swan();
            var when = Epoch + TimeSpan.FromHours(0.75);
            var solution = Matrix3x2.CreateRotation(0.21f) * Matrix3x2.CreateScale(1.03f)
                * Matrix3x2.CreateTranslation(-17f, 44f);

            var src = mask.SourcePositionAt(solution, when);
            src.ShouldNotBeNull();
            // Round-tripping forward through the same solution must land back on the reference
            // position: this is the whole contract, and an inverted composition order would break it
            // while still producing a perfectly finite, wrong answer.
            var back = Vector2.Transform(src.Value, solution);
            var expected = mask.ReferencePositionAt(when);
            (back - expected).Length().ShouldBeLessThan(1e-2f);
        }

        [Fact]
        public void ASingularSolutionAnswersNullRatherThanInfinity()
        {
            var mask = Swan();
            mask.SourcePositionAt(new Matrix3x2(0f, 0f, 0f, 0f, 0f, 0f), Epoch).ShouldBeNull();
        }

        [Fact]
        public void TheRadiusIsRestatedInTheFramesOwnPixels()
        {
            var mask = Swan(radiusPx: 80f);
            // Registration scale sits within 0.028% of unity on real data, so this is nearly a no-op
            // there. It exists for the case where it is not -- a resampled or binned input -- because
            // a mask silently too small leaves exactly the residue it was added to remove.
            mask.SourceRadius(Matrix3x2.Identity).ShouldBe(80f, 1e-3f);
            mask.SourceRadius(Matrix3x2.CreateScale(2f)).ShouldBe(40f, 1e-3f);
            mask.SourceRadius(Matrix3x2.CreateScale(0.5f)).ShouldBe(160f, 1e-3f);
            // A pure rotation is not a scale change.
            mask.SourceRadius(Matrix3x2.CreateRotation(1.1f)).ShouldBe(80f, 1e-3f);
        }

        [Fact]
        public void AcrossTheSessionTheMaskSweepsTheWholeTrackAndNothingElse()
        {
            // The property the star layer actually depends on: every pixel ON the track is excluded
            // at some point, and pixels well off it never are -- which is what keeps the cost to a
            // narrow band rather than the whole frame.
            var mask = Swan(radiusPx: 80f);
            var img = Flat(2000, 2000);
            var everMasked = new bool[2000, 2000];
            for (var i = 0; i < 89; i++)
            {
                var when = Epoch + TimeSpan.FromHours(1.4581 * i / 88.0);
                var p = mask.ReferencePositionAt(when);
                var frame = Flat(2000, 2000);
                CometMask.Punch(frame, p, 80f);
                for (var y = 0; y < 2000; y++)
                {
                    for (var x = 0; x < 2000; x++)
                    {
                        if (float.IsNaN(frame[0, y, x])) { everMasked[y, x] = true; }
                    }
                }
            }

            var start = mask.ReferencePositionAt(Epoch);
            var end = mask.ReferencePositionAt(Epoch + TimeSpan.FromHours(1.4581));
            var mid = (start + end) * 0.5f;
            everMasked[(int)start.Y, (int)start.X].ShouldBeTrue();
            everMasked[(int)mid.Y, (int)mid.X].ShouldBeTrue();
            everMasked[(int)end.Y, (int)end.X].ShouldBeTrue();

            // 300 px off the track, perpendicular to it, is never touched.
            var dir = Vector2.Normalize(end - start);
            var perp = new Vector2(-dir.Y, dir.X) * 300f;
            var off = mid + perp;
            everMasked[(int)off.Y, (int)off.X].ShouldBeFalse();

            // And the swept area stays a band: a 160 px-wide capsule 357 px long is ~77k px, far
            // short of the 4M-pixel frame. The point of the mask is that it is cheap.
            var swept = 0;
            for (var y = 0; y < 2000; y++)
            {
                for (var x = 0; x < 2000; x++)
                {
                    if (everMasked[y, x]) { swept++; }
                }
            }
            swept.ShouldBeLessThan(100_000);
            swept.ShouldBeGreaterThan(60_000);
            _ = img;
        }
    }
}
