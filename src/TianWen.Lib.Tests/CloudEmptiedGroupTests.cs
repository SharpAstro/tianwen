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

    // #1992, #2419: a forced drizzle needs 60 matched frames, counted after the star-quality and the cloud filter, which are
    // both on by default. A group that REGISTERED enough says which filter took it under and what keeps the frames; one that
    // registration left short, or that no filter touched, says only how many it got.
    [Theory]
    [InlineData(57, 0, 4, "BayerDrizzle requires >= 60 matched frames (got 57: 4 of 61 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(59, 0, 1, "BayerDrizzle requires >= 60 matched frames (got 59: 1 of 60 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(20, 0, 40, "BayerDrizzle requires >= 60 matched frames (got 20: 40 of 60 were left out for cloud or twilight; --reject-cloud false keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(57, 4, 0, "BayerDrizzle requires >= 60 matched frames (got 57: 4 of 61 were left out by the star-quality filter; --quality-reject-sigma 0 keeps them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(57, 3, 1, "BayerDrizzle requires >= 60 matched frames (got 57: of 61 registered, 3 were left out by the star-quality filter and 1 for cloud or twilight; --quality-reject-sigma 0 and --reject-cloud false keep them, --drizzle-min-frames lowers the minimum)")]
    [InlineData(57, 0, 0, "BayerDrizzle requires >= 60 matched frames (got 57)")]  // no filter involved: the registration is short
    [InlineData(50, 0, 4, "BayerDrizzle requires >= 60 matched frames (got 50)")]  // 54 registered: short without the filter too
    [InlineData(50, 3, 4, "BayerDrizzle requires >= 60 matched frames (got 50)")]  // 57 registered: short without either
    public void AForcedDrizzleAFilterTookUnderItsMinimumSaysWhich(int matched, int leftOutForQuality, int leftOutForCloud, string expected)
        => DrizzleStrategy.ForcedSkipReason(IntegrationStrategyKind.BayerDrizzle, 60, matched, leftOutForQuality, leftOutForCloud).ShouldBe(expected);
}
