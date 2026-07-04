using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Gemini FlatPanel Lite cover/calibrator addressed by URI. The <c>port</c> query parameter carries the
/// serial port (e.g. <c>serial:COM4</c>); baud is fixed at <see cref="GeminiFlatPanelProtocol.Baud"/>. A
/// driver-controlled light panel with no cover flap — see <c>docs/architecture/gemini-flatpanel-lite-protocol.md</c>.
/// </summary>
public record class GeminiDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public GeminiDevice(string deviceId, string displayName, string port)
        : this(new Uri($"{DeviceType.CoverCalibrator}://{typeof(GeminiDevice).Name}/{deviceId}?{new NameValueCollection { [DeviceQueryKey.Port.Key] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.CoverCalibrator => new GeminiFlatPanelDriver(this, sp),
        _ => null
    };

    public override async ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = GeminiFlatPanelProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);
        if (port is null)
        {
            return null;
        }

        // The Gemini controller needs DTR + RTS asserted on open (the CH341 USB bridge otherwise holds the
        // MCU in reset and it never answers >H#).
        var conn = await external.OpenSerialDeviceAsync(port, GeminiFlatPanelProtocol.Baud, encoding ?? Encoding.ASCII, assertControlLines: true, cancellationToken);
        if (conn is not null)
        {
            // The CH341 bridge spuriously aborts async BaseStream reads (ERROR_OPERATION_ABORTED) after the
            // first read, so use the cancellable synchronous read path (see ISerialConnection.SynchronousReads).
            conn.SynchronousReads = true;
        }
        return conn;
    }
}
