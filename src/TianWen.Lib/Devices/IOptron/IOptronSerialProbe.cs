using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.IOptron;

/// <summary>
/// Serial probe for iOptron SkyGuider Pro. Isolated in its own 28800-baud group (no
/// other TianWen-supported device uses 28800), so this probe doesn't share an open
/// handle with any LX200-style sibling: the port is opened just for this probe.
/// </summary>
internal sealed partial class IOptronSerialProbe : ISerialProbe
{
    public string Name => "iOptron";
    public int BaudRate => IOptronDevice.SGP_BAUD_RATE;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    // SGP responses end in '#'. (Alone in the 28800-baud group, but the declaration
    // keeps the probe consistent with the rest of the registry.)
    public ProbeFraming Framing => ProbeFraming.HashTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(500);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(IOptronDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken);

        // Through SgpWire, never a single read: a mount a previous session left out of step answers the first query with
        // a reply it was holding, and the probe used to read that as "not an SGP" (bench, 2026-11-05).
        var (response, _) = await SgpWire.ExchangeAsync(conn, ":MRSVE#", repeatable: true, sendTwice: false, logger: null, cancellationToken);
        if (response is null || SgpFirmwareRegex().Match(response) is not { Success: true } fwMatch)
        {
            return null;
        }

        var firmwareVersion = fwMatch.Groups[1].Value;

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        // SGP has no serial number and nowhere to write a UUID, so its id ends in the port, which discovery swaps for the
        // USB bridge's serial or socket where it has one (#1090, SerialProbeService.WithPortIdentity). The firmware is
        // evidence, never identity (#2040): it rides in the query, so an update does not make the mount a new device.
        var deviceId = string.Join('_',
            "SkyGuider-Pro",
            SafeName(portWithoutPrefix));
        var displayName = $"iOptron SkyGuider Pro ({firmwareVersion}) on {portWithoutPrefix}";

        var device = new IOptronDevice(DeviceType.Mount, deviceId, displayName, port);
        return new SerialProbeMatch(port, device.DeviceUri.WithQueryValues((DeviceQueryKey.Firmware.Key, firmwareVersion)));
    }

    private static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    /// <summary>Matches SGP firmware response: :RMRVE12xxxxxx where xxxxxx is the firmware version date.</summary>
    [GeneratedRegex(@"^:RMRVE12(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex SgpFirmwareRegex();
}
