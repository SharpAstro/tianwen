using System;

namespace TianWen.Lib.Devices;

/// <summary>Which reading set a warm-up's target (<see cref="WarmUpTarget.Resolve"/>).</summary>
public enum WarmUpTargetSource : byte
{
    /// <summary>The camera's own heat-sink temperature.</summary>
    HeatSink,

    /// <summary>The sensor temperature a cool-down started from, once it had settled with the cooler off (#1842).</summary>
    SettledSensorBeforeCooling,

    /// <summary><see cref="CameraReading.WarmUpFallbackTargetC"/>: no heat sink and no settled reading.</summary>
    Fallback
}

/// <summary>
/// Where a warm-up takes the sensor, and which reading said so. ONE rule for the hub's ramp (and so the crash journal's
/// recovered warm-up), the session's warm-up and a client's quit question: the heat sink when the camera gives one,
/// else the SETTLED sensor reading its cool-down started from (a fan-cooled body with no heat sink rests at the air
/// around it), else <see cref="CameraReading.WarmUpFallbackTargetC"/>. An unsettled reading never sets the target.
/// </summary>
public readonly record struct WarmUpTarget(double TempC, WarmUpTargetSource Source)
{
    public static WarmUpTarget Resolve(double heatsinkTemperatureC, SensorAmbientReading? preCool)
    {
        if (double.IsFinite(heatsinkTemperatureC))
        {
            return new WarmUpTarget(heatsinkTemperatureC, WarmUpTargetSource.HeatSink);
        }

        if (preCool is { Settled: true } reading && double.IsFinite(reading.TempC))
        {
            return new WarmUpTarget(reading.TempC, WarmUpTargetSource.SettledSensorBeforeCooling);
        }

        return new WarmUpTarget(CameraReading.WarmUpFallbackTargetC, WarmUpTargetSource.Fallback);
    }

    /// <summary>The source in words, for a log line or an activity label.</summary>
    public string Describe() => Source switch
    {
        WarmUpTargetSource.HeatSink => "the heat sink",
        WarmUpTargetSource.SettledSensorBeforeCooling => "the sensor settled before cooling",
        _ => "no heat sink and no settled reading"
    };
}
