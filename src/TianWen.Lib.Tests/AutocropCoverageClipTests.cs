using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <c>MasterPostProcessor.ClipToCoverage</c>: the stack's <c>_autocrop</c> is the frames' outline intersection
/// clipped to the master's own coverage plane. On the SV605CC Tarantula of 2026-10-17 the dark mask dropped the sensor's
/// warm corner from every sub, the subs moved only +-9 px, and the outline crop kept a 22 px wedge no frame reached.
/// </summary>
public class AutocropCoverageClipTests
{
    private const int Size = 256;
    private static readonly PixelRect Geometric = new(4, 4, Size - 8, Size - 8);
    private static readonly MasterPostProcessor Processor = new(NullLogger.Instance, catalogDb: null);

    [Fact]
    public void AHoleInsideTheOutlinesIsCroppedOut()
    {
        // A drizzle weight plane, as the hole arises there (a masked photosite is simply not deposited): cells
        // scatter about 10 % inside full coverage, and a corner wedge no frame reached sits inside the outline crop.
        var coverage = Plane(static (x, y) => x + y < 28 ? 0f : 40f * (0.9f + (0.2f * (((x * 7) + (y * 13)) % 17) / 16f)));

        var clipped = Processor.ClipToCoverage(Geometric, Plane(static (_, _) => 0.25f), coverage);

        clipped.ShouldNotBe(Geometric);
        Geometric.Contains(clipped).ShouldBeTrue("the coverage clip only ever shrinks the outline crop");
        ZerosIn(Geometric, coverage).ShouldBe(210, "the wedge is inside the outline crop");
        // The clip works on 8 px blocks, so at most the tip of one block's share of the wedge may stay.
        ZerosIn(clipped, coverage).ShouldBeLessThanOrEqualTo(10, $"the crop {clipped} keeps the wedge out");
    }

    private static int ZerosIn(PixelRect rect, Image coverage)
    {
        var zeros = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                zeros += coverage.GetChannelSpan(0)[(y * Size) + x] > 0f ? 0 : 1;
            }
        }
        return zeros;
    }

    /// <summary>
    /// The clip removes absence, not a few percent of noise. Clipped by the viewer's 0.95 rule the Tarantula, its corner
    /// no longer masked, lost 34 good columns to one 16 px block beside the rim at 0.927 of full weight.
    /// </summary>
    [Fact]
    public void AMildDeficitBesideTheRimCostsNothing()
    {
        var coverage = Plane(static (x, y) => x >= Size - 20 && y >= 96 && y < 112 ? 40f * 0.927f : 40f);

        Processor.ClipToCoverage(Geometric, Plane(static (_, _) => 0.25f), coverage).ShouldBe(Geometric);
    }

    [Fact]
    public void FullCoverageLeavesTheOutlineCropAlone()
        => Processor.ClipToCoverage(Geometric, Plane(static (_, _) => 0.25f), Plane(static (_, _) => 31f)).ShouldBe(Geometric);

    [Fact]
    public void NoCoveragePlaneLeavesTheOutlineCropAlone()
        => Processor.ClipToCoverage(Geometric, Plane(static (_, _) => 0.25f), coverage: null).ShouldBe(Geometric);

    private static Image Plane(System.Func<int, int, float> valueAt)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = valueAt(x, y);
            }
        }
        return new Image([plane], BitDepth.Float32, maxValue: 31f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }
}
