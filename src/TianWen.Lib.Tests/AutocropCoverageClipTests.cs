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
        for (var y = clipped.Top; y < clipped.Bottom; y++)
        {
            for (var x = clipped.Left; x < clipped.Right; x++)
            {
                coverage.GetChannelSpan(0)[(y * Size) + x].ShouldBeGreaterThan(0f, $"({x}, {y}) is inside the crop");
            }
        }
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
