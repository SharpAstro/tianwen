using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <see cref="Image.ScanBackgroundRegion"/> ranks a grid of patches and keeps the darkest one,
    /// scanning row strips in parallel. The reduction used to be a lock around a strict
    /// <c>&lt;</c> compare, so on an exact luma tie the winner was whichever thread arrived first and
    /// the chosen patch depended on scheduling. That patch feeds background neutralisation, so the
    /// gains were not reproducible on any frame flat enough to produce a tie. The reduction is now a
    /// serial pass over one slot per strip, which gives a tie to the lowest strip index.
    /// </summary>
    public sealed class ScanBackgroundRegionTests
    {
        private const int SquareSize = 32;
        private const int Width = 1000;
        private const int Height = 600;

        // The scan steps by squareSize * 4 and insets a 5% border, so on this geometry the grid
        // starts at (50, 30) and steps 128 px: strip 0 is y=30, strip 1 is y=158, strip 2 is y=286.
        private const int GridX = 50;
        private const int GridStep = SquareSize * 4;
        private const int StripZeroY = 30;
        private const int StripOneY = StripZeroY + GridStep;
        private const int StripTwoY = StripOneY + GridStep;

        private const float Elsewhere = 0.9f;

        [Fact]
        public void ScanBackgroundRegion_GivesAnExactTie_ToTheLowestStrip()
        {
            var data = BuildTiedPatches();

            // Guard the test's own premise: the two patches must be an EXACT tie in the mean, or this
            // asserts nothing about the tie-break. Mirrors AverageRegionChannel's double accumulation.
            MeanOf(data, GridX, StripZeroY).ShouldBe(MeanOf(data, GridX, StripOneY));

            var image = ToMonoImage(data);

            // Patch A (strip 0) medians to 0.25 because it is uniform; patch B (strip 1) medians to
            // 0.375. Both mean exactly 0.25, so which value comes back is decided purely by the
            // reduction's tie-break, and it must be the same one every time.
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var (perChannel, luma) = image.ScanBackgroundRegion([0f], SquareSize);

                perChannel[0].ShouldBe(0.25f, $"attempt {attempt} resolved the tie to a different patch");
                luma.ShouldBe(0.25f);
            }
        }

        [Fact]
        public void TheTwoTiedPatches_ReportDifferentValues_SoTheTieTestCannotPassVacuously()
        {
            // Without this, a tie-break assertion would still pass if both patches happened to median
            // to the same value, proving nothing. Present each tied patch on its own and check they
            // are distinguishable through the public API: patch A gives 0.25, patch B gives 0.375.
            var onlyA = Uniform(Elsewhere);
            FillPatch(onlyA, GridX, StripZeroY, _ => 0.25f);
            ToMonoImage(onlyA).ScanBackgroundRegion([0f], SquareSize).PerChannel[0].ShouldBe(0.25f);

            var onlyB = Uniform(Elsewhere);
            FillPatch(onlyB, GridX, StripOneY, row => row < SquareSize / 2 ? 0.125f : 0.375f);
            ToMonoImage(onlyB).ScanBackgroundRegion([0f], SquareSize).PerChannel[0].ShouldBe(0.375f);
        }

        [Fact]
        public void ScanBackgroundRegion_StillPicksTheGloballyDarkestPatch_AcrossStrips()
        {
            // Behaviour the parallel-scan refactor must preserve: the darkest patch wins even when it
            // is neither in the first strip nor the first column of its own strip.
            var data = Uniform(Elsewhere);
            FillPatch(data, GridX, StripOneY, _ => 0.5f);
            FillPatch(data, GridX + GridStep, StripTwoY, _ => 0.2f);

            var (perChannel, _) = ToMonoImage(data).ScanBackgroundRegion([0f], SquareSize);

            perChannel[0].ShouldBe(0.2f);
        }

        [Fact]
        public void ScanBackgroundRegion_SubtractsThePedestal()
        {
            var data = Uniform(Elsewhere);
            FillPatch(data, GridX, StripOneY, _ => 0.25f);

            var (perChannel, _) = ToMonoImage(data).ScanBackgroundRegion([0.05f], SquareSize);

            perChannel[0].ShouldBe(0.2f, tolerance: 1e-6f);
        }

        /// <summary>
        /// Patch A is uniform 0.25. Patch B is half 0.125 and half 0.375, which means exactly 0.25:
        /// every partial sum is a multiple of 0.125 and stays well under 2^24, so the accumulation is
        /// exact in double and the tie is bit-for-bit rather than approximate. Their medians differ
        /// because <c>MedianRegion</c> returns <c>span[count / 2]</c>, the upper middle of the 1024
        /// samples, which lands in patch B's brighter half.
        /// </summary>
        private static float[,] BuildTiedPatches()
        {
            var data = Uniform(Elsewhere);
            FillPatch(data, GridX, StripZeroY, _ => 0.25f);
            FillPatch(data, GridX, StripOneY, row => row < SquareSize / 2 ? 0.125f : 0.375f);
            return data;
        }

        private static float[,] Uniform(float value)
        {
            var data = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    data[y, x] = value;
                }
            }

            return data;
        }

        private static void FillPatch(float[,] data, int x0, int y0, Func<int, float> byRow)
        {
            for (var row = 0; row < SquareSize; row++)
            {
                var value = byRow(row);
                for (var col = 0; col < SquareSize; col++)
                {
                    data[y0 + row, x0 + col] = value;
                }
            }
        }

        private static float MeanOf(float[,] data, int x0, int y0)
        {
            double sum = 0;
            var count = 0;
            for (var y = y0; y < y0 + SquareSize; y++)
            {
                for (var x = x0; x < x0 + SquareSize; x++)
                {
                    sum += data[y, x];
                    count++;
                }
            }

            return (float)(sum / count);
        }

        private static Image ToMonoImage(float[,] data)
        {
            // Monochrome and single-channel, so ScanBackgroundRegion takes the MedianRegion path
            // rather than the Bayer-demosaic one, and the luma is just channel 0.
            var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 100, -1, Filter.Luminance, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

            return new Image([data], BitDepth.Float32, 1f, 0f, 0, meta);
        }
    }
}
