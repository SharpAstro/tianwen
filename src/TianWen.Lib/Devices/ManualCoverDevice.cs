using System;

namespace TianWen.Lib.Devices;

/// <summary>
/// Device record for a manual (dumb) flat light panel — a hand-switched light source with no ASCOM/Alpaca
/// interface (e.g. an analog LED tracing panel with a physical brightness knob). It has no motorised cover
/// flap (the driver reports <see cref="CoverStatus.NotPresent"/>) and no software brightness control: the
/// user switches it on and sets the brightness by hand, and the flat routine converges the <em>exposure</em>
/// against whatever light is arranged. Modelled as a driver — exactly like <see cref="ManualFilterWheelDevice"/>
/// — so the session drives it through the normal calibrator path with no special-casing. Assign it to an
/// OTA's cover slot to shoot manual flats.
/// </summary>
public record class ManualCoverDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "manual";
    private const string DefaultDisplayName = "Manual Light Panel";

    public ManualCoverDevice()
        : this(new Uri($"{DeviceType.CoverCalibrator}://{typeof(ManualCoverDevice).Name}/{DefaultDeviceId}#{DefaultDisplayName}"))
    {
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.CoverCalibrator => new ManualCoverDriver(this, sp),
        _ => null
    };
}
