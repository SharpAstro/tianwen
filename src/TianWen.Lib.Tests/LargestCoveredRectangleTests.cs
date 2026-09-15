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

        /// <summary>NaN is absence, on the same border-reachability terms as zero.</summary>
        [Fact]
        public void ANaNReachingTheBorderIsAbsent()
            => Frame([
                "nnnnn",
                "n###n",
                "nnnnn",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(1, 1, 3, 1));

        /// <summary>
        /// <b>An interior NaN is a DRIZZLE HOLE, not a canvas ring, and this used to shred the frame.</b>
        /// A drizzle canvas carries NaN wherever no drop's footprint reached, scattered through the
        /// interior rather than gathered at the edge, and NaN used to be absence anywhere while an
        /// interior zero was already exempt. Measured over the 79 masters of the 2026-09-12-clamped bake
        /// (issue #250): 53 of them carry interior holes, 19 to 324 components and 35 to 1,856 px, and on
        /// the Great Orion Nebula master 1,856 such pixels in 20 components took the answer to 0.528 of
        /// the canvas -- columns 10 to 1638 of 3024 -- on a frame that is 99.94 percent covered. The same
        /// frame keeps 0.981 once the holes stop counting.
        /// <para>The old rule reasoned that "NaN is unambiguous" and so needs no border test. It is
        /// unambiguous about the PIXEL being unusable and says nothing about WHY, which is the only
        /// question a largest rectangle is asking.</para>
        /// </summary>
        [Fact]
        public void AnInteriorNaNIsADrizzleHoleAndNotAbsence()
            => Frame([
                "#####",
                "##n##",
                "#####",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(0, 0, 5, 3));

        /// <summary>
        /// A real ring is RAGGED and mixes the two producers: a drizzle canvas leaves NaN where no drop
        /// reached and exact zero where the integration never accumulated, side by side along the same
        /// edge. Neither kind may anchor the flood alone, or half the ring survives.
        /// </summary>
        [Fact]
        public void ARingOfMixedNaNAndZeroIsDiscardedWhole()
            => Frame([
                "nn..nn",
                "n####.",
                ".####n",
                "..nn..",
            ]).LargestCoveredRectangle().ShouldBe(new Rectangle(1, 1, 4, 2));

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

        /// <summary>
        /// The other side of the same rule. Once the rectangle KEEPS an interior hole, something has to
        /// give the pixel a number, and it is the mean of the neighbours that were actually measured.
        /// </summary>
        [Fact]
        public void AnInteriorHoleIsFilledFromItsNeighbours()
        {
            var image = Frame([
                "#####",
                "##n##",
                "#####",
            ]);

            image.FillInteriorHolesInPlace().ShouldBe(1);

            var plane = image.GetChannelArray(0);
            float.IsNaN(plane[1, 2]).ShouldBeFalse("the hole is surrounded by data, so it can be interpolated");
            // Frame's covered value ramps with x, so the eight neighbours of (2, 1) average its own column.
            plane[1, 2].ShouldBe(0.25f + (0.01f * 2), 1e-5f);
        }

        /// <summary>
        /// <b>The ring is never filled, and that is what makes the fill safe.</b> No frame reached it, so
        /// a number there would be invented rather than interpolated, and it would erase the only
        /// evidence the crop has to work from: fill the ring and the next auto-crop keeps the whole
        /// canvas, ragged edge and all.
        /// </summary>
        [Fact]
        public void TheCanvasRingIsLeftAlone()
        {
            var image = Frame([
                "nnnnn",
                "n###n",
                "nnnnn",
            ]);

            image.FillInteriorHolesInPlace().ShouldBe(0);

            var plane = image.GetChannelArray(0);
            float.IsNaN(plane[0, 0]).ShouldBeTrue();
            float.IsNaN(plane[1, 0]).ShouldBeTrue();
            image.LargestCoveredRectangle().ShouldBe(new Rectangle(1, 1, 3, 1), "the crop still has its evidence");
        }

        /// <summary>
        /// A pixel is a hole when ANY channel is NaN, but only the channels that actually are get
        /// written: a plane that has a number keeps the one it has.
        /// </summary>
        [Fact]
        public void OnlyTheChannelThatIsNaNIsWritten()
        {
            var image = Frame(["#####", "#####", "#####"], channels: 3);
            image.GetChannelArray(1)[1, 2] = float.NaN;
            var greenBefore = image.GetChannelArray(2)[1, 2];

            image.FillInteriorHolesInPlace().ShouldBe(1, "one pixel-channel, not three");

            float.IsNaN(image.GetChannelArray(1)[1, 2]).ShouldBeFalse();
            image.GetChannelArray(2)[1, 2].ShouldBe(greenBefore, "a channel that had a number is not rewritten");
        }

        /// <summary>
        /// The common case by far, and the one that must cost nothing: a frame with no NaN anywhere is
        /// not walked, not flooded, and not written.
        /// </summary>
        [Fact]
        public void AFrameWithNoNaNIsNotTouched()
        {
            var image = Frame(["####", "####"]);
            var before = (float[,]) image.GetChannelArray(0).Clone();

            image.FillInteriorHolesInPlace().ShouldBe(0);

            image.GetChannelArray(0).ShouldBe(before);
        }

        /// <summary>
        /// A hole deeper than the pass budget keeps its core rather than acquiring a fabricated one. The
        /// fill closes a hole from its rim inward at one pixel per pass, so the budget bounds the RADIUS;
        /// leaving the middle NaN is the honest outcome, and the real distribution never reaches it
        /// (the largest component measured over 79 masters needs three passes).
        /// </summary>
        [Fact]
        public void AHoleDeeperThanTheBudgetKeepsItsCore()
        {
            var image = Frame([
                "#######",
                "#nnnnn#",
                "#nnnnn#",
                "#nnnnn#",
                "#######",
            ]);

            image.FillInteriorHolesInPlace(maxPasses: 1);

            var plane = image.GetChannelArray(0);
            float.IsNaN(plane[1, 1]).ShouldBeFalse("the rim touches data and fills on the first pass");
            float.IsNaN(plane[2, 3]).ShouldBeTrue("the core is two pixels in and the budget was one");
        }

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
