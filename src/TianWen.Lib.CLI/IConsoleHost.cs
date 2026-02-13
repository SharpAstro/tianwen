using ImageMagick;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    Task<bool> HasSixelSupportAsync();

    Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase;

    Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken);

    IDeviceUriRegistry DeviceUriRegistry { get; }

    IHostApplicationLifetime ApplicationLifetime { get; }

    IExternal External { get; }

    ValueTask RenderImageAsync(IMagickImage<float> image);

    void WriteScrollable(string content);

    void WriteError(string error);

    void WriteError(Exception exception);
}

[Flags]
public enum DeviceDiscoveryOption
{
    None = 0,
    Force       = 0b0001,
    IncludeFake = 0b0010 
}