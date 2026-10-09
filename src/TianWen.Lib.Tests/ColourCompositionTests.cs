using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="ColourComposition"/>, the recipe, against its steps run one at a time with a FITS file between each, as the
/// <c>image</c> verbs run them: the two must give the same image, or the PixInsight-style steps and the one-call recipe
/// would be two tools that drift.
/// </summary>
[Collection("Imaging")]
public class ColourCompositionTests(ITestOutputHelper output)
{
    private const int Size = 160;

    /// <summary>A star remover that takes every pixel to the median of its 5 by 5 neighbourhood.</summary>
    private sealed class MedianStarRemover : IStarRemover
    {
        public string Name => "Test/Median";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(Filter(input, static window =>
            {
                Array.Sort(window);
                return window[window.Length / 2];
            }, radius: 2));
    }

    /// <summary>A deblurrer that lifts every pixel above its 3 by 3 mean, as a sharpening lifts a star.</summary>
    private sealed class UnsharpDeblurrer : IImageDeblurrer
    {
        public string Name => "Test/Unsharp";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(Filter(input, static window =>
            {
                var mean = 0f;
                foreach (var v in window)
                {
                    mean += v;
                }
                mean /= window.Length;
                return window[window.Length / 2] + (0.6f * (window[window.Length / 2] - mean));
            }, radius: 1));
    }

    /// <summary>A denoiser that blends every pixel toward its 3 by 3 mean by the strength asked (all of it by default).</summary>
    private sealed class BoxDenoiser : IDenoiseEnhancer
    {
        public string Name => "Test/Box";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => EnhanceAsync(input, EnhanceOptions.Default, null, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            var strength = options.Tuning?.DenoiseStrength ?? 1f;
            return Task.FromResult(Filter(input, window =>
            {
                var mean = 0f;
                foreach (var v in window)
                {
                    mean += v;
                }
                mean /= window.Length;
                var centre = window[window.Length / 2];
                return centre + (strength * (mean - centre));
            }, radius: 1));
        }
    }

    [Fact]
    public async Task TheStepsRunOneByOneThroughFilesGiveTheRecipesImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = SharedTestData.CreateTempTestOutputDir();
        // The masters as every path reads them: from a file, the absent ring masked.
        Image[] masters = [Read(Write(Master(colour: 0.7, sky: 300, noise: 3, line: 0, seed: 1), dir, "red")),
            Read(Write(Master(colour: 1.0, sky: 400, noise: 4, line: 0, seed: 2), dir, "green")),
            Read(Write(Master(colour: 1.3, sky: 250, noise: 5, line: 0, seed: 3), dir, "blue")),
            Read(Write(Master(colour: 0.15, sky: 60, noise: 2, line: 900, seed: 4), dir, "ha"))];
        var remover = new MedianStarRemover();
        var deblurrer = new UnsharpDeblurrer();
        var denoiser = new BoxDenoiser();
        var options = new ColourCompositionOptions { Deblur = true, Starless = true, Lrgb = true, Denoise = true };

        var recipe = await ColourComposition.RunAsync(masters, options, new ColourCompositionRoles(remover, deblurrer, denoiser), output.WriteLine, ct);

        // image linear-fit green.fits red.fits blue.fits
        var red = RoundTrip(LinearFit.Apply(masters[0], LinearFit.Measure(masters[0], masters[1])), dir, "red_linearfit");
        var blue = RoundTrip(LinearFit.Apply(masters[2], LinearFit.Measure(masters[2], masters[1])), dir, "blue_linearfit");
        red.ImageMeta.FluxScale.ShouldNotBeNull();
        // image deblur red_linearfit.fits green.fits blue_linearfit.fits ha.fits
        var (deblurred, _) = await NarrowbandCombination.DeblurAsync([red, masters[1], blue, masters[3]], deblurrer, cancellationToken: ct);
        deblurred = [RoundTrip(deblurred[0], dir, "r_d"), RoundTrip(deblurred[1], dir, "g_d"), RoundTrip(deblurred[2], dir, "b_d"), RoundTrip(deblurred[3], dir, "h_d")];
        // image continuum --dry-run --line ha_deblur.fits --continuum red_linearfit_deblur.fits (the number carried by hand)
        var k = ContinuumSubtractor.FlattestResidualScale(deblurred[3], deblurred[0]).K;
        // image remove-stars (all four, one scale)
        var (starless, stars) = await NarrowbandCombination.SplitStarsAsync(deblurred, remover, ct);
        starless = [RoundTrip(starless[0], dir, "r_s"), RoundTrip(starless[1], dir, "g_s"), RoundTrip(starless[2], dir, "b_s"), RoundTrip(starless[3], dir, "h_s")];
        stars = [RoundTrip(stars[0], dir, "r_st", mask: false), RoundTrip(stars[1], dir, "g_st", mask: false), RoundTrip(stars[2], dir, "b_st", mask: false)];
        // image add-line --broadband red_starless --line ha_starless --scale k
        var (redLine, _) = NarrowbandCombination.AddLineFrom(starless[0], starless[3], k, 1.0);
        redLine = RoundTrip(redLine, dir, "r_line");
        // image add-stars, then image luminance on the three with their stars
        Image[] full = [RoundTrip(NarrowbandCombination.WithStars(redLine, stars[0]), dir, "r_full"),
            RoundTrip(NarrowbandCombination.WithStars(starless[1], stars[1]), dir, "g_full"),
            RoundTrip(NarrowbandCombination.WithStars(starless[2], stars[2]), dir, "b_full")];
        var (luminance, _, _) = await SyntheticLuminance.BuildAsync(full, null, reference: 1, ct);
        luminance = RoundTrip(luminance, dir, "lum");
        // image remove-stars lum.fits; image denoise lum_starless.fits
        var (luminanceStarless, _) = await NarrowbandCombination.SplitStarsAsync([luminance], remover, ct);
        var luminanceDenoised = RoundTrip((await NarrowbandCombination.DenoiseAsync([RoundTrip(luminanceStarless[0], dir, "lum_s")], denoiser, options.DetailDenoise, ct))[0], dir, "lum_s_dn");
        // image denoise --colour --strength 0.5 red_line.fits green_starless.fits blue_starless.fits
        var colour = await NarrowbandCombination.DenoiseColourAsync(redLine, starless[1], starless[2], denoiser, options.ColourDenoise, ct);
        // image lrgb --luminance lum_s_dn.fits ..., then image add-stars
        var chain = new Image[3];
        for (var i = 0; i < 3; i++)
        {
            var channel = RoundTrip(colour[i], dir, $"c{i}_dn");
            var detailed = RoundTrip(LuminanceDetail.Apply(channel, luminanceDenoised, LuminanceDetail.ScaleFor(channel, luminanceDenoised)), dir, $"c{i}_lrgb");
            chain[i] = NarrowbandCombination.WithStars(detailed, stars[i]);
        }

