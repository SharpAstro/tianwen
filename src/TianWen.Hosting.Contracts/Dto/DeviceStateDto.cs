using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// One device as the node sees it now: whether it is connected, who holds it, and what it last read. The device
/// plane's read side (P2 of docs/plans/hardware-in-the-server.md, #929): <c>GET /api/v1/devices/state</c> answers a
/// list of these, authoritatively, and <c>DEVICE-STATE</c> pushes one as it changes.
/// </summary>
/// <remarks>
/// A reading a device does not give is <see langword="null"/>, never 0: a cooler at 0 °C and a mount at RA 0 are real
/// readings, and a client must be able to tell them from "not known". Exactly one of the typed parts is set, for the
/// device's type, once the node has read it.
///
/// These are records, unlike the other DTOs, so that the node's copies of a reading (stamped with its read time, and as
/// a push compares it) are <c>with</c> expressions, which cannot forget a field: a field a hand-listed comparison left
/// out would be a change that is never pushed.
/// </remarks>
public sealed record DeviceStateDto
{
    public required string DeviceUri { get; init; }

    public required DeviceType DeviceType { get; init; }

    public string? DisplayName { get; init; }

    public required bool Connected { get; init; }

    /// <summary>
    /// The run holding the device (its lease's owner label), or null when nothing does. What P6 re-sources the
    /// client's ownership gate from: a device a run holds refuses every command but a read.
    /// </summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When the node last read the device; null until it first has. A leased device keeps its last reading.</summary>
    public DateTimeOffset? ReadUtc { get; init; }

    public CameraDeviceStateDto? Camera { get; init; }

    public FocuserDeviceStateDto? Focuser { get; init; }

    public FilterWheelDeviceStateDto? FilterWheel { get; init; }

    public MountDeviceStateDto? Mount { get; init; }

    public CoverDeviceStateDto? Cover { get; init; }

    /// <summary>A finite reading, or null for the NaN a device gives as "not known".</summary>
    internal static double? Known(double value) => double.IsFinite(value) ? value : null;

    /// <summary>The key a <c>DEVICE-STATE</c> event carries the device's state under.</summary>
    public const string EventKey = "Device";

    /// <summary>
    /// The device state a <c>DEVICE-STATE</c> event carries (<see cref="Api.NodeWire.DeviceStateEvent"/>): false for any
    /// other event, or one whose payload does not read as a device state.
    /// </summary>
    public static bool TryFromEvent(WebSocketEventDto dto, [NotNullWhen(true)] out DeviceStateDto? device)
    {
        device = dto.Event == Api.NodeWire.DeviceStateEvent && dto.Data is { } data && data.TryGetValue(EventKey, out var value)
            ? value switch
            {
                DeviceStateDto inProcess => inProcess,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize(HostingJsonContext.Default.DeviceStateDto),
                _ => null,
            }
            : null;
        return device is not null;
    }
}

/// <summary>What a camera reads (<see cref="CameraReading"/>), with the cooler intent the node keeps for it.</summary>
public sealed record CameraDeviceStateDto
{
    public double? CcdTemperatureC { get; init; }
    public double? HeatsinkTemperatureC { get; init; }
    public double? SetpointC { get; init; }
    public double? CoolerPowerPercent { get; init; }
    public required bool CoolerOn { get; init; }
    public required CameraState State { get; init; }
    public required bool UsesGainValue { get; init; }
    public required bool UsesGainMode { get; init; }
    public required short GainMin { get; init; }
    public required short GainMax { get; init; }
    public required short Gain { get; init; }
    public string[]? GainModes { get; init; }
    public required int SensorWidth { get; init; }
    public required int SensorHeight { get; init; }

    /// <summary>What the cooler is being asked to do, as the node's hub records it; null when nothing has asked.</summary>
    public CoolerIntentKind? CoolerIntent { get; init; }

    /// <summary>The target of a <see cref="CoolerIntentKind.Cool"/> intent; null otherwise.</summary>
    public double? CoolerIntentSetpointC { get; init; }

