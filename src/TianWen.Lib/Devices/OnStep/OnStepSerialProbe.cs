using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.OnStep;

/// <summary>
/// Serial probe for OnStep / OnStepX mounts. Reuses <see cref="OnStepDeviceSource.TryGetMountInfo"/>
/// so the UUID-in-unused-site-slot trick works identically for serial and TCP/WiFi paths —
/// the mount-resident UUID produces the same deviceId across transports, so the same profile
/// entry survives a USB ↔ WiFi switch.
/// <para>
/// Cold-start tolerance: OnStep ESP32 controllers can take ~500ms–1s to respond on first
/// connect after a cold boot. The INDI-documented first-query quirk (first LX200 "get
/// product" returns a bare "0" without the '#' terminator) is now absorbed by the Meade
/// probe running ahead of us in the framed-protocol sort order, and a late-arriving '0'
/// is caught by the post-probe drain in <c>SerialProbeService.RunSingleProbeAsync</c>.
/// With that cleaned up, 800ms is enough to catch a warm controller and a reasonably
/// fast cold boot; the pass-2 budget-multiplier ladder (2× = 1.6s) still covers the
/// rare stubborn cold start. <see cref="MaxAttempts"/> is 1 — per-port retries are
/// handled by the two-pass ladder, not in-probe.
/// </para>
/// </summary>
internal sealed class OnStepSerialProbe : ISerialProbe
{
    public string Name => "OnStep";
    public int BaudRate => 9600;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    // LX200 responses end in '#'.
    public ProbeFraming Framing => ProbeFraming.HashTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(800);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(OnStepDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var (productName, productNumber, siteNames, uuid) = await OnStepDeviceSource.TryGetMountInfo(conn, cancellationToken);

        if (productName is null || productNumber is null || !OnStepDeviceSource.SupportedProductsRegex.IsMatch(productName))
        {
            return null;
        }

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        string deviceId;
        if (uuid is not null)
        {
            deviceId = string.Join('_',
                OnStepDeviceSource.SafeName(productName),
                OnStepDeviceSource.SafeName(productNumber),
                uuid);
        }
        else
        {
            // No UUID (mount doesn't expose 4 site slots, or slot write failed) — fall back
            // to a port-qualified id so multiple same-model mounts stay distinguishable.
            // Note: this id is NOT transport-stable — a USB port reshuffle will change it.
            deviceId = string.Join('_',
                OnStepDeviceSource.SafeName(productName),
                OnStepDeviceSource.SafeName(productNumber),
                OnStepDeviceSource.SafeName(string.Join(',', siteNames)),
                OnStepDeviceSource.SafeName(portWithoutPrefix));
        }

        var displayName = $"{productName} ({productNumber}) on {portWithoutPrefix}";
        var device = new OnStepDevice(DeviceType.Mount, deviceId, displayName, port);
        return new SerialProbeMatch(port, device.DeviceUri);
    }
}
