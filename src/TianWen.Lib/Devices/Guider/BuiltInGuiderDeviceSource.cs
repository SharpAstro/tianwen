using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Device source for the built-in guider. Always available — no external software needed.
/// Returns a single <see cref="BuiltInGuiderDevice"/> instance on discovery.
/// </summary>
internal sealed class BuiltInGuiderDeviceSource : IDeviceSource<BuiltInGuiderDevice>
{
    private readonly BuiltInGuiderDevice _device = new BuiltInGuiderDevice();

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = [DeviceType.Guider];

    public IEnumerable<BuiltInGuiderDevice> RegisteredDevices(DeviceType deviceType)
        => deviceType is DeviceType.Guider ? [_device] : [];
}
