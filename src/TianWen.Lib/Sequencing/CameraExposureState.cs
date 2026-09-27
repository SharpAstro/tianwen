using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Per-camera exposure state snapshot for the live session UI.
/// Updated by the imaging loop, polled by the UI for countdown display.
/// </summary>
/// <param name="FilterName">Null until the camera's first frame: the session publishes a DEFAULT state for every
/// camera from the start of its run (and again on leaving Observing), and a default struct's string is null
/// whatever the type says. Declared nullable so every reader handles it, the wire included.</param>
public readonly record struct CameraExposureState(
    int CameraIndex,
    DateTimeOffset ExposureStart,
    TimeSpan SubExposure,
    int FrameNumber,
    string? FilterName,
    int FocusPosition,
    CameraState State,
    double FocuserTemperature = double.NaN,
    bool FocuserIsMoving = false);
