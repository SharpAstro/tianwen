using System;
using System.Collections.Generic;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the separation mechanism of <see cref="BadPixelAccumulator"/> (task #22): a defect is
    /// fixed in SENSOR space and so persists as an outlier at the same pixel across frames, while a
    /// star is fixed in SKY space and, under dither, contaminates any one sensor pixel in only a
    /// minority of frames. The "star" in these tests is a single bright pixel that moves with the
    /// dither -- the sharpest possible stand-in, since a broad PSF only makes the star EASIER to
    /// tell apart (it is an outlier at each position for the same minority of frames).
    /// </summary>
    [Collection("Imaging")]
    public class BadPixelAccumulatorTests
    {
        private const int Size = 128;
        private const int FrameCount = 12;

        /// <summary>Integer dither offsets whose RMS radial spread (~4 px) clears
        /// <see cref="BadPixelAccumulator.MinTranslationSpreadPx"/>, asserted below rather than
        /// assumed.</summary>
        private static readonly (int DX, int DY)[] Dither =
        [
            (0, 0), (4, 2), (-3, 5), (5, -4), (-5, -2), (2, 5),
            (-4, 3), (3, -5), (-2, -4), (5, 3), (-5, 4), (4, -2),
        ];

        private static Image MonoFrame(int seed, Action<float[,]>? mutate = null)
        {
            var arr = new float[Size, Size];
            var rng = new Random(seed);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    arr[y, x] = 100f + (float)(rng.NextDouble() * 10.0 - 5.0);
                }
            }
            mutate?.Invoke(arr);
            var meta = new ImageMeta
            {
                Instrument = "synth",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, seed % 60, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(60),
                FrameType = FrameType.Light,
                SensorType = SensorType.Monochrome,
            };
            return new Image([arr], BitDepth.Float32, maxValue: 8192f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        /// <summary>RGGB mosaic with strongly separated per-subplane levels (R 1000 / G 500 / B
        /// 100), so a cross-subplane neighbourhood would make EVERY pixel an outlier. This is the
        /// input that tells step-2 (same-colour) neighbours apart from step-1.</summary>
        private static Image RggbFrame(int seed, Action<float[,]>? mutate = null)
        {
            var arr = new float[Size, Size];
            var rng = new Random(seed);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var level = (y % 2, x % 2) switch
                    {
                        (0, 0) => 1000f,
                        (1, 1) => 100f,
                        _ => 500f,
                    };
                    arr[y, x] = level + (float)(rng.NextDouble() * 10.0 - 5.0);
                }
            }
            mutate?.Invoke(arr);
            var meta = new ImageMeta
            {
                Instrument = "synth",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, seed % 60, TimeSpan.Zero),
                ExposureDuration = TimeSpan.FromSeconds(60),
                FrameType = FrameType.Light,
                SensorType = SensorType.RGGB,
            };
            return new Image([arr], BitDepth.Float32, maxValue: 8192f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        private static List<Matrix3x2> DitherTransforms()
        {
            var transforms = new List<Matrix3x2>(Dither.Length);
            foreach (var (dx, dy) in Dither)
            {
                transforms.Add(Matrix3x2.CreateTranslation(dx, dy));
            }
            return transforms;
        }

        private static long CountFlagged(BitMatrix mask)
        {
            var flagged = 0L;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (mask[y, x])
                    {
                        flagged++;
                    }
                }
            }
            return flagged;
        }

        [Fact]
        public void AFixedDefectIsFlagged_AndADitheredStarIsNot()
        {
            var acc = new BadPixelAccumulator();
            for (var i = 0; i < FrameCount; i++)
            {
                var (dx, dy) = Dither[i];
                acc.Accumulate(MonoFrame(100 + i, arr =>
                {
                    arr[40, 40] += 5000f;             // defect: fixed in sensor space
                    arr[80 + dy, 80 + dx] += 3000f;   // star: moves with the dither
                }));
            }
            acc.FramesAccumulated.ShouldBe(FrameCount);

            var transforms = DitherTransforms();
            BadPixelAccumulator.TranslationSpreadPx(transforms)
                .ShouldBeGreaterThanOrEqualTo(BadPixelAccumulator.MinTranslationSpreadPx,
                    "the fixture's dither must clear the gate, or every assertion below is vacuous");

            var mask = acc.BuildMask(transforms);
            mask.ShouldNotBeNull();
            mask[0][40, 40].ShouldBeTrue("the pixel that is an outlier in every frame is a defect");
            foreach (var (dx, dy) in Dither)
            {
                mask[0][80 + dy, 80 + dx].ShouldBeFalse(
                    $"star position ({80 + dx},{80 + dy}) is an outlier in only 1 of {FrameCount} frames");
            }
            CountFlagged(mask[0]).ShouldBe(1, "noise cannot persist at one pixel across 80% of frames");
        }

        [Fact]
        public void AnUnmovedSessionRefusesTheMask()
        {
            // The same star, but the session never moved -- the star is now indistinguishable from
            // a defect by persistence, which is exactly why BuildMask must refuse rather than guess.
            var acc = new BadPixelAccumulator();
            var transforms = new List<Matrix3x2>();
            for (var i = 0; i < FrameCount; i++)
            {
                acc.Accumulate(MonoFrame(200 + i, arr => arr[80, 80] += 3000f));
                transforms.Add(Matrix3x2.Identity);
            }
            acc.BuildMask(transforms).ShouldBeNull();
        }

        [Fact]
        public void TooFewFramesRefuseTheMask()
        {
            var acc = new BadPixelAccumulator();
            var transforms = new List<Matrix3x2>();
            for (var i = 0; i < BadPixelAccumulator.MinFramesForMask - 1; i++)
            {
                var (dx, dy) = Dither[i];
                acc.Accumulate(MonoFrame(300 + i, arr => arr[40, 40] += 5000f));
                transforms.Add(Matrix3x2.CreateTranslation(dx, dy));
            }
            acc.BuildMask(transforms).ShouldBeNull();
        }

        [Fact]
        public void ARunawayFlaggedFractionRefusesTheMask()
        {
            // ~2% of pixels persistently hot is past any plausible defect population
            // (BadPixelDetection.DefaultMaxMaskedFraction = 1%); the accumulator must refuse
            // rather than hand drizzle a mask that eats real signal.
            var acc = new BadPixelAccumulator();
            for (var i = 0; i < FrameCount; i++)
            {
                acc.Accumulate(MonoFrame(400 + i, arr =>
                {
                    for (var y = 0; y < Size; y++)
                    {
                        for (var x = 0; x < Size; x++)
                        {
                            if ((y * Size + x) % 47 == 0)
                            {
                                arr[y, x] += 5000f;
                            }
                        }
                    }
                }));
            }
            acc.BuildMask(DitherTransforms()).ShouldBeNull();
        }

        [Fact]
        public void UnionIntoOrsTheOtherMaskInPlace()
        {
            var target = new BitMatrix(4, 4);
            var other = new BitMatrix(4, 4);
            target[0, 0] = true;
            target[2, 3] = true;
            other[2, 3] = true;
            other[3, 1] = true;

            BadPixelAccumulator.UnionInto(target, other, width: 4, height: 4);

            target[0, 0].ShouldBeTrue("target-only bits survive");
            target[2, 3].ShouldBeTrue("shared bits survive");
            target[3, 1].ShouldBeTrue("other-only bits are adopted");
            target[1, 2].ShouldBeFalse("bits in neither stay clear");
        }

        [Fact]
        public void CfaNeighboursComeFromTheSameSubplane()
        {
            // The RGGB fixture's subplane levels differ by hundreds of ADU while the noise MAD is
            // ~2.5, so a step-1 (cross-colour) neighbourhood would flag essentially every pixel and
            // trip the runaway guard into refusing. A non-null mask with exactly the injected
            // defect flagged is therefore proof the neighbourhood is same-subplane (step 2).
            var acc = new BadPixelAccumulator();
            for (var i = 0; i < FrameCount; i++)
            {
                acc.Accumulate(RggbFrame(500 + i, arr => arr[40, 41] += 5000f)); // a G-site defect
            }
            var mask = acc.BuildMask(DitherTransforms());
            mask.ShouldNotBeNull("a cross-subplane neighbourhood would have refused via the runaway guard");
            mask[0][40, 41].ShouldBeTrue();
            CountFlagged(mask[0]).ShouldBe(1);
        }
    }
}
