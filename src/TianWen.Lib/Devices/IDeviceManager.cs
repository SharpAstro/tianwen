using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

public interface IDeviceManager<TDevice> : IDeviceSource<TDevice>
    where TDevice : DeviceBase
{
    bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device);
}
