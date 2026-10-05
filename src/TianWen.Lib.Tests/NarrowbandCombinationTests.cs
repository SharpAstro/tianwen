using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="NarrowbandCombination"/>: masters through a star remover on one scale, and a line added to a
/// broadband channel.</summary>
[Collection("Imaging")]
public class NarrowbandCombinationTests
{
    /// <summary>A remover that halves its input and remembers what it was handed.</summary>
    private sealed class HalvingRemover : IStarRemover
    {
        public List<float[]> Inputs { get; } = [];

        public string Name => "Test/Halving";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var src = input.GetChannelSpan(0);
            Inputs.Add(src.ToArray());
            var plane = new float[input.Height, input.Width];
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    plane[y, x] = 0.5f * src[(y * input.Width) + x];
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>
    /// Every plane reaches the remover divided by ONE divisor, the largest value among them, so their linear relation
    /// holds through it; an absent pixel is handed over filled and comes back absent; each plane returns on its own scale.
    /// </summary>
    [Fact]
    public async Task EveryPlaneGoesThroughTheRemoverOnOneScale()
    {
        var a = Plane(1000f, nanAt: -1);
        var b = Plane(4000f, nanAt: 5);
        var remover = new HalvingRemover();

        var starless = await NarrowbandCombination.StarlessAsync([a, b], remover, TestContext.Current.CancellationToken);

        remover.Inputs.Count.ShouldBe(2);
        remover.Inputs[0].Max().ShouldBe(0.25f, 1e-6f);
        remover.Inputs[1].Max().ShouldBe(1f, 1e-6f);
        remover.Inputs[1].ShouldAllBe(v => float.IsFinite(v));
        float.IsNaN(starless[1][0, 0, 5]).ShouldBeTrue();
        starless[0][0, 0, 3].ShouldBe(0.5f * a[0, 0, 3], 1e-2f);
        starless[1][0, 0, 3].ShouldBe(0.5f * b[0, 0, 3], 1e-2f);

        var stars = NarrowbandCombination.Stars(a, starless[0]);
        var back = NarrowbandCombination.WithStars(starless[0], stars);
        back[0, 0, 7].ShouldBe(a[0, 0, 7], 1e-3f);
    }

    /// <summary>A deblurrer that lifts every value by a gain, as sharpening lifts a star's peak, and stops at the ceiling of
    /// <c>[0, 1]</c> as BlurX's output does.</summary>
    private sealed class SharpeningDeblurrer(float gain) : IImageDeblurrer
    {
        public List<float> PeaksSeen { get; } = [];

        public string Name => "Test/Sharpening";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var src = input.GetChannelSpan(0);
            PeaksSeen.Add(src.ToArray().Max());
            var plane = new float[input.Height, input.Width];
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    plane[y, x] = Math.Min(1f, gain * src[(y * input.Width) + x]);
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>
    /// With the default headroom the brightest plane's peak reaches the deblurrer at a quarter of the ceiling, so a peak
    /// sharpened 3.4 times stays below it; every plane comes back on its own scale and an absent pixel absent.
    /// </summary>
    [Fact]
    public async Task ADeblurredPlaneComesBackOnItsOwnScaleWithNoStarAtTheCeiling()
    {
        var a = Plane(1000f, nanAt: 5);
        var b = Plane(3000f, nanAt: -1);
        var deblurrer = new SharpeningDeblurrer(3.4f);

        var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync([a, b], deblurrer, cancellationToken: TestContext.Current.CancellationToken);

        deblurrer.PeaksSeen[1].ShouldBe(1f / NarrowbandCombination.DefaultDeblurHeadroom, 1e-6f);
        atCeiling.ShouldBe(0);
        deblurred[1][0, 0, 9].ShouldBe(3.4f * b[0, 0, 9], 1e-2f);
        deblurred[0][0, 0, 3].ShouldBe(3.4f * a[0, 0, 3], 1e-2f);
        float.IsNaN(deblurred[0][0, 0, 5]).ShouldBeTrue();
    }

