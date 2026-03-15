using TianWen.Lib.Devices.IOptron;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSgpMountDriver(FakeDevice device, IExternal external) : SgpMountDriverBase<FakeDevice>(device, external)
{

}
