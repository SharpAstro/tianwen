using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class LuckyImagingStackerTests
{
    private const int W = 64, H = 64;

    // A mono Gaussian-blob frame as 16-bit samples, with optional additive noise.
    private static ushort[] BlobFrame(double cx, double cy, double sigma, double peak, Random? noise, double noiseAmp)
    {
        var f = new ushort[W * H];
        var twoSigma2 = 2.0 * sigma * sigma;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                var v = peak * Math.Exp(-d2 / twoSigma2);
                if (noise is not null)
                {
                    v += (noise.NextDouble() - 0.5) * noiseAmp;
                }

                v = Math.Clamp(v, 0.0, 1.0);
                f[(y * W) + x] = (ushort)(v * 60000);
            }
        }

        return f;
    }

    private static double CornerStd(Image img)
    {
        double sum = 0, sum2 = 0;
        var n = 0;
        for (var y = 2; y < 14; y++)
        {
            for (var x = 2; x < 14; x++)
            {
                double v = img[0, y, x];
                sum += v;
                sum2 += v * v;
                n++;
            }
        }

        var mean = sum / n;
        return Math.Sqrt(Math.Max((sum2 / n) - (mean * mean), 0));
    }

    [Fact]
    public async Task StackGlobal_aligns_drifting_blobs_to_reference()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            const double bx = 28.0, by = 36.0, sigma = 3.0;
            double[] dxs = [0, 1.3, -2.1, 0.7, -1.5, 2.4, -0.6, 1.1];
            double[] dys = [0, -1.1, 0.9, 2.2, -0.4, 1.7, -2.3, 0.5];
            var frames = new ushort[dxs.Length][];
            for (var i = 0; i < dxs.Length; i++)
            {
                frames[i] = BlobFrame(bx + dxs[i], by + dys[i], sigma, 0.8, noise: null, noiseAmp: 0);
            }

            PlanetarySerFixtures.WriteSer(path, W, H, SerColorId.Mono, frames);

            using var stream = SerFrameStream.Open(path);
            var result = await new LuckyImagingStacker()
                .StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = 1.0 }, TestContext.Current.CancellationToken);

            result.FramesGraded.ShouldBe(8);
            result.FramesUsed.ShouldBe(8);

            // Aligned to whichever frame was chosen as reference -> master blob sits at that frame's centre.
            var refX = bx + dxs[result.ReferenceIndex];
            var refY = by + dys[result.ReferenceIndex];
            var (mx, my) = PlanetaryDisk.CenterOfMass(result.Master, PlanetaryDisk.BoundingBox(result.Master));
            mx.ShouldBe(refX, 0.7);
            my.ShouldBe(refY, 0.7);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StackGlobal_selects_sharpest_as_reference_and_keeps_fraction()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            // All sigma 4 (soft) except index 5 (sharpest, sigma 2) and index 2 (sigma 3).
            var frames = new ushort[8][];
            for (var i = 0; i < 8; i++)
            {
                var sigma = i == 5 ? 2.0 : i == 2 ? 3.0 : 4.0;
                frames[i] = BlobFrame(32 + (i * 0.3), 32 - (i * 0.2), sigma, 0.8, noise: null, noiseAmp: 0);
            }

            PlanetarySerFixtures.WriteSer(path, W, H, SerColorId.Mono, frames);

            using var stream = SerFrameStream.Open(path);
            var result = await new LuckyImagingStacker()
                .StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = 0.25 }, TestContext.Current.CancellationToken);

            result.ReferenceIndex.ShouldBe(5);  // sharpest
            result.FramesUsed.ShouldBe(2);       // top 25% of 8
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StackGlobal_reduces_background_noise()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            var rng = new Random(42);
            var frames = new ushort[12][];
            for (var i = 0; i < 12; i++)
            {
                frames[i] = BlobFrame(32 + (i * 0.4 - 2), 32 - (i * 0.3 - 1.5), 3.0, 0.7, rng, noiseAmp: 0.08);
            }

            PlanetarySerFixtures.WriteSer(path, W, H, SerColorId.Mono, frames);

            // Background noise std of a single input frame.
            double singleFrameNoise;
            using (var probe = SerFrameStream.Open(path))
            {
                var f0 = await probe.LoadAsync(0, TestContext.Current.CancellationToken);
                singleFrameNoise = CornerStd(f0);
                f0.Release();
            }

            using var stream = SerFrameStream.Open(path);
            var result = await new LuckyImagingStacker()
                .StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = 1.0 }, TestContext.Current.CancellationToken);

            // Averaging ~12 aligned frames knocks the background noise down well below one frame's.
            CornerStd(result.Master).ShouldBeLessThan(singleFrameNoise * 0.7);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
