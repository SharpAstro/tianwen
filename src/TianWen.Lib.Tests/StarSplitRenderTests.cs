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
        var screened = new ScreenedStars(stars, DefaultBeta, StarSplit.StarSaturation);

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
        var cli = plate.AsinhStretch(DefaultBeta);
        var (r, g, b) = new ScreenedStars(plate, DefaultBeta, 1f).Display(0.012f, 0.009f, 0.006f);

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
        var (r, g, b) = new ScreenedStars(Uniform(0f), DefaultBeta, 1f).Display(0.30f, 0.35f, 0.50f);

        b.ShouldBe(1f, 1e-6f);
        (b / r).ShouldBe(0.50f / 0.30f, 1e-4f);
        (g / r).ShouldBe(0.35f / 0.30f, 1e-4f);
    }

    [Fact]
    public void TheStarColourDialPushesTheColourApartAroundTheSameLuminance()
    {
        var plate = Uniform(0f);
        // A faint star, so the doubled colour stays below the clip at the default stretch.
        var flat = new ScreenedStars(plate, DefaultBeta, 1f).Display(0.0006f, 0.0008f, 0.0012f);
        var vivid = new ScreenedStars(plate, DefaultBeta, 2f).Display(0.0006f, 0.0008f, 0.0012f);
        var grey = new ScreenedStars(plate, DefaultBeta, 0f).Display(0.0006f, 0.0008f, 0.0012f);

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

    [Theory]
    [InlineData(2f)]
    [InlineData(47f)]
    [InlineData(250f)]
    [InlineData(1000f)]
    public void TheStarsDialIsPixInsightsStretchFactorTheCurvesSlopeAtBlack(float stretch)
    {
        // PixInsight's ArcsinhStretch "stretch" is the slope at black of asinh(beta x) / asinh(beta), beta / asinh(beta);
        // the beta is what the curve takes, and the dial is never one.
        var beta = StarSplit.BetaFor(stretch);
        (beta / System.MathF.Asinh(beta)).ShouldBe(stretch, stretch * 1e-4f);
    }

    [Fact]
    public void AtItsDefaultTheDialDrawsAStarAsPixInsightsArcsinhAtOneThousand()
    {
        // The owner's ArcsinhStretch at 1000, black point 0, on the enhanced Sculptor master's stars plate: a pixel of linear
        // luminance 3e-4 came out at 0.183 (Image01_asinh_1000.tif, its median over the plate's pixels at that level), 1e-3 at
        // 0.301. The beta of 300 this default replaced drew them at 0.014 and 0.046.
        StarSplit.StarCurve.ShouldBe(StarSplit.MaxStretch, "1000 is PixInsight's own maximum");
        var screen = new StarSplit(Uniform(0f), Uniform(0f)).ScreenAt(StarSplit.StarCurve, 1f);
        Luma(screen.Display(3e-4f, 3e-4f, 3e-4f)).ShouldBe(0.183f, 0.005f);
        Luma(screen.Display(1e-3f, 1e-3f, 1e-3f)).ShouldBe(0.301f, 0.01f);
    }

    [Fact]
    public void AStretchOfOneIsTheIdentity()
    {
        var screen = new StarSplit(Uniform(0f), Uniform(0f)).ScreenAt(StarSplit.MinStretch, 1f);
        screen.Display(0.2f, 0.2f, 0.2f).R.ShouldBe(0.2f, 1e-3f);
        screen.Display(0.002f, 0.002f, 0.002f).R.ShouldBe(0.002f, 1e-4f);
    }

    [Theory]
    [InlineData(5f, 1e-3f, 0.190f)]
    [InlineData(5f, 3e-3f, 0.414f)]
    [InlineData(6f, 1e-3f, 0.411f)]
    [InlineData(6f, 3e-3f, 0.679f)]
    public void TheStarStretchIsSetiAstrosCurveOnEachChannel(float amount, float linear, float seti)
    {
        // The owner's SETI Astro Star Stretch exports of the enhanced Sculptor master's stars plate (Image01_seti_starstretch_s5_b1,
        // _s6_b1.5): the median output over the plate's pixels at that linear luminance. A grey star, so the per-channel
        // curve is the luminance's, and colour boost 1 (the Colour dial at 1) leaves it.
        var screen = new ScreenedStars(Uniform(0f), 1f, 1f, StarStretchMethod.StarStretch, amount);
        Luma(screen.Display(linear, linear, linear)).ShouldBe(seti, 0.012f);
    }

    [Fact]
    public void TheStarStretchStretchesEachChannelAloneAndTheRenditionCarriesItsChoice()
    {
        // Each channel through the curve on its own, which is what pales a star's colour as it lifts it.
        var k = ScreenedStars.StarStretchFactor(5f);
        var (r, g, b) = new ScreenedStars(Uniform(0f), 1f, 1f, StarStretchMethod.StarStretch, 5f).Display(0.004f, 0.002f, 0.001f);
        r.ShouldBe(ScreenedStars.StarStretch(0.004f, k), 1e-6f);
        g.ShouldBe(ScreenedStars.StarStretch(0.002f, k), 1e-6f);
        b.ShouldBe(ScreenedStars.StarStretch(0.001f, k), 1e-6f);

        // The viewer's choice reaches every CPU render through the rendition, and a new() rendition reads the defaults.
        new DisplayRendition().StarMethod.ShouldBe(StarStretchMethod.Arcsinh);
        new DisplayRendition().StarAmount.ShouldBe(ScreenedStars.DefaultAmount);
        var rendition = DisplayRendition.FromState(default, new ViewerState { StarMethod = StarStretchMethod.StarStretch, StarAmount = 6f });
        var screen = new StarSplit(Uniform(0f), Uniform(0f)).ScreenFor(rendition);
        screen.Method.ShouldBe(StarStretchMethod.StarStretch);
        screen.Amount.ShouldBe(6f);
    }

    private static readonly float DefaultBeta = StarSplit.BetaFor(StarSplit.StarCurve);

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
