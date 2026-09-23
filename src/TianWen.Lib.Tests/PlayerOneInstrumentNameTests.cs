using Shouldly;
using TianWen.Lib.Devices.PlayerOne;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A Player One camera names itself in INSTRUME the way SharpCap does, because calibration only
/// pairs with lights whose camera name matches exactly.
/// </summary>
public class PlayerOneInstrumentNameTests
{
    [Fact]
    public void TheBenchUranusCIsNamedAsSharpCapNamedItsLights()
    {
        // SharpCap v4.0 wrote exactly this into every Uranus-C light in the archive; TianWen wrote
        // the bare "Uranus-C", which CalibrationResolver hard-mismatches.
        PlayerOneDevice.InstrumentName("Uranus-C", "IMX585").ShouldBe("Uranus-C (IMX585)");
    }

    [Theory]
    [InlineData("Uranus-C", null, "Uranus-C")]
    [InlineData("Uranus-C", "", "Uranus-C")]
    [InlineData("Mars-C IMX462", "IMX462", "Mars-C IMX462")]
    [InlineData("Poseidon-C (imx571)", "IMX571", "Poseidon-C (imx571)")]
    public void ANameThatCannotOrNeedNotCarryTheDieIsLeftAlone(string model, string? sensor, string expected)
    {
        PlayerOneDevice.InstrumentName(model, sensor).ShouldBe(expected);
    }
}
