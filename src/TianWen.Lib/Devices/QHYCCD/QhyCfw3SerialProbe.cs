using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// Serial probe for standalone QHYCFW3 filter wheels (the non-camera-attached variant).
/// Shares the 9600-baud group with OnStep / Meade / QFOC so the port handle is reused.
/// </summary>
internal sealed class QhyCfw3SerialProbe : ISerialProbe
{
    public string Name => "QHYCFW3";
    public int BaudRate => QHYSerialControlledFilterWheelDriver.CFW_BAUD;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(500);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(QHYDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var fwVersion = await QHYSerialControlledFilterWheelDriver.ProbeAsync(conn, cancellationToken);
        if (fwVersion is null)
        {
            return null;
        }

        var slotCount = await QHYSerialControlledFilterWheelDriver.QuerySlotCountAsync(conn, cancellationToken);
        if (slotCount <= 0)
        {
            return null;
        }

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        var deviceId = $"QHYCFW3_{portWithoutPrefix}";
        var displayName = $"QHYCFW3 {slotCount}-Slot (FW {fwVersion}) on {portWithoutPrefix}";

        var filterParams = QHYDeviceSource.SeedFilterParams(slotCount);
        var portParam = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(port)}";
        var query = filterParams is { Length: > 0 } ? $"{portParam}&{filterParams}" : portParam;
        var uri = new Uri($"{DeviceType.FilterWheel}://{typeof(QHYDevice).Name}/{deviceId}?{query}#{displayName}");

        return new SerialProbeMatch(port, uri);
    }
}
