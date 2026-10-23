using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The background reduce (<see cref="Image.ReduceBackgroundValue"/>, <see cref="StretchUniforms.BackgroundSky"/>, the
/// Background popover): the CLI's <c>--dual-stretch</c> curve, now also a stage of the display render, on by default.
/// </summary>
public class BackgroundReduceTests
{
    [Fact]
    public void TheCurveIsTheClisLut()
    {
        // The LUT the CLI's ReduceBackground reads is built from the one definition; sampled at its knots they agree exactly.
        Span<float> lut = stackalloc float[4096];
        Image.BuildBackgroundReduceLut(lut, 0.2f, 0.55f);
        for (var i = 0; i < lut.Length; i += 97)
        {
            lut[i].ShouldBe(Image.ReduceBackgroundValue(i / 4095f, 0.2f, 0.55f), 1e-6f);
        }
    }

    [Theory]
    [InlineData(0.2f, 0.55f)]
    [InlineData(0.11f, 0.36f)]
    [InlineData(0.3f, 0.2f)]
    public void TheSkyGoesToItsShareAndTheEndsAndTheMiddleStay(float sky, float compression)
    {
        Image.ReduceBackgroundValue(sky, sky, compression).ShouldBe(sky * compression, 1e-6f, "the sky lands at its share");
        Image.ReduceBackgroundValue(0f, sky, compression).ShouldBe(0f, 1e-6f);
        Image.ReduceBackgroundValue(0.5f, sky, compression).ShouldBe(0.5f, 1e-6f, "the midpoint is kept");
        Image.ReduceBackgroundValue(1f, sky, compression).ShouldBe(1f, 1e-6f, "white is kept");

        // Monotonic: a brighter input is never a darker output.
        var previous = -1f;
        for (var v = 0f; v <= 1f; v += 1f / 512f)
        {
            var reduced = Image.ReduceBackgroundValue(v, sky, compression);
            reduced.ShouldBeGreaterThanOrEqualTo(previous - 1e-6f);
            previous = reduced;
        }
    }

    [Theory]
    [InlineData(0f, 0.5f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.2f, 1f)]
    [InlineData(0.2f, 0f)]
    public void OutsideItsDomainTheCurveIsTheIdentity(float sky, float compression)
        => Image.ReduceBackgroundValue(0.3f, sky, compression).ShouldBe(0.3f);

    [Fact]
    public void ADefaultStretchHasNoReduceAndTheViewerTurnsItOn()
    {
        var u = new StretchUniforms(StretchMode.Linked, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));
        u.BackgroundSky.ShouldBe(1f, "a stretch built anywhere else renders as before");
        u.HasBackgroundReduce.ShouldBeFalse();

        var state = new ViewerState();
        state.BackgroundReduceEnabled.ShouldBeTrue("on by default in the viewer");
        state.BackgroundSky.ShouldBe(ViewerState.DefaultBackgroundSkyLevel);
        // Within a float step: the uniform stores its distance from 1, as FinishSaturation does.
        DisplayRendition.FromState(u, state).Stretch.BackgroundSky.ShouldBe(ViewerState.DefaultBackgroundSkyLevel, 1e-6f);

        state.BackgroundReduceEnabled = false;
        DisplayRendition.FromState(u, state).Stretch.HasBackgroundReduce.ShouldBeFalse();
    }

    [Fact]
    public void APlanetsStretchStandsTheReduceDown()
    {
        var state = new ViewerState { StretchMode = StretchMode.Planetary };
        state.BackgroundReduceEnabled.ShouldBeTrue();
        state.BackgroundSky.ShouldBe(1f, "a planet's stretch already puts the sky at black");
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.25f)]
    [InlineData(0.15f)]
    public void TheSkyLandsAtTheLevelWhateverTheStretchsTarget(float stretchedSky)
    {
        // #1792: a share of the sky multiplied with the stretch's own target; a level does not.
        var u = Linked() with { BackgroundSky = 0.11f };
        u.ReducedBackground(stretchedSky).ShouldBe(0.11f, 1e-6f);
        Image.ReduceBackgroundValue(stretchedSky, stretchedSky, u.BackgroundReduceCompression(stretchedSky)).ShouldBe(0.11f, 1e-5f);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.11f)]
    [InlineData(0.06f)]
    public void ASkyAlreadyAtOrBelowTheLevelIsLeftAlone(float stretchedSky)
    {
        var u = Linked() with { BackgroundSky = 0.11f };
        u.BackgroundReduceCompression(stretchedSky).ShouldBe(1f);
        u.ReducedBackground(stretchedSky).ShouldBe(stretchedSky);
        Image.ReduceBackgroundValue(0.3f, stretchedSky, u.BackgroundReduceCompression(stretchedSky)).ShouldBe(0.3f, "the curve is the identity");
    }

    [Fact]
    public void TheRenderReducesTheSkyAndTheLaterStagesPivotOnTheReducedSky()
    {
        // A flat frame at the stretched sky: the reduce takes it to the level, which is where every later stage pivots.
        var plane = new float[4, 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                plane[y, x] = 0.2f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        // Linked with shadows 0, midtones 0.5 and rescale 1 is the identity, so the stretched sky is the input's 0.2.
        var u = Linked() with { BackgroundSky = 0.1f };

        var rgba = new byte[4 * 4 * 4];
        image.RenderStretchedRgba(u, rgba, curvesMidpoint: 0.2f);

        rgba[0].ShouldBe((byte)(0.1f * 255f), "the sky at the level");
        u.ReducedBackground(0.2f).ShouldBe(0.1f, 1e-6f, "the boost and the finish pivot on the reduced sky");
    }

    private static StretchUniforms Linked()
        => new(StretchMode.Linked, 1f, default, default, (0.5f, 0.5f, 0.5f), (1f, 1f, 1f), (1f, 1f, 1f));
}
