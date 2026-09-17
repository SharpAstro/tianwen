using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A written master is in unit scale, and says so truthfully: its brightest finite pixel is 1.0 and its
/// <c>MaxValue</c> / <c>DATAMAX</c> is that peak.
/// </summary>
/// <remarks>
/// <para>
/// Every integration strategy normalises each frame so a channel's sky median lands on
/// <c>NormalizationTarget</c> (0.5), and nothing divided back afterwards. A star sits tens of times above
/// that: on the real 10P/Tempel 2 master the standard tile strategy peaked at 61.7 and the per-colour
/// drizzle at 62.0. <see cref="MasterPostProcessor"/> nevertheless tagged <c>MaxValue = 1</c> on the belief
/// that the data were "already in [0, 1]", so the file claimed <c>DATAMAX = 1</c> over pixels up to 62,
/// and the viewer, which trusts that label, clipped every star core flat and saturated the comet.
/// </para>
/// <para>
/// The master here is shaped like a real one: background 0.5 in every channel, one coloured star far
/// above it, a NaN hole, the <c>MaxValue = 1</c> label the strategies used to hand over and a light's
/// sensor full scale (65535 ADU, what a TianWen-captured light carries as <c>SATURATE</c>). It goes through
/// <see cref="IntegratedMaster.Labelled"/> exactly as every strategy's master does. Ratios are asserted
/// alongside the ceiling, because dividing each channel by its own peak would also "fit in [0, 1]" and
/// would silently change the star's colour.
/// </para>
/// </remarks>
[Collection("Stacking")]
public class MasterUnitScaleTests
{
    private const int Size = 64;
    private const int StarX = 30;
    private const int StarY = 20;
    private const float Background = 0.5f;
    private const float StarR = 60f;
    private const float StarG = 20f;
    private const float StarB = 30f;

