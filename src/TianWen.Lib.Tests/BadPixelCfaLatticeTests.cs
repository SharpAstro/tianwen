using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A CFA dark is ONE channel carrying four interleaved populations that do not share a level, and
    /// a hot-pixel threshold measured from one of them and applied to another destroys a colour.
    ///
    /// <para>Measured, not hypothetical. On <c>master_dark_10s_21C_g121_ZWOASI294MC.fits</c> the four
    /// photosite colours sit at R 540, G 520, G 520, B 621 ADU -- a non-neutral in-camera white
    /// balance was left on, and a ZWO applies it as a digital gain on the raw stream, so it scales
    /// the PEDESTAL too (the 32 us bias beside it already reads B/G 1.2031, R/G 1.0469). The old
    /// <c>StatStride</c> was 8, an EVEN stride, so every sampled position was (even, even) and the
    /// sample WAS the red plane: median 540, MAD collapsed to 0, non-zero-tail fallback 4.0, giving a
    /// sigma-8 threshold of exactly 587.4432. Blue's own floor is 621, so 100.000% of the blue
    /// photosites were flagged hot, drizzle deposited nothing into that plane, and the master was
    /// written with an all-NaN blue channel while the session reported success.</para>
    ///
    /// <para>The fixture reproduces that shape at 512x512 rather than asserting on the archive.</para>
    /// </summary>
    public class BadPixelCfaLatticeTests
    {
        private const int Size = 512;
        private const int LatticePx = (Size / 2) * (Size / 2);

        /// <summary>The four measured floors, indexed by Bayer position (dy, dx) under RGGB.</summary>
        private static readonly float[,] Floor = { { 540f, 520f }, { 520f, 621f } };

        /// <summary>Genuine defects planted per Bayer position, unmistakable at any threshold.</summary>
        private const int HotPerLattice = 30;

        private const float HotValue = 5000f;

        /// <summary>
        /// A mosaic dark with the eta Carinae level pattern: each Bayer position on its own floor, each
        /// quantized two ways so the MAD collapses to 0 exactly as a real cooled-CMOS dark's does and
        /// the non-zero-tail fallback is the live path, and each carrying its own planted defects.
        /// </summary>
        private static Image MosaicDark(SensorType sensorType)
        {
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    // 60/40 split about the floor: the majority value is the median, so the median
                    // absolute deviation is 0 and the detector takes its non-zero-tail fallback.
                    var quantized = ((x / 2) + (y / 2) * 3) % 5 >= 3;
                    data[y, x] = Floor[y & 1, x & 1] + (quantized ? 4f : 0f);
                }
            }

            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    // Spread along a stride coprime with the sampling stride so no lattice's defects
                    // can systematically land on, or miss, the statistics sample.
                    for (var i = 0; i < HotPerLattice; i++)
                    {
                        var cell = 11 + i * 173;
                        data[dy + 2 * (cell / (Size / 2) % (Size / 2)), dx + 2 * (cell % (Size / 2))] = HotValue;
                    }
                }
            }

            var meta = new ImageMeta("synthetic", DateTime.UnixEpoch, TimeSpan.FromSeconds(10),
                FrameType.Dark, "", 4.63f, 4.63f, 121, 8, Filter.Unknown, 1, 1,
                21f, sensorType, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Float32, HotValue, 520f, 0f, meta);
        }

        /// <summary>Masked pixels belonging to one Bayer position.</summary>
        private static int CountIn(BitMatrix mask, int dy, int dx)
        {
            var n = 0;
            for (var y = dy; y < Size; y += 2)
            {
                for (var x = dx; x < Size; x += 2)
                {
                    if (mask[y, x]) n++;
                }
            }
            return n;
        }

        /// <summary>
        /// The regression itself: blue's photosites must not read as hot merely because red's floor is
        /// 81 ADU lower. Each position keeps its own noise scale, so each flags its own defects and
        /// nothing else.
        /// </summary>
        [Fact]
        public void NoBayerPositionIsFlaggedHotForSittingOnItsOwnFloor()
        {
            var mask = BadPixelDetection.BuildMaskFromDark(MosaicDark(SensorType.RGGB), 8f).ShouldNotBeNull().ShouldHaveSingleItem();

            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    var flagged = CountIn(mask, dy, dx);

                    // Before the fix this read 65,536 of 65,536 for the (1,1) position.
                    flagged.ShouldBeLessThan(LatticePx / 100,
                        $"Bayer position ({dy},{dx}) at floor {Floor[dy, dx]} flagged {flagged} of {LatticePx} px");

                    // And the mask still does its job: every planted defect in this position is found.
                    flagged.ShouldBeGreaterThanOrEqualTo(HotPerLattice,
                        $"Bayer position ({dy},{dx}) found {flagged} of its {HotPerLattice} planted defects");
                }
            }
        }

        /// <summary>
        /// The same statement made about one pixel, because a count can hide a wrong reason: a clean
        /// blue photosite, the highest floor in the frame and the population that was wiped, is not
        /// masked.
        /// </summary>
        [Fact]
        public void ACleanPhotositeOnTheHighestFloorIsNotMasked()
        {
            var dark = MosaicDark(SensorType.RGGB);
            var mask = BadPixelDetection.BuildMaskFromDark(dark, 8f).ShouldNotBeNull().ShouldHaveSingleItem();

            // (1,1) is blue under RGGB; pick a pixel the fixture left on its floor.
            var plane = dark.GetChannelArray(0);
            var found = false;
            for (var y = 1; y < Size && !found; y += 2)
            {
                for (var x = 1; x < Size; x += 2)
                {
                    if (plane[y, x] is 621f)
                    {
                        mask[y, x].ShouldBeFalse($"clean blue photosite at ({y},{x}) on floor 621 was masked");
                        found = true;
                        break;
                    }
                }
            }
            found.ShouldBeTrue("the fixture should contain at least one un-quantized, un-defective blue photosite");
        }

        /// <summary>
        /// The second half of the fix, which covers the case the dispatch cannot see: a mosaic whose
        /// pattern was never declared arrives as a plain channel, so the four populations ARE pooled.
        /// An odd sampling stride is what keeps the pooled estimate from collapsing onto one colour,
        /// and no colour is lost even though none gets its own scale.
        /// </summary>
        [Fact]
        public void AMosaicWhosePatternWasNeverDeclaredStillKeepsEveryColour()
        {
            var mask = BadPixelDetection.BuildMaskFromDark(MosaicDark(SensorType.Monochrome), 8f)
                .ShouldNotBeNull().ShouldHaveSingleItem();

            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    CountIn(mask, dy, dx).ShouldBeLessThan(LatticePx / 100,
                        $"Bayer position ({dy},{dx}) at floor {Floor[dy, dx]} lost to a pooled estimate");
                }
            }
        }

        /// <summary>
        /// The lattice arithmetic on an ODD frame, where the four positions no longer hold equal
        /// counts: a 2n+1 dimension gives the phase-0 lattice one more row or column than phase 1.
        /// Every fraction in the detector is measured against a lattice's OWN extent, so getting this
        /// wrong reads as a budget that differs between colours, and both of the real frames to hand
        /// (512x512 and the archive's 4144x2822) are even on both axes and could never show it.
        /// </summary>
        [Theory]
        [InlineData(255, 255)]
        [InlineData(255, 256)]
        [InlineData(256, 255)]
        public void AnOddSizedMosaicSplitsIntoFourUnequalLatticesAndKeepsThemAll(int h, int w)
        {
            var data = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    data[y, x] = Floor[y & 1, x & 1] + (((x / 2) + (y / 2) * 3) % 5 >= 3 ? 4f : 0f);
                }
            }
            // One unmistakable defect per position, so each lattice has something to find.
            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    data[dy + 2 * (h / 4), dx + 2 * (w / 4)] = HotValue;
                }
            }

            var meta = new ImageMeta("synthetic", DateTime.UnixEpoch, TimeSpan.FromSeconds(10),
                FrameType.Dark, "", 4.63f, 4.63f, 121, 8, Filter.Unknown, 1, 1,
                21f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            var mask = BadPixelDetection.BuildMaskFromDark(
                new Image([data], BitDepth.Float32, HotValue, 520f, 0f, meta), 8f)
                .ShouldNotBeNull().ShouldHaveSingleItem();

            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    var latticePx = ((h - dy + 1) / 2) * ((w - dx + 1) / 2);
                    var n = 0;
                    for (var y = dy; y < h; y += 2)
                    {
                        for (var x = dx; x < w; x += 2)
                        {
                            if (mask[y, x]) n++;
                        }
                    }
                    n.ShouldBeGreaterThan(0, $"({dy},{dx}) on a {h}x{w} frame found none of its defects");
                    n.ShouldBeLessThan(latticePx / 100, $"({dy},{dx}) on a {h}x{w} frame flagged {n} of {latticePx}");
                }
            }
        }

        /// <summary>
        /// The backstop. A threshold whose count is past the runaway guard is not a defect population,
        /// so the noise scale behind it is degenerate and nothing is masked -- rather than applying it,
        /// which is what happened at 25.17% of the eta Carinae sensor.
        ///
        /// <para>Distinct from the defect BUDGET, which deliberately keeps an over-budget mask: that is
        /// a caller asking for more defects than usual within the same distribution, this is an
        /// estimate that has lost the distribution.</para>
        /// </summary>
        [Fact]
        public void AThresholdThatFlagsFarMoreThanAnyDefectPopulationMasksNothing()
        {
            var data = new float[Size, Size];
            var total = Size * Size;
            for (var i = 0; i < total; i++)
            {
                var y = i / Size;
                var x = i % Size;
                // 70% floor / 25% one quantization step up / 5% far above: the step sets a tiny
                // non-zero-tail MAD, and the 5% then sits above the threshold that MAD produces.
                data[y, x] = (i % 20) switch
                {
                    < 14 => 100f,
                    < 19 => 102f,
                    _ => 200f,
                };
            }

            var meta = new ImageMeta("synthetic", DateTime.UnixEpoch, TimeSpan.FromSeconds(120),
                FrameType.Dark, "", 3.76f, 3.76f, 130, -1, Filter.Unknown, 1, 1,
                -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            var dark = new Image([data], BitDepth.Float32, 200f, 100f, 0f, meta);

            BadPixelDetection.CountMaskedPixels(BadPixelDetection.BuildMaskFromDark(dark, 8f), Size, Size)
                .ShouldBe(0);

            // The guard is the caller's to relax: with it disabled the same threshold flags the 5%.
            BadPixelDetection.CountMaskedPixels(
                    BadPixelDetection.BuildMaskFromDark(dark, 8f, maxMaskedFraction: 0f), Size, Size)
                .ShouldBeGreaterThan(total / 100);
        }
    }
}
