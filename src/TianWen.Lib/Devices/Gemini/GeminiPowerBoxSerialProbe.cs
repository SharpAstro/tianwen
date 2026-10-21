using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Serial probe for the Gemini Power&amp;Data Hub Advanced 3: sends <c>&gt;H#</c> and matches the model name it answers,
/// <c>*HGeminiPowerBoxPlusAdv3#</c>, reading past any status frame the box pushes (it does, every 3 s, once anything has
/// asked it for status). The only 19200-baud probe; <c>#</c>-terminated.
/// </summary>
internal sealed class GeminiPowerBoxSerialProbe : ISerialProbe
{
    public string Name => "GeminiPowerBox";
    public int BaudRate => GeminiPowerBoxProtocol.Baud;
    public Encoding Encoding => Encoding.ASCII;
    public ProbeFraming Framing => ProbeFraming.HashTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(1000);
    public int MaxAttempts => 2;

    // The vendor driver waits 2 s after opening before its first command. The bench box answered sooner and sent nothing
    // at the open, but a slower board's start is not measured, so the vendor's wait stands.
    public TimeSpan Warmup => TimeSpan.FromMilliseconds(2000);

    // The vendor driver opens with DTR and RTS on.
    public bool AssertControlLines => true;

    private static readonly IReadOnlyCollection<string> _hosts = [nameof(GeminiPowerBoxDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        if (await GeminiPowerBoxProtocol.IdentifyAsync(conn, cancellationToken) != GeminiPowerBoxProtocol.Identity)
        {
            return null;
        }

        var firmware = await GeminiPowerBoxProtocol.GetFirmwareAsync(conn, cancellationToken);
        var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(port);
        var device = new GeminiPowerBoxDevice($"GeminiPowerBox_{portWithoutPrefix}", $"Gemini Power&Data Hub Advanced 3 on {portWithoutPrefix}", port);
        return new SerialProbeMatch(port, device.DeviceUri, firmware is { } fw ? new Dictionary<string, string> { ["firmware"] = fw.ToString(System.Globalization.CultureInfo.InvariantCulture) } : null);
    }
}