    private static Image SyntheticMaster(float starR, float starG, float starB)
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    planes[c][y, x] = Background;
                }
            }
        }

        planes[0][StarY, StarX] = starR;
        planes[1][StarY, StarX] = starG;
        planes[2][StarY, StarX] = starB;
        // Drizzle holes in EVERY channel, the first pixel among them, as a real drizzle canvas has: a
        // peak scan that does not skip NaN answers NaN and scales nothing. A hole in one channel only
        // hid exactly that, because the channels without one still supplied the peak.
        for (var c = 0; c < 3; c++)
        {
            planes[c][0, 0] = float.NaN;
            planes[c][5, 5] = float.NaN;
        }

        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(60),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN)
        {
            SensorFullScaleAdu = 65535f,
        };
        // The labels a strategy used to hand over (MaxValue = 1, the light's full scale), through the
        // one relabelling every strategy now applies, as a strategy whose frames were normalised.
        return IntegratedMaster.Labelled(new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, meta), normalised: true);
    }

    private static async Task<(Image Full, Image Crop, Image Input)> WriteAsync(Image master, string dir)
    {
        var (_, w, h) = master.Shape;
        var rejection = new Image([new float[h, w]], BitDepth.Float32, 1.0f, 0f, 0f, master.ImageMeta);
        var result = new IntegrationResult(master, rejection, FrameCount: 1, TotalRejections: 0, MeanRejectionRate: 0.0);
        var masterPath = Path.Combine(dir, "master_test.fits");

        var processor = new MasterPostProcessor(NullLogger.Instance, catalogDb: null);
        await processor.WriteMasterAsync(
            result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
            autocropRect: new PixelRect(4, 4, Size - 8, Size - 8), strategy: IntegrationStrategyKind.InRamAllFrames,
            enhance: false, enhanceBlend: 1.0f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
            outputs: MasterRenderOutputs.None, ct: TestContext.Current.CancellationToken);

        Image.TryReadFitsFile(masterPath, out var full, out _).ShouldBeTrue("the master FITS reads back");
        Image.TryReadFitsFile(Path.Combine(dir, "master_test_autocrop.fits"), out var crop, out _)
            .ShouldBeTrue("the autocrop FITS reads back");
        return (full!, crop!, master);
    }

    /// <summary>
    /// The pipeline-level half of this contract, for any test that stacks a master through
    /// <c>StackingPipeline</c> and reads the written file back: its brightest finite pixel is at most 1
    /// and its label states that peak.
    /// </summary>
    internal static void ShouldBeUnitScaleWithATrueLabel(Image written, string what)
    {
        var peak = FinitePeak(written);
        peak.ShouldBeLessThanOrEqualTo(1f + 1e-5f, $"{what}: the written master's brightest finite pixel is in [0, 1]");
        written.MaxValue.ShouldBe(peak, 1e-5f, $"{what}: its DATAMAX / MaxValue is that peak");
    }

    private static float FinitePeak(Image image)
    {
        var peak = float.MinValue;
        for (var c = 0; c < image.ChannelCount; c++)
        {
            foreach (var v in image.GetChannelArray(c))
            {
                if (float.IsFinite(v) && v > peak)
                {
                    peak = v;
                }
            }
        }

        return peak;
    }

    [Fact]
    public void AnIntegratedMasterIsLabelledWithItsObservedPeakAndNoSensorFullScale()
    {
        var master = SyntheticMaster(StarR, StarG, StarB);

        master.MaxValue.ShouldBe(StarR, "the label is the brightest finite pixel, the NaN hole skipped");
        master.ImageMeta.SensorFullScaleAdu.ShouldBeNull(
            "a normalised master has no sensor saturation level; the light's 65535 ADU would win UnitScaleDivisor");
        master.GetChannelArray(0)[StarY, StarX].ShouldBe(StarR, "relabelling changes no pixel");
    }

    [Fact]
    public async Task AMasterAboveUnitScaleIsWrittenWithItsPeakAtOne()
    {
        var dir = Directory.CreateTempSubdirectory("MasterUnitScaleTests_");
        try
        {
            var (full, crop, input) = await WriteAsync(SyntheticMaster(StarR, StarG, StarB), dir.FullName);

            FinitePeak(full).ShouldBe(1f, 1e-5f, "the brightest finite pixel of the written master is exactly 1");
            full.MaxValue.ShouldBe(FinitePeak(full), 1e-5f, "and its label is that peak, not a claim over bigger data");
            full.ImageMeta.SensorFullScaleAdu.ShouldBeNull("no SATURATE card claims a raw-ADU saturation level");

            // One scalar for every channel: the star keeps its colour and its height above the sky.
            var r = full.GetChannelArray(0)[StarY, StarX];
            var g = full.GetChannelArray(1)[StarY, StarX];
            var b = full.GetChannelArray(2)[StarY, StarX];
            (r / g).ShouldBe(StarR / StarG, 1e-4f, "the star's R/G is unchanged");
            (b / g).ShouldBe(StarB / StarG, 1e-4f, "the star's B/G is unchanged");
            (full.GetChannelArray(1)[40, 40] / g).ShouldBe(Background / StarG, 1e-5f,
                "the sky keeps its level relative to the star");
            float.IsNaN(full.GetChannelArray(1)[5, 5]).ShouldBeTrue("a hole stays a hole");

            // The autocrop is the same master in the same units, not rescaled by its own peak.
            crop.GetChannelArray(0)[StarY - 4, StarX - 4].ShouldBe(r, 1e-6f, "the crop shares the full frame's scale");

            // The pipeline keeps using the integration it handed over (the comet composite is built from
            // it afterwards), so the rescale must be a new image, never an in-place division.
            input.GetChannelArray(0)[StarY, StarX].ShouldBe(StarR, "the input master is not rescaled in place");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// A peak between 1 and 2 is still scaled to 1.
    /// </summary>
    /// <remarks>
    /// <see cref="Image.HasUnitScalePeak"/> counts any peak up to 2.0 as unit-referred, because flat
    /// division pushes a saturated star off axis above 1 by construction, and
    /// <see cref="Image.ScaleFloatValuesToUnit"/> leaves such an image alone. That is the right answer
    /// to "are these samples ADU?", and the wrong one to "is anything above 1?", which is what a written
    /// master promises: a starless or nebula-only layer normalised to a sky of 0.5 can peak at 1.5, and
    /// the tolerant question wrote it with <c>DATAMAX = 1.5</c>, which the viewer clips above 1.
    /// </remarks>
    [Fact]
    public async Task AMasterPeakingInsideTheUnitScaleToleranceIsStillWrittenWithItsPeakAtOne()
    {
        var dir = Directory.CreateTempSubdirectory("MasterUnitScaleTests_");
        try
        {
            const float starR = 1.5f;
            const float starG = 0.6f;
            const float starB = 0.9f;
            var (full, _, _) = await WriteAsync(SyntheticMaster(starR, starG, starB), dir.FullName);

            FinitePeak(full).ShouldBe(1f, 1e-5f, "a peak of 1.5 is inside HasUnitScalePeak's tolerance and must still land on 1");
            full.MaxValue.ShouldBe(FinitePeak(full), 1e-5f, "and its label is that peak");
            var g = full.GetChannelArray(1)[StarY, StarX];
            (full.GetChannelArray(0)[StarY, StarX] / g).ShouldBe(starR / starG, 1e-4f, "one scalar for every channel");
            (full.GetChannelArray(1)[40, 40] / g).ShouldBe(Background / starG, 1e-5f, "the sky keeps its level relative to the star");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task AMasterAlreadyInUnitScaleIsWrittenWithItsValuesUnchanged()
    {
        var dir = Directory.CreateTempSubdirectory("MasterUnitScaleTests_");
        try
        {
            // Background 0.5 cannot sit under a star below 1, so use a sky and star already in unit scale.
            var master = SyntheticMaster(0.9f, 0.3f, 0.45f);
            var (full, _, _) = await WriteAsync(master, dir.FullName);

            full.GetChannelArray(0)[StarY, StarX].ShouldBe(0.9f, 1e-6f, "a master already in [0, 1] is not stretched to its peak");
            full.GetChannelArray(1)[40, 40].ShouldBe(Background, 1e-6f);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
