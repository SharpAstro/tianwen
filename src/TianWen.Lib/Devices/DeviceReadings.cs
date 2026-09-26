using System.Collections.Immutable;

namespace TianWen.Lib.Devices;

/// <summary>
/// What a connected camera reads now, as <see cref="DeviceHubReadingExtensions.ReadCameraAsync"/> takes it. A
/// value the camera does not give is NaN, never 0: a cooler at 0 °C is a real reading.
/// </summary>
public readonly record struct CameraReading(
    double CcdTemperatureC,
    double HeatsinkTemperatureC,
    double SetpointC,
    double CoolerPowerPercent,
    bool CoolerOn,
    CameraState State,
    bool UsesGainValue,
    bool UsesGainMode,
    short GainMin,
    short GainMax,
    short Gain,
    ImmutableArray<string> GainModes,
    int SensorWidth,
    int SensorHeight,
    RoiConstraints RoiConstraints)
{
    /// <summary>Exposing, downloading or otherwise at work: what a disconnect or a new exposure has to wait for.</summary>
    public bool IsBusy => State is not (CameraState.Idle or CameraState.NotConnected);
}

/// <summary>What a connected focuser reads now; a temperature it does not give is NaN.</summary>
public readonly record struct FocuserReading(int Position, double TemperatureC, bool IsMoving);

/// <summary>What a connected filter wheel reads now: the slot it is at and the filter in it, if named.</summary>
public readonly record struct FilterWheelReading(int Position, string? FilterName);

/// <summary>
/// What a connected cover or flat panel reads now: the flap, the light, and the light's brightness. A brightness it does
/// not give is -1, as <see cref="ICoverDriver.MaxBrightness"/> is when not known.
/// </summary>
public readonly record struct CoverReading(
    CoverStatus Cover,
    CalibratorStatus Calibrator,
    int Brightness,
    int MaxBrightness,
    bool CanControlBrightness);
