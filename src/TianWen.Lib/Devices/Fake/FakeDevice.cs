using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TianWen.Lib;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Fake;

public record FakeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    /// <summary>
    /// Represents a fake device (for testing and simulation).
    /// </summary>
    /// <param name="deviceType"></param>
    /// <param name="deviceId">Fake device id (starting from 1)</param>
    public FakeDevice(DeviceType deviceType, int deviceId, NameValueCollection? values = null)
        : this(new Uri($"{deviceType}://{typeof(FakeDevice).Name}/Fake{deviceType}{deviceId}{(values is { Count: > 0 } ? "?" + values.ToQueryString() : "")}#Fake {deviceType.PascalCaseStringToName()} {deviceId}"))
    {
        // calls primary constructor
    }

    private static readonly ImmutableArray<DeviceSettingDescriptor> CameraSettings =
    [
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.PePeriodSeconds.Key, "PE Period",
            defaultValue: 600, min: 60, max: 3600, step: 60,
            suffix: "s"),
        DeviceSettingHelper.FloatSetting(
            DeviceQueryKey.PePeakTopeakArcsec.Key, "PE Amplitude",
            defaultValue: 20.0, min: 0.0, max: 120.0, step: 2.0,
            format: "F1", suffix: "\""),
    ];

    // Polar-misalignment knobs. Only honoured by FakeSkywatcherMountDriver (port=SkyWatcher),
    // which has the encoder model the polar-align routine needs. The visibility predicate
    // hides the rows for the other fake mount stacks (LX200, OnStep, SGP, default) where
    // these keys would silently be ignored.
    private static bool IsFakeSkywatcherMount(Uri uri) =>
        string.Equals(HttpUtility.ParseQueryString(uri.Query)[DeviceQueryKey.Port.Key], "SkyWatcher", StringComparison.OrdinalIgnoreCase);

    private static readonly ImmutableArray<DeviceSettingDescriptor> MountSettings =
    [
        DeviceSettingHelper.FloatSetting(
            DeviceQueryKey.PolarMisalignmentAzArcmin.Key, "Polar Az Err",
            defaultValue: 30.0, min: -180.0, max: 180.0, step: 1.0,
            format: "F1", suffix: "'",
            isVisible: IsFakeSkywatcherMount),
        DeviceSettingHelper.FloatSetting(
            DeviceQueryKey.PolarMisalignmentAltArcmin.Key, "Polar Alt Err",
            defaultValue: -10.0, min: -180.0, max: 180.0, step: 1.0,
            format: "F1", suffix: "'",
            isVisible: IsFakeSkywatcherMount),
    ];

    private static readonly ImmutableArray<DeviceSettingDescriptor> FocuserSettings =
    [
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserInitialPosition.Key, "Initial Pos",
            defaultValue: 980, min: 0, max: 2000, step: 10,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBestFocus.Key, "Best Focus",
            defaultValue: 1000, min: 0, max: 2000, step: 50,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBacklashIn.Key, "Backlash In",
            defaultValue: 20, min: 0, max: 200, step: 5,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBacklashOut.Key, "Backlash Out",
            defaultValue: 15, min: 0, max: 200, step: 5,
            suffix: " steps"),
    ];

    public override ImmutableArray<DeviceSettingDescriptor> Settings => DeviceType switch
    {
        DeviceType.Camera => CameraSettings,
        DeviceType.Focuser => FocuserSettings,
        DeviceType.Mount => MountSettings,
        _ => [],
    };

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new FakeCameraDriver(this, sp),
        DeviceType.CoverCalibrator => new FakeCoverDriver(this, sp),
        DeviceType.FilterWheel => new FakeFilterWheelDriver(this, sp),
        DeviceType.Focuser => new FakeFocuserDriver(this, sp),
        DeviceType.Guider => new FakeGuider(this, sp),
        DeviceType.Mount => CreateMountDriver(sp),
        DeviceType.Weather => new FakeWeatherDriver(this, sp),
        _ => null
    };

    private IDeviceDriver CreateMountDriver(IServiceProvider sp)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);

        // If port=LX200 is specified, use the full Meade serial protocol stack.
        if (string.Equals(port, "LX200", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeMeadeLX200ProtocolMountDriver(this, sp);
        }

        // If port=SGP is specified, use the iOptron SkyGuider Pro serial protocol stack.
        if (string.Equals(port, "SGP", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeSgpMountDriver(this, sp);
        }

        // If port=SkyWatcher is specified, use the Skywatcher motor controller protocol stack.
        if (string.Equals(port, "SkyWatcher", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeSkywatcherMountDriver(this, sp);
        }

        // If port=OnStep is specified, use the OnStep driver on top of the LX200 protocol stack.
        if (string.Equals(port, "OnStep", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeOnStepMountDriver(this, sp);
        }

        // Otherwise use the lightweight direct driver.
        return new FakeMountDriver(this, sp);
    }

    public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = 9600, Encoding? encoding = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<ISerialConnection?>(DeviceType switch
        {
            DeviceType.Mount when string.Equals(Query.QueryValue(DeviceQueryKey.Port), "SGP", StringComparison.OrdinalIgnoreCase)
                => new FakeSgpSerialDevice(logger, encoding ?? Encoding.ASCII, timeProvider, SiteLatitude >= 0, true),
            DeviceType.Mount when string.Equals(Query.QueryValue(DeviceQueryKey.Port), "SkyWatcher", StringComparison.OrdinalIgnoreCase)
                => new FakeSkywatcherSerialDevice(logger, encoding ?? Encoding.ASCII, timeProvider, true),
            DeviceType.Mount when string.Equals(Query.QueryValue(DeviceQueryKey.Port), "OnStep", StringComparison.OrdinalIgnoreCase)
                => new FakeOnStepSerialDevice(logger, encoding ?? Encoding.Latin1, timeProvider, SiteLatitude, SiteLongitude, true),
            DeviceType.Mount
                => new FakeMeadeLX200SerialDevice(logger, encoding ?? Encoding.Latin1, timeProvider, SiteLatitude, SiteLongitude, true),
            _ => null
        });

    [JsonIgnore]
    private double SiteLatitude => double.TryParse(Query.QueryValue(DeviceQueryKey.Latitude), out var latitude) ? latitude : throw new InvalidOperationException("Failed to parse latitude");

    [JsonIgnore]
    private double SiteLongitude => double.TryParse(Query.QueryValue(DeviceQueryKey.Longitude), out var longitude) ? longitude : throw new InvalidOperationException("Failed to parse longitude");

}
