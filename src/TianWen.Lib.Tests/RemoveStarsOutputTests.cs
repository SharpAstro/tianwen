using Shouldly;
using TianWen.Cli;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen image remove-stars</c> writes the stars beside the STARLESS output. When <c>-o</c> named a
/// <c>_starless</c> file in another folder, the stars used to go beside the input instead, which for a master read
/// from a dataset store put a <c>_stars.fits</c> into that store's session masters.
/// </summary>
public class RemoveStarsOutputTests
{
    [Theory]
    [InlineData("masters/m45_starless.fits", "masters/m45_stars.fits")]   // the default: beside the input
    [InlineData("out/m45_starless.fits", "out/m45_stars.fits")]           // -o elsewhere with a _starless name
    [InlineData("out/m45.fits", "out/m45_stars.fits")]                    // -o elsewhere with any other name
    [InlineData("out/M45_STARLESS.fits", "out/M45_stars.fits")]           // the suffix in any case
    [InlineData("out/m45_starless.fit", "out/m45_stars.fits")]            // another FITS extension
    public void TheStarsGoBesideTheStarlessOutput(string starless, string expected)
    {
        ImageSubCommand.StarsOutput(starless).ShouldBe(expected);
    }
}
