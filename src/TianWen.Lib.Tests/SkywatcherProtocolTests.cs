using System;
using Shouldly;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Skywatcher")]
public class SkywatcherProtocolTests
{
    #region Hex LE Encoding Roundtrips

    [Theory]
    [InlineData(0x800000u, "000080")] // home position: LE bytes 0x00,0x00,0x80
    [InlineData(0u, "000000")]
    [InlineData(0xFFFFFFu, "FFFFFF")]
    [InlineData(1u, "010000")]
    [InlineData(0x123456u, "563412")]
    public void EncodeUInt24_ProducesCorrectHex(uint value, string expected)
    {
        SkywatcherProtocol.EncodeUInt24(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("000080", 0x800000u)]
    [InlineData("000000", 0u)]
    [InlineData("FFFFFF", 0xFFFFFFu)]
    [InlineData("010000", 1u)]
    [InlineData("563412", 0x123456u)]
    public void DecodeUInt24_ProducesCorrectValue(string hex, uint expected)
    {
        SkywatcherProtocol.DecodeUInt24(hex.AsSpan()).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x800000u)]
    [InlineData(0xFFFFFFu)]
    [InlineData(0x123456u)]
    [InlineData(9024000u)] // EQ6-R CPR
    public void EncodeDecodeUInt24_Roundtrip(uint value)
    {
        // Only test values that fit in 24 bits
        var encoded = SkywatcherProtocol.EncodeUInt24(value & 0xFFFFFF);
        SkywatcherProtocol.DecodeUInt24(encoded.AsSpan()).ShouldBe(value & 0xFFFFFF);
    }

    #endregion

    #region Position Encoding

    [Theory]
    [InlineData(0, "000080")]    // home = 0x800000, LE: 0x00,0x00,0x80
    [InlineData(1, "010080")]    // 0x800001, LE: 0x01,0x00,0x80
    [InlineData(-1, "FFFF7F")]   // 0x7FFFFF, LE: 0xFF,0xFF,0x7F
    public void EncodePosition_OffsetsFromHome(int steps, string expected)
    {
        SkywatcherProtocol.EncodePosition(steps).ShouldBe(expected);
    }

