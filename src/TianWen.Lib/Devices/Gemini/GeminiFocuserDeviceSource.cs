using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Discovers Gemini Focuser Pro focusers. Serial probing lives in <see cref="GeminiFocuserSerialProbe"/> and
/// runs inside <see cref="ISerialProbeService"/>; this source just reads the matches the service has already
/// published (each match's URI already has the deviceId + port baked in by the probe).
/// </summary>
internal sealed class GeminiFocuserDeviceSource(ISerialProbeService probeService) : IDeviceSource<GeminiFocuserDevice>
{
    private Dictionary<DeviceType, IReadOnlyList<GeminiFocuserDevice>> _cachedDevices = new();

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Focuser];

    // Reads serial-probe matches from ISerialProbeService, so must run after the serial probe pass.
    public bool ConsumesSerialProbe => true;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public IEnumerable<GeminiFocuserDevice> RegisteredDevices(DeviceType deviceType) =>
        _cachedDevices.TryGetValue(deviceType, out var devices) ? devices : [];

    public ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        var focusers = new List<GeminiFocuserDevice>();
        foreach (var match in probeService.ResultsFor("GeminiFocuser"))
        {
            focusers.Add(new GeminiFocuserDevice(match.DeviceUri));
        }

        Interlocked.Exchange(ref _cachedDevices, new Dictionary<DeviceType, IReadOnlyList<GeminiFocuserDevice>>
        {
            [DeviceType.Focuser] = focusers
        });

        return ValueTask.CompletedTask;
    }
}
