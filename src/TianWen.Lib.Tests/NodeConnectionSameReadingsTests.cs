using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Whether a periodic read of a node's devices changed what the view shows (#2130). A read that changed nothing asks for
/// no frame, so this comparison is what keeps an idle rig from redrawing its whole window every two seconds. Each reading
/// below is built anew, as a read off the wire is: nothing here is equal by reference.
/// </summary>
public class NodeConnectionSameReadingsTests
{
    private const string CameraUri = "camera://fake/1";
    private const string WheelUri = "filterwheel://fake/1";
    private const string FocuserUri = "focuser://fake/1";

    private static DeviceStateDto Camera(double ccd = -10.02, double power = 41.3, short gain = 100, string lastMode = "HCG") => new DeviceStateDto
    {
        DeviceUri = CameraUri,
        DeviceType = DeviceType.Camera,
        DisplayName = "Fake camera",
        Connected = true,
        ReadUtc = DateTimeOffset.UtcNow,
        Camera = new CameraDeviceStateDto
        {
            CcdTemperatureC = ccd,
            HeatsinkTemperatureC = 18.44,
            SetpointC = -10,
            CoolerPowerPercent = power,
            CoolerOn = true,
            State = CameraState.Idle,
            UsesGainValue = true,
            UsesGainMode = true,
            GainMin = 0,
            GainMax = 500,
            Gain = gain,
            GainModes = ["LCG", lastMode],
            SensorWidth = 4144,
            SensorHeight = 2822,
            VideoBitDepths = [BitDepth.Int8, BitDepth.Int16],
            ReadoutModes = ["Normal", "Fast"],
            ReadoutMode = "Normal",
        },
    };

    private static DeviceStateDto Wheel(string lastSlot = "B", int lastOffset = 12) => new DeviceStateDto
    {
        DeviceUri = WheelUri,
        DeviceType = DeviceType.FilterWheel,
        Connected = true,
        ReadUtc = DateTimeOffset.UtcNow,
        FilterWheel = new FilterWheelDeviceStateDto
        {
            Position = 1,
            FilterName = "G",
            Slots = ["R", "G", lastSlot],
            Offsets = [0, -5, lastOffset],
        },
    };

    private static DeviceStateDto Focuser(double temperature = 11.52) => new DeviceStateDto
    {
        DeviceUri = FocuserUri,
        DeviceType = DeviceType.Focuser,
        Connected = true,
        ReadUtc = DateTimeOffset.UtcNow,
        Focuser = new FocuserDeviceStateDto { Position = 12_000, TemperatureC = temperature, IsMoving = false },
    };

    private static ImmutableDictionary<string, DeviceStateDto> Reading(params DeviceStateDto[] devices)
    {
        var keyed = ImmutableDictionary.CreateBuilder<string, DeviceStateDto>();
        foreach (var device in devices)
        {
            keyed[device.DeviceUri] = device;
        }

        return keyed.ToImmutable();
    }

    // THE guard: a camera's gain modes and a wheel's slots are arrays, which a record compares by reference. Compared
    // through the record's own equality, two reads of an unchanged rig were never the same, and the rig redrew for each.
    [Fact]
    public void TwoReadsOfAnUnchangedRigAreTheSame()
        => NodeConnection.SameReadings(Reading(Camera(), Wheel(), Focuser()), Reading(Camera(), Wheel(), Focuser())).ShouldBeTrue();

    // A thermometer's last digits and a cooler's fraction of a percent move on every read and are shown nowhere.
    [Fact]
    public void AReadingThatMovedBelowWhatIsShownIsTheSame()
        => NodeConnection.SameReadings(
            Reading(Camera(ccd: -10.02, power: 41.3), Focuser(11.52)),
            Reading(Camera(ccd: -10.04, power: 41.4), Focuser(11.54))).ShouldBeTrue();

    [Fact]
    public void ATenthOfADegreeIsAChange()
    {
        NodeConnection.SameReadings(Reading(Camera(ccd: -10.02)), Reading(Camera(ccd: -10.22))).ShouldBeFalse();
        NodeConnection.SameReadings(Reading(Focuser(11.52)), Reading(Focuser(11.72))).ShouldBeFalse();
    }

    [Fact]
    public void AChangedSettingIsAChange()
        => NodeConnection.SameReadings(Reading(Camera(gain: 100)), Reading(Camera(gain: 120))).ShouldBeFalse();

    // An array is compared by what it holds.
    [Fact]
    public void AChangedArrayElementIsAChange()
    {
        NodeConnection.SameReadings(Reading(Camera(lastMode: "HCG")), Reading(Camera(lastMode: "HDR"))).ShouldBeFalse();
        NodeConnection.SameReadings(Reading(Wheel(lastSlot: "B")), Reading(Wheel(lastSlot: "L"))).ShouldBeFalse();
        NodeConnection.SameReadings(Reading(Wheel(lastOffset: 12)), Reading(Wheel(lastOffset: 14))).ShouldBeFalse();
    }

    [Fact]
    public void ADeviceThatCameOrWentIsAChange()
    {
        NodeConnection.SameReadings(Reading(Camera()), Reading(Camera(), Wheel())).ShouldBeFalse();
        NodeConnection.SameReadings(Reading(Camera(), Wheel()), Reading(Camera())).ShouldBeFalse();
        NodeConnection.SameReadings(Reading(Camera()), Reading(Wheel())).ShouldBeFalse();
    }
}