    [Theory]
    [InlineData("000080", 0)]
    [InlineData("010080", 1)]
    [InlineData("FFFF7F", -1)]
    public void DecodePosition_OffsetsFromHome(string hex, int expected)
    {
        SkywatcherProtocol.DecodePosition(hex.AsSpan()).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000000)]
    [InlineData(-1000000)]
    [InlineData(4512000)]  // +180° for EQ6 CPR 9024000
    [InlineData(-4512000)] // -180°
    public void EncodeDecodePosition_Roundtrip(int steps)
    {
        var encoded = SkywatcherProtocol.EncodePosition(steps);
        SkywatcherProtocol.DecodePosition(encoded.AsSpan()).ShouldBe(steps);
    }

    #endregion

    #region Speed Formula

    [Fact]
    public void ComputeT1Preset_SiderealRate_ProducesReasonableValue()
    {
        // EQ6-R: CPR=9024000, TMR_FREQ=1500000, sidereal = 15.0417"/sec = 0.00417825 deg/sec
        var siderealDegPerSec = 15.0417 / 3600.0;
        var t1 = SkywatcherProtocol.ComputeT1Preset(1500000, 9024000, siderealDegPerSec, false, 16);
        // T1 = 1500000 * 360 / 0.00417825 / 9024000 ≈ 14323
        t1.ShouldBeInRange(14000u, 15000u);
    }

    [Fact]
    public void ComputeT1Preset_HighSpeed_MultipliesByRatio()
    {
        // Use values that divide evenly: CPR=1600000, TMR=1600000 → exact division
        var speed = 1.0; // 1 deg/sec → T1 = 1600000 * 360 / 1.0 / 1600000 = 360
        var lowSpeed = SkywatcherProtocol.ComputeT1Preset(1600000, 1600000, speed, false, 16);
        var highSpeed = SkywatcherProtocol.ComputeT1Preset(1600000, 1600000, speed, true, 16);
        lowSpeed.ShouldBe(360u);
        highSpeed.ShouldBe(360u * 16);
    }

    #endregion

    #region Firmware Parsing

    [Fact]
    public void TryParseFirmwareResponse_EQ6R_ParsesCorrectly()
    {
        // EQ6 (model 0x00), firmware 3.39
        // Board=0x00, minor=39 (0x27), major=3 (0x03)
        // LE encoded: byte0=0x00, byte1=0x27, byte2=0x03 → "002703"
        var result = SkywatcherProtocol.TryParseFirmwareResponse("002703", out var info);
        result.ShouldBeTrue();
        info.MountModel.ShouldBe(SkywatcherMountModel.Eq6);
        info.VersionString.ShouldBe("3.39");
    }

    [Fact]
    public void TryParseFirmwareResponse_AzGTi_ParsesCorrectly()
    {
        // AZ-GTi (model 0xA5), firmware 3.40
        // LE: byte0=0xA5, byte1=0x28, byte2=0x03 → "A52803"
        var result = SkywatcherProtocol.TryParseFirmwareResponse("A52803", out var info);
        result.ShouldBeTrue();
        info.MountModel.ShouldBe(SkywatcherMountModel.AzGTi);
        info.VersionString.ShouldBe("3.40");
    }

    [Fact]
    public void TryParseFirmwareResponse_TooShort_ReturnsFalse()
    {
        SkywatcherProtocol.TryParseFirmwareResponse("0027", out _).ShouldBeFalse();
    }

    #endregion

    #region Mount Model Names

    [Fact]
    public void MountModel_DisplayName_ReturnsExpected()
    {
        SkywatcherMountModel.Eq6.DisplayName.ShouldBe("EQ6");
        SkywatcherMountModel.Heq5.DisplayName.ShouldBe("HEQ5");
        SkywatcherMountModel.AzGTi.DisplayName.ShouldBe("AZ-GTi");
        SkywatcherMountModel.StarAdventurer.DisplayName.ShouldBe("Star Adventurer");
        SkywatcherMountModel.StarDiscovery.DisplayName.ShouldBe("Star Discovery");
        ((SkywatcherMountModel)0xFF).DisplayName.ShouldBe("Unknown (0xFF)");
    }

    #endregion

    #region Capability Parsing

    [Fact]
    public void ParseCapabilities_AllZeros_NothingSet()
    {
        var caps = SkywatcherProtocol.ParseCapabilities("000000");
        caps.IsPPecTraining.ShouldBeFalse();
        caps.IsPPecOn.ShouldBeFalse();
        caps.CanDualEncoders.ShouldBeFalse();
        caps.CanPPec.ShouldBeFalse();
        caps.CanHomeSensors.ShouldBeFalse();
        caps.CanAzEq.ShouldBeFalse();
        caps.CanPolarLed.ShouldBeFalse();
        caps.CanAxisSlewsIndependent.ShouldBeFalse();
        caps.CanHalfCurrentTracking.ShouldBeFalse();
        caps.CanWifi.ShouldBeFalse();
    }

    [Fact]
    public void ParseCapabilities_PPecAndHomeSensors()
    {
        // nibble0=0, nibble1=6 (CanPPec=0x2 | CanHomeSensors=0x4), nibble2=0
        var caps = SkywatcherProtocol.ParseCapabilities("060000");
        caps.CanPPec.ShouldBeTrue();
        caps.CanHomeSensors.ShouldBeTrue();
        caps.CanDualEncoders.ShouldBeFalse();
        caps.CanAzEq.ShouldBeFalse();
    }

    [Fact]
    public void ParseCapabilities_WiFiAndIndependentSlews()
    {
        // nibble0=0, nibble1=0, nibble2=A (CanAxisSlewsIndependent=0x2 | CanWifi=0x8)
        var caps = SkywatcherProtocol.ParseCapabilities("00A000");
        caps.CanAxisSlewsIndependent.ShouldBeTrue();
        caps.CanWifi.ShouldBeTrue();
        caps.CanPolarLed.ShouldBeFalse();
        caps.CanHalfCurrentTracking.ShouldBeFalse();
    }

    [Fact]
    public void ParseCapabilities_AllSet()
    {
        var caps = SkywatcherProtocol.ParseCapabilities("3FF000");
        caps.IsPPecTraining.ShouldBeTrue();
        caps.IsPPecOn.ShouldBeTrue();
        caps.CanDualEncoders.ShouldBeTrue();
        caps.CanPPec.ShouldBeTrue();
        caps.CanHomeSensors.ShouldBeTrue();
        caps.CanAzEq.ShouldBeTrue();
        caps.CanPolarLed.ShouldBeTrue();
        caps.CanAxisSlewsIndependent.ShouldBeTrue();
        caps.CanHalfCurrentTracking.ShouldBeTrue();
        caps.CanWifi.ShouldBeTrue();
    }

    #endregion

    #region Advanced Commands Support

    [Fact]
    public void SupportsAdvancedCommands_GeneralThreshold()
    {
        // Version > 0x032200 should support advanced
        SkywatcherProtocol.SupportsAdvancedCommands(0x032201, SkywatcherMountModel.Eq6).ShouldBeTrue();
        SkywatcherProtocol.SupportsAdvancedCommands(0x032200, SkywatcherMountModel.Eq6).ShouldBeFalse();
        SkywatcherProtocol.SupportsAdvancedCommands(0x010000, SkywatcherMountModel.Eq6).ShouldBeFalse();
    }

    [Fact]
    public void SupportsAdvancedCommands_AzGTi_RequiresHigherVersion()
    {
        // AZ-GTi (0xA5): needs >= 3.40
        // intVersion for AzGTi 3.40: (0xA5 << 16) | (40 << 8) | 3 = 0xA52803
        var threshold = ((int)SkywatcherMountModel.AzGTi << 16) | (40 << 8) | 3;
        SkywatcherProtocol.SupportsAdvancedCommands(threshold + 1, SkywatcherMountModel.AzGTi).ShouldBeTrue();
        SkywatcherProtocol.SupportsAdvancedCommands(threshold, SkywatcherMountModel.AzGTi).ShouldBeFalse();
    }

    [Fact]
    public void SupportsAdvancedCommands_StarAdventurer_Excluded()
    {
        // Version 0x038207 on Star Adventurer is explicitly excluded
        SkywatcherProtocol.SupportsAdvancedCommands(0x038207, SkywatcherMountModel.StarAdventurer).ShouldBeFalse();
    }

    #endregion

    #region Goto Step Adjustment

    [Fact]
    public void AdjustGotoSteps_NonStarDiscovery_Passthrough()
    {
        SkywatcherMountModel.Eq6.AdjustGotoSteps(5).ShouldBe(5);
    }

    [Fact]
    public void AdjustGotoSteps_StarDiscovery_SmallStepsClamped()
    {
        SkywatcherMountModel.StarDiscovery.AdjustGotoSteps(5).ShouldBe(10);
        SkywatcherMountModel.StarDiscovery.AdjustGotoSteps(9).ShouldBe(10);
        SkywatcherMountModel.StarDiscovery.AdjustGotoSteps(10).ShouldBe(10);
        SkywatcherMountModel.StarDiscovery.AdjustGotoSteps(100).ShouldBe(100);
    }

    #endregion

    #region Gear Ratio Override

    [Fact]
    public void OverrideGearRatio_80GT()
    {
        SkywatcherProtocol.OverrideGearRatio(9024000, SkywatcherMountModel.A80Gt).ShouldBe(78_848u);
    }

    [Fact]
    public void OverrideGearRatio_114GT()
    {
        SkywatcherProtocol.OverrideGearRatio(9024000, SkywatcherMountModel.A114Gt).ShouldBe(78_848u);
    }

    [Fact]
    public void OverrideGearRatio_Normal_Passthrough()
    {
        SkywatcherProtocol.OverrideGearRatio(9024000, SkywatcherMountModel.Eq6).ShouldBe(9024000u);
    }

    #endregion

    #region Guide Speed

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, 0.75)]
    [InlineData(2, 0.5)]
    [InlineData(3, 0.25)]
    [InlineData(4, 0.125)]
    public void GuideSpeedFraction_ReturnsExpected(int index, double expected)
    {
        SkywatcherProtocol.GuideSpeedFraction(index).ShouldBe(expected);
    }

    #endregion

    #region Build Command

    [Fact]
    public void BuildCommand_NoData()
    {
        var cmd = SkywatcherProtocol.BuildCommand('e', '1');
        cmd.ShouldBe(new byte[] { (byte)':', (byte)'e', (byte)'1', (byte)'\r' });
    }

    [Fact]
    public void BuildCommand_WithData()
    {
        var cmd = SkywatcherProtocol.BuildCommand('E', '1', "000008");
        cmd.ShouldBe(new byte[] { (byte)':', (byte)'E', (byte)'1', (byte)'0', (byte)'0', (byte)'0', (byte)'0', (byte)'0', (byte)'8', (byte)'\r' });
    }

    #endregion

    #region TryParseResponse

    [Fact]
    public void TryParseResponse_ValidOk()
    {
        SkywatcherProtocol.TryParseResponse("=000008", out var data).ShouldBeTrue();
        data.ShouldBe("000008");
    }

    [Fact]
    public void TryParseResponse_EmptyOk()
    {
        SkywatcherProtocol.TryParseResponse("=", out var data).ShouldBeTrue();
        data.ShouldBe("");
    }

    [Fact]
    public void TryParseResponse_Error()
    {
        SkywatcherProtocol.TryParseResponse("!1", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseResponse_Null()
    {
        SkywatcherProtocol.TryParseResponse(null, out _).ShouldBeFalse();
    }

    #endregion
}
