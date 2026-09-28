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

    /// <summary>Where a warm-up takes a sensor when the camera gives no heat-sink temperature.</summary>
    public const double WarmUpFallbackTargetC = 25.0;

    /// <summary>How close to its target a sensor counts as warm.</summary>
    public const double WarmUpToleranceC = 1.0;

    /// <summary>Whether a warm-up has anything to do for this camera (<see cref="NeedsWarmUpFrom"/>).</summary>
    public bool NeedsWarmUp => NeedsWarmUpFrom(CoolerOn, CcdTemperatureC, HeatsinkTemperatureC);

    /// <summary>
    /// Whether a warm-up has anything to do: the cooler is on and the sensor is below where the ramp takes it (the heat
    /// sink, else <see cref="WarmUpFallbackTargetC"/>) by more than <see cref="WarmUpToleranceC"/>. ONE rule for the node's
    /// ramp and a client's quit question, which says "warm up" only when a camera needs it: a camera with no cooler, or one
    /// whose cooler is off, has nothing to warm (the ZWO live check, 2026-09-28). A sensor giving no temperature counts as
    /// cold, since over-waiting is safer than a thermal shock.
    /// </summary>
    public static bool NeedsWarmUpFrom(bool coolerOn, double ccdTemperatureC, double heatsinkTemperatureC)
    {
        return coolerOn && !(ccdTemperatureC >= WarmUpTargetC(heatsinkTemperatureC) - WarmUpToleranceC);
    }

    /// <summary>Where a warm-up takes the sensor: the heat sink when the camera gives one, else <see cref="WarmUpFallbackTargetC"/>.</summary>
    public static double WarmUpTargetC(double heatsinkTemperatureC)
    {
        return double.IsFinite(heatsinkTemperatureC) ? heatsinkTemperatureC : WarmUpFallbackTargetC;
    }
}

/// <summary>What a connected focuser reads now; a temperature it does not give is NaN.</summary>
public readonly record struct FocuserReading(int Position, double TemperatureC, bool IsMoving);

/// <summary>
/// What a connected filter wheel reads now: the slot it is at, counted from 0 (-1 while it turns, or when it cannot say,
/// as ASCOM's wheels report it), and the name of the filter in that slot, null when there is none to name.
/// </summary>
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
