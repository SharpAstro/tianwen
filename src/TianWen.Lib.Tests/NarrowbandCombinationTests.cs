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
