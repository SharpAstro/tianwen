using System;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

public class CameraDriverExtensionsTests
{
    [Theory]
    // remainingMs, leadMs, expectedMs
    [InlineData(2000, 50, 1950)]  // far from end: one long sleep landing leadMargin before the end
    [InlineData(60, 50, 10)]      // inside the lead window (> fine window): coarse 10 ms cadence
    [InlineData(50, 50, 10)]      // exactly at the lead boundary: coarse cadence (not the long sleep)
    [InlineData(11, 50, 10)]      // just above the fine window: still coarse
    [InlineData(10, 50, 1)]       // at the fine window: switch to 1 ms cadence
    [InlineData(3, 50, 1)]        // final stretch: 1 ms cadence
    [InlineData(0, 50, 1)]        // predicted end reached, not yet ready: 1 ms cadence
    [InlineData(-25, 50, 1)]      // exposure overran (negative remaining): still 1 ms, never <= 0
    public void NextImageReadyPollDelay_FollowsAdaptiveCadence(int remainingMs, int leadMs, int expectedMs)
    {
        var delay = CameraDriverExtensions.NextImageReadyPollDelay(
            TimeSpan.FromMilliseconds(remainingMs), TimeSpan.FromMilliseconds(leadMs));

        delay.ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Theory]
    [InlineData(5000, 50)]
    [InlineData(50, 50)]
    [InlineData(10, 50)]
    [InlineData(0, 50)]
    [InlineData(-100, 50)]
    public void NextImageReadyPollDelay_IsAlwaysStrictlyPositive(int remainingMs, int leadMs)
    {
        var delay = CameraDriverExtensions.NextImageReadyPollDelay(
            TimeSpan.FromMilliseconds(remainingMs), TimeSpan.FromMilliseconds(leadMs));

        delay.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
