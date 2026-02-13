using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class DeviceSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var discoverCommand = new Command("discover", "Discover all available devices");
        discoverCommand.SetAction(DiscoverDevicesActionAsync);

        var listCommand = new Command("list", "List all available devices");
        listCommand.SetAction(ListDevicesActionAsync);

        return new Command("device", "Manage connected devices")
        {
            Subcommands = {
                discoverCommand,
                listCommand
            }
        };
    }

    internal async Task DiscoverDevicesActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        foreach (var device in await DoListDevicesExceptProfiles(true, cancellationToken))
        {
            consoleHost.WriteScrollable($"\n{device}");
        }
    }

    internal async Task ListDevicesActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        foreach (var device in await DoListDevicesExceptProfiles(false, cancellationToken))
        {
            consoleHost.WriteScrollable($"\n{device}");
        }
    }

    private async Task<IEnumerable<DeviceBase>> DoListDevicesExceptProfiles(bool forceDiscovery, CancellationToken cancellationToken)
    {
        var options = DeviceDiscoveryOption.None
            | (forceDiscovery ? DeviceDiscoveryOption.Force : DeviceDiscoveryOption.None);

        var result = (await consoleHost.ListAllDevicesAsync(options, cancellationToken))
            .Where(d => d.DeviceType is not DeviceType.Profile);

        return result;
    }
}
