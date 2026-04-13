using System;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSkywatcherMountDriver(FakeDevice device, IServiceProvider serviceProvider) : SkywatcherMountDriverBase<FakeDevice>(device, serviceProvider)
{

}