    public static CameraDeviceStateDto FromReading(CameraReading reading, CoolerIntent? intent) => new CameraDeviceStateDto
    {
        CcdTemperatureC = DeviceStateDto.Known(reading.CcdTemperatureC),
        HeatsinkTemperatureC = DeviceStateDto.Known(reading.HeatsinkTemperatureC),
        SetpointC = DeviceStateDto.Known(reading.SetpointC),
        CoolerPowerPercent = DeviceStateDto.Known(reading.CoolerPowerPercent),
        CoolerOn = reading.CoolerOn,
        State = reading.State,
        UsesGainValue = reading.UsesGainValue,
        UsesGainMode = reading.UsesGainMode,
        GainMin = reading.GainMin,
        GainMax = reading.GainMax,
        Gain = reading.Gain,
        GainModes = reading.GainModes.IsDefaultOrEmpty ? null : [.. reading.GainModes],
        SensorWidth = reading.SensorWidth,
        SensorHeight = reading.SensorHeight,
        CoolerIntent = intent?.Kind,
        CoolerIntentSetpointC = intent is { Kind: CoolerIntentKind.Cool } cool ? DeviceStateDto.Known(cool.SetpointC) : null,
    };
}

/// <summary>What a focuser reads (<see cref="FocuserReading"/>).</summary>
public sealed record FocuserDeviceStateDto
{
    public required int Position { get; init; }
    public double? TemperatureC { get; init; }
    public required bool IsMoving { get; init; }

    public static FocuserDeviceStateDto FromReading(FocuserReading reading) => new FocuserDeviceStateDto
    {
        Position = reading.Position,
        TemperatureC = DeviceStateDto.Known(reading.TemperatureC),
        IsMoving = reading.IsMoving,
    };
}

/// <summary>What a filter wheel reads (<see cref="FilterWheelReading"/>).</summary>
public sealed record FilterWheelDeviceStateDto
{
    public required int Position { get; init; }
    public string? FilterName { get; init; }

    public static FilterWheelDeviceStateDto FromReading(FilterWheelReading reading) => new FilterWheelDeviceStateDto
    {
        Position = reading.Position,
        FilterName = reading.FilterName,
    };
}

/// <summary>
/// What a mount reads (<see cref="MountState"/>), with the node's safety-limit verdict for it
/// (<c>MountLimitWatcher.VerdictFor</c>), which is what the GUI reads every frame today.
/// </summary>
public sealed record MountDeviceStateDto
{
    public double? RightAscension { get; init; }
    public double? Declination { get; init; }
    public double? HourAngle { get; init; }
    public double? RaJ2000 { get; init; }
    public double? DecJ2000 { get; init; }
    public required PointingState PierSide { get; init; }
    public required bool IsSlewing { get; init; }
    public required bool IsTracking { get; init; }
    public MountLimitDto? Limit { get; init; }

    public static MountDeviceStateDto FromState(MountState state, MountLimitVerdict? verdict) => new MountDeviceStateDto
    {
        RightAscension = DeviceStateDto.Known(state.RightAscension),
        Declination = DeviceStateDto.Known(state.Declination),
        HourAngle = DeviceStateDto.Known(state.HourAngle),
        RaJ2000 = DeviceStateDto.Known(state.RaJ2000),
        DecJ2000 = DeviceStateDto.Known(state.DecJ2000),
        PierSide = state.PierSide,
        IsSlewing = state.IsSlewing,
        IsTracking = state.IsTracking,
        Limit = verdict is { } v ? MountLimitDto.FromVerdict(v) : null,
    };
}

/// <summary>What a cover or flat panel reads (<see cref="CoverReading"/>): the flap, the light and its brightness.</summary>
public sealed record CoverDeviceStateDto
{
    public required CoverStatus CoverState { get; init; }
    public required CalibratorStatus CalibratorState { get; init; }

    /// <summary>The light's brightness; null when there is no light or it did not say.</summary>
    public int? Brightness { get; init; }

    /// <summary>The highest brightness the light takes; null when not known.</summary>
    public int? MaxBrightness { get; init; }

    /// <summary>False for a hand-switched panel, whose level a person sets: the node can only record it.</summary>
    public required bool CanControlBrightness { get; init; }

    public static CoverDeviceStateDto FromReading(CoverReading reading) => new CoverDeviceStateDto
    {
        CoverState = reading.Cover,
        CalibratorState = reading.Calibrator,
        Brightness = reading.Brightness >= 0 ? reading.Brightness : null,
        MaxBrightness = reading.MaxBrightness >= 0 ? reading.MaxBrightness : null,
        CanControlBrightness = reading.CanControlBrightness,
    };
}
