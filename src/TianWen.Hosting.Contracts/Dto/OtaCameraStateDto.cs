using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Per-OTA camera and frame metrics snapshot.
/// </summary>
public sealed class OtaCameraStateDto
{
    public required int OtaIndex { get; init; }

    /// <summary>Camera display label, and whether this OTA has a focuser / filter wheel fitted.
    /// Sourced from <see cref="ISessionTelemetry.TelescopeDisplays"/>: without them a mirroring client
    /// cannot label an OTA column or decide whether to draw the focus / filter rows, and would have to
    /// infer "fitted" from whether a numeric field happens to be non-zero.</summary>
    public required string CameraName { get; init; }

    /// <inheritdoc cref="CameraName"/>
    public required bool HasFocuser { get; init; }

    /// <inheritdoc cref="CameraName"/>
    public required bool HasFilterWheel { get; init; }

    public required string State { get; init; }
    public required DateTimeOffset ExposureStart { get; init; }
    public required double SubExposureSeconds { get; init; }
    public required int FrameNumber { get; init; }
    /// <summary>
    /// Null until the camera's first frame (<see cref="CameraExposureState.FilterName"/>). Nullable and NOT
    /// <c>required</c>: the serializer omits a null, and a required property then fails the WHOLE read, so a
    /// session's state could not be read by any client from its start until its first frame.
    /// </summary>
    public string? FilterName { get; init; }
    public required int FocusPosition { get; init; }
    public double? FocuserTemperature { get; init; }
    public required bool FocuserIsMoving { get; init; }

    // Last frame metrics
    public required int StarCount { get; init; }
    public float? MedianHfd { get; init; }
    public float? MedianFwhm { get; init; }

    // Cooling. Taken from the newest CoolingSample for this camera rather than added to
    // CameraExposureState: the session already records the ramp, so re-polling the driver for the state
    // projection would be a second source of the same fact that could disagree with the ramp chart drawn
    // beside it. NaN (-> 0 on a strict wire) until the first cooling sample lands.
    public double? SensorTemperatureC { get; init; }
    public double? SetpointTemperatureC { get; init; }
    public double? CoolerPowerPercent { get; init; }

    public static OtaCameraStateDto FromState(int otaIndex, CameraExposureState camera, FrameMetrics metrics,
        TelescopeDisplayInfo display, CoolingSample? cooling = null) => new()
    {
        OtaIndex = otaIndex,
        CameraName = display.CameraName,
        HasFocuser = display.HasFocuser,
        HasFilterWheel = display.HasFilterWheel,
        State = camera.State.ToString(),
        ExposureStart = camera.ExposureStart,
        SubExposureSeconds = camera.SubExposure.TotalSeconds,
        FrameNumber = camera.FrameNumber,
        FilterName = camera.FilterName,
        FocusPosition = camera.FocusPosition,
        // NaN by default on CameraExposureState whenever no focuser is fitted, and the HFD/FWHM below
        // are NaN until a frame has been measured -- all three occur on an ordinary healthy session.
        FocuserTemperature = JsonNumber.OrNull(camera.FocuserTemperature),
        FocuserIsMoving = camera.FocuserIsMoving,
        StarCount = metrics.StarCount,
        MedianHfd = JsonNumber.OrNull(metrics.MedianHfd),
        MedianFwhm = JsonNumber.OrNull(metrics.MedianFwhm),
        SensorTemperatureC = JsonNumber.OrNull(cooling?.TemperatureC ?? double.NaN),
        SetpointTemperatureC = JsonNumber.OrNull(cooling?.SetpointTempC ?? double.NaN),
        CoolerPowerPercent = JsonNumber.OrNull(cooling?.CoolerPowerPercent ?? double.NaN),
    };
}
