using System;

namespace TianWen.Lib.Devices.IOptron;

internal class SgpMountDriver(IOptronDevice device, IServiceProvider serviceProvider) : SgpMountDriverBase<IOptronDevice>(device, serviceProvider)
{

}
