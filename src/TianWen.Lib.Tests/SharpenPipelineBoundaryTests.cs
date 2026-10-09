using System.Threading;
using System.Threading.Tasks;
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
    public void ABorderNaNInOneChannelBecomesTheZeroRingInEvery()
    {
        var image = PlateWithABrightCore();
        // A NaN on the border is the canvas ring, which the interior fill leaves alone by design; the
        // models still cannot be handed it. It used to get each channel's mean, which every step then read
        // as sky (#1399); it becomes the zero ring, in every channel, since a pixel with any channel NaN is
        // absent by the one rule the crop and the steps share.
        image.GetChannelArray(1)[0, 0] = float.NaN;

        var finite = SharpenPipeline.SanitiseForEnhance(image, logger: null);

        for (var c = 0; c < 3; c++)
        {
            finite[c, 0, 0].ShouldBe(0f, $"channel {c} of the ring pixel");
        }
        finite.AbsentPixels().ShouldNotBeNull()[0, 0].ShouldBeTrue("the ring is still absence after the sanitiser");
        finite[0, 1, 1].ShouldBe(0.01f, "a covered pixel beside it keeps its value");
    }

    /// <summary>
    /// <b>A NaN canvas ring reaches every step as the zero ring, and leaves the enhance as one</b> (#1399). The
    /// sanitiser used to write each channel's mean into it, which every step read as sky, so no step's own ring
    /// handling ever saw a ring. And a step may write into the ring (an RC-Astro product does not know it is there),
    /// so the pipeline puts it back after every step. The fake corrector adds to EVERY pixel, the ring included, as
    /// such a product would.
    /// </summary>
    [Fact]
    public async Task ANaNRingReachesTheStepsAsTheZeroRingAndLeavesAsOne()
    {
        const int Ring = 4;
        var image = PlateWithABrightCore();
        var ringPixels = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (InRing(y, x, Ring))
                {
                    ringPixels++;
                    for (var c = 0; c < 3; c++)
                    {
                        image.GetChannelArray(c)[y, x] = float.NaN;
                    }
                }
            }
        }
        var corrector = new AddsEverywhere(0.1f);
        var pipe = new SharpenPipeline(gradientCorrector: corrector);

        var result = await pipe.ProcessAsync(new SharpenRequest(image, [new GradientCorrectionStep()]), TestContext.Current.CancellationToken);

        corrector.RingItSaw.ShouldBe(ringPixels, "the step is handed the ring as absence, not as a band of sky");
        var final = result.Final.ShouldNotBeNull();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    if (InRing(y, x, Ring))
                    {
                        final[c, y, x].ShouldBe(0f, $"ring ({x}, {y}) channel {c} comes back as absence");
                    }
                    else
                    {
                        final[c, y, x].ShouldBe(image[c, y, x] + 0.1f, 1e-6f, $"covered ({x}, {y}) channel {c} keeps the step's work");
                    }
                }
            }
        }
    }

    private static bool InRing(int y, int x, int ring) => y < ring || x < ring || y >= Size - ring || x >= Size - ring;

    /// <summary>A gradient corrector that adds <paramref name="offset"/> to every sample, the ring included, and
    /// records how large a ring its input carried.</summary>
    private sealed class AddsEverywhere(float offset) : IGradientCorrector
    {
        public int RingItSaw { get; private set; } = -1;

        public string Name => "Test/AddsEverywhere";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            RingItSaw = input.AbsentPixels()?.PopCount() ?? 0;
            var (channels, width, height) = input.Shape;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[height, width];
                var src = input.GetChannelSpan(c);
                for (var i = 0; i < src.Length; i++)
                {
                    plane[i / width, i % width] = src[i] + offset;
                }
                planes[c] = plane;
            }
            return Task.FromResult(new Image(planes, BitDepth.Float32, 1.1f, 0f, 0f, input.ImageMeta));
        }
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
