using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Per-camera exposure state snapshot for the live session UI.
/// Updated by the imaging loop, polled by the UI for countdown display.
/// </summary>
public readonly record struct CameraExposureState(
    int CameraIndex,
    DateTimeOffset ExposureStart,
    TimeSpan SubExposure,
    int FrameNumber,
    string FilterName,
    int FocusPosition,
    CameraState State,
    double FocuserTemperature = double.NaN,
    bool FocuserIsMoving = false);
