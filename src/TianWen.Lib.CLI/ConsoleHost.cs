using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    ICombinedDeviceManager deviceManager
) : IConsoleHost
{
    public async Task<IReadOnlyCollection<Profile>> ListProfilesAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10), external.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, applicationLifetime.ApplicationStopping);
        await deviceManager.DiscoverAsync(linked.Token);

        var list = new List<Profile>();
        foreach (var deviceType in deviceManager.RegisteredDeviceTypes.Where(t => t is DeviceType.Profile))
        {
            list.AddRange(deviceManager.RegisteredDevices(deviceType).OfType<Profile>());
        }

        return list;
    }
}