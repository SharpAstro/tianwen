using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device source for the OpenWeatherMap weather service.
/// Discovery checks reachability via HTTP HEAD; connect re-validates with the actual API
/// (including API key and version auto-detection).
/// </summary>
internal sealed class OpenWeatherMapDeviceSource(ILogger<OpenWeatherMapDeviceSource> logger) : IDeviceSource<OpenWeatherMapDevice>
{
    private static readonly HttpClient s_httpClient = new HttpClient()
    {
        Timeout = System.TimeSpan.FromSeconds(5)
    };

    private readonly OpenWeatherMapDevice _device = new OpenWeatherMapDevice();
    private bool _reachable;

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://api.openweathermap.org");
            using var response = await s_httpClient.SendAsync(request, cancellationToken);
            _reachable = true;
            logger.LogDebug("OpenWeatherMap reachable: {Status}", (int)response.StatusCode);
        }
        catch (System.Exception ex)
        {
            _reachable = false;
            logger.LogDebug(ex, "OpenWeatherMap HEAD request failed — device will not appear in the picker");
        }
        return _reachable;
    }

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = [DeviceType.Weather];

    public IEnumerable<OpenWeatherMapDevice> RegisteredDevices(DeviceType deviceType)
        => deviceType is DeviceType.Weather && _reachable ? [_device] : [];
}
