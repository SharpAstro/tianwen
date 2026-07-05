using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Serial probe for the Gemini Focuser Pro. Sends <c>:02#</c> and matches the myFocuserPro2
/// controller-present reply (<c>OK</c>). The Gemini Focuser Pro carries no distinctive identity on the wire
/// (it is a rebadged myFocuserPro2, and the vendor ASCOM driver validates nothing beyond this handshake), so
/// the match is deliberately the generic myFocuserPro2 handshake and any responder is surfaced as a Gemini
/// Focuser Pro — the reported <c>:04#</c> firmware name is captured into metadata so the actual flashed name
/// is visible once real hardware connects. Shares the 9600-baud probe group (the LX200 family); framing is
/// <c>#</c>-terminated.
/// </summary>
internal sealed class GeminiFocuserSerialProbe : ISerialProbe
{
    public string Name => "GeminiFocuser";
    public int BaudRate => GeminiFocuserProtocol.Baud;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeFraming Framing => ProbeFraming.HashTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(500);
    public int MaxAttempts => 1;

    // The myFocuserPro2 Arduino resets when the port opens (DTR auto-reset) and ignores input until it has
    // booted (~2s). Discovery reopens the port per probe, so the boot restarts every probe — warm up here.
    public TimeSpan Warmup => TimeSpan.FromMilliseconds(2200);

    // The Arduino holds in reset until DTR is asserted; honoured only on the isolated per-probe pass
    // (see ISerialProbe.AssertControlLines) so we don't reset a different DTR-triggered controller on pass 1.
    public bool AssertControlLines => true;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(GeminiFocuserDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        var identity = await GeminiFocuserProtocol.IdentifyAsync(conn, cancellationToken);
        if (identity != GeminiFocuserProtocol.PresentReply)
        {
            return null;
        }

        var firmware = await GeminiFocuserProtocol.GetFirmwareAsync(conn, cancellationToken);

        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        var deviceId = $"GeminiFocuser_{portWithoutPrefix}";
        var displayName = $"Gemini Focuser Pro on {portWithoutPrefix}";
        var query = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(port)}";
        var uri = new Uri($"{DeviceType.Focuser}://{typeof(GeminiFocuserDevice).Name}/{deviceId}?{query}#{displayName}");

        Dictionary<string, string>? metadata = null;
        if (firmware is { } fw)
        {
            metadata = new Dictionary<string, string> { ["firmwareName"] = fw.Name };
            if (fw.Version is { } v)
            {
                metadata["firmware"] = v.ToString();
            }
        }

        return new SerialProbeMatch(port, uri, metadata);
    }
}
