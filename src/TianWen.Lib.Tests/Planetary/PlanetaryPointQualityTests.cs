using System;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A point of the disk, frame after frame (docs/plans/planetary-restoration.md, R4 per-point, #1071): the share of its quality that is its
/// own, a patch cut between a plane's samples, and a patch's band gain against a reference.
/// </summary>
public class PlanetaryPointQualityTests
{
    [Theory]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.5, 1.0, 0.2)]
    [InlineData(1.0, 0.0, 1.0)]
    public void APointsOwnShareIsWhatItsFrameDoesNotCarry(double ownSigma, double frameSigma, double expected)
    {
        // Each point's quality is its patch's constant, its frame's draw and its own: the share left once the frame's mean over its points
        // is out is the own draw's variance over both draws' (less the 1 / points the frame's mean takes of the own draw itself).
        const int frames = 600, points = 60;
        var random = new Random(7);
        var quality = new double[frames * points];
        var patch = new double[points];
        for (var p = 0; p < points; p++)
        {
            patch[p] = 3 * random.NextDouble();
        }
        for (var t = 0; t < frames; t++)
        {
            var frame = frameSigma * PhaseScreen.Gaussian(random);
            for (var p = 0; p < points; p++)
            {
                quality[(t * points) + p] = patch[p] + frame + (ownSigma * PhaseScreen.Gaussian(random));
            }
        }
        var share = PlanetaryPointQuality.OwnShare(quality, frames, points);
        TestContext.Current.TestOutputHelper?.WriteLine($"own share {share:0.0000}");
        share.ShouldBe(expected * (1 - (1.0 / points)), 0.02);
    }

    [Fact]
    public void ACutReadsAPlaneBetweenItsSamples()
    {
        // Bilinear reading is exact on a plane that is linear in x and y.
        const int width = 40, height = 30, size = 8;
        var plane = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[(y * width) + x] = (3f * x) + (5f * y) + 1;
            }
        }
        var patch = new float[size * size];
        PlanetaryPointQuality.Cut(plane, width, height, 10.3, 12.7, size, patch);
        for (var j = 0; j < size; j++)
        {
            for (var i = 0; i < size; i++)
            {
                patch[(j * size) + i].ShouldBe((float)((3 * (10.3 - (size / 2) + i)) + (5 * (12.7 - (size / 2) + j)) + 1), 1e-3f);
            }
        }
    }

    [Fact]
    public void ABandGainIsAPatchsTransferOverTheReferences()
    {
        // A patch that is the reference scaled reads the scale in every band; one that is the reference blurred reads less in its finer
        // bands than in its coarser ones, and less than one in each.
        const int size = 32, inner = 16;
        var random = new Random(11);
        var reference = new float[size * size];
        for (var i = 0; i < reference.Length; i++)
        {
            reference[i] = (float)random.NextDouble();
        }
        var scaled = Array.ConvertAll(reference, v => 0.7f * v);
        for (var band = 1; band <= 3; band++)
        {
            PlanetaryPointQuality.BandGain(scaled, reference, size, band, inner).ShouldBe(0.7, 1e-5);
        }
        var blurred = Blur(reference, size, 1.0);
        var gains = new double[3];
        for (var band = 1; band <= 3; band++)
        {
            gains[band - 1] = PlanetaryPointQuality.BandGain(blurred, reference, size, band, inner);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"blurred: bands 1 to 3 {gains[0]:0.000}, {gains[1]:0.000}, {gains[2]:0.000}");
        gains[0].ShouldBeLessThan(gains[1]);
        gains[1].ShouldBeLessThan(gains[2]);
        gains[2].ShouldBeLessThan(1);
    }

    // A periodic Gaussian blur of `sigma` samples.
    private static float[] Blur(float[] plane, int size, double sigma)
    {
        var radius = (int)Math.Ceiling(4 * sigma);
        var kernel = new double[(2 * radius) + 1];
        double sum = 0;
        for (var k = -radius; k <= radius; k++)
        {
            sum += kernel[k + radius] = Math.Exp(-k * k / (2 * sigma * sigma));
        }
        var (rows, result) = (new float[plane.Length], new float[plane.Length]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double v = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    v += kernel[k + radius] * plane[(y * size) + (((x + k) % size) + size) % size];
                }
                rows[(y * size) + x] = (float)(v / sum);
            }
        }
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double v = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    v += kernel[k + radius] * rows[(((((y + k) % size) + size) % size) * size) + x];
                }
                result[(y * size) + x] = (float)(v / sum);
            }
        }
        return result;
    }
}
