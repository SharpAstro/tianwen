using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Meade;

/// <summary>
/// Serial probe for Meade LX200 / Autostar / Audiostar mounts. Uses
/// <see cref="MeadeDeviceSource.TryGetMountInfo"/> which also writes a UUID into an
/// unused site-name slot so the deviceId stays stable across USB re-enumeration.
/// Lives in the 9600-baud group and shares the open handle with other LX200-style
/// probes (OnStep, QHYCFW3, QFOC) — no redundant opens.
/// </summary>
internal sealed class MeadeSerialProbe : ISerialProbe
{
    public string Name => "Meade";
    public int BaudRate => 9600;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(500);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(MeadeDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var (productName, productNumber, siteNames, uuid) = await MeadeDeviceSource.TryGetMountInfo(conn, cancellationToken);

        if (productName is null || productNumber is null || !MeadeDeviceSource.SupportedProductsRegex.IsMatch(productName))
        {
            return null;
        }

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        string deviceId;
        if (uuid is not null)
        {
            deviceId = string.Join('_',
                MeadeDeviceSource.SafeName(productName),
                MeadeDeviceSource.SafeName(productNumber),
                uuid);
        }
        else
        {
            deviceId = string.Join('_',
                MeadeDeviceSource.SafeName(productName),
                MeadeDeviceSource.SafeName(productNumber),
                MeadeDeviceSource.SafeName(string.Join(',', siteNames)),
                MeadeDeviceSource.SafeName(portWithoutPrefix));
        }

        var displayName = $"{productName} ({productNumber}) on {portWithoutPrefix}";
        var device = new MeadeDevice(DeviceType.Mount, deviceId, displayName, port);
        return new SerialProbeMatch(port, device.DeviceUri);
    }
}
