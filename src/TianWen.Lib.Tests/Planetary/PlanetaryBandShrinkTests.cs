using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The master's two halves and the shrink that reads its noise off them (#1313): the halves lie on the master's grid, finished as it is, and
/// half their difference is its noise band by band.
/// </summary>
public sealed class PlanetaryBandShrinkTests : IDisposable
{
    private const int Size = 128;
    private const double Radius = 40;
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void HalvesThatHoldNoNoiseLeaveThePlaneAsItIs()
    {
        var scene = Scene();
        var shrink = PlanetaryBandShrink.Shrink(scene, scene, scene, Size, Size, new MetricDisk(Size / 2.0, Size / 2.0, Radius));

        shrink.Bands.ShouldAllBe(band => band.NoiseSigma == 0 && band.Threshold == 0);
        for (var i = 0; i < scene.Length; i++)
        {
            shrink.Shrunk[i].ShouldBe(scene[i], 1e-5f);
        }
    }

    [Fact]
    public void ItReadsTheMastersNoiseOffItsHalvesAndShrinksItTowardTheScene()
    {
        const double sigma = 0.05;
        var scene = Scene();
        var rng = new Random(7);
        var (a, b, master) = (new float[scene.Length], new float[scene.Length], new float[scene.Length]);
        for (var i = 0; i < scene.Length; i++)
        {
            a[i] = scene[i] + (float)(sigma * Gaussian(rng));
            b[i] = scene[i] + (float)(sigma * Gaussian(rng));
            master[i] = (a[i] + b[i]) / 2;
        }
        var disk = new MetricDisk(Size / 2.0, Size / 2.0, Radius);

        var shrink = PlanetaryBandShrink.Shrink(master, a, b, Size, Size, disk);

        // White noise of spread s puts s times sqrt(sum of (delta - h)^2) = 0.889 s in the finest a trous band, h the B3 kernel; the
        // master's noise is sigma / sqrt(2).
        shrink.Bands[0].NoiseSigma.ShouldBe(0.889 * sigma / Math.Sqrt(2), 0.03 * sigma);
        ErrorInside(shrink.Shrunk, scene, disk).ShouldBeLessThan(0.8 * ErrorInside(master, scene, disk));
    }

    [Fact]
    public async Task AStacksHalvesLieOnItsGridFinishedAsItIs()
    {
        // A colour capture drifting across the frame: its master is cropped to the frames' coverage and its colours moved onto green, and
        // each half must be cut and moved the same, or half their difference holds the master's edge and its colour shift, not its noise.
        const int n = 64;
        var path = Path.Combine(_folders.Create("halves").FullName, "drift.ser");
        var rng = new Random(11);
        var frames = new ushort[12][];
        for (var i = 0; i < frames.Length; i++)
        {
            var (cx, cy) = (32 + (5 * Math.Sin(i)), 32 + (5 * Math.Cos(i * 0.7)));
            var frame = new ushort[n * n];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                    var level = 0.05 + (0.7 * Math.Exp(-d2 / (2 * 5.0 * 5.0))) + (0.01 * Gaussian(rng));
                    frame[(y * n) + x] = (ushort)Math.Clamp(level * 65535, 0, 65535);
                }
            }
            frames[i] = frame;
        }
        PlanetarySerFixtures.WriteSer(path, n, n, SerColorId.BayerRGGB, frames);

        using var stream = SerFrameStream.Open(path);
        var result = await new LuckyImagingStacker().StackAsync(stream,
            new PlanetaryStackOptions { KeepFraction = 1.0, AlignmentPatchSize = 16, Halves = true }, TestContext.Current.CancellationToken);
        try
        {
            var halves = result.Halves.ShouldNotBeNull();
            result.Cropped.IsEmpty.ShouldBeFalse("the drift must leave edges the frames did not all reach, or the crop goes untested");
            foreach (var half in new[] { halves.A, halves.B })
            {
                (half.Width, half.Height, half.ChannelCount).ShouldBe((result.Master.Width, result.Master.Height, result.Master.ChannelCount));
                for (var c = 0; c < half.ChannelCount; c++)
                {
                    // Each half is the master's scene with the noise of half its frames: within a few of its noise on the disk.
                    var masterPlane = result.Master.GetChannelSpan(c);
                    var halfPlane = half.GetChannelSpan(c);
                    double sum = 0;
                    for (var i = 0; i < masterPlane.Length; i++)
                    {
                        sum += Math.Abs(halfPlane[i] - masterPlane[i]);
                    }
                    (sum / masterPlane.Length).ShouldBeLessThan(0.01 * result.Master.MaxValue);
                }
            }
        }
        finally
        {
            result.Master.Release();
            result.Halves?.A.Release();
            result.Halves?.B.Release();
        }
    }

    // A disk with texture at every scale on a dark sky.
    private static float[] Scene()
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - (Size / 2.0)) * (x - (Size / 2.0))) + ((y - (Size / 2.0)) * (y - (Size / 2.0))));
                var disk = 1 / (1 + Math.Exp((r - Radius) / 1.5));
                plane[(y * Size) + x] = (float)(0.02 + (disk * (0.6 + (0.15 * Math.Sin(y * 0.35)) + (0.08 * Math.Sin((x * 0.9) + (y * 0.4))))));
            }
        }
        return plane;
    }

    private static double ErrorInside(float[] plane, float[] scene, MetricDisk disk)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (disk.RadiiAt(x, y) < PlanetaryBandShrink.InsideRadii)
                {
                    var d = plane[(y * Size) + x] - scene[(y * Size) + x];
                    sum += d * d;
                    count++;
                }
            }
        }
        return Math.Sqrt(sum / count);
    }

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
