using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Device record for the built-in guider that uses <see cref="GuideLoop"/> internally.
/// Always available (no external software needed). Configuration is carried in URI query parameters:
/// <list type="bullet">
///   <item><c>pulseGuideSource</c> — <see cref="PulseGuideSource"/> (Auto, Camera, Mount)</item>
/// </list>
/// </summary>
public record class BuiltInGuiderDevice(Uri DeviceUri) : GuiderDeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "builtin";
    private const string DefaultDisplayName = "Built-in Guider";

    public BuiltInGuiderDevice()
        : this(new Uri($"{DeviceType.Guider}://{typeof(BuiltInGuiderDevice).Name}/{DefaultDeviceId}#{DefaultDisplayName}"))
    {
    }

    public override string? ProfileName => null;

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Guider => new BuiltInGuiderDriver(this, external),
        _ => null
    };

    /// <summary>
    /// Parses the <see cref="PulseGuideSource"/> from the device URI query string.
    /// Defaults to <see cref="PulseGuideSource.Auto"/> if not specified or invalid.
    /// </summary>
    internal PulseGuideSource GetPulseGuideSource()
    {
        var value = Query.QueryValue(DeviceQueryKey.PulseGuideSource);
        if (value is not null && Enum.TryParse<PulseGuideSource>(value, ignoreCase: true, out var source))
        {
            return source;
        }
        return PulseGuideSource.Auto;
    }
}
