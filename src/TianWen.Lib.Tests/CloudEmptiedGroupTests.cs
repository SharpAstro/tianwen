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

    // #1992: a forced drizzle needs 60 matched frames, counted after the cloud filter. A group that had them before the filter
    // says that the filter took it under, and what keeps the frames; one that registration left short says only how many it got.
    [Theory]
    [InlineData(57, 4, "BayerDrizzle requires >= 60 matched frames (got 57: 4 of 61 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(59, 1, "BayerDrizzle requires >= 60 matched frames (got 59: 1 of 60 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(20, 40, "BayerDrizzle requires >= 60 matched frames (got 20: 40 of 60 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(57, 0, "BayerDrizzle requires >= 60 matched frames (got 57)")]  // no cloud involved
    [InlineData(50, 4, "BayerDrizzle requires >= 60 matched frames (got 50)")]  // 54 before the filter: short without it too
    public void AForcedDrizzleTheFilterTookUnderItsMinimumSaysSo(int matched, int leftOut, string expected)
        => CloudRejection.ForcedDrizzleSkipReason(IntegrationStrategyKind.BayerDrizzle, 60, matched, leftOut).ShouldBe(expected);
}
