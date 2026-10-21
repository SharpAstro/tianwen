using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Gemini Power&amp;Data Hub Advanced 3, a power box and USB hub addressed by URI; the <c>port</c> query parameter carries the
/// serial port (<c>serial:COM5</c>), baud fixed at <see cref="GeminiPowerBoxProtocol.Baud"/>. A native (ASCOM-free) switch
/// driver: see <c>docs/architecture/gemini-powerbox-adv3-protocol.md</c>. The box reports no serial number (its CH340 has
/// none, and <c>&gt;H#</c> names only the model), so it is known by its port, as the Gemini Focuser Pro is.
/// </summary>
public record class GeminiPowerBoxDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public GeminiPowerBoxDevice(string deviceId, string displayName, string port)
        : this(new Uri($"{DeviceType.Switch}://{typeof(GeminiPowerBoxDevice).Name}/{deviceId}?{new NameValueCollection { [DeviceQueryKey.Port.Key] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Switch => new GeminiPowerBoxDriver(this, sp),
        _ => null
    };

    public override async ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = GeminiPowerBoxProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
        => Query.QueryValue(DeviceQueryKey.Port) is { } port
            // DTR and RTS asserted, as the vendor driver opens the port; the bench box answered with them on and sent no
            // boot chatter after the open (2026-10-21).
            ? await external.OpenSerialDeviceAsync(port, GeminiPowerBoxProtocol.Baud, encoding ?? Encoding.ASCII, assertControlLines: true, cancellationToken)
            : null;
}
