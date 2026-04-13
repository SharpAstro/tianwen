using System;
using TianWen.Lib.Devices.IOptron;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSgpMountDriver(FakeDevice device, IServiceProvider serviceProvider) : SgpMountDriverBase<FakeDevice>(device, serviceProvider)
{

}
