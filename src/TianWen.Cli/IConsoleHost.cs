using Console.Lib;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;

namespace TianWen.Cli;

public interface IConsoleHost
{
    IVirtualTerminal Terminal { get; }

    Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase;

    Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken);

    IDeviceHub DeviceHub { get; }

    IHostApplicationLifetime ApplicationLifetime { get; }

    IExternal External { get; }

    ITimeProvider TimeProvider { get; }

    void WriteScrollable(string content, bool newLine = true);

    string? ReadLine();

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
