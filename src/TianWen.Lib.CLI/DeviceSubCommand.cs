using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class DeviceSubCommand(IConsoleHost consoleHost)
{
    private bool _discoveryRan;

    public Command Build()
    {
        var discoverCommand = new Command("discover", "Discover all connected devices");
        discoverCommand.SetAction(DiscoverDevicesAsync);

        var listCommand = new Command("list", "List all connected devices");
        listCommand.SetAction(ListDevicesAsync);

        return new Command("device", "Manage connected devices")
        {
            Subcommands = {
                discoverCommand,
                listCommand
            }
        };
    }

    internal async Task DiscoverDevicesAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        foreach (var device in await DoListDevicesExceptProfiles(true, cancellationToken))
        {
            Console.WriteLine();
            Console.WriteLine(device.ToString());
        }
    }

    internal async Task ListDevicesAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        foreach (var device in await DoListDevicesExceptProfiles(false, cancellationToken))
        {
            Console.WriteLine();
            Console.WriteLine(device.ToString());
        }
    }

    private async Task<IEnumerable<DeviceBase>> DoListDevicesExceptProfiles(bool forceDiscovery, CancellationToken cancellationToken)
    {
        var result = (await consoleHost.ListAllDevicesAsync(forceDiscovery || !_discoveryRan, cancellationToken))
            .Where(d => d.DeviceType is not DeviceType.Profile);

        _discoveryRan = true;

        return result;
    }
}
