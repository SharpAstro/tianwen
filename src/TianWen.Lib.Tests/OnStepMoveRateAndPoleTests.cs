using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.OnStep;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The two pure rules of #1940 finding 5: the top move rate worked out of <c>:GXE6#</c> and <c>:GX92#</c>, and which
/// declination is the pole a mount's home points at.
/// </summary>
public class OnStepMoveRateAndPoleTests
{
    // 1000000 / :GX92# (microseconds a step) / :GXE6# (steps a sidereal second) / 240. The first row is what the fake
    // answers (11378 steps a degree is 47.408333 a sidereal second, a step every 50 us): 422x sidereal.
    [Theory]
    [InlineData("47.408333", "50.000", 1.7578)]
    [InlineData("2.0", "1000", 2.0833)]
    [InlineData("1", "240", 17.3611)]
    [InlineData("47.408333", "1.000", 87.8906)]
    public void FastestMoveIsTheConfiguredTopRateOverTheSiderealStepRate(string gxe6, string gx92, double expectedDegPerSec)
        => OnStepMountDriver<FakeDevice>.FastestMoveDegPerSec(gxe6, gx92).ShouldNotBeNull().ShouldBe(expectedDegPerSec, 1e-3);

    [Theory]
    [InlineData(null, null)]
    [InlineData("47.408333", null)]
    [InlineData(null, "50.000")]
    [InlineData("0", "50.000")]
    [InlineData("-47.4", "50.000")]
    [InlineData("47.408333", "0")]
    [InlineData("47.408333", "0.999")]
    [InlineData("47.408333", "-50")]
    [InlineData("abc", "50.000")]
    [InlineData("47.408333", "abc")]
    [InlineData("", "")]
    [InlineData("NaN", "50.000")]
    [InlineData("Infinity", "50.000")]
    public void FastestMoveIsUnknownWhenEitherReadIsMissingOrMakesNoSense(string? gxe6, string? gx92)
        => OnStepMountDriver<FakeDevice>.FastestMoveDegPerSec(gxe6, gx92).ShouldBeNull();

    [Theory]
    // North pole of a northern site, and within the half degree tolerance of it.
    [InlineData(90.0, 48.2, true)]
    [InlineData(89.5, 48.2, true)]
    [InlineData(89.4, 48.2, false)]
    // South pole of a southern site.
    [InlineData(-90.0, -37.9, true)]
    [InlineData(-89.5, -37.9, true)]
    [InlineData(-89.4, -37.9, false)]
    // The other hemisphere's pole is not the pole home points at.
    [InlineData(-90.0, 48.2, false)]
    [InlineData(90.0, -37.9, false)]
    // Not a pole at all.
    [InlineData(0.0, 48.2, false)]
    [InlineData(45.0, -37.9, false)]
    public void ThePoleIsTheOneOfTheSitesOwnHemisphere(double dec, double siteLatitude, bool expected)
        => MountGoto.IsSitesCelestialPole(dec, siteLatitude).ShouldBe(expected);
}
