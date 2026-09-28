using PlayerOne.SDK;
using QHYCCD.SDK;
using Shouldly;
using TianWen.DAL;
using Xunit;
using ZWOptical.SDK;

namespace TianWen.Lib.Tests;

/// <summary>
/// A vendor SDK refuses a control it has no equivalent for with <see cref="CMOSErrorCode.InvalidControlType"/>,
/// and never throws.
/// </summary>
/// <remarks>
/// <para>The DAL camera's connect writes ONE list of start-up controls to every vendor and ignores a
/// refusal, since each vendor maps a different subset. So a throw is not a refusal but a failed connect:
/// ZWOptical.SDK 6.0 unmapped <see cref="CMOSControlType.EnableDDR"/> (a QHY control) and threw for it,
/// and every ZWO camera stopped connecting, found live with an ASI462MC over the Alpaca plane.
/// QHYCCD.SDK had thrown for <see cref="CMOSControlType.Flip"/>, the first control of the list, since
/// its first version, so no QHY camera had connected through the driver at all.</para>
/// <para>Each row is a control of the connect's list that the vendor does not map, which is also what
/// makes the test safe: the refusal is answered before any native call, so no camera and no native
/// library is involved. ToupTek is not here because its camera cannot be made without a device; its
/// refusals go through <c>ToDALError(E_NOTIMPL)</c>, which is the same code.</para>
/// </remarks>
public class VendorControlContractTests
{
    public static TheoryData<string, CMOSControlType> UnmappedStartupControls => new()
    {
        { "ZWO", CMOSControlType.EnableDDR },
        { "QHY", CMOSControlType.Flip },
        { "QHY", CMOSControlType.MonoBin },
        { "QHY", CMOSControlType.HardwareBin },
        { "QHY", CMOSControlType.PatternAdjust },
        { "PlayerOne", CMOSControlType.Flip },
        { "PlayerOne", CMOSControlType.Gamma },
        { "PlayerOne", CMOSControlType.HighSpeedMode },
        { "PlayerOne", CMOSControlType.PatternAdjust },
        { "PlayerOne", CMOSControlType.EnableDDR },
    };

    [Theory]
    [MemberData(nameof(UnmappedStartupControls))]
    public void AControlTheVendorDoesNotMapIsRefusedByCodeOnWrite(string vendor, CMOSControlType control)
    {
        Camera(vendor).SetControlValue(control, 1).ShouldBe(CMOSErrorCode.InvalidControlType);
    }

    [Theory]
    [MemberData(nameof(UnmappedStartupControls))]
    public void AControlTheVendorDoesNotMapIsRefusedByCodeOnRead(string vendor, CMOSControlType control)
    {
        Camera(vendor).GetControlValue(control, out var value, out var isAuto).ShouldBe(CMOSErrorCode.InvalidControlType);
        value.ShouldBe(0);
        isAuto.ShouldBeFalse();
    }

    /// <summary>
    /// Every DAL control ZWO has an equivalent for, and the three it does not. The table is transcribed here so a
    /// control dropped from the binding fails a test: <see cref="CMOSControlType.TemperatureDeci"/> was missing from
    /// it from the day it was written, so no ZWO body reported a sensor temperature and a cooled one's ramp had
    /// nothing to read.
    /// </summary>
    [Fact]
    public void ZwoMapsEveryControlItHas()
    {
        var expected = new System.Collections.Generic.Dictionary<CMOSControlType, ASICamera2.ASI_CONTROL_TYPE?>
        {
            [CMOSControlType.Gain] = ASICamera2.ASI_CONTROL_TYPE.ASI_GAIN,
            [CMOSControlType.Exposure] = ASICamera2.ASI_CONTROL_TYPE.ASI_EXPOSURE,
            [CMOSControlType.Gamma] = ASICamera2.ASI_CONTROL_TYPE.ASI_GAMMA,
            [CMOSControlType.WB_R] = ASICamera2.ASI_CONTROL_TYPE.ASI_WB_R,
            [CMOSControlType.WB_G] = null,
            [CMOSControlType.WB_B] = ASICamera2.ASI_CONTROL_TYPE.ASI_WB_B,
            [CMOSControlType.Brightness] = ASICamera2.ASI_CONTROL_TYPE.ASI_BRIGHTNESS,
            [CMOSControlType.BandwidthOverload] = ASICamera2.ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD,
            [CMOSControlType.Overclock] = ASICamera2.ASI_CONTROL_TYPE.ASI_OVERCLOCK,
            [CMOSControlType.TemperatureDeci] = ASICamera2.ASI_CONTROL_TYPE.ASI_TEMPERATURE,
            [CMOSControlType.Flip] = ASICamera2.ASI_CONTROL_TYPE.ASI_FLIP,
            [CMOSControlType.AutoMaxGain] = ASICamera2.ASI_CONTROL_TYPE.ASI_AUTO_MAX_GAIN,
            [CMOSControlType.AutoMaxExposure] = ASICamera2.ASI_CONTROL_TYPE.ASI_AUTO_MAX_EXP,
            [CMOSControlType.AutoMaxBrightness] = ASICamera2.ASI_CONTROL_TYPE.ASI_AUTO_MAX_BRIGHTNESS,
            [CMOSControlType.HardwareBin] = ASICamera2.ASI_CONTROL_TYPE.ASI_HARDWARE_BIN,
            [CMOSControlType.HighSpeedMode] = ASICamera2.ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE,
            [CMOSControlType.CoolerPowerPercent] = ASICamera2.ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC,
            [CMOSControlType.TargetTemperature] = ASICamera2.ASI_CONTROL_TYPE.ASI_TARGET_TEMP,
            [CMOSControlType.CoolerOn] = ASICamera2.ASI_CONTROL_TYPE.ASI_COOLER_ON,
            [CMOSControlType.MonoBin] = ASICamera2.ASI_CONTROL_TYPE.ASI_MONO_BIN,
            [CMOSControlType.FanOn] = ASICamera2.ASI_CONTROL_TYPE.ASI_FAN_ON,
            [CMOSControlType.PatternAdjust] = ASICamera2.ASI_CONTROL_TYPE.ASI_PATTERN_ADJUST,
            [CMOSControlType.AntiDewHeater] = ASICamera2.ASI_CONTROL_TYPE.ASI_ANTI_DEW_HEATER,
            [CMOSControlType.Humidity] = null,
            [CMOSControlType.EnableDDR] = null,
        };

        expected.Keys.ShouldBe(System.Enum.GetValues<CMOSControlType>(), ignoreOrder: true, "every DAL control has a row here");
        foreach (var (control, asi) in expected)
        {
            var mapped = ASICamera2.DALControlTypeToASI(control, out var actual);
            (mapped ? actual : (ASICamera2.ASI_CONTROL_TYPE?)null).ShouldBe(asi, $"{control}");
        }
    }

    private static ICMOSNativeInterface Camera(string vendor) => vendor switch
    {
        "ZWO" => default(ASICamera2.ASI_CAMERA_INFO),
        "QHY" => default(QHYCamera.QHYCCD_CAMERA_INFO),
        "PlayerOne" => default(PlayerOneCamera.POACameraProperties),
        _ => throw new System.ArgumentOutOfRangeException(nameof(vendor), vendor, "no such vendor in this test"),
    };
}
