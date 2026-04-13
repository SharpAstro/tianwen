using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Text;
using TianWen.Lib;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.IOptron;

public record IOptronDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    internal const int SGP_BAUD_RATE = 28800;

    public IOptronDevice(DeviceType deviceType, string deviceId, string displayName, string port)
        : this(new Uri($"{deviceType}://{typeof(IOptronDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Mount => new SgpMountDriver(this, sp),
        _ => null
    };

    public override ISerialConnection? ConnectSerialDevice(IExternal external, int baud = SGP_BAUD_RATE, Encoding? encoding = null, ILogger? logger = null, ITimeProvider? timeProvider = null)
    {
        // SGP requires exactly 28800 baud — refuse to connect at any other speed
        if (baud != SGP_BAUD_RATE)
        {
            return null;
        }

        return external.OpenSerialDevice(
            Query.QueryValue(DeviceQueryKey.Port) ?? throw new InvalidOperationException("No port specified"),
            baud,
            encoding ?? Encoding.ASCII
        );
    }
}
