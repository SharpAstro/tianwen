using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>A staged integration says how many frames reached each pixel.</b>
/// <para>
/// Its rejection map is a FRACTION, which nothing reads and the exact crop tier cannot use, so every
/// consumer of a staged master fell to the coverage edge walk, which ESTIMATES the border and refuses
/// an edge whose band never settles. On the V1045 Ori master a dither strip 350 px wide and 2.7x the
/// noise ran down the left; the walk declined, the fallback trimmed 264 px, and the strip reached the
/// gallery. The count is taken in the one loop that already reads every sample.
/// </para>
/// </summary>
public class StreamingIntegratorCoverageTests
{
    private const int Size = 32;

    [Fact]
    public void CoverageCountsTheFramesWithAFiniteSampleOnEachPixel()
    {
        // Three frames whose left margins are absent to different depths, the way dithered subs
        // land on a union canvas: columns 0-7 are reached by one frame, 8-15 by two, 16+ by all three.
        var frames = new List<StagedAlignedFrame>
        {
            Staged(Frame(nanColumns: 0)),
            Staged(Frame(nanColumns: 8)),
            Staged(Frame(nanColumns: 16)),
        };
        try
        {
            var result = StreamingIntegrator.Integrate(frames, new IntegrationOptions(ApplyNormalization: false));

            result.RejectionMap.ShouldNotBeNull("the rejection map keeps its own meaning; coverage is beside it");
            var coverage = result.Coverage.ShouldNotBeNull("a staged integration reports coverage");
            coverage.Width.ShouldBe(Size);
            coverage.Height.ShouldBe(Size);
            coverage[0, Size / 2, 4].ShouldBe(1f);
            coverage[0, Size / 2, 12].ShouldBe(2f);
            coverage[0, Size / 2, 24].ShouldBe(3f);
            coverage.MaxValue.ShouldBe(3f, "labelled with the frame count, the most a pixel can reach");
        }
        finally
        {
            foreach (var f in frames)
            {
                f.Dispose();
            }
        }
    }

    private static StagedAlignedFrame Staged(Image image)
        => new(StreamingFrameReader.InMemoryOnly(image), image.ImageMeta, image.MaxValue, image.Pedestal, null, null);

    private static Image Frame(int nanColumns)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = x < nanColumns ? float.NaN : 0.25f;
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
