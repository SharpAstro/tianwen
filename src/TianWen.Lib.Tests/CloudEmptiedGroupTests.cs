using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A group the cloud filter leaves with fewer than two frames is skipped for cloud or twilight, not as "fewer than 2 matched frames"
/// with the registration census, since its frames registered (#1998); a group registration already left short keeps the census.
/// </summary>
public class CloudEmptiedGroupTests
{
    [Theory]
    [InlineData(12, 12, "12 of 12 matched frames left out for cloud or twilight, fewer than 2 remain")]
    [InlineData(12, 11, "11 of 12 matched frames left out for cloud or twilight, fewer than 2 remain")]
    [InlineData(2, 1, "1 of 2 matched frames left out for cloud or twilight, fewer than 2 remain")]
    [InlineData(12, 10, null)] // two frames remain: the group stacks
    [InlineData(1, 0, null)]   // registration was short, no cloud involved: the census is the diagnosis
    [InlineData(1, 1, null)]   // registration was already short before the filter
    [InlineData(0, 0, null)]
    public void AGroupTheFilterEmptiesSaysSoRatherThanBlamingRegistration(int matchedBeforeCloud, int leftOut, string? expected)
        => CloudRejection.EmptiedGroupReason(matchedBeforeCloud, leftOut).ShouldBe(expected);
}
