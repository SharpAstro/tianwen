using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A calibration frame serves a light only in the light's readout mode: some modes keep the frame's size and change what
/// the gain means, so the size alone cannot keep them apart. An unknown mode on either side stays a wildcard.
/// </summary>
public class CalibrationReadoutModeTests
{
    private static MasterGroupKey Key(FrameType type, string? mode) =>
        new(type, TimeSpan.FromSeconds(60), -10, "", 4212, 2850, 1, SensorType.RGGB, 1600, 20, mode);

    [Theory]
    [InlineData("11M MODE", "11M MODE", true)]
    [InlineData("11M MODE", "11m mode", true)]
    [InlineData("11M MODE", "High Gain", false)]
    [InlineData(null, "11M MODE", true)]
    [InlineData("11M MODE", null, true)]
    [InlineData(null, null, true)]
    public void ADarkMatchesALightOnlyInItsReadoutMode(string? darkMode, string? lightMode, bool compatible)
        => CalibrationResolver.DimensionCompatible(Key(FrameType.Dark, darkMode), Key(FrameType.Light, lightMode)).ShouldBe(compatible);

    [Fact]
    public void TwoModesOfOneSizeWriteTwoMastersAndNoModeKeepsTheNameItHad()
    {
        Key(FrameType.Dark, "11M MODE").Slug().ShouldNotBe(Key(FrameType.Dark, "High Gain").Slug());
        Key(FrameType.Dark, "11M MODE").Slug().ShouldEndWith("_11MMODE");
        Key(FrameType.Dark, null).Slug().ShouldBe("dark_60s_-10C_g1600");
    }
}
