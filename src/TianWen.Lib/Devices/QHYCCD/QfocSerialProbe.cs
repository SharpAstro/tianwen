using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// Serial probe for QFOC focusers (Standard + High Precision). Uses the JSON init
/// command — see <see cref="QHYFocuserDriver.ProbeAsync"/>. 1500ms budget covers
/// the JSON handshake which includes a 150ms drain and a 300ms response wait.
/// </summary>
internal sealed class QfocSerialProbe : ISerialProbe
{
    public string Name => "QFOC";
    public int BaudRate => QHYFocuserDriver.QFOC_BAUD;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(1500);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(QHYDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var probeResult = await QHYFocuserDriver.ProbeAsync(conn, cancellationToken);
        if (probeResult is not var (firmwareVersion, boardVersion))
        {
            return null;
        }

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        var deviceId = $"QFOC_{portWithoutPrefix}";
        var displayName = $"QFOC (FW {firmwareVersion}, Board {boardVersion}) on {portWithoutPrefix}";

        var portParam = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(port)}";
        var uri = new Uri($"{DeviceType.Focuser}://{typeof(QHYDevice).Name}/{deviceId}?{portParam}#{displayName}");

        return new SerialProbeMatch(port, uri);
    }
}
