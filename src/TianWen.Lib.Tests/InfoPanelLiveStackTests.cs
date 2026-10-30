using System;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

public class InfoPanelLiveStackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 13, 21, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Stamp = new(2026, 10, 31, 13, 20, 41, 0, TimeSpan.Zero);

    [Fact]
    public void ALineNamesTheFrameItsTimeHowFarBehindAndHowOld()
        => InfoPanelData.LiveStackBuiltLine(5399, Stamp, 5811, Now, Now - TimeSpan.FromSeconds(8))
            .ShouldBe("Live stack: built at frame 5400 (13:20:41.0 UT), 412 frames behind the playhead, updated 8 s ago");

    [Fact]
    public void AMasterAtThePlayheadSaysSo()
        => InfoPanelData.LiveStackBuiltLine(99, Stamp, 99, Now, Now)
            .ShouldBe("Live stack: built at frame 100 (13:20:41.0 UT), at the playhead, updated 0 s ago");

    [Fact]
    public void OneFrameBehindIsSingular()
        => InfoPanelData.LiveStackBuiltLine(9, null, 10, Now, null)
            .ShouldBe("Live stack: built at frame 10, 1 frame behind the playhead");

    [Fact]
    public void AMissingTimestampDropsOnlyItsPart()
        => InfoPanelData.LiveStackBuiltLine(12344, null, 12400, Now, Now - TimeSpan.FromSeconds(3))
            .ShouldBe("Live stack: built at frame 12345, 56 frames behind the playhead, updated 3 s ago");

    [Fact]
    public void NoMasterYetSaysNothing()
        => InfoPanelData.LiveStackBuiltLine(-1, Stamp, 10, Now, Now).ShouldBeNull();

    [Theory]
    [InlineData(59, "updated 59 s ago")]
    [InlineData(60, "updated 1 min ago")]
    [InlineData(185, "updated 3 min ago")]
    public void TheAgeRollsFromSecondsToMinutes(int seconds, string expected)
        => InfoPanelData.LiveStackBuiltLine(0, null, -1, Now, Now - TimeSpan.FromSeconds(seconds))
            .ShouldBe($"Live stack: built at frame 1, {expected}");
}
