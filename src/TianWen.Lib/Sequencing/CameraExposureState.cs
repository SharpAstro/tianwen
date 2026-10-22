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
/// <param name="FocusPosition">The focuser's position, <see cref="FocuserPositions.None"/> for none (#1764).</param>
public readonly record struct CameraExposureState(
    int CameraIndex,
    DateTimeOffset ExposureStart,
    TimeSpan SubExposure,
    int FrameNumber,
    string? FilterName,
    int FocusPosition,
    CameraState State,
    double FocuserTemperature = double.NaN,
    bool FocuserIsMoving = false)
{
    /// <summary>
    /// A camera's state before its first frame: no focus position and no focuser temperature. A default struct reads 0 for
    /// both, and 0 is a position and a temperature, so the session publishes this, never <see langword="default"/>.
    /// </summary>
    public static readonly CameraExposureState Unset = default(CameraExposureState) with
    {
        FocusPosition = FocuserPositions.None,
        FocuserTemperature = double.NaN,
    };

    /// <summary><paramref name="count"/> cameras, each <see cref="Unset"/>.</summary>
    public static CameraExposureState[] UnsetFor(int count)
    {
        var states = new CameraExposureState[count];
        Array.Fill(states, Unset);
        return states;
    }
}
