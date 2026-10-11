using System;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="MilkyWaySvsImport"/>: NASA's starless map (right ascension rising to the LEFT, linear light) becomes the
/// sky map's texture (right ascension rising to the right, sRGB colour with the dimming in alpha). Synthetic maps,
/// since the real one is 35 MB: what is pinned is where a point of sky lands and what value it is written as.
/// </summary>
public class MilkyWaySvsImportTests
{
    private const int SourceW = 16;
    private const int SourceH = 8;

    private static Half[] Uniform(float level)
    {
        var rgb = new Half[SourceW * SourceH * 3];
        rgb.AsSpan().Fill((Half)level);
        return rgb;
    }

    // The column holding right ascension raDeg in each convention, as skymap_mw.frag and NASA's map lay it out.
    private static int SourceColumn(double raDeg, int width) => (int)((0.5 - Signed(raDeg) / 360.0) * width);

    private static int TextureColumn(double raDeg, int width) => (int)((Signed(raDeg) / 360.0 + 0.5) * width);

    private static double Signed(double raDeg) => raDeg > 180 ? raDeg - 360 : raDeg;

    [Theory]
    [InlineData(80.9, -69.8)]   // the Large Magellanic Cloud
    [InlineData(266.4, -29.0)]  // the galactic centre
    [InlineData(10.7, 41.3)]    // M 31
    public void APointOfSkyLandsAtItsOwnRightAscensionAndDeclination(double raDeg, double decDeg)
    {
        var rgb = Uniform(0f);
        var sourceY = (int)((0.5 - decDeg / 180.0) * SourceH);
        var at = (sourceY * SourceW + SourceColumn(raDeg, SourceW)) * 3;
        rgb[at] = rgb[at + 1] = rgb[at + 2] = (Half)1f;

        var options = new MilkyWaySvsImportOptions(Width: 8, Height: 4, BlackPoint: 0f);
        var bgra = MilkyWaySvsImport.ToBgra(rgb, SourceW, SourceH, options);

        var lit = Array.FindIndex(bgra, 0, b => b > 0);
        var pixel = lit / 4;
        (pixel % 8).ShouldBe(TextureColumn(raDeg, 8), "right ascension rises to the right in the texture");
        (pixel / 8).ShouldBe((int)((0.5 - decDeg / 180.0) * 4), "north stays at the top");
    }

    [Fact]
    public void EachPixelAveragesItsBoxInLinearLightThenTakesTheBlackPointAndEncodesSrgb()
    {
        // One source box of four, half at 0.5 and half at 0.1: the mean is 0.3 in LINEAR light, not the mean of codes.
        var rgb = Uniform(0f);
        for (var j = 0; j < 2; j++)
        {
            for (var i = 0; i < 2; i++)
            {
                var at = (j * SourceW + (SourceW - 1 - i)) * 3;
                var level = (Half)(i == 0 ? 0.5f : 0.1f);
                rgb[at] = rgb[at + 1] = rgb[at + 2] = level;
            }
        }

        var options = new MilkyWaySvsImportOptions(Width: 8, Height: 4, BlackPoint: 0.05f);
        var bgra = MilkyWaySvsImport.ToBgra(rgb, SourceW, SourceH, options);

        var mean = ((float)(Half)0.5f + (float)(Half)0.1f) / 2f;
        var expected = (byte)MathF.Round(Bt2020Pq.SrgbOetf((mean - 0.05f) / 0.95f) * 255f);
        bgra[0].ShouldBe(expected);
        bgra[1].ShouldBe(expected);
        bgra[2].ShouldBe(expected);
    }

    [Fact]
    public void TheDimmingIsTheAlphaAndAnySkyBelowTheBlackPointIsBlack()
    {
        var options = new MilkyWaySvsImportOptions(Width: 8, Height: 4, Scale: 0.33f, BlackPoint: 0.01f);
        var bgra = MilkyWaySvsImport.ToBgra(Uniform(0.005f), SourceW, SourceH, options);

        for (var p = 0; p < bgra.Length; p += 4)
        {
            bgra[p].ShouldBe((byte)0);
            bgra[p + 1].ShouldBe((byte)0);
            bgra[p + 2].ShouldBe((byte)0);
            bgra[p + 3].ShouldBe((byte)MathF.Round(0.33f * 255f), "the sky map shows colour times alpha");
        }
    }

    [Fact]
    public void ThePeakUsesTheWholeColourRange()
    {
        var bgra = MilkyWaySvsImport.ToBgra(Uniform(1f), SourceW, SourceH, new MilkyWaySvsImportOptions(Width: 8, Height: 4));

        bgra[0].ShouldBe((byte)255);
    }

    [Theory]
    [InlineData(16, 8, 6, 3)]   // not a whole multiple
    [InlineData(16, 7, 8, 4)]   // source not 2:1
    [InlineData(16, 8, 8, 3)]   // output not 2:1
    public void AMapThatIsNotTwoToOneOrNotAWholeMultipleIsRefused(int sourceW, int sourceH, int width, int height)
        => Should.Throw<ArgumentException>(() => MilkyWaySvsImport.ToBgra(
            new Half[sourceW * sourceH * 3], sourceW, sourceH, new MilkyWaySvsImportOptions(width, height)));
}
