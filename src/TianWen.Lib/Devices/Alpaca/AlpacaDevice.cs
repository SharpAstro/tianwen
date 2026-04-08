using System;
using System.Globalization;
using System.Web;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Represents an ASCOM Alpaca device discovered via the Alpaca management API.
/// URI format: <c>{deviceType}://AlpacaDevice/{uniqueId}?host={host}&amp;port={port}&amp;deviceNumber={deviceNumber}#{displayName}</c>
/// </summary>
public record class AlpacaDevice : DeviceBase
{
    internal AlpacaClient? Client { get; init; }

    public AlpacaDevice(Uri deviceUri) : base(deviceUri)
    {
    }

    public AlpacaDevice(DeviceType deviceType, string uniqueId, string host, int port, int deviceNumber, string displayName)
        : base(new Uri($"{deviceType}://{nameof(AlpacaDevice)}/{uniqueId}?host={Uri.EscapeDataString(host)}&port={port.ToString(CultureInfo.InvariantCulture)}&deviceNumber={deviceNumber.ToString(CultureInfo.InvariantCulture)}#{Uri.EscapeDataString(displayName)}"))
    {
    }

    /// <summary>
    /// The Alpaca server host (IP or hostname).
    /// </summary>
    public string Host => Query.QueryValue(DeviceQueryKey.Host) ?? "localhost";

    /// <summary>
    /// The Alpaca server port.
    /// </summary>
    public int Port => int.TryParse(Query.QueryValue(DeviceQueryKey.Port), CultureInfo.InvariantCulture, out var port) ? port : 11111;

    /// <summary>
    /// The device number on the Alpaca server.
    /// </summary>
    public int DeviceNumber => int.TryParse(Query.QueryValue(DeviceQueryKey.DeviceNumber), CultureInfo.InvariantCulture, out var num) ? num : 0;

    /// <summary>
    /// The base URL for this device's Alpaca server.
    /// </summary>
    internal string BaseUrl => $"http://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The ASCOM device type string (lowercase) for Alpaca API calls.
    /// </summary>
    internal string AlpacaDeviceType => DeviceType switch
    {
        Devices.DeviceType.Camera => "camera",
        Devices.DeviceType.CoverCalibrator => "covercalibrator",
        Devices.DeviceType.Telescope => "telescope",
        Devices.DeviceType.Focuser => "focuser",
        Devices.DeviceType.FilterWheel => "filterwheel",
        Devices.DeviceType.Switch => "switch",
        _ => DeviceType.ToString().ToLowerInvariant()
    };

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp)
    {
        if (Client is null)
        {
            return null;
        }

        var external = sp.External;
        return DeviceType switch
        {
            Devices.DeviceType.Camera => new AlpacaCameraDriver(this, external),
            Devices.DeviceType.CoverCalibrator => new AlpacaCoverCalibratorDriver(this, external),
            Devices.DeviceType.FilterWheel => new AlpacaFilterWheelDriver(this, external),
            Devices.DeviceType.Focuser => new AlpacaFocuserDriver(this, external),
            Devices.DeviceType.Switch => new AlpacaSwitchDriver(this, external),
            Devices.DeviceType.Telescope => new AlpacaTelescopeDriver(this, external),
            _ => null
        };
    }
}
