using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="PsfMatch"/>, <see cref="SyntheticLuminance"/> and <see cref="LuminanceDetail"/>: mono channels brought
/// to one star width, made a luminance, and that luminance's detail given to each channel.</summary>
[Collection("Imaging")]
public class SyntheticLuminanceTests(ITestOutputHelper output)
{
    private const int Size = 384;

    [Fact]
    public void TheWideningKernelAddsInQuadrature()
    {
        PsfMatch.SigmaToWiden(2.0, 2.5).ShouldBe((float)(1.5 / PsfMatch.FwhmPerSigma), 1e-5f);
        PsfMatch.SigmaToWiden(2.5, 2.0).ShouldBe(0f);
    }

    /// <summary>Two channels of one field at different star widths come back at the wider one's, the wider untouched.</summary>
    [Fact]
    public async Task TheSharperChannelIsBlurredToTheWiderOnesStars()
    {
        var ct = TestContext.Current.CancellationToken;
        var sharp = Channel(starSigma: 0.9, colour: 1.0, noise: 2.0, seed: 1);
        var wide = Channel(starSigma: 1.25, colour: 1.0, noise: 2.0, seed: 2);

        var (images, report, target) = await PsfMatch.ToWidestAsync([sharp, wide], ct);

        output.WriteLine($"target {target:F2}: sharp {report[0].FwhmBefore:F2} -> {report[0].FwhmAfter:F2} (sigma {report[0].Sigma:F2}), wide {report[1].FwhmBefore:F2} -> {report[1].FwhmAfter:F2}");
        report[1].Sigma.ShouldBe(0f);
        images[1].ShouldBeSameAs(wide);
        report[0].FwhmAfter.ShouldBe(target, target * 0.06);
    }

    /// <summary>
    /// Three channels of one field in three colours and three noises: the scales onto the reference are the colours, the
    /// weights the inverse variances on its scale, and the luminance quieter than any channel, as the variances say.
    /// </summary>
    [Fact]
    public async Task TheLuminanceWeightsEachChannelByItsNoiseOnOneScale()
    {
        var ct = TestContext.Current.CancellationToken;
        double[] colour = [0.6, 1.0, 1.4];
        double[] noise = [2.0, 3.0, 6.0];
        var channels = new Image[3];
        for (var c = 0; c < 3; c++)
        {
            channels[c] = Channel(starSigma: 1.1, colour[c], noise[c], seed: 10 + c);
        }

        var (luminance, parts, lNoise) = await SyntheticLuminance.BuildAsync(channels, reference: 1, ct);

        // On green's scale a channel's noise is noise / colour; the weights are their inverse variances.
        var scaled = new double[3];
        double sum = 0;
        for (var c = 0; c < 3; c++)
        {
            scaled[c] = noise[c] / colour[c];
            sum += 1 / (scaled[c] * scaled[c]);
        }
        var expectedNoise = Math.Sqrt(1 / sum);
        for (var c = 0; c < 3; c++)
        {
            output.WriteLine($"channel {c}: scale {parts[c].Scale:F3} (true {1 / colour[c]:F3}), scaled noise {parts[c].ScaledNoise:F3} (true {scaled[c]:F3}), weight {parts[c].Weight:P1} (true {1 / (scaled[c] * scaled[c]) / sum:P1})");
            parts[c].Scale.ShouldBe(1 / colour[c], 0.03 / colour[c]);
            parts[c].Weight.ShouldBe(1 / (scaled[c] * scaled[c]) / sum, 0.03);
        }
        output.WriteLine($"luminance noise {lNoise:F3}, expected {expectedNoise:F3}, best channel {Math.Min(scaled[0], Math.Min(scaled[1], scaled[2])):F3}");
        lNoise.ShouldBe(expectedNoise, expectedNoise * 0.08);
        luminance.Width.ShouldBe(Size);
    }

    /// <summary>A channel keeps its own level above the colour blur and takes the luminance's quieter detail below it.</summary>
    [Fact]
    public void AChannelTakesTheLuminancesDetailAndKeepsItsOwnColour()
    {
        var noisy = Flat(level: 100, noise: 5, seed: 3);
        var quiet = Flat(level: 50, noise: 1, seed: 4);

        var detailed = LuminanceDetail.Apply(noisy, quiet, scale: 0.5, colourSigma: 1.5f);

        var before = SyntheticLuminance.Noise(noisy);
        var after = SyntheticLuminance.Noise(detailed);
        var level = Mean(detailed);
        output.WriteLine($"noise {before:F2} -> {after:F2}, level {level:F2}");
        after.ShouldBeLessThan(before * 0.6);
        level.ShouldBe(100, 0.5);
    }

    private static Image Channel(double starSigma, double colour, double noise, int seed)
    {
        var field = new Random(77);
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var s = 0; s < 220; s++)
        {
            var cx = field.NextDouble() * Size;
            var cy = field.NextDouble() * Size;
            var flux = colour * (3000 + (field.NextDouble() * 40000));
            var norm = flux / (2 * Math.PI * starSigma * starSigma);
            for (var y = Math.Max(0, (int)cy - 8); y < Math.Min(Size, (int)cy + 9); y++)
            {
                for (var x = Math.Max(0, (int)cx - 8); x < Math.Min(Size, (int)cx + 9); x++)
                {
                    plane[y, x] += (float)(norm * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / (2 * starSigma * starSigma)));
                }
            }
        }
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] += (float)(200 + (noise * Gaussian(rng)));
            }
        }
        return new Image([plane], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());
    }

    private static Image Flat(double level, double noise, int seed)
    {
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = (float)(level + (noise * Gaussian(rng)));
            }
        }
        return new Image([plane], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());
    }

    private static double Mean(Image image)
    {
        double sum = 0;
        var n = 0;
        foreach (var v in image.GetChannelSpan(0))
        {
            if (float.IsFinite(v))
            {
                sum += v;
                n++;
            }
        }
        return sum / n;
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
