using System;
using TianWen.Lib.Devices.OnStep;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Fake OnStep mount driver — subclasses the generic OnStep driver with
/// <see cref="FakeDevice"/> as the device type, mirroring how
/// <see cref="FakeMeadeLX200ProtocolMountDriver"/> uses the LX200 base directly.
/// All OnStep-specific overrides (<c>:GU#</c>, <c>:Gm#</c>, park polling, etc.)
/// inherit unchanged. Fake protocol behaviour comes from
/// <see cref="FakeOnStepSerialDevice"/>, instantiated by
/// <see cref="FakeDevice.ConnectSerialDevice"/> when <c>port=OnStep</c>.
/// </summary>
internal class FakeOnStepMountDriver(FakeDevice device, IServiceProvider serviceProvider) : OnStepMountDriver<FakeDevice>(device, serviceProvider)
{
}
