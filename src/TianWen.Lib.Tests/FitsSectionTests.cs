using System.Drawing;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="FitsSection"/>: the IRAF section string against the 0-based rectangle it means.
/// </summary>
/// <remarks>
/// The conversion differs from the identity in TWO places, the origin and the extent, so a test that
/// only uses sections starting at 1 passes with the extent fix deleted (<c>[1:16]</c> is sixteen
/// columns from 0 either way). Every case here that matters therefore starts somewhere other than 1.
/// </remarks>
public class FitsSectionTests
{
    [Theory]
    // section, X, Y, Width, Height
    [InlineData("[1:16,1:8]", 0, 0, 16, 8)]
    [InlineData("[25:4200,1:2795]", 24, 0, 4176, 2795)]      // a QHY294-shaped picture past its black columns
    [InlineData("[1:24,1:2795]", 0, 0, 24, 2795)]            // and the shielded strip beside it
    [InlineData("[7:7,9:9]", 6, 8, 1, 1)]                    // one pixel: the case an off-by-one cannot hide in
    [InlineData("25:4200,1:2795", 24, 0, 4176, 2795)]        // brackets are optional, some writers omit them
    [InlineData("  [25:4200 , 1:2795 ]  ", 24, 0, 4176, 2795)]
    [InlineData("[25:4200,1:2795,1:3]", 24, 0, 4176, 2795)]  // a cube's third axis is accepted and ignored
    public void ASectionIsOneBasedAndInclusiveWhereARectangleIsNot(string value, int x, int y, int w, int h)
    {
        FitsSection.TryParse(value, out var section).ShouldBeTrue();
        section.ShouldBe(new Rectangle(x, y, w, h));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1:16]")]                 // one axis is not a rectangle
    [InlineData("[1:16,1:8,1:2,1:2]")]     // four is not either
    [InlineData("[16:1,1:8]")]             // reversed: legal IRAF for a mirrored read, not this rectangle
    [InlineData("[0:16,1:8]")]             // 0 is not a FITS index
    [InlineData("[-4:16,1:8]")]
    [InlineData("[1:16:2,1:8]")]           // a stride is not a rectangle, and must not read as its bounds
    [InlineData("[a:16,1:8]")]
    [InlineData("[1-16,1-8]")]
    public void AnythingThatIsNotASectionIsRefusedRatherThanGuessed(string? value)
    {
        FitsSection.TryParse(value, out var section).ShouldBeFalse();
        section.ShouldBe(default);
    }

    [Theory]
    [InlineData(24, 0, 4176, 2795, "[25:4200,1:2795]")]
    [InlineData(0, 0, 16, 8, "[1:16,1:8]")]
    [InlineData(6, 8, 1, 1, "[7:7,9:9]")]
    public void FormatWritesBackWhatParseRead(int x, int y, int w, int h, string expected)
    {
        var section = new Rectangle(x, y, w, h);
        FitsSection.Format(section).ShouldBe(expected);
        FitsSection.TryParse(expected, out var round).ShouldBeTrue();
        round.ShouldBe(section);
    }

    [Fact]
    public void AnEmptyRectangleHasNoSectionToWrite()
    {
        // A zero-width section would format as [5:4,...], which reads back as reversed and is refused
        // above, so the round trip would silently lose it. Throwing says so at the write instead.
        Should.Throw<System.ArgumentOutOfRangeException>(() => FitsSection.Format(new Rectangle(4, 0, 0, 8)));
        Should.Throw<System.ArgumentOutOfRangeException>(() => FitsSection.Format(new Rectangle(4, 0, 8, 0)));
        Should.Throw<System.ArgumentOutOfRangeException>(() => FitsSection.Format(new Rectangle(-1, 0, 8, 8)));
    }
}
