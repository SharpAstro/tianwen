using Console.Lib;
using Microsoft.Extensions.Hosting;
using Pastel;
using System.Collections.Concurrent;
using System.Diagnostics;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;

namespace TianWen.Lib.CLI;

internal class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    ICombinedDeviceManager deviceManager,
    IDeviceUriRegistry deviceUriRegistry,
    IVirtualTerminal terminal
) : IConsoleHost
{
    private readonly ConcurrentDictionary<DeviceType, bool> _discoveryRanForDevice = [];

    public IVirtualTerminal Terminal { get; } = terminal;

    public IDeviceUriRegistry DeviceUriRegistry { get; } = deviceUriRegistry;

    public IHostApplicationLifetime ApplicationLifetime { get; } = applicationLifetime;

    public IExternal External { get; } = external;

    public void WriteScrollable(string content, bool newLine = true)
    {
        if (newLine)
        {
            System.Console.WriteLine(content);
        }
        else
        {
            System.Console.Write(content);
        }
    }

    public string? ReadLine() => System.Console.ReadLine();

    public void WriteError(string error)
    {
        System.Console.Error.WriteLine(error);
    }

    public void WriteError(Exception exception)
    {
        System.Console.Error.WriteLine(exception.Message.Pastel(ConsoleColor.Red));
    }

    public async Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken)
    {
        var discoveryTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(25);
        using var cts = new CancellationTokenSource(discoveryTimeout, External.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        await deviceManager.CheckSupportAsync(linked.Token);

        if (options.HasFlag(DeviceDiscoveryOption.Force) || deviceManager.RegisteredDeviceTypes.Any(t => !_discoveryRanForDevice.TryGetValue(t, out var ran) || !ran))
        {
            await deviceManager.DiscoverAsync(linked.Token);

            foreach (var type in deviceManager.RegisteredDeviceTypes)
            {
                _discoveryRanForDevice[type] = true;
            }
        }

        var includeFake = options.HasFlag(DeviceDiscoveryOption.IncludeFake);

        return [.. deviceManager
            .RegisteredDeviceTypes
            .SelectMany(deviceManager.RegisteredDevices)
            .Where(d => includeFake || d is not FakeDevice)
            .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)
        ];
    }

    public async Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase
    {
        var discoveryTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(25);

        using var cts = new CancellationTokenSource(discoveryTimeout, External.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        if (await deviceManager.CheckSupportAsync(linked.Token) && (
            options.HasFlag(DeviceDiscoveryOption.Force) || !_discoveryRanForDevice.TryGetValue(deviceType, out var ran) || !ran)
        )
        {
            await deviceManager.DiscoverOnlyDeviceType(deviceType, linked.Token);
        }

        var includeFake = options.HasFlag(DeviceDiscoveryOption.IncludeFake);

        return [.. deviceManager
            .RegisteredDevices(deviceType)
            .OfType<TDevice>()
            .Where(d => includeFake || d is not FakeDevice)
            .OrderBy(d => d.DisplayName)
        ];
    }
}
