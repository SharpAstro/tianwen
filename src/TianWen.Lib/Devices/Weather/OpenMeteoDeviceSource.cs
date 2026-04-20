using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device source for the built-in Open-Meteo weather service.
/// Discovery checks reachability via HTTP HEAD; connect re-validates with a real API call.
/// No API key needed.
/// </summary>
internal sealed class OpenMeteoDeviceSource(ILogger<OpenMeteoDeviceSource> logger) : IDeviceSource<OpenMeteoDevice>
{
    private static readonly HttpClient s_httpClient = new HttpClient()
    {
        Timeout = System.TimeSpan.FromSeconds(5)
    };

    private readonly OpenMeteoDevice _device = new OpenMeteoDevice();
    private bool _reachable;

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://api.open-meteo.com");
            using var response = await s_httpClient.SendAsync(request, cancellationToken);
            _reachable = true;
            logger.LogDebug("Open-Meteo reachable: {Status}", (int)response.StatusCode);
        }
        catch (System.Exception ex)
        {
            _reachable = false;
            logger.LogDebug(ex, "Open-Meteo HEAD request failed — device will not appear in the picker");
        }
        return _reachable;
    }

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = [DeviceType.Weather];

    public IEnumerable<OpenMeteoDevice> RegisteredDevices(DeviceType deviceType)
        => deviceType is DeviceType.Weather && _reachable ? [_device] : [];
}
