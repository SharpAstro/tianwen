using System;
using TianWen.Lib;

namespace TianWen.Lib.Devices.PlayerOne;

public record class PlayerOneDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public PlayerOneDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(PlayerOneDevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    /// <summary>
    /// Cameras only for now. Player One also make a filter wheel, driven by a SEPARATE native
    /// library (<c>PlayerOnePW</c>) with its own entry points, so it is a second binding rather
    /// than another branch here; the native is already vendored and packaged for when it is
    /// written.
    /// </summary>
    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new PlayerOneCameraDriver(this, sp),
        _ => null
    };
}
