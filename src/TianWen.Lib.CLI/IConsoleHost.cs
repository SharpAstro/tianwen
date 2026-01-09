using ImageMagick;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    Task<bool> HasSixelSupportAsync();

    Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, bool forceDiscovery, CancellationToken cancellationToken)
        where TDevice : DeviceBase;

    Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(bool forceDiscovery, CancellationToken cancellationToken);

    IDeviceUriRegistry DeviceUriRegistry { get; }

    IHostApplicationLifetime ApplicationLifetime { get; }

    IExternal External { get; }

    ValueTask RenderImageAsync(IMagickImage<float> image);
}
