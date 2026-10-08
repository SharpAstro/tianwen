using System;
using System.Linq;
using System.Threading;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R2e's S4: holes cut in filaments where the truth is known, filled by R0's push-pull and by the conditional draw of the
/// steered texture.
/// </summary>
public sealed class TexturedHoleFillTests
{
    private const int Size = 512;
    private const float Noise = 0.002f;
    private const double Fwhm = 2.5;

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private const int HoleRadius = 20;
    private const int InteriorRadius = 10;

    // A sky whose right quarter holds filaments along y (white noise smeared 6 px along y and 1 px across, so their
    // gradient runs along x) at four times the noise over a gentle level, and discs cut on a grid through them, with
    // their interiors. Broadband, as a nebula's fine structure is: a pure sinusoid put its one frequency into every
    // starlet scale's amplitude map and the texture drawn at each piled up where the bands overlap (2.6 times its
    // energy). A quarter, as a nebula is a minority of a real plate's sky: the generator reads its noise off the clean sky
    // as a whole, and a sky structured edge to edge read the structure as noise and drew nothing. The interiors are 10 px
    // in: the 4 px starlet scale reaches 8 px, so a hole's own edge is out of its kernel (read at every hole pixel, the
    // push-pull's edge against the structure was 3.5 times its energy).
    private static (float[] Plane, BitMatrix Holes, BitMatrix Interiors) FilamentSky()
    {
        var rng = new Random(31);
        var white = new float[Size * Size];
        for (var i = 0; i < white.Length; i++)
        {
            white[i] = (float)Gaussian(rng);
        }
        var filaments = Smear(white, sigmaX: 1.0, sigmaY: 6.0);
        var quarter = Enumerable.Range(0, Size * Size).Where(static i => i % Size >= 3 * Size / 4).Select(i => (double)filaments[i]).ToArray();
        var rms = Math.Sqrt(quarter.Sum(static v => v * v) / quarter.Length);
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size) + x;
                var fine = x >= 3 * Size / 4 ? 4 * Noise * filaments[i] / rms : 0.0;
                plane[i] = (float)(0.1 + (0.02 * y / Size) + fine + (Noise * Gaussian(rng)));
            }
        }
        var holes = new BitMatrix(Size, Size);
        var interiors = new BitMatrix(Size, Size);
        for (var cy = 64; cy <= Size - 64; cy += 64)
        {
            const int cx = 448;
            for (var y = cy - HoleRadius; y <= cy + HoleRadius; y++)
            {
                for (var x = cx - HoleRadius; x <= cx + HoleRadius; x++)
                {
                    var r2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                    holes[y, x] = holes[y, x] || r2 <= HoleRadius * HoleRadius;
                    interiors[y, x] = interiors[y, x] || r2 <= InteriorRadius * InteriorRadius;
                }
            }
        }
        return (plane, holes, interiors);
    }

    // A Gaussian blur of its own width along each axis, the edges clamped.
    private static float[] Smear(float[] plane, double sigmaX, double sigmaY)
    {
        static double[] Kernel(double sigma)
        {
            var r = (int)Math.Ceiling(3 * sigma);
            var k = Enumerable.Range(-r, (2 * r) + 1).Select(i => Math.Exp(-0.5 * i * i / (sigma * sigma))).ToArray();
            var sum = k.Sum();
            return [.. k.Select(v => v / sum)];
        }
        var (kx, ky) = (Kernel(sigmaX), Kernel(sigmaY));
        var (rx, ry) = (kx.Length / 2, ky.Length / 2);
        var across = new float[plane.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = 0.0;
                for (var i = -rx; i <= rx; i++)
                {
                    v += kx[i + rx] * plane[(y * Size) + Math.Clamp(x + i, 0, Size - 1)];
                }
                across[(y * Size) + x] = (float)v;
            }
        }
        var along = new float[plane.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = 0.0;
                for (var j = -ry; j <= ry; j++)
                {
                    v += ky[j + ry] * across[(Math.Clamp(y + j, 0, Size - 1) * Size) + x];
                }
                along[(y * Size) + x] = (float)v;
            }
        }
        return along;
    }

    // Over the holes' interiors: the 4 px starlet scale's energy, and the coherence-weighted cos 2 theta of its gradient
    // (+1 along x, the filaments' own).
    private static (double Energy, double Along) Read(float[] plane, BitMatrix interiors)
    {
        var detail = ATrousWaveletTransform.Decompose(plane, Size, Size, 4).Detail(2).ToArray();
        var (coherence, cos2, _, _) = SkyTexture.StructureTensor(detail, Size, Size, sigma: 3f);
        double energy = 0, weighted = 0, weights = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!interiors[y, x])
                {
                    continue;
                }
                var i = (y * Size) + x;
                energy += detail[i] * detail[i];
                weighted += coherence[i] * cos2[i];
                weights += coherence[i];
            }
        }
        return (energy, weights > 0 ? weighted / weights : 0);
    }

    [Fact]
    public void TheConditionalDrawPutsTheFilamentsBackIntoTheHolesWhereThePushPullLeavesThemSmooth()
    {
        var (original, holes, interiors) = FilamentSky();
        var pushPull = new[] { (float[])original.Clone() };
        HoleFill.Fill(pushPull, Size, Size, holes, absent: null, Fwhm, seed: 1, ceiling: null, CancellationToken.None);
        var drawn = new[] { (float[])original.Clone() };
        TexturedHoleFill.Fill(drawn, Size, Size, holes, absent: null, Fwhm, seed: 1, ceiling: null,
            new SyntheticBackground.Steering(Strength: 1.0, Exponent: 4.0, Fine: 1.0), CancellationToken.None);

        var truth = Read(original, interiors);
        var smooth = Read(pushPull[0], interiors);
        var textured = Read(drawn[0], interiors);

        TestContext.Current.TestOutputHelper?.WriteLine($"energy over truth: push-pull {smooth.Energy / truth.Energy:F3}, drawn {textured.Energy / truth.Energy:F3}; along: truth {truth.Along:F3}, push-pull {smooth.Along:F3}, drawn {textured.Along:F3}");
        (smooth.Energy / truth.Energy).ShouldBeLessThan(0.5, "the push-pull fill is smooth across the filaments");
        (textured.Energy / truth.Energy).ShouldBeInRange(0.5, 1.5, "the draw puts their energy back");
        textured.Along.ShouldBeGreaterThan(0.3, "running the filaments' way, read off the sky round the holes");
        drawn[0].Where((v, i) => !holes[i / Size, i % Size]).SequenceEqual(original.Where((v, i) => !holes[i / Size, i % Size]))
            .ShouldBeTrue("and nothing outside a hole moves");
    }
}
