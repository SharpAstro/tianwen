using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device source for the built-in Open-Meteo weather service. Always available — no hardware or API key needed.
/// Returns a single <see cref="OpenMeteoDevice"/> instance on discovery.
/// </summary>
internal sealed class OpenMeteoDeviceSource : IDeviceSource<OpenMeteoDevice>
{
    private readonly OpenMeteoDevice _device = new OpenMeteoDevice();

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = [DeviceType.Weather];

    public IEnumerable<OpenMeteoDevice> RegisteredDevices(DeviceType deviceType)
        => deviceType is DeviceType.Weather ? [_device] : [];
}
