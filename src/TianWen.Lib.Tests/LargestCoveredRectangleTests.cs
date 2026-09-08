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
    }
}
