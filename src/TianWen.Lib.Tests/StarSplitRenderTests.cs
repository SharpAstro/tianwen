using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's star split (<see cref="StarSplit"/>): the starless plate through the display, the stars stretched once by
/// their own curve on the frame's own scale and screened over the result (<see cref="Image.ScreenStars"/>), as the CLI's
/// <c>--dual-stretch</c> composes them. One curve over the recombined stars lifted their wings with the faint signal: the blue
/// rings round the enhanced Sculptor master's bright stars.
/// </summary>
public class StarSplitRenderTests
{
    [Fact]
    public void TheScreenAddsLightAndNeverPassesWhite()
    {
        Image.ScreenStars(0.2f, 0f).ShouldBe(0.2f, 1e-6f, "no star leaves the picture as it was");
        Image.ScreenStars(0f, 0.6f).ShouldBe(0.6f, 1e-6f, "on black the star is itself");
        Image.ScreenStars(0.5f, 0.5f).ShouldBe(0.75f, 1e-6f);
        Image.ScreenStars(0.9f, 1f).ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void TheCpuRenderScreensTheStarsOverTheStretchedPicture()
    {
        var starless = new Image([Plane(0.2f), Plane(0.2f), Plane(0.2f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var stars = new Image([Plane(0.5f), Plane(0f), Plane(0f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        // A linear passthrough, so the starless value is what the screen sees.
        var u = new StretchUniforms(StretchMode.None, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));

        var rgba = new byte[4 * 4 * 4];
        starless.RenderStretchedRgba(u, rgba, starScreen: stars);

        rgba[0].ShouldBe((byte)(Image.ScreenStars(0.2f, 0.5f) * 255f));
        rgba[1].ShouldBe((byte)(0.2f * 255f), "a channel with no star is the picture's");
    }

    [Fact]
    public void TheStarsAreStretchedOnTheFramesOwnScale()
    {
        // A frame topping out at 0.02: its brightest star, divided by that peak, is 1 before the fixed curve, and white after.
        var starless = new Image([Plane(0.01f), Plane(0.01f), Plane(0.01f)], BitDepth.Float32, 0.02f, 0f, 0f, new ImageMeta());
        var stars = new Image([Plane(0.01f), Plane(0.01f), Plane(0.01f)], BitDepth.Float32, 0.02f, 0f, 0f, new ImageMeta());

        var split = StarSplit.FromLinear(starless, stars, peak: 0.02f);

        // Half the peak through StarStretch(2): midtones 1/10, so MTF(0.1, 0.5) = 0.9.
        split.Stars[0, 0, 0].ShouldBe(0.9f, 1e-4f);
        split.Starless.ShouldBeSameAs(starless, "the starless plate is shown as it is");
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
