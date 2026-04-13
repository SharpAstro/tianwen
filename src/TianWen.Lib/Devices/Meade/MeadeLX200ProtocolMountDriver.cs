using System;

namespace TianWen.Lib.Devices.Meade;

internal class MeadeLX200ProtocolMountDriver(MeadeDevice device, IServiceProvider serviceProvider) : MeadeLX200ProtocolMountDriverBase<MeadeDevice>(device, serviceProvider)
{

}
