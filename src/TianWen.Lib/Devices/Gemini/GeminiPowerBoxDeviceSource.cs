using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Discovers Gemini Power&amp;Data Hub Advanced 3 power boxes from the matches <see cref="GeminiPowerBoxSerialProbe"/>
/// published through <see cref="ISerialProbeService"/>.
/// </summary>
internal sealed class GeminiPowerBoxDeviceSource(ISerialProbeService probeService) : IDeviceSource<GeminiPowerBoxDevice>
{
    private Dictionary<DeviceType, IReadOnlyList<GeminiPowerBoxDevice>> _cachedDevices = new();

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Switch];

    // Reads serial-probe matches from ISerialProbeService, so must run after the serial probe pass.
    public bool ConsumesSerialProbe => true;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public IEnumerable<GeminiPowerBoxDevice> RegisteredDevices(DeviceType deviceType) =>
        _cachedDevices.TryGetValue(deviceType, out var devices) ? devices : [];

    public ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        var boxes = new List<GeminiPowerBoxDevice>();
        foreach (var match in probeService.ResultsFor("GeminiPowerBox"))
        {
            boxes.Add(new GeminiPowerBoxDevice(match.DeviceUri));
        }

        Interlocked.Exchange(ref _cachedDevices, new Dictionary<DeviceType, IReadOnlyList<GeminiPowerBoxDevice>>
        {
            [DeviceType.Switch] = boxes
        });

        return ValueTask.CompletedTask;
    }
}
