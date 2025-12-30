using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    ICombinedDeviceManager deviceManager,
    IDeviceUriRegistry deviceUriRegistry
) : IConsoleHost
{
    public IDeviceUriRegistry DeviceUriRegistry => deviceUriRegistry;

    public async Task<IReadOnlyCollection<Profile>> ListProfilesAsync()
    {
        TimeSpan discoveryTimeout;
#if DEBUG
        discoveryTimeout = TimeSpan.FromHours(1);
#else
        discoveryTimeout = TimeSpan.FromSeconds(25);
#endif

        using var cts = new CancellationTokenSource(discoveryTimeout, external.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, applicationLifetime.ApplicationStopping);

        if (await deviceManager.CheckSupportAsync(linked.Token))
        {
            await deviceManager.DiscoverAsync(linked.Token);
        }

        var list = new List<Profile>();
        foreach (var deviceType in deviceManager.RegisteredDeviceTypes.Where(t => t is DeviceType.Profile))
        {
            list.AddRange(deviceManager.RegisteredDevices(deviceType).OfType<Profile>());
        }

        return list;
    }
}