    /// <summary>At the peak's own scale, as the deblur verb used to hand it over, the sharpened stars clip and are counted.</summary>
    [Fact]
    public async Task WithoutHeadroomTheSharpenedPeaksClipAndAreCounted()
    {
        var b = Plane(3000f, nanAt: -1);

        var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync([b], new SharpeningDeblurrer(3.4f), headroom: 1f,
            cancellationToken: TestContext.Current.CancellationToken);

        atCeiling.ShouldBe(8);
        deblurred[0][0, 0, 9].ShouldBe(3000f, 1e-2f);
    }

    /// <summary>A denoiser that pulls every value halfway to 0.1 and remembers the channel counts and strengths it was
    /// handed.</summary>
    private sealed class RecordingDenoiser : IDenoiseEnhancer
    {
        public List<(int Channels, float? Strength)> Calls { get; } = [];

        public string Name => "Test/Denoise";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => EnhanceAsync(input, EnhanceOptions.Default, null, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((input.ChannelCount, options.Tuning?.DenoiseStrength));
            var planes = new float[input.ChannelCount][,];
            for (var c = 0; c < input.ChannelCount; c++)
            {
                var src = input.GetChannelSpan(c);
                var plane = new float[input.Height, input.Width];
                for (var y = 0; y < input.Height; y++)
                {
                    for (var x = 0; x < input.Width; x++)
                    {
                        plane[y, x] = 0.5f * (src[(y * input.Width) + x] + 0.1f);
                    }
                }
                planes[c] = plane;
            }
            return Task.FromResult(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>The three starless channels reach the denoiser as ONE colour image at the strength asked, and come back
    /// as three planes on their own scales, an absent pixel absent.</summary>
    [Fact]
    public async Task TheColourIsDenoisedAsOneImageAndComesBackAsThreePlanes()
    {
        var red = Plane(1000f, nanAt: 4);
        var green = Plane(2000f, nanAt: -1);
        var blue = Plane(500f, nanAt: -1);
        var denoiser = new RecordingDenoiser();
        var options = new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: 0.5f));

        var denoised = await NarrowbandCombination.DenoiseColourAsync(red, green, blue, denoiser, options,
            TestContext.Current.CancellationToken);

        denoiser.Calls.ShouldBe([(3, 0.5f)]);
        denoised.Length.ShouldBe(3);
        // One divisor, the brightest value of the three (2000): v -> 0.5 (v / 2000 + 0.1) * 2000.
        denoised[1][0, 0, 9].ShouldBe(0.5f * (green[0, 0, 9] + 200f), 1e-2f);
        denoised[2][0, 0, 3].ShouldBe(0.5f * (blue[0, 0, 3] + 200f), 1e-2f);
        float.IsNaN(denoised[0][0, 0, 4]).ShouldBeTrue();
    }

    /// <summary>A line goes into a broadband channel above its own background, and its counts are worth the exposure
    /// ratio there.</summary>
    [Fact]
    public void ALineIsAddedAboveItsOwnBackground()
    {
        var broadband = Plane(100f, nanAt: -1);
        var line = new Image([new float[,] { { 10f, 10f, 10f, 10f, 10f, 10f, 10f, 10f, 30f, 10f } }], BitDepth.Float32, 30f, 0f, 0f, new ImageMeta());

        var added = NarrowbandCombination.AddLine(broadband, line, 0.5);

        added[0, 0, 0].ShouldBe(broadband[0, 0, 0], 1e-4f);
        added[0, 0, 8].ShouldBe(broadband[0, 0, 8] + 10f, 1e-4f);
        NarrowbandCombination.LineToBroadband(
            new ImageMeta { ExposureDuration = TimeSpan.FromSeconds(1800) },
            new ImageMeta { ExposureDuration = TimeSpan.FromSeconds(900) }).ShouldBe(0.5);
        NarrowbandCombination.LineToBroadband(new ImageMeta(), new ImageMeta { ExposureDuration = TimeSpan.FromSeconds(900) }).ShouldBe(1.0);
    }

    private static Image Plane(float max, int nanAt)
    {
        var plane = new float[1, 10];
        for (var x = 0; x < 10; x++)
        {
            plane[0, x] = max * (x + 1) / 10f;
        }
        if (nanAt >= 0)
        {
            plane[0, nanAt] = float.NaN;
        }
        return new Image([plane], BitDepth.Float32, max, 0f, 0f, new ImageMeta());
    }
}
