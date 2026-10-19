using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// The weak vote: a photosite only a few sigma warm in every frame is never an outlier at the caller's sigma in
        /// any one of them, so the strong vote never counts it (here +12 ADU against a strong delta of about 8 * 1.48 *
        /// 2.5 = 30 ADU), yet it is the same pixel in every frame. A star as faint, moving with the dither, is not.
        /// Found on the SV605CC nights of 2026-09-12 and 2026-10-17, whose masters kept dither-shaped blotches.
        /// </summary>
        [Fact]
        public void AFaintlyWarmPixelInEveryFrameIsFlagged_AndAFaintDitheredStarIsNot()
        {
            // A session's length, the dither cycled: each star position holds 2 or 3 of the 30 frames.
            const int Frames = 30;
            var acc = new BadPixelAccumulator();
            for (var i = 0; i < Frames; i++)
            {
                var (dx, dy) = Dither[i % Dither.Length];
                acc.Accumulate(MonoFrame(300 + i, arr =>
                {
                    arr[40, 40] += 12f;               // warm: a few sigma, fixed in sensor space
                    arr[80 + dy, 80 + dx] += 12f;     // as faint a star, moving with the dither
                }));
            }

            var mask = acc.BuildMask(DitherTransforms()).ShouldNotBeNull();
            mask[0][40, 40].ShouldBeTrue("a pixel warm in every frame is a defect however faint");
            foreach (var (dx, dy) in Dither)
            {
                mask[0][80 + dy, 80 + dx].ShouldBeFalse($"the star at ({80 + dx}, {80 + dy}) sits there in 3 frames of {Frames} at most");
            }
            CountFlagged(mask[0]).ShouldBe(1, "noise alone never reaches the weak vote's count");
        }

        /// <summary>
        /// A faint STAR that barely moves sits on the same photosite in most frames, as on the SV605CC Tarantula of
        /// 2026-10-17 (3 px dithers, 5,000 stars), where the weak vote took such stars for defects. On a mosaic its
        /// same-colour neighbours, 2 px away, see little of a 2.4 px star; the photosites BESIDE its core, the other
        /// colours, see half of it, and that is what tells it from a lone warm photosite.
        /// </summary>
        [Fact]
        public void AFaintStarThatBarelyMovesIsNotADefect_ButALonePixelBesideItIs()
        {
            const int Frames = 30;
            var transforms = new List<Matrix3x2>(Frames);
            var acc = new BadPixelAccumulator();
            for (var i = 0; i < Frames; i++)
            {
                // Two frames in three at the same place, the rest dithered far: the session moved, the star mostly did not.
                var (dx, dy) = i % 3 == 2 ? Dither[1 + ((i / 3) % (Dither.Length - 1))] : (0, 0);
                transforms.Add(Matrix3x2.CreateTranslation(dx * 2.5f, dy * 2.5f));
                acc.Accumulate(RggbFrame(500 + i, arr =>
                {
                    var cx = 80.3f + (dx * 2.5f);
                    var cy = 60.4f + (dy * 2.5f);
                    for (var y = (int)cy - 4; y <= (int)cy + 4; y++)
                    {
                        for (var x = (int)cx - 4; x <= (int)cx + 4; x++)
                        {
                            var r2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                            arr[y, x] += 14f * MathF.Exp(-r2 / (2f * 1.02f * 1.02f));   // FWHM 2.4 px, as the Tarantula's
                        }
                    }
                    arr[40, 40] += 12f;   // a lone warm photosite, a few sigma
                }));
            }

            var mask = acc.BuildMask(transforms).ShouldNotBeNull();
            mask[0][40, 40].ShouldBeTrue("a lone photosite warm in every frame is a defect");
            for (var y = 56; y <= 65; y++)
            {
                for (var x = 76; x <= 85; x++)
                {
                    mask[0][y, x].ShouldBeFalse($"({x}, {y}) is the star's, which sits there in two frames of three");
                }
            }
        }

        /// <summary>Nights are runs of subs no gap over six hours separates: a pause for focus or cloud stays in its night,
        /// the next session is another (the SV605CC Sculptor of 2026-09-12 and 2026-10-17, stacked together).</summary>
        [Fact]
        public void NightsSplitOnlyAtALongGap()
        {
            var t0 = new DateTimeOffset(2026, 9, 12, 11, 19, 0, TimeSpan.Zero);
            DateTimeOffset[] starts =
            [
                t0, t0.AddMinutes(2), t0.AddHours(2), t0.AddHours(7),                 // one night, a two-hour pause in it
                t0.AddDays(35), t0.AddDays(35).AddMinutes(2),                           // the next session
            ];
            var nights = BadPixelAccumulator.Nights(starts, static s => s);
            starts.Select(s => nights[s]).ShouldBe([0, 0, 0, 0, 1, 1]);
        }

        [Theory]
        [InlineData(10, 10)]
        [InlineData(12, 11)]
        [InlineData(30, 18)]
        [InlineData(90, 54)]
        [InlineData(166, 100)]
        public void TheWeakVoteNeedsSixtyPercentOrMoreWhereNoiseCouldReachThat(int frames, int required)
            => BadPixelAccumulator.WeakRequiredCount(frames).ShouldBe(required);

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
