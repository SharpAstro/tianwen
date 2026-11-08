using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.ChannelOffsets"/> reads a colour's offset from green on the stars: a synthetic field with a KNOWN shift per colour,
/// sampled to a Bayer mosaic (every phase) and debayered, read back to 0.02 px.
/// </summary>
/// <remarks>
/// Synthetic because the truth has to be exact: each colour's field is rendered at the star's position plus that colour's own shift,
/// and the photosite then takes the colour its place in the 2 x 2 cell gives it. Red goes the M83 table's way (+0.28, +0.15 px) and
/// blue the opposite (-0.15, -0.08 px), which is the sign of atmospheric dispersion; the colours also differ in level and gain, as a
/// sensor's do, so a centroid that confused them would read a shift of its own.
/// </remarks>
public class ChannelOffsetsTests
{
    private const int Size = 260;
    private const int GridStep = 40;
    private const float Sigma = 1.9f;
    private const float Amplitude = 9000f;
    private const float Noise = 4f;
    private const double Tolerance = 0.02;

    private static readonly (double Dx, double Dy) RedShift = (0.28, 0.15);
    private static readonly (double Dx, double Dy) BlueShift = (-0.15, -0.08);

    // (level, gain) per colour, R G B: the sky is brighter and the stars fainter in the colours the sensor is less sensitive to.
    private static readonly (float Level, float Gain)[] Colours = [(1100f, 1.00f), (1500f, 0.85f), (900f, 0.55f)];

    private static ImageMeta Meta(int offsetX, int offsetY) => new(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, FocuserPositions.None, Filter.Luminance, 1, 1,
        float.NaN, SensorType.RGGB, offsetX, offsetY, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>Stars on a grid, each at a sub-pixel position of its own so no phase of the cell is favoured.</summary>
    private static List<(double X, double Y)> StarPositions(int count)
    {
        var positions = new List<(double, double)>();
        var rnd = new Random(1993);
        for (var gy = 0; gy < 6 && positions.Count < count; gy++)
        {
            for (var gx = 0; gx < 6 && positions.Count < count; gx++)
            {
                positions.Add((GridStep * (gx + 1) + rnd.NextDouble(), GridStep * (gy + 1) + rnd.NextDouble()));
            }
        }

        return positions;
    }

    private static float Field(int x, int y, int colour, (double Dx, double Dy) shift, List<(double X, double Y)> stars)
    {
        var (level, gain) = Colours[colour];
        var value = (double)level;
        foreach (var (sx, sy) in stars)
        {
            var dx = x - (sx + shift.Dx);
            var dy = y - (sy + shift.Dy);
            value += Amplitude * gain * Math.Exp(-((dx * dx) + (dy * dy)) / (2d * Sigma * Sigma));
        }

        return (float)value;
    }

    private static (double Dx, double Dy) ShiftOf(int colour, double scale) => colour switch
    {
        0 => (RedShift.Dx * scale, RedShift.Dy * scale),
        2 => (BlueShift.Dx * scale, BlueShift.Dy * scale),
        _ => (0d, 0d),
    };

    private static Image Mosaic(int offsetX, int offsetY, List<(double X, double Y)> stars, double shiftScale = 1d)
    {
        var pattern = SensorType.RGGB.GetBayerPatternMatrix(offsetX, offsetY);
        var rnd = new Random(7);
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var colour = pattern[y & 1, x & 1];
                data[y, x] = Field(x, y, colour, ShiftOf(colour, shiftScale), stars) + (float)(Noise * Gaussian(rnd));
            }
        }

