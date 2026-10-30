using System.Collections.Immutable;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

/// <summary>
/// What a connected camera reads now, as <see cref="DeviceHubReadingExtensions.ReadCameraAsync"/> takes it. A
/// value the camera does not give is NaN, never 0: a cooler at 0 °C is a real reading.
/// </summary>
/// <param name="VideoBitDepths">The depths it streams in (<see cref="IVideoCameraDriver.VideoBitDepths"/>); empty when it
/// does not stream, or cannot choose.</param>
/// <param name="CanFastReadout">Whether it has a high-speed readout (<see cref="ICameraDriver.CanFastReadout"/>), which a
/// planetary capture switches.</param>
/// <param name="ReadoutModes">Its readout modes by name (<see cref="ICameraDriver.ReadoutModes"/>); empty when it has one.</param>
/// <param name="ReadoutMode">The readout mode it is in, one of <paramref name="ReadoutModes"/>; null when it has one or cannot say.</param>
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
    RoiConstraints RoiConstraints,
    ImmutableArray<BitDepth> VideoBitDepths = default,
    bool CanFastReadout = false,
    ImmutableArray<string> ReadoutModes = default,
    string? ReadoutMode = null)
{
    /// <summary>Exposing, downloading or otherwise at work: what a disconnect or a new exposure has to wait for.</summary>
    public bool IsBusy => State is not (CameraState.Idle or CameraState.NotConnected);

    /// <summary>Where a warm-up takes a sensor when the camera gives no heat-sink temperature.</summary>
    public const double WarmUpFallbackTargetC = 25.0;

    /// <summary>How close to its target a sensor counts as warm.</summary>
    public const double WarmUpToleranceC = 1.0;

    /// <summary>Whether a warm-up has anything to do for this camera (<see cref="NeedsWarmUpFrom"/>).</summary>
    public bool NeedsWarmUp => NeedsWarmUpFrom(CoolerOn, CcdTemperatureC, HeatsinkTemperatureC, PreCool);

    /// <summary>
    /// The settled sensor reading this camera's last cool-down started from, when the reader knows it (the hub does);
    /// null is "none known", which resolves to the fallback.
    /// </summary>
    public SensorAmbientReading? PreCool { get; init; }

    /// <summary>
    /// Whether a warm-up has anything to do: the cooler is on and the sensor is below where the ramp takes it
    /// (<see cref="WarmUpTarget.Resolve"/>) by more than <see cref="WarmUpToleranceC"/>. ONE rule for the node's
    /// ramp and a client's quit question, which says "warm up" only when a camera needs it: a camera with no cooler, or one
    /// whose cooler is off, has nothing to warm (the ZWO live check, 2026-09-28). A sensor giving no temperature counts as
    /// cold, since over-waiting is safer than a thermal shock.
    /// </summary>
    public static bool NeedsWarmUpFrom(bool coolerOn, double ccdTemperatureC, double heatsinkTemperatureC, SensorAmbientReading? preCool = null)
    {
        return coolerOn && !(ccdTemperatureC >= WarmUpTarget.Resolve(heatsinkTemperatureC, preCool).TempC - WarmUpToleranceC);
    }
}

/// <summary>
/// What a connected focuser reads now; a temperature it does not give is NaN, a position it does not give (a failed read)
/// <see cref="FocuserPositions.None"/>.
/// </summary>
public readonly record struct FocuserReading(int Position, double TemperatureC, bool IsMoving);

/// <summary>
/// What a connected filter wheel reads now: the slot it is at, counted from 0 (-1 while it turns, or when it cannot say,
/// as ASCOM's wheels report it), and the name of the filter in that slot, null when there is none to name; and the name of
/// every slot's filter, as the wheel's driver names them, which a filter change goes by (<see cref="FilterChange.ChangeAsync"/>).
/// </summary>
public readonly record struct FilterWheelReading(int Position, string? FilterName, System.Collections.Immutable.ImmutableArray<string> Slots = default,
    System.Collections.Immutable.ImmutableArray<int> Offsets = default);

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
