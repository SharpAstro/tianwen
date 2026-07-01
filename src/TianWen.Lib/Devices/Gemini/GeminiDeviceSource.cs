using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Discovers Gemini FlatPanel Lite cover/calibrators. Serial probing lives in
/// <see cref="GeminiFlatPanelSerialProbe"/> and runs inside <see cref="ISerialProbeService"/>; this source
/// just reads the matches the service has already published (each match's URI already has the deviceId +
/// port baked in by the probe).
/// </summary>
internal sealed class GeminiDeviceSource(ISerialProbeService probeService) : IDeviceSource<GeminiDevice>
{
    private Dictionary<DeviceType, IReadOnlyList<GeminiDevice>> _cachedDevices = new();

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.CoverCalibrator];

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public IEnumerable<GeminiDevice> RegisteredDevices(DeviceType deviceType) =>
        _cachedDevices.TryGetValue(deviceType, out var devices) ? devices : [];

    public ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        var panels = new List<GeminiDevice>();
        foreach (var match in probeService.ResultsFor("GeminiFlatPanel"))
        {
            panels.Add(new GeminiDevice(match.DeviceUri));
        }

        Interlocked.Exchange(ref _cachedDevices, new Dictionary<DeviceType, IReadOnlyList<GeminiDevice>>
        {
            [DeviceType.CoverCalibrator] = panels
        });

        return ValueTask.CompletedTask;
    }
}
