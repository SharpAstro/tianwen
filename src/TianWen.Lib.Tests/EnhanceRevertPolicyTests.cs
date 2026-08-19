using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Turning Enhance off has to get the original pixels back, and the two ways of doing that trade
/// memory against time.
/// </summary>
/// <remarks>
/// <para>Retaining is a REFERENCE, not a copy -- the pre-enhance document is already alive for the
/// duration of the run, so keeping it is declining to drop it. But on a large master that reference
/// pins hundreds of megabytes for a revert the user may never press, and "keep RAM low" is a
/// standing constraint here. Reloading pins nothing and costs a read.</para>
/// <para>The budget is on the image's OWN footprint, not on free system memory: available memory
/// changes between the decision and its consequence, so keying on it would make the viewer behave
/// differently on identical input for reasons the user cannot see.</para>
/// </remarks>
[Collection("Imaging")]
public class EnhanceRevertPolicyTests
{
    private static ImageMeta Meta() => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
        FrameType.Light, "", 2.9f, 2.9f, 1180, -1, Filter.None, 1, 1,
        float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>An image of a given shape, allocated small and reported at the true size via the
    /// pure footprint helper -- the policy only ever reads dimensions.</summary>
    private static Image OfShape(int width, int height, int channels)
    {
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            planes[c] = new float[height, width];
        }
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, Meta());
    }

    [Fact]
    public void AFourKColourMasterIsWorthHolding()
    {
        // 3840x2160x3 floats is ~95 MB -- the case that motivated the toggle, and the one where an
        // instant revert is worth the memory.
        var image = OfShape(3840, 2160, 3);

        EnhanceRevertPolicy.FootprintBytes(image).ShouldBeLessThan(EnhanceRevertPolicy.RetainBudgetBytes);
        EnhanceRevertPolicy.Decide(image, canReload: true).ShouldBe(EnhanceRevert.Retained);
    }

    [Fact]
    public void ALargeSensorMosaicReloadsInstead()
    {
        // ~9600x6400x3 is over 700 MB. Holding that for a button the user may never press is the
        // case the budget exists to refuse.
        var image = OfShape(9600, 6400, 3);

        EnhanceRevertPolicy.FootprintBytes(image).ShouldBeGreaterThan(EnhanceRevertPolicy.RetainBudgetBytes);
        EnhanceRevertPolicy.Decide(image, canReload: true).ShouldBe(EnhanceRevert.Reload);
    }

    [Fact]
    public void WithNoFileToReopenItHoldsTheImageHoweverLargeItIs()
    {
        // The budget yields here on purpose. A toggle that cannot return to the state it promises is
        // worse than the memory it saves, and this is the only case where the two conflict.
        var image = OfShape(9600, 6400, 3);

        EnhanceRevertPolicy.Decide(image, canReload: false).ShouldBe(EnhanceRevert.Retained);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TheFootprintCountsEveryChannel(int channels)
    {
        // Channel count is half the answer on a colour master, so a footprint that ignored it would
        // let a 3-channel image through a budget sized for one plane.
        var image = OfShape(1000, 1000, channels);

        EnhanceRevertPolicy.FootprintBytes(image).ShouldBe(1000L * 1000 * channels * sizeof(float));
    }
}
