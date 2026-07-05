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
/// Gemini Focuser Pro robotic focuser addressed by URI. The <c>port</c> query parameter carries the serial
/// port (e.g. <c>serial:COM5</c>); baud is fixed at <see cref="GeminiFocuserProtocol.Baud"/>. A native
/// (ASCOM-free) driver for the rebadged myFocuserPro2 Arduino controller — see
/// <c>docs/architecture/gemini-focuser-pro-protocol.md</c>. Distinct from the flat-panel
/// <see cref="GeminiDevice"/> (different product, protocol and <see cref="DeviceBase.DeviceClass"/>).
/// </summary>
public record class GeminiFocuserDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public GeminiFocuserDevice(string deviceId, string displayName, string port)
        : this(new Uri($"{DeviceType.Focuser}://{typeof(GeminiFocuserDevice).Name}/{deviceId}?{new NameValueCollection { [DeviceQueryKey.Port.Key] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Focuser => new GeminiFocuserDriver(this, sp),
        _ => null
    };

    public override async ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = GeminiFocuserProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);
        if (port is null)
        {
            return null;
        }

        // The myFocuserPro2 Arduino resets when DTR is asserted on open (classic auto-reset); the driver
        // then waits out the boot before handshaking. Asserting DTR+RTS matches the vendor driver's
        // ResetControllerOnConnect path and the FlatPanel's CH34x requirement.
        var conn = await external.OpenSerialDeviceAsync(port, GeminiFocuserProtocol.Baud, encoding ?? Encoding.ASCII, assertControlLines: true, cancellationToken);
        if (conn is not null)
        {
            // CH34x bridges spuriously abort async BaseStream reads (ERROR_OPERATION_ABORTED) after the first
            // read, so use the cancellable synchronous read path (see ISerialConnection.SynchronousReads).
            conn.SynchronousReads = true;
        }
        return conn;
    }
}
