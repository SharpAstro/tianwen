using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which file the Save-As menu's depth row and the dialog's extension between them ask for.
/// </summary>
/// <remarks>
/// <para>Depth and container are NOT two free axes, which is the thing worth pinning: each format
/// carries its own depth (JPEG is 8-bit because baseline JPEG is, a TIFF here is 32-bit float), and
/// only PNG has two depths behind one extension. So the menu row decides exactly one column of this
/// table and the extension decides the rest.</para>
/// <para>The rows where the extension overrides the menu are asserted deliberately rather than left
/// to emerge: asking for 8-bit and then typing <c>.tif</c> writes the float TIFF the NAME asked for,
/// and someone reading that as a bug should find it stated here as a choice.</para>
/// </remarks>
public class DisplayRasterFormatChoiceTests
{
    [Theory]
    // The one column the menu row governs.
    [InlineData("shot.png", PngDepth.SixteenBit, DisplayRasterFormat.Png16)]
    [InlineData("shot.png", PngDepth.EightBit, DisplayRasterFormat.Png8)]
    [InlineData("shot.PNG", PngDepth.EightBit, DisplayRasterFormat.Png8)]
    // Everywhere else the extension already names one unambiguous format, and keeps deciding.
    [InlineData("shot.jpg", PngDepth.SixteenBit, DisplayRasterFormat.Jpeg)]
    [InlineData("shot.jpg", PngDepth.EightBit, DisplayRasterFormat.Jpeg)]
    [InlineData("shot.jpeg", PngDepth.EightBit, DisplayRasterFormat.Jpeg)]
    [InlineData("shot.tif", PngDepth.SixteenBit, DisplayRasterFormat.TiffFloat)]
    [InlineData("shot.tif", PngDepth.EightBit, DisplayRasterFormat.TiffFloat)]
    [InlineData("shot.tiff", PngDepth.EightBit, DisplayRasterFormat.TiffFloat)]
    public void TheDepthRowDecidesPngAndNothingElse(string path, PngDepth depth, DisplayRasterFormat expected)
        => DisplayRasterExport.FromExtension(path, depth).ShouldBe(expected);

    [Fact]
    public void AnExtensionWeDoNotWriteResolvesToNothing()
    {
        // The caller substitutes its own default; FromExtension does not guess, so a path the dialog
        // could not have produced cannot silently become a PNG.
        DisplayRasterExport.FromExtension("shot.bmp", PngDepth.EightBit).ShouldBeNull();
        DisplayRasterExport.FromExtension("shot", PngDepth.EightBit).ShouldBeNull();
    }

    [Fact]
    public void TheDefaultIsTheLosslessOne()
    {
        // Every caller that says nothing gets 16-bit, which is what the parameterless behaviour was
        // before the depth row existed.
        DisplayRasterExport.FromExtension("shot.png").ShouldBe(DisplayRasterFormat.Png16);
        PngDepth.SixteenBit.Png().ShouldBe(DisplayRasterFormat.Png16);
        PngDepth.EightBit.Png().ShouldBe(DisplayRasterFormat.Png8);
    }

    [Fact]
    public void TheDialogNamesTheDepthItIsAboutToWrite()
    {
        // The menu row vanishes when the dialog opens, so the filter name is the only thing left
        // saying which PNG this save is. These strings ARE that confirmation.
        PngDepth.SixteenBit.Png().DisplayName().ShouldBe("PNG (16-bit)");
        PngDepth.EightBit.Png().DisplayName().ShouldBe("PNG (8-bit)");
        DisplayRasterFormat.Jpeg.DisplayName().ShouldBe("JPEG");
        DisplayRasterFormat.TiffFloat.DisplayName().ShouldBe("TIFF (32-bit float)");
    }

    [Fact]
    public void BothPngDepthsShareTheOneExtension()
    {
        // This is the whole reason the depth cannot ride on the file name, and the reason the choice
        // had to become a menu row rather than a fourth filter in the dialog.
        DisplayRasterFormat.Png16.Extension().ShouldBe(".png");
        DisplayRasterFormat.Png8.Extension().ShouldBe(".png");
    }
}
