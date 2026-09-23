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
    /// The camera's name as it goes into <c>INSTRUME</c>: the model with the sensor die the camera
    /// itself reports, <c>Uranus-C (IMX585)</c>, which is how SharpCap names a Player One body.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. <c>CalibrationResolver</c> treats two known instruments that differ as a hard
    /// mismatch, and every Player One light in this archive was written by SharpCap, so calibration
    /// captured here under the bare model name (<c>Uranus-C</c>) could never calibrate them: 253
    /// darks and biases shot for exactly that purpose had to be retagged by hand (2026-09-23). A
    /// model name that already carries the die, or a camera that reports none, is left as it is.
    /// Only the display name takes this form; the URI path keeps the SDK's own name, because the
    /// connect-time match compares against that.
    /// </remarks>
    internal static string InstrumentName(string modelName, string? sensorModel)
        => sensorModel is { Length: > 0 } sensor && !modelName.Contains(sensor, StringComparison.OrdinalIgnoreCase)
            ? $"{modelName} ({sensor})"
            : modelName;

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
