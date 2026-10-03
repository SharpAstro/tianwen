using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests.Planetary;

/// <summary>
/// A colour master's planes moved onto green (docs/plans/planetary-restoration.md, "A colour master's planes aligned onto each other",
/// #1202): a synthetic disk whose red and blue the atmosphere has displaced by a known amount, sampled through a Bayer pattern.
/// </summary>
public class PlanetaryChannelAlignmentTests
{
    private const int Size = 256;
    private const double CentreX = 128.3, CentreY = 127.6, Radius = 50;
    // Jupiter's apparent polar over equatorial radius, the poles along y.
    private const double AxisRatio = 0.935;
    private static readonly (double X, double Y) RedShift = (1.3, -0.8), BlueShift = (-1.7, 1.1);

    [Theory]
    [InlineData(0, 0)] // RGGB
    [InlineData(1, 0)] // GRBG
    [InlineData(0, 1)] // GBRG
    [InlineData(1, 1)] // BGGR
    public void ASplitMastersDispersionIsReadInMosaicPixelsWithThePhotositesPhaseTakenOut(int offsetX, int offsetY)
    {
        var split = Mosaic(offsetX, offsetY).SplitBayerChannels();

        var (aligned, result) = PlanetaryChannelAlignment.Align(split, PlanetaryFrameLayout.SplitCfa);

        var read = result.ShouldNotBeNull();
        read.Applied.ShouldBeTrue();
        read.Red.Dx.ShouldBe(RedShift.X, 0.05);
        read.Red.Dy.ShouldBe(RedShift.Y, 0.05);
        read.Blue.Dx.ShouldBe(BlueShift.X, 0.05);
        read.Blue.Dy.ShouldBe(BlueShift.Y, 0.05);
        // Both greens are green: once the phase is out they agree, which is what says the arithmetic is right.
        read.GreenCheck.ShouldNotBeNull().Length.ShouldBeLessThan(0.05);
        aligned.ChannelCount.ShouldBe(4);
        aligned.GetChannelArray(1).ShouldBeSameAs(split.GetChannelArray(1));

        // Read again, the aligned planes sit on green.
        var (_, again) = PlanetaryChannelAlignment.Align(aligned, PlanetaryFrameLayout.SplitCfa);
        again.ShouldNotBeNull().Red.Length.ShouldBeLessThan(0.05);
        again.Blue.Length.ShouldBeLessThan(0.05);
    }

    [Theory]
    [InlineData(0, 0)] // RGGB
    [InlineData(1, 1)] // BGGR
    public void ASplitMastersColoursAreReadAtTheirLimbsWithThePhotositesPhaseTakenOut(int offsetX, int offsetY)
    {
        var split = Mosaic(offsetX, offsetY, bluePoleToPole: 0.2).SplitBayerChannels();

        var (_, result) = PlanetaryChannelAlignment.Align(split, PlanetaryFrameLayout.SplitCfa, new LimbFitOptions(AxisRatio));

        // What this pins is the photosites' phase, which a mistake would put half a pixel or more off. A sub-plane of this scene is a
        // 25 px disk with its edge in 1.25 px, coarse for the fit's model, so it lands within a tenth or so; R5a's colour twin, a
        // rendered planet, is where the reading is judged to 0.1 px (docs/plans/planetary-restoration.md, #1202).
        var read = result.ShouldNotBeNull();
        read.Reading.ShouldBe(PlanetaryChannelReading.Limb);
        read.Applied.ShouldBeTrue();
        read.Red.Dx.ShouldBe(RedShift.X, 0.2);
        read.Red.Dy.ShouldBe(RedShift.Y, 0.2);
        read.Blue.Dx.ShouldBe(BlueShift.X, 0.2);
        read.Blue.Dy.ShouldBe(BlueShift.Y, 0.2);
        read.GreenCheck.ShouldNotBeNull().Length.ShouldBeLessThan(0.1);
    }

    [Fact]
    public async Task TheDemosaicOfAlignedSubPlanesHasItsColoursOnGreen()
    {
        var split = Mosaic(0, 0).SplitBayerChannels();
        var (aligned, _) = PlanetaryChannelAlignment.Align(split, PlanetaryFrameLayout.SplitCfa);

        var master = await PlanetaryMaster.MergeAndDemosaicAsync(aligned, PlanetaryFrameLayout.SplitCfa, TestContext.Current.CancellationToken);
        var (_, read) = PlanetaryChannelAlignment.Align(master, PlanetaryFrameLayout.Rgb);

        read.ShouldNotBeNull().Red.Length.ShouldBeLessThan(0.1);
        read.Blue.Length.ShouldBeLessThan(0.1);
    }

