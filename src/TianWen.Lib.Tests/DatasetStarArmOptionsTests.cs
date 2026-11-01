using Shouldly;
using TianWen.Cli;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The CLI's check of which star-arm flags cannot be given together (#1949).</summary>
public class DatasetStarArmOptionsTests
{
    [Fact]
    public void TheClampAndAChromaticWidthAreRefusedWithTheReason()
    {
        var message = DatasetSubCommand.StarArmConflict(clampWingRelations: true, channelWingCut: false, chromaticWidth: true, channelRelations: false);
        message.ShouldNotBeNull();
        message.ShouldContain("--clamp-wing-relations and --chromatic-width");
        message.ShouldContain("channel widths");
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false)]
    public void EachFlagAloneIsAllowed(bool clamp, bool wingCut, bool chromatic, bool relations) =>
        DatasetSubCommand.StarArmConflict(clamp, wingCut, chromatic, relations).ShouldBeNull();

    [Theory]
    [InlineData(true, true, false, false, "--clamp-wing-relations and --channel-wing-cut are two arms; give one")]
    [InlineData(false, true, false, true, "--channel-wing-cut and --channel-relations are two arms; give one")]
    [InlineData(false, false, true, true, "--chromatic-width and --channel-relations or --channel-wing-cut are two arms; give one")]
    [InlineData(false, true, true, false, "--chromatic-width and --channel-relations or --channel-wing-cut are two arms; give one")]
    public void TheOlderPairsKeepTheirMessages(bool clamp, bool wingCut, bool chromatic, bool relations, string expected) =>
        DatasetSubCommand.StarArmConflict(clamp, wingCut, chromatic, relations).ShouldBe(expected);
}
