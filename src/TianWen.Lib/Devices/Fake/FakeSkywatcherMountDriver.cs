using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSkywatcherMountDriver(FakeDevice device, IExternal external) : SkywatcherMountDriverBase<FakeDevice>(device, external)
{

}