    [Fact]
    public void AColourDarkerTowardOnePoleIsReadAtItsLimbNotWhereItsBrightnessLeans()
    {
        // Blue a fifth darker from one pole to the other, as a colour sees its own belts and poles: anything that weighs brightness
        // reads that as a shift along the axis, and the limb fit, whose albedo has a term for one hemisphere against the other, does not.
        var rgb = new Image([Render(RedShift.X, RedShift.Y, 0.9), Render(0, 0), Render(BlueShift.X, BlueShift.Y, 0.7, poleToPole: 0.2)],
            BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        var limb = new LimbFitOptions(AxisRatio);

        var (_, byCorrelation) = PlanetaryChannelAlignment.Align(rgb, PlanetaryFrameLayout.Rgb);
        var (_, byLimb) = PlanetaryChannelAlignment.Align(rgb, PlanetaryFrameLayout.Rgb, limb);

        var read = byLimb.ShouldNotBeNull();
        read.Reading.ShouldBe(PlanetaryChannelReading.Limb);
        read.Red.Dx.ShouldBe(RedShift.X, 0.1);
        read.Red.Dy.ShouldBe(RedShift.Y, 0.1);
        read.Blue.Dx.ShouldBe(BlueShift.X, 0.1);
        read.Blue.Dy.ShouldBe(BlueShift.Y, 0.1);
        var pulled = byCorrelation.ShouldNotBeNull();
        pulled.Reading.ShouldBe(PlanetaryChannelReading.Correlation);
        Math.Abs(pulled.Blue.Dy - BlueShift.Y).ShouldBeGreaterThan(3 * Math.Abs(read.Blue.Dy - BlueShift.Y));
    }

    [Fact]
    public void AThreePlaneMasterIsAlignedAsItIs()
    {
        var rgb = Planes();

        var (aligned, result) = PlanetaryChannelAlignment.Align(rgb, PlanetaryFrameLayout.Rgb);

        var read = result.ShouldNotBeNull();
        read.Applied.ShouldBeTrue();
        read.GreenCheck.ShouldBeNull();
        read.Red.Dx.ShouldBe(RedShift.X, 0.05);
        read.Red.Dy.ShouldBe(RedShift.Y, 0.05);
        read.Blue.Dx.ShouldBe(BlueShift.X, 0.05);
        read.Blue.Dy.ShouldBe(BlueShift.Y, 0.05);
        var (_, again) = PlanetaryChannelAlignment.Align(aligned, PlanetaryFrameLayout.Rgb);
        again.ShouldNotBeNull().Red.Length.ShouldBeLessThan(0.05);
        again.Blue.Length.ShouldBeLessThan(0.05);
    }

    [Fact]
    public void AMonoMasterIsUntouched()
    {
        var mono = new Image([Render(0, 0)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

        var (aligned, result) = PlanetaryChannelAlignment.Align(mono, PlanetaryFrameLayout.Mono);

        aligned.ShouldBeSameAs(mono);
        result.ShouldBeNull();
    }

    [Fact]
    public void AReadingBeyondWhatDispersionCanBeLeavesTheMasterAsStacked()
    {
        // Blue a third of the disk's radius off green: no atmosphere does that, a misread plane does.
        var rgb = Planes(blue: (Radius / 3, 0));

        var (aligned, result) = PlanetaryChannelAlignment.Align(rgb, PlanetaryFrameLayout.Rgb);

        aligned.ShouldBeSameAs(rgb);
        var read = result.ShouldNotBeNull();
        read.Applied.ShouldBeFalse();
        read.Refusal.ShouldNotBeNull();
    }

    // The scene a colour shows, its content at (x + dx, y + dy): an oblate disk with a soft edge, its poles along y, darkening to its
    // limb, crossed by belts and carrying a spot, so the correlation has detail in both directions, and darkened linearly from one
    // pole to the other by poleToPole of its brightness. Area-averaged over a 4 x 4 grid of the pixel.
    private static float[,] Render(double dx, double dy, double gain = 1, double poleToPole = 0)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = (float)(gain * Pixel(x - dx, y - dy, poleToPole));
            }
        }
        return plane;
    }

    private static double Pixel(double x, double y, double poleToPole)
    {
        var sum = 0.0;
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                sum += Scene(x - 0.375 + (0.25 * i), y - 0.375 + (0.25 * j), poleToPole);
            }
        }
        return sum / 16;
    }

    private static double Scene(double x, double y, double poleToPole = 0)
    {
        var (u, v) = (x - CentreX, y - CentreY);
        var r = Math.Sqrt((u * u) + (v * v / (AxisRatio * AxisRatio)));
        // A soft edge, 2.5 px of Gaussian blur.
        var edge = 0.5 * (1 - Math.Tanh((r - Radius) / 2.5));
        var mu = Math.Sqrt(Math.Max(0, 1 - Math.Min(1, (r * r) / (Radius * Radius))));
        var belts = 1 - (0.25 * Math.Cos(2 * Math.PI * v / 23)) - (0.1 * Math.Cos((2 * Math.PI * v / 9) + 0.7));
        var spot = 0.3 * Math.Exp(-(((u - 14) * (u - 14)) + ((v + 9) * (v + 9))) / 30);
        var lean = 1 - (0.5 * poleToPole * Math.Clamp(v / (AxisRatio * Radius), -1, 1));
        return 0.01 + (edge * lean * ((0.6 + (0.4 * mu)) * belts - spot));
    }

    // A Bayer mosaic of the scene with red and blue displaced, red at the photosites whose parity matches the offsets.
    private static Image Mosaic(int offsetX, int offsetY, double bluePoleToPole = 0)
    {
        var (red, green, blue) = (Render(RedShift.X, RedShift.Y, 0.9), Render(0, 0), Render(BlueShift.X, BlueShift.Y, 0.7, bluePoleToPole));
        var mosaic = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            var yp = (y - offsetY) & 1;
            for (var x = 0; x < Size; x++)
            {
                var xp = (x - offsetX) & 1;
                mosaic[y, x] = ((yp * 2) + xp) switch { 0 => red[y, x], 3 => blue[y, x], _ => green[y, x] };
            }
        }
        return new Image([mosaic], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.RGGB, BayerOffsetX = offsetX, BayerOffsetY = offsetY });
    }

    private static Image Planes((double X, double Y)? blue = null)
    {
        var (bx, by) = blue ?? BlueShift;
        return new Image([Render(RedShift.X, RedShift.Y, 0.9), Render(0, 0), Render(bx, by, 0.7)], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }
}
