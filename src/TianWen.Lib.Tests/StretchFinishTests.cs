using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The masked finish (<see cref="StretchUniforms.FinishContrast"/>, <see cref="StretchUniforms.FinishSaturation"/>): the
/// Affinity masked contrast boost and saturation as ONE stage of the stretch, which the viewer's Tone popover, the CLI's
/// <c>image render --contrast-boost --saturation</c> and the stacking preview all run (<see cref="Image.ApplyFinish"/> on the
/// CPU, <c>applyFinish</c> in <c>image.frag</c>). It leaves the sky and the star cores alone and works on the signal between.
/// </summary>
public class StretchFinishTests
{
    private static readonly (float R, float G, float B) Rec709 = (0.2126f, 0.7152f, 0.0722f);
    private const float Background = 0.1f;

    [Fact]
    public void TheMaskIsZeroOnTheSkyAndOnWhiteAndFullOnTheSignalBetween()
    {
        Image.FinishMask(0.05f, Background).ShouldBe(0f, "below the background");
        Image.FinishMask(Background, Background).ShouldBe(0f, "at the background");
        Image.FinishMask(0.5f, Background).ShouldBe(1f, "a quarter of the way to white and beyond");
        Image.FinishMask(1f, Background).ShouldBe(0f, "white: a star core");
        Image.FinishMask(0.9f, Background).ShouldBe(0.5f, 1e-5f, "easing back over the last fifth");
    }

    [Fact]
    public void ContrastSeparatesTheSignalAgainstTheBackgroundAndKeepsItsColour()
    {
        // Two grey levels either side of the middle of [background, white]: contrast moves them apart.
        var (dr, dg, db) = (0.35f, 0.35f, 0.35f);
        var (br, bg, bb) = (0.75f, 0.75f, 0.75f);
        Image.ApplyFinish(ref dr, ref dg, ref db, contrast: 1f, saturation: 1f, Background, Rec709);
        Image.ApplyFinish(ref br, ref bg, ref bb, contrast: 1f, saturation: 1f, Background, Rec709);
        dr.ShouldBeLessThan(0.35f, "the darker signal goes down");
        br.ShouldBeGreaterThan(0.75f, "the brighter goes up");

        // A coloured pixel keeps its hue: its channels scale together.
        var (r, g, b) = (0.6f, 0.45f, 0.3f);
        Image.ApplyFinish(ref r, ref g, ref b, contrast: 0.5f, saturation: 1f, Background, Rec709);
        (r / g).ShouldBe(0.6f / 0.45f, 1e-4f);
        (g / b).ShouldBe(0.45f / 0.3f, 1e-4f);
    }

    [Fact]
    public void SaturationWidensTheSignalsColourAndLeavesTheSkyGrey()
    {
        var (r, g, b) = (0.6f, 0.45f, 0.3f);
        Image.ApplyFinish(ref r, ref g, ref b, contrast: 0f, saturation: 1.5f, Background, Rec709);
        (r - b).ShouldBeGreaterThan(0.6f - 0.3f);

        // A slightly tinted sky pixel at the background: the mask is zero, so its noise takes no colour.
        var (sr, sg, sb) = (0.105f, 0.1f, 0.085f);
        Image.ApplyFinish(ref sr, ref sg, ref sb, contrast: 1f, saturation: 2f, Background, Rec709);
        (sr, sg, sb).ShouldBe((0.105f, 0.1f, 0.085f));
    }

    [Fact]
    public void AnIdentityFinishChangesNothing()
    {
        new StretchUniforms().HasFinish.ShouldBeFalse("default: contrast 0, saturation 1");
        var (r, g, b) = (0.6f, 0.45f, 0.3f);
        Image.ApplyFinish(ref r, ref g, ref b, contrast: 0f, saturation: 1f, Background, Rec709);
        (r, g, b).ShouldBe((0.6f, 0.45f, 0.3f));
    }

    [Fact]
    public void TheCpuRenderAppliesTheFinishFromTheUniforms()
    {
        // A linear passthrough (StretchMode.None, normFactor 1): the render's input IS the display value, so the finish
        // is the only stage that moves it.
        var image = new Image([Plane(0.6f), Plane(0.45f), Plane(0.3f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plain = new StretchUniforms(StretchMode.None, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));
        var finished = plain with { FinishSaturation = 1.5f, FinishContrast = 0.5f };

        var before = new byte[4 * 4 * 4];
        var after = new byte[4 * 4 * 4];
        image.RenderStretchedRgba(plain, before, curvesMidpoint: Background);
        image.RenderStretchedRgba(finished, after, curvesMidpoint: Background);

        (after[0] - after[2]).ShouldBeGreaterThan(before[0] - before[2], "the finish reached the pixels");
    }

    [Fact]
    public void AHeadlessBoostOverOneIsTheFullCurve()
    {
        MaskedBoostOptions.ToFinishContrast(1.5f).ShouldBe(1f);
        MaskedBoostOptions.ToFinishContrast(0.25f).ShouldBe(0.25f);
    }

    [Fact]
    public void StretchedImageFromRgba16RoundTripsColourAndMono()
    {
        var rgba = new ushort[2 * 2 * 4];
        for (var i = 0; i < 4; i++)
        {
            rgba[i * 4] = (ushort)(0.6f * 65535f + 0.5f);
            rgba[i * 4 + 1] = (ushort)(0.45f * 65535f + 0.5f);
            rgba[i * 4 + 2] = (ushort)(0.3f * 65535f + 0.5f);
            rgba[i * 4 + 3] = 65535;
        }

        var colour = MasterPreviewRenderer.StretchedImageFromRgba16(rgba, channelCount: 3, 2, 2, new ImageMeta());
        colour.ChannelCount.ShouldBe(3);
        colour[2, 1, 1].ShouldBe(0.3f, 1e-4f);

        var mono = MasterPreviewRenderer.StretchedImageFromRgba16(rgba, channelCount: 1, 2, 2, new ImageMeta());
        mono.ChannelCount.ShouldBe(1);
        mono[0, 0, 0].ShouldBe(0.6f, 1e-4f);
    }

    private static float[,] Plane(float value)
    {
        var plane = new float[4, 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                plane[y, x] = value;
            }
        }
        return plane;
    }
}
