using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's star split (<see cref="StarSplit"/>): the starless plate through the display, the linear stars stretched by
/// the CLI's colour asinh, the magenta out and the stars' own saturation (<see cref="ScreenedStars"/>, the Tone popover's Stars and Colour dials) and screened over the result (<see cref="Image.ScreenStars"/>), as the CLI's <c>--dual-stretch</c> composes them. One curve over the recombined stars lifted their wings with the faint signal: the blue
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
        var screened = new ScreenedStars(stars, StarSplit.StarCurve, StarSplit.StarSaturation);

        var rgba = new byte[4 * 4 * 4];
        starless.RenderStretchedRgba(u, rgba, starScreen: screened);

        var (red, _, _) = screened.Display(0.002f, 0f, 0f);
        rgba[0].ShouldBe((byte)(Image.ScreenStars(0.2f, red) * 255f));
        rgba[1].ShouldBe((byte)(0.2f * 255f), "a channel with no star is the picture's");
    }

    [Fact]
    public void AtSaturationOneTheStarsAreTheClisColourAsinh()
    {
        // The same pixel through the CLI's stretch of a whole plate and through the screen's per-pixel display: green is not
        // short of both red and blue, so the magenta fix leaves it, and the stretched values stay below 1, so no clip.
        var plate = new Image([Plane(0.012f), Plane(0.009f), Plane(0.006f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var cli = plate.AsinhStretch(StarSplit.StarCurve);
        var (r, g, b) = new ScreenedStars(plate, StarSplit.StarCurve, 1f).Display(0.012f, 0.009f, 0.006f);

        r.ShouldBe(cli[0, 0, 0], 1e-5f);
        g.ShouldBe(cli[1, 0, 0], 1e-5f);
        b.ShouldBe(cli[2, 0, 0], 1e-5f);
    }

    [Fact]
    public void MagentaIsTakenOutOnlyWhereRedAndBlueBothExceedGreen()
    {
        ScreenedStars.Demagenta(0.010f, 0.006f, 0.012f).ShouldBe(0.010f, "a magenta pixel's green rises to the lesser of red and blue");
        ScreenedStars.Demagenta(0.012f, 0.009f, 0.006f).ShouldBe(0.009f, "an orange pixel is left alone");
        ScreenedStars.Demagenta(0.006f, 0.009f, 0.012f).ShouldBe(0.009f, "a blue pixel is left alone");
    }

    [Fact]
    public void ABrightCoreKeepsItsHueWhereAPerChannelClampWhitenedIt()
    {
        // A bright blue star the asinh lifts past 1: the whole pixel is scaled down by its largest channel, so blue stays
        // the largest and the ratios hold, where clamping each channel to 1 would have left it near white.
        var (r, g, b) = new ScreenedStars(Uniform(0f), StarSplit.StarCurve, 1f).Display(0.30f, 0.35f, 0.50f);

        b.ShouldBe(1f, 1e-6f);
        (b / r).ShouldBe(0.50f / 0.30f, 1e-4f);
        (g / r).ShouldBe(0.35f / 0.30f, 1e-4f);
    }

    [Fact]
    public void TheStarColourDialPushesTheColourApartAroundTheSameLuminance()
    {
        var plate = Uniform(0f);
        var flat = new ScreenedStars(plate, StarSplit.StarCurve, 1f).Display(0.006f, 0.008f, 0.012f);
        var vivid = new ScreenedStars(plate, StarSplit.StarCurve, 2f).Display(0.006f, 0.008f, 0.012f);
        var grey = new ScreenedStars(plate, StarSplit.StarCurve, 0f).Display(0.006f, 0.008f, 0.012f);

        (vivid.B - vivid.R).ShouldBeGreaterThan(flat.B - flat.R);
        grey.R.ShouldBe(grey.B, 1e-6f, "no saturation is grey");
        Luma(vivid).ShouldBe(Luma(flat), 1e-5f, "below the clip the saturation keeps the luminance");
    }

    [Fact]
    public void TheStarsDialMovesOnlyTheStars()
    {
        var split = new StarSplit(Uniform(0.01f), Uniform(0.002f));

        var soft = split.ScreenAt(30f, 1f).Display(0.002f, 0.002f, 0.002f).R;
        var hard = split.ScreenAt(1000f, 1f).Display(0.002f, 0.002f, 0.002f).R;
        split.ScreenAt(1f, 1f).Display(0.002f, 0.002f, 0.002f).R.ShouldBeLessThan(soft);
        hard.ShouldBeGreaterThan(soft);

        // The CPU render takes the strength through the rendition, and the starless picture under no star is untouched.
        var u = new StretchUniforms(StretchMode.None, 1f, default, default, default, (1f, 1f, 1f), (1f, 1f, 1f));
        var weak = new byte[4 * 4 * 4];
        var strong = new byte[4 * 4 * 4];
        split.Starless.RenderStretchedRgba(u, weak, starScreen: split.ScreenAt(30f, 1f));
        split.Starless.RenderStretchedRgba(u, strong, starScreen: split.ScreenAt(1000f, 1f));
        strong[0].ShouldBeGreaterThan(weak[0]);
    }

    [Fact]
    public void ARenditionWithNoStarDialsReadsTheDefaults()
    {
        new DisplayRendition().StarStretch.ShouldBe(StarSplit.StarCurve);
        new DisplayRendition().StarSaturation.ShouldBe(StarSplit.StarSaturation);
        var rendition = DisplayRendition.FromState(default, new ViewerState { StarStretch = 120f, StarSaturation = 2.2f });
        rendition.StarStretch.ShouldBe(120f);
        rendition.StarSaturation.ShouldBe(2.2f);
    }

    private static float Luma((float R, float G, float B) c)
        => (ScreenedStars.LumaWeights.R * c.R) + (ScreenedStars.LumaWeights.G * c.G) + (ScreenedStars.LumaWeights.B * c.B);

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
