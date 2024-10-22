using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

public interface IDeviceManager<TDevice> : IEnumerable<TDevice>
    where TDevice : DeviceBase
{
    void Refresh();

    bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device);

    IReadOnlyList<TDevice> FindAllByType(DeviceType type);
}
