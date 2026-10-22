using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's star split (<see cref="StarSplit"/>): the starless plate through the display, the linear stars stretched by
/// the CLI's colour asinh (<see cref="ScreenedStars"/>, the Tone popover's Stars dial) and screened over the result (<see cref="Image.ScreenStars"/>), as the CLI's <c>--dual-stretch</c> composes them. One curve over the recombined stars lifted their wings with the faint signal: the blue
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
        var stars = new Image([Plane(0.002f), Plane(0f), Plane(0f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        // A linear passthrough, so the starless value is what the screen sees.
        var u = new StretchUniforms(StretchMode.None, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));
        var screened = new ScreenedStars(stars, StarSplit.StarCurve);

        var rgba = new byte[4 * 4 * 4];
        starless.RenderStretchedRgba(u, rgba, starScreen: screened);

        var (red, _, _) = screened.Display(0.002f, 0f, 0f);
        rgba[0].ShouldBe((byte)(Image.ScreenStars(0.2f, red) * 255f));
        rgba[1].ShouldBe((byte)(0.2f * 255f), "a channel with no star is the picture's");
    }

    [Fact]
    public void TheStarsAreTheClisColourAsinh()
    {
        // The same pixel through the CLI's stretch of a whole plate and through the screen's per-pixel display.
        var plate = new Image([Plane(0.012f), Plane(0.006f), Plane(0.009f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var cli = plate.AsinhStretch(StarSplit.StarCurve);
        var (r, g, b) = new ScreenedStars(plate, StarSplit.StarCurve).Display(0.012f, 0.006f, 0.009f);

        r.ShouldBe(cli[0, 0, 0], 1e-5f);
        g.ShouldBe(cli[1, 0, 0], 1e-5f);
        b.ShouldBe(cli[2, 0, 0], 1e-5f);
    }

    [Fact]
    public void AStarKeepsItsColourToTheCore()
    {
        // A bright, blue-white star: one factor for every channel, so the ratios survive however hard the stretch, where a
        // per-channel curve pushed all three toward 1 and whitened the core.
        var screened = new ScreenedStars(Uniform(0f), 1000f);
        var (r, g, b) = screened.Display(0.004f, 0.005f, 0.008f);

        (b / r).ShouldBe(2f, 1e-4f);
        (g / r).ShouldBe(1.25f, 1e-4f);
        r.ShouldBeGreaterThan(0.004f, "the stretch lifted it");
    }

    [Fact]
    public void TheStarsDialMovesOnlyTheStars()
    {
        var split = new StarSplit(Uniform(0.01f), Uniform(0.002f));

        var soft = split.ScreenAt(30f).Display(0.002f, 0.002f, 0.002f).R;
        var hard = split.ScreenAt(1000f).Display(0.002f, 0.002f, 0.002f).R;
        split.ScreenAt(1f).Display(0.002f, 0.002f, 0.002f).R.ShouldBeLessThan(soft);
        hard.ShouldBeGreaterThan(soft);

        // The CPU render takes the strength through the rendition, and the starless picture under no star is untouched.
        var u = new StretchUniforms(StretchMode.None, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));
        var weak = new byte[4 * 4 * 4];
        var strong = new byte[4 * 4 * 4];
        split.Starless.RenderStretchedRgba(u, weak, starScreen: split.ScreenAt(30f));
        split.Starless.RenderStretchedRgba(u, strong, starScreen: split.ScreenAt(1000f));
        strong[0].ShouldBeGreaterThan(weak[0]);
    }

    [Fact]
    public void ARenditionWithNoStarStrengthReadsTheDefault()
    {
        new DisplayRendition().StarStretch.ShouldBe(StarSplit.StarCurve);
        DisplayRendition.FromState(default, new ViewerState { StarStretch = 120f }).StarStretch.ShouldBe(120f);
    }

    private static Image Uniform(float value)
        => new([Plane(value), Plane(value), Plane(value)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

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
