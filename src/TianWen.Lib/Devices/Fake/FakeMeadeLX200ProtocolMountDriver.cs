using System;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200ProtocolMountDriver(FakeDevice device, IServiceProvider serviceProvider) : MeadeLX200ProtocolMountDriverBase<FakeDevice>(device, serviceProvider)
{

}
