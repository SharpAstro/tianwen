using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.IOptron;

/// <summary>
/// Materialises <see cref="IOptronDevice"/> instances from the serial matches published
/// by <see cref="IOptronSerialProbe"/> via <see cref="ISerialProbeService"/>.
/// </summary>
internal class IOptronDeviceSource(ISerialProbeService probeService) : IDeviceSource<IOptronDevice>
{
    private Dictionary<DeviceType, List<IOptronDevice>>? _cachedDevices;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<IOptronDevice>>();
        var matches = probeService.ResultsFor("iOptron");
        if (matches.Count > 0)
        {
            var list = devices[DeviceType.Mount] = [];
            foreach (var match in matches)
            {
                list.Add(new IOptronDevice(match.DeviceUri));
            }
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
        return ValueTask.CompletedTask;
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public IEnumerable<IOptronDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_cachedDevices is not null && _cachedDevices.TryGetValue(deviceType, out var devices))
        {
            return devices;
        }
        return [];
    }
}
