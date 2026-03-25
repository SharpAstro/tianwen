using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Polled mount state snapshot for the live session UI.
/// </summary>
public readonly record struct MountState(
    double RightAscension,
    double Declination,
    double HourAngle,
    PointingState PierSide,
    bool IsSlewing,
    bool IsTracking);
