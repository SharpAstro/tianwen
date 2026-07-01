using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Serial probe for the Gemini FlatPanel Lite. Sends <c>&gt;H#</c> and matches the
/// <c>&gt;HGeminiFlatPanelLite#</c> identity reply. Shares the 9600-baud probe group (the LX200 family) so
/// the port handle is opened once and reused; framing is <c>#</c>-terminated.
/// </summary>
internal sealed class GeminiFlatPanelSerialProbe : ISerialProbe
{
    public string Name => "GeminiFlatPanel";
    public int BaudRate => GeminiFlatPanelProtocol.Baud;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeFraming Framing => ProbeFraming.HashTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(500);
    public int MaxAttempts => 1;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(GeminiDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var identity = await GeminiFlatPanelProtocol.IdentifyAsync(conn, cancellationToken);
        if (identity != GeminiFlatPanelProtocol.Identity)
        {
            return null;
        }

        var firmware = await GeminiFlatPanelProtocol.GetFirmwareVersionAsync(conn, cancellationToken);

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        var deviceId = $"Gemini_{portWithoutPrefix}";
        var fwLabel = firmware is { } fw ? $" (FW {fw})" : "";
        var displayName = $"Gemini FlatPanel Lite{fwLabel} on {portWithoutPrefix}";
        var query = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(port)}";
        var uri = new Uri($"{DeviceType.CoverCalibrator}://{typeof(GeminiDevice).Name}/{deviceId}?{query}#{displayName}");

        var metadata = firmware is { } fwv
            ? new Dictionary<string, string> { ["firmware"] = fwv.ToString() }
            : null;

        return new SerialProbeMatch(port, uri, metadata);
    }
}
