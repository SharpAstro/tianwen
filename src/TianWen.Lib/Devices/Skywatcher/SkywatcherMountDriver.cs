using System;

namespace TianWen.Lib.Devices.Skywatcher;

internal class SkywatcherMountDriver(SkywatcherDevice device, IServiceProvider serviceProvider) : SkywatcherMountDriverBase<SkywatcherDevice>(device, serviceProvider)
{

}
