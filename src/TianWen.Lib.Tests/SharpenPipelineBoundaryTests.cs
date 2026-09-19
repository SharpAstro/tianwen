using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>An interior hole handed to the enhance takes its NEIGHBOURS' value, never the frame mean.</b>
/// <para>
/// The pipeline always made its input finite, and for as long as it did it wrote the per-channel
/// mean into every non-finite sample, on the reasoning that such samples sit in the border a crop
/// discards. A drizzle master's rejection voids are the opposite: dead centre on a saturated core,
/// different pixels per channel. The Great Orion master carries 1,240 / 493 / 769 NaN at the
/// Trapezium; where red was NaN and green was not, red got the sky (0.002) beside green's real
/// 0.27, and the enhanced view of that card, and only that view, carried a cyan-and-magenta speck
/// on the brightest thing in the picture. The display render and the viewer's document open both
/// fill from the neighbours; the enhance was the third implementation of the same rule and the
/// wrong one.
/// </para>
/// <para>
/// The plate is deliberately NOT flat: on a flat plate the neighbours' value and the frame mean
/// coincide, which is exactly why no earlier test could have failed.
/// </para>
/// </summary>
public class SharpenPipelineBoundaryTests
{
    private const int Size = 64;

    [Fact]
    public void AnInteriorHoleIsFilledFromItsNeighboursNotTheFrameMean()
    {
        var image = PlateWithABrightCore();
        // Red loses a pixel INSIDE the bright core; green and blue keep it, as rejection does when
        // one channel saturates first.
        image.GetChannelArray(0)[Size / 2, Size / 2] = float.NaN;

        var finite = SharpenPipeline.SanitiseForEnhance(image, logger: null);

        finite.ShouldNotBeSameAs(image);
        var filled = finite[0, Size / 2, Size / 2];
        float.IsFinite(filled).ShouldBeTrue();
        // The core is 0.9 and the frame mean is under 0.1; a fill from the neighbours lands on the
        // core, a fill from the mean lands on the sky and makes a red-black pixel beside real green.
        filled.ShouldBe(0.9f, tolerance: 0.05f, "the hole takes the value of the pixels around it");
        float.IsNaN(image[0, Size / 2, Size / 2]).ShouldBeTrue("the caller's image is untouched");
    }

    [Fact]
    public void ABorderNaNStillBecomesFiniteThroughTheMeanFallback()
    {
        var image = PlateWithABrightCore();
        // A NaN on the border is the canvas ring, which the interior fill leaves alone by design;
        // the models still cannot be handed it.
        image.GetChannelArray(1)[0, 0] = float.NaN;

        var finite = SharpenPipeline.SanitiseForEnhance(image, logger: null);

        float.IsFinite(finite[1, 0, 0]).ShouldBeTrue("a ring sample must still be finite for the models");
    }

    [Fact]
    public void ACleanPlateComesBackAsTheSameInstance()
    {
        var image = PlateWithABrightCore();
        SharpenPipeline.SanitiseForEnhance(image, logger: null).ShouldBeSameAs(image);
    }

    /// <summary>Three channels of faint sky with a bright 16 x 16 core in the middle, so a
    /// neighbourhood value and a frame mean are far apart.</summary>
    private static Image PlateWithABrightCore()
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var inCore = x >= Size / 2 - 8 && x < Size / 2 + 8 && y >= Size / 2 - 8 && y < Size / 2 + 8;
                    plane[y, x] = inCore ? 0.9f : 0.01f;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta());
    }
}
