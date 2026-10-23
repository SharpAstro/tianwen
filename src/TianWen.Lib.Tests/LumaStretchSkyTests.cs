using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Luma curve is placed on the sky as the pixel path carries it: through each channel's own background neutralisation
/// and white balance, then weighted into a luminance.
/// </summary>
public class LumaStretchSkyTests
{
    [Fact]
    public void ANeutralSkyUnderAColouredBalanceStaysAGreySkyInLuma()
    {
        // The unenhanced Sculptor master: a sky the stacker levelled to 0.01056 in every channel, an SPCC triple far from
        // neutral, and the MinPivot gains that take the balanced sky back to grey. Its pixels reach the curve at 0.0045 in
        // every channel; the curve was solved for 0.00514 (the gains and the balance averaged apart, then multiplied), put
        // its black point above every sky pixel, and the sky rendered black.
        var sky = 0.01056f;
        var stats = new[] { new ChannelStretchStats(0f, sky, 3e-4f), new ChannelStretchStats(0f, sky, 3e-4f), new ChannelStretchStats(0f, sky, 3e-4f) };
        var luma = new ChannelStretchStats(0f, sky, 3e-4f);
        var wb = (0.424f, 1f, 1.4066f);
        var bn = BackgroundNeutralization.ComputeGains([sky, sky, sky], BackgroundNeutralizationMethod.MinPivot, wb);

        var u = StretchSolver.ComputeStretchUniforms(StretchMode.Luma, StretchParameters.Default, stats, luma, 1f,
            whiteBalance: wb, backgroundNeutralization: bn);
        var linked = StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, stats, luma, 1f,
            whiteBalance: wb, backgroundNeutralization: bn);

        var (r, g, b) = Image.StretchLumaPixelCpu(sky, sky, sky, u);
        var linkedSky = Image.StretchChannelCpu(sky, 1, linked);

        g.ShouldBe(linkedSky, 0.02f, "the sky lands where the Linked stretch puts the same sky");
        r.ShouldBe(g, 0.01f, "a grey sky stays grey");
        b.ShouldBe(g, 0.01f, "a grey sky stays grey");
    }
}
