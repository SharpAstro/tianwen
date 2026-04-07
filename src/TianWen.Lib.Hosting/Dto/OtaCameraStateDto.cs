using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Dto;

/// <summary>
/// Per-OTA camera and frame metrics snapshot.
/// </summary>
public sealed class OtaCameraStateDto
{
    public required int OtaIndex { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset ExposureStart { get; init; }
    public required double SubExposureSeconds { get; init; }
    public required int FrameNumber { get; init; }
    public required string FilterName { get; init; }
    public required int FocusPosition { get; init; }
    public required double FocuserTemperature { get; init; }
    public required bool FocuserIsMoving { get; init; }

    // Last frame metrics
    public required int StarCount { get; init; }
    public required float MedianHfd { get; init; }
    public required float MedianFwhm { get; init; }

    public static OtaCameraStateDto FromState(int otaIndex, CameraExposureState camera, FrameMetrics metrics) => new()
    {
        OtaIndex = otaIndex,
        State = camera.State.ToString(),
        ExposureStart = camera.ExposureStart,
        SubExposureSeconds = camera.SubExposure.TotalSeconds,
        FrameNumber = camera.FrameNumber,
        FilterName = camera.FilterName,
        FocusPosition = camera.FocusPosition,
        FocuserTemperature = camera.FocuserTemperature,
        FocuserIsMoving = camera.FocuserIsMoving,
        StarCount = metrics.StarCount,
        MedianHfd = metrics.MedianHfd,
        MedianFwhm = metrics.MedianFwhm,
    };
}