        return new Image([data], BitDepth.Float32, Amplitude + 1500f, 0f, 0f, Meta(offsetX, offsetY));
    }

    private static double Gaussian(Random rnd)
        => Math.Sqrt(-2d * Math.Log(1d - rnd.NextDouble())) * Math.Cos(2d * Math.PI * rnd.NextDouble());

    private static void ShouldRead(ChannelOffsetsResult result, double scale = 1d)
    {
        result.IsMeasured.ShouldBeTrue();
        result.Green.Dx.ShouldBe(0d);
        result.Green.Dy.ShouldBe(0d);
        result.StarsUsed.ShouldBeGreaterThanOrEqualTo(20);

        var red = ShiftOf(0, scale);
        var blue = ShiftOf(2, scale);
        result.Red.Dx.ShouldBe(red.Dx, Tolerance);
        result.Red.Dy.ShouldBe(red.Dy, Tolerance);
        result.Blue.Dx.ShouldBe(blue.Dx, Tolerance);
        result.Blue.Dy.ShouldBe(blue.Dy, Tolerance);
    }

    [Theory]
    [InlineData(0, 0)] // RGGB
    [InlineData(1, 0)] // GRBG
    [InlineData(0, 1)] // GBRG
    [InlineData(1, 1)] // BGGR
    public async Task AMosaicReadsRedAndBlueBackToTheirKnownShifts(int offsetX, int offsetY)
    {
        var mosaic = Mosaic(offsetX, offsetY, StarPositions(36));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);
        stars.Count.ShouldBeGreaterThanOrEqualTo(20);

        ShouldRead(mosaic.ChannelOffsets(stars));
    }

    /// <summary>
    /// A debayer that keeps the colours apart: each colour at each pixel is the mean of that colour's own photosites in the 3 x 3
    /// about it (the classic bilinear demosaic), so no colour borrows another's structure and the shift the mosaic holds is the
    /// shift the planes hold.
    /// </summary>
    private static Image BilinearPerColour(Image mosaic, int offsetX, int offsetY)
    {
        var pattern = SensorType.RGGB.GetBayerPatternMatrix(offsetX, offsetY);
        var raw = mosaic.GetChannelArray(0);
        var planes = new[] { new float[Size, Size], new float[Size, Size], new float[Size, Size] };
        Span<double> sum = stackalloc double[3];
        Span<int> count = stackalloc int[3];
        for (var y = 1; y < Size - 1; y++)
        {
            for (var x = 1; x < Size - 1; x++)
            {
                sum.Clear();
                count.Clear();
                var own = pattern[y & 1, x & 1];
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var c = pattern[(y + dy) & 1, (x + dx) & 1];
                        // Green at a green photosite is that photosite alone; elsewhere the colour's photosites in the 3 x 3.
                        if (c == own && (dx != 0 || dy != 0))
                        {
                            continue;
                        }

                        sum[c] += raw[y + dy, x + dx];
                        count[c]++;
                    }
                }

                for (var c = 0; c < 3; c++)
                {
                    planes[c][y, x] = (float)(sum[c] / count[c]);
                }
            }
        }

        return new Image(planes, BitDepth.Float32, mosaic.MaxValue, 0f, 0f, mosaic.ImageMeta with { SensorType = SensorType.Color, BayerOffsetX = 0, BayerOffsetY = 0 });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task ADebayeredFrameReadsTheSameShiftsWhenItsDemosaicKeepsTheColoursApart(int offsetX, int offsetY)
    {
        var mosaic = Mosaic(offsetX, offsetY, StarPositions(36));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);
        var rgb = BilinearPerColour(mosaic, offsetX, offsetY);
        rgb.ChannelCount.ShouldBe(3);

        ShouldRead(rgb.ChannelOffsets(stars));
    }

    /// <summary>
    /// The stacker's default demosaic, AHD, rebuilds red and blue as green plus a median of the colour difference, so red takes
    /// green's structure and the debayered frame HOLDS a smaller shift than the sky put there (about two thirds of it here). The
    /// primitive reads what the frame holds: an independent estimator (a plain, unweighted moment over a wide window) agrees with it
    /// on the AHD planes, and both fall well short of the truth that the mosaic reads. That is why a frame's colours are measured
    /// on its mosaic, before the demosaic, never on the debayered frame.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task TheStackersDefaultDemosaicPullsRedAndBlueTowardGreenAndTheReadSaysSo(int offsetX, int offsetY)
    {
        var mosaic = Mosaic(offsetX, offsetY, StarPositions(36));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);
        var rgb = await mosaic.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: TestContext.Current.CancellationToken);

        var read = rgb.ChannelOffsets(stars);
        read.IsMeasured.ShouldBeTrue();

        var (redX, redY) = PlainMomentOffset(rgb, stars, 0);
        var (blueX, blueY) = PlainMomentOffset(rgb, stars, 2);
        // AHD leaves each star slightly lopsided in red and blue, so a Gaussian-weighted centre and a plain moment weigh it a
        // little differently (0.18 against 0.20 px on red): they agree to 0.04 px, and both sit a third short of the truth.
        const double agreement = 0.04;
        read.Red.Dx.ShouldBe(redX, agreement);
        read.Red.Dy.ShouldBe(redY, agreement);
        read.Blue.Dx.ShouldBe(blueX, agreement);
        read.Blue.Dy.ShouldBe(blueY, agreement);

        // Same direction as the truth, and short of it by more than the bound: the demosaic moved the colours, not the read.
        read.Red.Dx.ShouldBeGreaterThan(0d);
        read.Red.Dx.ShouldBeLessThan(RedShift.Dx - (3 * Tolerance));
        read.Blue.Dx.ShouldBeLessThan(0d);
        read.Blue.Dx.ShouldBeGreaterThan(BlueShift.Dx + (3 * Tolerance));
        mosaic.ChannelOffsets(stars).Red.Dx.ShouldBe(RedShift.Dx, Tolerance);
    }

    /// <summary>The median over the stars of a colour's plain first moment less green's, over a 9 px window less a ring median.</summary>
    private static (double Dx, double Dy) PlainMomentOffset(Image rgb, StarList stars, int colour)
    {
        const int radius = 9;
        const int ring = 13;
        var dxs = new List<double>();
        var dys = new List<double>();
        foreach (var star in stars)
        {
            var ix = (int)MathF.Round(star.XCentroid);
            var iy = (int)MathF.Round(star.YCentroid);
            if (ix - ring < 0 || iy - ring < 0 || ix + ring >= Size || iy + ring >= Size)
            {
                continue;
            }

            var c = Moment(rgb.GetChannelArray(colour), ix, iy);
            var g = Moment(rgb.GetChannelArray(1), ix, iy);
            dxs.Add(c.X - g.X);
            dys.Add(c.Y - g.Y);
        }

        dxs.Sort();
        dys.Sort();
        return (dxs[dxs.Count / 2], dys[dys.Count / 2]);

        static (double X, double Y) Moment(float[,] plane, int ix, int iy)
        {
            var background = new List<float>();
            for (var y = iy - ring; y <= iy + ring; y++)
            {
                for (var x = ix - ring; x <= ix + ring; x++)
                {
                    var d2 = ((x - ix) * (x - ix)) + ((y - iy) * (y - iy));
                    if (d2 > radius * radius && d2 <= ring * ring)
                    {
                        background.Add(plane[y, x]);
                    }
                }
            }

            background.Sort();
            var level = background[background.Count / 2];
            double s = 0d, sx = 0d, sy = 0d;
            for (var y = iy - radius; y <= iy + radius; y++)
            {
                for (var x = ix - radius; x <= ix + radius; x++)
                {
                    if (((x - ix) * (x - ix)) + ((y - iy) * (y - iy)) <= radius * radius)
                    {
                        var f = plane[y, x] - level;
                        s += f;
                        sx += f * x;
                        sy += f * y;
                    }
                }
            }

            return (sx / s, sy / s);
        }
    }

    [Fact]
    public async Task ARedReferenceReadsGreenAndBlueAgainstRed()
    {
        var mosaic = Mosaic(0, 0, StarPositions(36));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);

        var result = mosaic.ChannelOffsets(stars, CfaChannel.Red);

        result.Reference.ShouldBe(CfaChannel.Red);
        result.Red.Dx.ShouldBe(0d);
        result.Green.Dx.ShouldBe(-RedShift.Dx, Tolerance);
        result.Green.Dy.ShouldBe(-RedShift.Dy, Tolerance);
        result.Blue.Dx.ShouldBe(BlueShift.Dx - RedShift.Dx, Tolerance);
        result.Blue.Dy.ShouldBe(BlueShift.Dy - RedShift.Dy, Tolerance);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task AFrameWithNoShiftReadsZero(int offsetX, int offsetY)
    {
        var mosaic = Mosaic(offsetX, offsetY, StarPositions(36), shiftScale: 0d);
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);

        var result = mosaic.ChannelOffsets(stars);

        result.IsMeasured.ShouldBeTrue();
        result.Red.Length.ShouldBeLessThan(Tolerance);
        result.Blue.Length.ShouldBeLessThan(Tolerance);
    }

    [Fact]
    public async Task TheSpreadIsTheScatterOfOneStarAndTheStarsAreCounted()
    {
        var mosaic = Mosaic(0, 0, StarPositions(36));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);

        var result = mosaic.ChannelOffsets(stars);

        result.Red.StarCount.ShouldBe(result.StarsUsed);
        result.Blue.StarCount.ShouldBe(result.StarsUsed);
        result.StarsOffered.ShouldBe(stars.Count);
        // Noise 4 on a 9000 star centroids to well under a hundredth of a pixel, and the error is smaller than the scatter of one star.
        result.Red.SpreadX.ShouldBeLessThan(0.05d);
        result.Red.SpreadX.ShouldBeGreaterThanOrEqualTo(0d);
        result.Red.ErrorX.ShouldBeLessThan(result.Red.SpreadX + 1e-9);
    }

    [Fact]
    public async Task TooFewStarsReadNothingRatherThanGuess()
    {
        var mosaic = Mosaic(0, 0, StarPositions(3));
        var stars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 3, cancellationToken: TestContext.Current.CancellationToken);

        var result = mosaic.ChannelOffsets(stars);

        result.IsMeasured.ShouldBeFalse();
        double.IsNaN(result.Red.Dx).ShouldBeTrue();
        double.IsNaN(result.Blue.Dy).ShouldBeTrue();
        result.StarsUsed.ShouldBe(0);
        result.StarsOffered.ShouldBe(stars.Count);
    }

    [Fact]
    public void AnEmptyStarListAndAMonoFrameReadNothing()
    {
        var mosaic = Mosaic(0, 0, StarPositions(1));
        mosaic.ChannelOffsets(StarList.Empty).IsMeasured.ShouldBeFalse();

        var mono = Image.FromChannel(new float[32, 32], 1f, 0f);
        mono.ChannelOffsets(StarList.Empty).IsMeasured.ShouldBeFalse();
    }

    [Fact]
    public async Task ACrowdedStarIsSkippedNotRead()
    {
        // The grid, plus a bright companion 7 px from every star of its first row: those stars are crowded and give no reading.
        var field = StarPositions(36);
        var crowded = new List<(double X, double Y)>(field);
        for (var i = 0; i < 6; i++)
        {
            crowded.Add((field[i].X + 7d, field[i].Y));
        }

        var mosaic = Mosaic(0, 0, crowded);
        var alone = Mosaic(0, 0, field);
        var crowdedStars = await mosaic.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);
        var aloneStars = await alone.FindStarsAsync(0, snrMin: 20f, minStars: 20, cancellationToken: TestContext.Current.CancellationToken);

        var withCompanions = mosaic.ChannelOffsets(crowdedStars);
        var without = alone.ChannelOffsets(aloneStars);

        withCompanions.StarsUsed.ShouldBeLessThan(without.StarsUsed);
        ShouldRead(withCompanions);
    }
}
