using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SkyMapState.MilkyWayAlpha"/>, the Milky Way fade both sky maps draw with: the Vulkan tab and
/// the browser's WebGL pipeline. It lived inline in the Vulkan tab until the browser needed the same
/// number, so these pin the curve itself rather than either host.
/// </summary>
public class SkyMapMilkyWayAlphaTests
{
    private static SkyMapState Loaded(double fovDeg = 30.0)
        => new SkyMapState { MilkyWayAvailable = true, ShowMilkyWay = true, FieldOfViewDeg = fovDeg };

    [Theory]
    [InlineData(-30.0, 1.0)]  // astronomical night
    [InlineData(-18.0, 1.0)]  // astronomical twilight begins: fully faded in
    [InlineData(-12.0, 0.5)]  // nautical twilight: halfway
    [InlineData(-6.0, 0.0)]   // civil twilight: gone
    [InlineData(10.0, 0.0)]   // day
    public void TheSunFadesItBetweenCivilAndAstronomicalTwilight(double sunAltDeg, double expected)
        => Loaded().MilkyWayAlpha(sunAltDeg).ShouldBe((float)expected, 1e-5f);

    [Theory]
    [InlineData(40.0, 1.0)]
    [InlineData(80.0, 0.5)]
    [InlineData(400.0, 0.3)]  // the floor: a whole-sky view keeps some of the band
    public void AWideFieldDimsItTowardAFloor(double fovDeg, double expected)
        => Loaded(fovDeg).MilkyWayAlpha(-30.0).ShouldBe((float)expected, 1e-5f);

    [Fact]
    public void NothingDrawsWhenSwitchedOffUnloadedOrWithoutASite()
    {
        new SkyMapState { MilkyWayAvailable = true, ShowMilkyWay = false }.MilkyWayAlpha(-30.0).ShouldBe(0f);
        new SkyMapState { MilkyWayAvailable = false, ShowMilkyWay = true }.MilkyWayAlpha(-30.0).ShouldBe(0f);
        // No site is a NaN sun altitude. The browser hands this value to the shader as a colour byte, so
        // it must be a number, not NaN.
        Loaded().MilkyWayAlpha(double.NaN).ShouldBe(0f);
    }
}
