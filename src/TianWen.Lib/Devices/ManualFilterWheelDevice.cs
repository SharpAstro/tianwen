using System;
using System.Web;
using TianWen.Lib;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

/// <summary>
/// Device record for a manual (fixed) filter holder — a single filter slot with no motor.
/// The filter name is encoded in the URI query parameter <c>filter</c>.
/// Used when a camera has a fixed filter (e.g., a dual-band filter in a nose adapter)
/// that should be reported in FITS headers but doesn't need a motorized wheel.
/// </summary>
public record class ManualFilterWheelDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "manual";
    private const string DefaultDisplayName = "Manual Filter Holder";

    public ManualFilterWheelDevice(Filter filter)
        : this(new Uri($"{DeviceType.FilterWheel}://{typeof(ManualFilterWheelDevice).Name}/{DefaultDeviceId}?filter1={Uri.EscapeDataString(filter.Name)}#{DefaultDisplayName} ({filter.ShortName})"))
    {
    }

    public ManualFilterWheelDevice(string filterName)
        : this(Filter.FromName(filterName))
    {
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.FilterWheel => new ManualFilterWheelDriver(this, sp.External),
        _ => null
    };

    /// <summary>
    /// The fixed filter installed in this holder, parsed from the URI query string.
    /// </summary>
    internal Filter InstalledFilter
    {
        get
        {
            var name = HttpUtility.ParseQueryString(DeviceUri.Query)[DeviceQueryKeyExtensions.FilterKey(1)];
            return name is not null ? Filter.FromName(name) : Filter.Unknown;
        }
    }
}
