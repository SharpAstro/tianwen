using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="RcAstroEnhancerBase"/> hands RC-Astro a plate inside <c>[0, 1]</c> whatever the caller's plate holds, and
/// gives the result back on the caller's scale: RC-Astro rescales a plate outside the range itself and returns its output on
/// that scale, which put a starless red's sky at 0.031 where the plate's was 0.009.
/// </summary>
[Collection("Imaging")]
public class RcAstroRangeMapTests
{
    /// <summary>A product that changes nothing: it hands its input back, and remembers the range it was handed.</summary>
    private sealed class EchoCli : IRcAstroCli
    {
        public string? ExecutablePath => "/fake/rc-astro";
        public bool IsAvailable => true;
        public bool IsLicensed(string productKey) => true;

        public float HandedMin { get; private set; }
        public float HandedMax { get; private set; }

        public Task<RcAstroRunResult> RunAsync(
            string productKey, string inputPath, string outputPath,
            IReadOnlyList<string> extraArgs, IProgress<RcAstroProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Image.TryReadFitsFile(inputPath, out var handed).ShouldBeTrue();
            HandedMin = float.PositiveInfinity;
            HandedMax = float.NegativeInfinity;
            foreach (var v in handed.GetChannelSpan(0))
            {
                HandedMin = Math.Min(HandedMin, v);
                HandedMax = Math.Max(HandedMax, v);
            }
            File.Copy(inputPath, outputPath, overwrite: true);
            return Task.FromResult(new RcAstroRunResult("cpu", "Fake", new RcAstroProgress(100, 1, 0)));
        }
    }

    [Theory]
    [InlineData(-0.02f, 0.9f)]
    [InlineData(0.01f, 1.5f)]
    [InlineData(-0.05f, 1.2f)]
    public async Task APlateOutsideTheUnitRangeIsHandedInsideItAndComesBackOnItsOwnScale(float low, float high)
    {
        var cli = new EchoCli();
        var plate = Ramp(low, high);

        var result = await new RcAstroDeblurrer(cli).EnhanceAsync(plate, TestContext.Current.CancellationToken);

        cli.HandedMin.ShouldBeGreaterThanOrEqualTo(0f);
        cli.HandedMax.ShouldBeLessThanOrEqualTo(1f);
        var src = plate.GetChannelSpan(0);
        var back = result.GetChannelSpan(0);
        for (var i = 0; i < src.Length; i++)
        {
            back[i].ShouldBe(src[i], 1e-6f);
        }
    }

    /// <summary>A plate already inside the range is handed over as it is, byte for byte.</summary>
    [Fact]
    public async Task APlateInsideTheUnitRangeIsHandedAsItIs()
    {
        var cli = new EchoCli();
        var plate = Ramp(0.01f, 0.9f);

        var result = await new RcAstroDeblurrer(cli).EnhanceAsync(plate, TestContext.Current.CancellationToken);

        cli.HandedMin.ShouldBe(0.01f);
        cli.HandedMax.ShouldBe(0.9f);
        result.GetChannelSpan(0).ToArray().ShouldBe(plate.GetChannelSpan(0).ToArray());
    }

    private static Image Ramp(float low, float high)
    {
        const int width = 16;
        const int height = 8;
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = low + ((high - low) * ((y * width) + x) / ((width * height) - 1f));
            }
        }
        return new Image([plane], BitDepth.Float32, high, low, 0f, new ImageMeta());
    }
}