        Image[] fromRecipe = [recipe.Red, recipe.Green, recipe.Blue];
        for (var i = 0; i < 3; i++)
        {
            var a = fromRecipe[i].GetChannelSpan(0);
            var b = chain[i].GetChannelSpan(0);
            var differing = 0;
            for (var p = 0; p < a.Length; p++)
            {
                if (!(a[p] == b[p] || (float.IsNaN(a[p]) && float.IsNaN(b[p]))))
                {
                    differing++;
                }
            }
            output.WriteLine($"channel {i}: {differing} of {a.Length} pixels differ");
            differing.ShouldBe(0);
        }
    }

    /// <summary>A linear fit's slope survives the file, so a line added to the fitted channel later is worth what it was.</summary>
    [Fact]
    public void AFittedChannelSaysItsScaleInItsFile()
    {
        var dir = SharedTestData.CreateTempTestOutputDir();
        var red = Master(colour: 0.7, sky: 300, noise: 3, line: 0, seed: 1);
        var green = Master(colour: 1.0, sky: 400, noise: 4, line: 0, seed: 2);
        var fit = LinearFit.Measure(red, green);

        var fitted = RoundTrip(LinearFit.Apply(red, fit), dir, "fitted");
        var line = Master(colour: 0.15, sky: 60, noise: 2, line: 900, seed: 4);

        fitted.ImageMeta.FluxScale.ShouldNotBeNull();
        fitted.ImageMeta.FluxScale.Value.ShouldBe(fit.Slope, 1e-9);
        NarrowbandCombination.LineToBroadband(line.ImageMeta, fitted.ImageMeta).ShouldBe(0.5 * fit.Slope, 1e-9);
    }

    // A field of stars in one colour over a sky, with a broad nebula and, for a line master, an emission blob; exposure
    // 900 s for broadband, 1800 s for the line.
    private static Image Master(double colour, double sky, double noise, double line, int seed)
    {
        var field = new Random(42);
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var s = 0; s < 140; s++)
        {
            var cx = 6 + (field.NextDouble() * (Size - 12));
            var cy = 6 + (field.NextDouble() * (Size - 12));
            var flux = colour * (4000 + (field.NextDouble() * 50000));
            var norm = flux / (2 * Math.PI * 1.3 * 1.3);
            for (var y = Math.Max(0, (int)cy - 6); y < Math.Min(Size, (int)cy + 7); y++)
            {
                for (var x = Math.Max(0, (int)cx - 6); x < Math.Min(Size, (int)cx + 7); x++)
                {
                    plane[y, x] += (float)(norm * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / (2 * 1.3 * 1.3)));
                }
            }
        }
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var nebula = 150 * colour * Math.Exp(-(((x - 70) * (x - 70)) + ((y - 90) * (y - 90))) / 2500.0);
                var emission = line * Math.Exp(-(((x - 100) * (x - 100)) + ((y - 60) * (y - 60))) / 900.0);
                plane[y, x] += (float)(sky + nebula + emission + (noise * Gaussian(rng)));
            }
        }
        var meta = new ImageMeta { ExposureDuration = TimeSpan.FromSeconds(line > 0 ? 1800 : 900) };
        var max = float.NegativeInfinity;
        foreach (var v in plane)
        {
            max = Math.Max(max, v);
        }
        return new Image([plane], BitDepth.Float32, max, 0f, 0f, meta);
    }

    private static string Write(Image image, string dir, string name)
    {
        var path = Path.Combine(dir, name + ".fits");
        image.WriteToFitsFile(path);
        return path;
    }

    private static Image Read(string path, bool mask = true)
    {
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue();
        return mask ? MasterAlignment.MaskAbsent(image) : image;
    }

    private static Image RoundTrip(Image image, string dir, string name, bool mask = true) => Read(Write(image, dir, name), mask);

    private static Image Filter(Image input, Func<float[], float> reduce, int radius)
    {
        var (channels, width, height) = input.Shape;
        var planes = new float[channels][,];
        var side = (2 * radius) + 1;
        var window = new float[side * side];
        for (var c = 0; c < channels; c++)
        {
            var src = input.GetChannelSpan(c);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var n = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var yy = Math.Clamp(y + dy, 0, height - 1);
                            var xx = Math.Clamp(x + dx, 0, width - 1);
                            window[n++] = src[(yy * width) + xx];
                        }
                    }
                    plane[y, x] = reduce(window);
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta);
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
