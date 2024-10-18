using System;

namespace Astap.Lib.Devices.Fake;

public record FakeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    /// <summary>
    /// Represents a fake device (for testing and simulation).
    /// </summary>
    /// <param name="deviceType"></param>
    /// <param name="deviceId">Fake device id (starting from 1)</param>
    public FakeDevice(DeviceType deviceType, int deviceId)
        : this(new Uri($"{deviceType}://{typeof(FakeDevice).Name}/{deviceId}#Fake {deviceType.PascalCaseStringToName()} {deviceId}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new FakeCameraDriver(this, external),
        DeviceType.FilterWheel => new FakeFilterWheelDriver(this, external),
        DeviceType.Focuser => new FakeFocuserDriver(this, external),
        DeviceType.DedicatedGuiderSoftware => new FakeGuider(this, external),
        _ => null
    };
}
