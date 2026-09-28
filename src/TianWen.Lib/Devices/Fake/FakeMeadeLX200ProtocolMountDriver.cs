using System;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200ProtocolMountDriver(FakeDevice device, IServiceProvider serviceProvider) : MeadeLX200ProtocolMountDriverBase<FakeDevice>(device, serviceProvider)
{
    /// <summary>The fake serial device while connected, for a test to read what went out on the wire.</summary>
    internal FakeMeadeLX200SerialDevice? SerialDevice => _deviceInfo.SerialDevice as FakeMeadeLX200SerialDevice;
}
