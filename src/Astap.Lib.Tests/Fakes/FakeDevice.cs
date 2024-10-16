using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Tests.Fakes;

public record FakeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    protected override object? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        // DeviceType.Camera => new FakeCameraDriver(this),
        DeviceType.FilterWheel => new FakeFilterWheelDriver(this, external),
        DeviceType.Focuser => new FakeFocuserDriver(this, external),
        DeviceType.DedicatedGuiderSoftware => new FakeGuider(this, external),
        _ => null
    };
}
