using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="OnnxTianWenStarRemover"/>'s whole-frame pass (<see cref="StarRemoverRunner"/>): the views it averages are the
/// ones <c>r2_infer.py</c> made when the averages were scored, each undone exactly; the triangle windows sum to one wherever
/// two overlap; the frame is mirrored about its edge sample; and a whole frame goes through end to end, its canvas ring
/// left as it came.
/// </summary>
public class StarRemoverRunnerTests
{
    [Fact]
    public void AQuarterTurnIsTorchsRot90AndTheMirrorFlipsColumns()
    {
        var turned = StarRemoverRunner.ViewMap(new StarRemoverRunner.View(false, 1), 3);
        var mirrored = StarRemoverRunner.ViewMap(new StarRemoverRunner.View(true, 0), 3);

        // torch.rot90([[0,1,2],[3,4,5],[6,7,8]], 1, dims=(-2,-1)) is [[2,5,8],[1,4,7],[0,3,6]].
        turned.ShouldBe([2, 5, 8, 1, 4, 7, 0, 3, 6]);
        mirrored.ShouldBe([2, 1, 0, 5, 4, 3, 8, 7, 6]);
    }

    [Fact]
    public void TheEightViewsAreEightDifferentPermutationsEachUndoneThroughItsOwnMap()
    {
        const int n = 7;
        var rng = new Random(20261018);
        var tile = Enumerable.Range(0, n * n).Select(_ => (float)rng.NextDouble()).ToArray();
        var maps = StarRemoverRunner.Views(8).Select(v => StarRemoverRunner.ViewMap(v, n)).ToArray();

        maps.Select(static m => string.Join(",", m)).Distinct().Count().ShouldBe(8);
        foreach (var map in maps)
        {
            map.Distinct().Count().ShouldBe(n * n);
            var view = map.Select(i => tile[i]).ToArray();
            var back = new float[n * n];
            for (var i = 0; i < map.Length; i++)
            {
                back[map[i]] = view[i];
            }
            back.ShouldBe(tile);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    public void AViewCountTakesR2InfersViews(int count, int expected)
    {
        var views = StarRemoverRunner.Views(count);

        views.Length.ShouldBe(expected);
        views[0].ShouldBe(new StarRemoverRunner.View(false, 0));
        if (count == 2)
        {
            views[1].ShouldBe(new StarRemoverRunner.View(true, 0));
        }
    }

    [Fact]
    public void TrianglesSteppedByHalfTheInteriorSumToOneAndLeaveTheRimOut()
    {
        const int tile = 256, rim = 16, length = 2000;
        var weight = StarRemoverRunner.Triangle(tile, rim);
        var step = StarRemoverRunner.Step(tile, rim);
        var origins = StarRemoverRunner.Origins(length, tile, step);
        var sum = new double[length];
        foreach (var o in origins)
        {
            for (var k = 0; k < tile; k++)
            {
                sum[o + k] += weight[k];
            }
        }

        weight.Take(rim).ShouldAllBe(static w => w == 0f);
        weight.Skip(tile - rim).ShouldAllBe(static w => w == 0f);
        step.ShouldBe(112);
        // Where two windows overlap, up to where the last window (flush with the far end, off the stepping) starts to count;
        // past it the runner's division by the summed weights does the normalising.
        for (var p = rim + step; p < origins[^1] + rim; p++)
        {
            sum[p].ShouldBe(1.0, 1e-6, $"at {p}");
        }
    }

    [Fact]
    public void TheLastWindowSitsFlushWithTheFarEnd()
    {
        var origins = StarRemoverRunner.Origins(556, 256, 112);

        origins.ShouldBe([0, 112, 224, 300]);
    }

    [Theory]
    [InlineData(-1, 5, 1)]
    [InlineData(-2, 5, 2)]
    [InlineData(5, 5, 3)]
    [InlineData(6, 5, 2)]
    [InlineData(8, 5, 0)]
    [InlineData(9, 5, 1)]
    [InlineData(-7, 5, 1)]
    [InlineData(3, 1, 0)]
    public void TheMirrorReflectsAboutTheEdgeSample(int index, int length, int expected)
        => StarRemoverRunner.Mirror(index, length).ShouldBe(expected);

    /// <summary>
    /// End to end on the shipped denoiser's weights, a graph of the same shape (an image and its noise plane) standing in for
    /// a star remover: the pass runs its windows and views on a frame with a canvas ring, gives back the frame's shape,
    /// finite, with the ring exactly as it came.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task AWholeFrameGoesThroughWithItsCanvasRingKept()
    {
        if (!new ModelResolver().TryResolve(N2nDenoiser.ModelFileName, out var modelPath) || modelPath is null)
        {
            Assert.Skip($"{N2nDenoiser.ModelFileName} not found (or is an LFS pointer); run 'git lfs pull' to enable this test.");
            return;
        }
        const int width = 300, height = 280, ring = 6;
        var rng = new Random(7);
        var data = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            data[c] = new float[height, width];
            for (var y = ring; y < height - ring; y++)
            {
                for (var x = ring; x < width - ring; x++)
                {
                    var star = 0.4f * MathF.Exp(-((x - 150) * (x - 150) + (y - 140) * (y - 140)) / 8f);
                    data[c][y, x] = 0.01f + 0.0015f * (float)rng.NextDouble() + star;
                }
            }
        }
        var input = new Image(data, BitDepth.Float32, 1f, 0f, 0f, default);

        using var remover = OnnxTianWenStarRemover.Open([modelPath], views: 2);
        var output = await remover.EnhanceAsync(input, TestContext.Current.CancellationToken);

        output.Shape.ShouldBe(input.Shape);
        for (var c = 0; c < 3; c++)
        {
            var o = output.GetChannelSpan(c).ToArray();
            o.ShouldAllBe(static v => float.IsFinite(v));
            for (var x = 0; x < width; x++)
            {
                o[x].ShouldBe(0f);
                o[(height - 1) * width + x].ShouldBe(0f);
            }
        }
    }
}
