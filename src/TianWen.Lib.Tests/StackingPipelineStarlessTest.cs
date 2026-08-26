using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for <c>StackingOptions.RemoveStarsPerFrame</c> (<c>stack --remove-stars</c>), the comet
/// LAYER path: every light is calibrated and run through an <see cref="IStarRemover"/> before
/// integration, so the stars never enter the stack and cannot trail.
///
/// <para>The assertion that matters is what the star remover is HANDED. A star remover normalises
/// internally and clips whatever is already above its range, so a calibrated 16-bit frame (sky
/// background in the thousands of ADU) comes back uniformly 1.0 -- every pixel white, no exception,
/// no warning, and a master that is a flat white rectangle. Measured on a real 60 s sub: ADU in
/// (min 1796 / med 3928 / max 65535) returned min = med = max = 1, while the same frame divided by
/// its container full scale returned min 0.028 / med 0.055 / max 0.14.</para>
/// </summary>
[Collection("Imaging")]
public class StackingPipelineStarlessTest(ITestOutputHelper output)
{
    /// <summary>
    /// Records what it was handed and returns a plate with the stellar peaks pulled down, which is
    /// the part of star removal the pipeline's arithmetic depends on: a starless plate's brightest
    /// pixel is far below the frame's full scale.
    /// </summary>
    private sealed class RecordingStarRemover : IStarRemover
    {
        public ConcurrentBag<(float Max, float Median, int Channels, int Width, int Height)> Seen { get; } = [];

        public string Name => nameof(RecordingStarRemover);

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channelCount, width, height) = input.Shape;
            var (_, median, _) = input.GetPedestralMedianAndMADScaledToUnit(0);
            Seen.Add((input.MaxValue, median, channelCount, width, height));

            // Clamp to the 20th percentile of the frame's own range, standing in for "the stars are
            // gone and the sky is not". Returns a NEW image: the pipeline releases the input and the
            // result independently.
            var ceiling = input.MinValue + (input.MaxValue - input.MinValue) * 0.2f;
            var data = new float[channelCount][,];
            for (var c = 0; c < channelCount; c++)
            {
                var src = input.GetChannelSpan(c);
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = MathF.Min(src[y * width + x], ceiling);
                    }
                }
                data[c] = plane;
            }

            return Task.FromResult(new Image(data, input.BitDepth, ceiling, input.MinValue, 0f, input.ImageMeta));
        }
    }

    [Theory]
    [InlineData(StarRemovalMode.Mosaic)]
    [InlineData(StarRemovalMode.SplitCfa)]
    public async Task TheStarRemoverIsHandedUnitScaledPixelsNotAdu(StarRemovalMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);

        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        var starRemover = new RecordingStarRemover();
        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.BayerDrizzle,
            DrizzleOptions: new DrizzleOptions(MinFrameCount: 6),
            RemoveStarsPerFrame: true,
            StarRemovalMode: mode);
        var logger = new XunitLogger(output);
        var pipeline = new StackingPipeline(options, logger, catalogDb: null, starRemover: starRemover);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1, "expected a single integrated light group");
        var result = results[0];
        result.SkipReason.ShouldBeEmpty($"group should not have skipped: '{result.SkipReason}'");

        // 1) Every frame reached the remover, and reached it in [0, 1]. The synthetic lights are
        //    Int16 with samples up to ~4096 ADU, so an unscaled hand-off records a max in the
        //    thousands -- which is the bug, exactly as it presented on the real dataset.
        // The mode decides the SHAPE the remover sees, and each mode's shape is asserted rather
        // than inferred from a call count. Mosaic hands over the whole CFA frame once; SplitCfa
        // hands over four half-resolution single-channel planes. The trade is measured in
        // StarRemovalMode: splitting kills the coloured residue and also eats an extended coma,
        // which is why Mosaic is the default and why a comet layer keeps it.
        var perFrame = mode is StarRemovalMode.SplitCfa ? 4 : 1;
        starRemover.Seen.Count.ShouldBe(result.FramesMatched * perFrame,
            $"{mode} should hand the remover {perFrame} image(s) per matched frame");

        foreach (var (_, _, channels, w, h) in starRemover.Seen)
        {
            channels.ShouldBe(1, "a CFA frame is single-channel in both modes; a multi-channel hand-off would be read as colour");
            var expected = mode is StarRemovalMode.SplitCfa
                ? RgbBayerSyntheticFixture.FrameSize / 2
                : RgbBayerSyntheticFixture.FrameSize;
            w.ShouldBeInRange(expected - 1, expected + 1);
            h.ShouldBeInRange(expected - 1, expected + 1);
        }

        // 2) The divisor is the CONTAINER full scale, so it is the SAME for every frame. Scaling
        //    each frame by its own observed peak would land every max at exactly 1.0 and quietly
        //    rescale each frame differently; the spread below is the sky varying frame to frame,
        //    which is what a shared divisor preserves and a per-frame one destroys.
        var maxima = new List<float>();
        foreach (var (max, _, _, _, _) in starRemover.Seen)
        {
            maxima.Add(max);
        }
        maxima.ShouldContain(m => m < 1.0f - 1e-6f,
            "at least one frame should sit below full scale; every frame at exactly 1.0 means each was divided by its own peak");

        // 3) The master carries structure. This is the symptom the scale bug produced -- a master
        //    whose every pixel was 1.0 -- and it is worth asserting separately, because a plate can
        //    be correctly scaled and still be integrated into a constant.
        result.MasterFitsPath.ShouldNotBeNull();
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.MaxValue.ShouldBeGreaterThan(master.MinValue,
            "a master with no range at all is the uniform-white failure this test exists for");
    }
}
