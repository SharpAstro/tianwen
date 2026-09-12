using System;
using System.Drawing;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The rectangle a viewer auto-crop keeps: the largest area holding no pixel that no frame covered.
    /// See docs/plans/viewer-prerelease-fixes.md P25.
    /// </summary>
    /// <remarks>
    /// The shapes here are the ones a real master produces rather than tidy ones. A stacked canvas ring is
    /// RAGGED, because each frame lands at its own sub-pixel offset and rotation, so the absent pixels
    /// reach every edge and a bounding box of the covered pixels answers "the whole frame". That is the
    /// case the last two tests are about, and it is why this is a largest-rectangle scan.
    /// </remarks>
    public class LargestCoveredRectangleTests
    {
        private static Image Frame(string[] rows, int channels = 1)
        {
            var h = rows.Length;
            var w = rows[0].Length;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        plane[y, x] = rows[y][x] switch
                        {
                            '.' => 0f,                   // absent: zero in every channel
                            'n' => float.NaN,            // absent: NaN
                            _ => 0.25f + (0.01f * x),    // covered
                        };
                    }
                }

                planes[c] = plane;
            }

            return new Image([.. planes], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        [Fact]
        public void AFrameWithNothingToDiscardKeepsItself()
            => Frame(["####", "####", "####"]).LargestCoveredRectangle()
                .ShouldBe(new Rectangle(0, 0, 4, 3));

        [Fact]
        public void APlainBorderIsDiscarded()
            => Frame([
                "......",
                ".####.",
                ".####.",
                "......",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(1, 1, 4, 2));

        /// <summary>NaN is absence too, and it does not have to be at an edge.</summary>
        [Fact]
        public void ANaNIsAbsent()
            => Frame([
                "#####",
                "##n##",
                "#####",
            ]).LargestCoveredRectangle().Contains(new Point(2, 1)).ShouldBeFalse();

        /// <summary>
        /// A zero surrounded by data is a pixel some calibration clipped, not a canvas ring, and unlike
        /// NaN it cannot say so by itself: 0.0 is a legal value. A largest RECTANGLE is merciless about
        /// the difference, which is why this is pinned rather than left to the reader. On the frame that
        /// reported it, 6230 exact zeros (0.0102% of the pixels, 6108 of them interior) took a
        /// 9576 x 6388 sub to 2922 x 949.
        /// </summary>
        [Fact]
        public void AnInteriorZeroIsADeadPixelAndNotAbsence()
            => Frame([
                "#####",
                "##.##",
                "#####",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(0, 0, 5, 3));

        /// <summary>
        /// Both halves of the rule at once: the ring still goes, and the zero island inside it still
        /// stays, so neither is expressed at the other's expense.
        /// </summary>
        [Fact]
        public void ARingIsDiscardedWhileTheZeroIslandInsideItIsKept()
            => Frame([
                ".......",
                ".#####.",
                ".##.##.",
                ".#####.",
                ".......",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(1, 1, 5, 3));

        /// <summary>
        /// The shape that makes this a largest-rectangle problem: absent pixels touch every edge, so the
        /// covered pixels' bounding box is the whole 6 x 5 frame while the answer is the 4 x 3 interior.
        /// </summary>
        [Fact]
        public void ARaggedRingIsNotABoundingBox()
        {
            var image = Frame([
                "..#...",
                ".####.",
                "######",
                ".####.",
                "...#..",
            ]);

            var rect = image.LargestCoveredRectangle();

            rect.ShouldBe(new Rectangle(1, 1, 4, 3));
            rect.Width.ShouldBeLessThan(image.Width, "a bounding box of the covered pixels would be the frame");
        }

        /// <summary>
        /// A wide rectangle and a tall one, where the larger AREA wins rather than the first one found.
        /// </summary>
        [Fact]
        public void TheLargestAreaWins()
            => Frame([
                "#########",
                "#########",
                "....#....",
                "....#....",
                "....#....",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(0, 0, 9, 2));

        /// <summary>Zero has to hold in EVERY channel: one black channel is not an uncovered pixel.</summary>
        [Fact]
        public void AZeroInOneChannelOfThreeIsNotAbsent()
        {
            var image = Frame(["###", "###"], channels: 3);
            var plane = image.GetChannelArray(1);
            plane[0, 1] = 0f;

            image.LargestCoveredRectangle().ShouldBe(new Rectangle(0, 0, 3, 2));
        }

        [Fact]
        public void AnEntirelyAbsentFrameKeepsNothing()
            => Frame(["...", "..."]).LargestCoveredRectangle().ShouldBe(Rectangle.Empty);

        /// <summary>A flat frame of the given size, so a coverage plane has something to be measured
        /// against; the pixel values do not matter to the coverage overload.</summary>
        private static Image Flat(int width, int height, int channels = 1)
            => Synthetic(width, height, channels, static (_, _, _) => 0.25f);

        private static Image Synthetic(int width, int height, int channels, Func<int, int, int, float> valueAt)
        {
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = valueAt(c, x, y);
                    }
                }
                planes[c] = plane;
            }

            return new Image([.. planes], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        /// <summary>The exact tier: a ramp in the weight map is cut at the requested fraction.</summary>
        [Fact]
        public void ACoveragePlaneCutsWhereTheWeightFallsOff()
        {
            var image = Flat(256, 128);
            // Full weight is 40; the left 32 px only ever saw a quarter of the frames.
            var coverage = Synthetic(256, 128, 1, static (_, x, _) => x < 32 ? 10f : 40f);

            var rect = image.LargestCoveredRectangle(coverage, minFraction: 0.95, blockSize: 16);

            rect.ShouldBe(new Rectangle(32, 0, 224, 128));
        }

        /// <summary>
        /// The regression for the block mean. A drizzle canvas gives neighbouring cells different drop
        /// counts, so a fully covered interior scatters about 10% either way -- 0.847 of the median at
        /// p0.1 on the 10P master. Comparing per PIXEL rejects pixels everywhere and collapses the
        /// answer (measured: 207 x 404 of a 4215 x 2884 frame); comparing per block keeps the frame.
        /// </summary>
        [Fact]
        public void PerPixelWeightScatterInsideAFullyCoveredFrameKeepsAllOfIt()
        {
            var rng = new Random(20260908);
            var image = Flat(256, 128);
            var coverage = Synthetic(256, 128, 1, (_, _, _) => (float)(40.0 * (0.85 + (0.3 * rng.NextDouble()))));

            var rect = image.LargestCoveredRectangle(coverage, minFraction: 0.95, blockSize: 16);

            rect.ShouldBe(new Rectangle(0, 0, 256, 128));
        }

        /// <summary>An unknown weight is not a full one.</summary>
        [Fact]
        public void ANaNWeightIsNotCoverage()
        {
            var image = Flat(256, 128);
            var coverage = Synthetic(256, 128, 1, static (_, x, y) => x < 16 && y < 16 ? float.NaN : 40f);

            var rect = image.LargestCoveredRectangle(coverage, minFraction: 0.95, blockSize: 16);

            rect.Contains(new Point(0, 0)).ShouldBeFalse();
            rect.Width.ShouldBe(240);
        }

        /// <summary>
        /// Every channel has to pass. A Bayer-drizzle canvas covers green twice as often as red, so the
        /// levels are per channel -- and a pixel under-covered in one channel of three renders as colour
        /// noise, which is worse than luminance noise.
        /// </summary>
        [Fact]
        public void TheWorstChannelDecides()
        {
            var image = Flat(256, 128, channels: 3);
            var coverage = Synthetic(256, 128, 3, static (c, x, _) => c switch
            {
                1 => x < 48 ? 20f : 80f,       // green: full is 80, its own falloff reaches 48 px
                _ => x < 16 ? 10f : 40f,       // red and blue: full is 40, falloff reaches 16 px
            });

            image.LargestCoveredRectangle(coverage, minFraction: 0.95, blockSize: 16)
                .ShouldBe(new Rectangle(48, 0, 208, 128));
        }

        /// <summary>One coverage channel is broadcast: a mono weight map for a colour master is legal.</summary>
        [Fact]
        public void ASingleCoverageChannelServesEveryImageChannel()
        {
            var image = Flat(256, 128, channels: 3);
            var coverage = Synthetic(256, 128, 1, static (_, x, _) => x < 32 ? 10f : 40f);

            image.LargestCoveredRectangle(coverage, minFraction: 0.95, blockSize: 16)
                .ShouldBe(new Rectangle(32, 0, 224, 128));
        }

        [Fact]
        public void ACoveragePlaneOfTheWrongShapeIsRefused()
        {
            var image = Flat(256, 128, channels: 3);

            Should.Throw<ArgumentException>(() => image.LargestCoveredRectangle(Flat(128, 128)));
            Should.Throw<ArgumentException>(() => image.LargestCoveredRectangle(Flat(256, 128, channels: 2)));
        }
    }
}
