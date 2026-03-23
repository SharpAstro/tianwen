using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A single cooling ramp sample for the live session cooling graph.
/// One sample per camera per ramp interval tick.
/// </summary>
public readonly record struct CoolingSample(
    DateTimeOffset Timestamp,
    int CameraIndex,
    double TemperatureC,
    double SetpointTempC,
    double CoolerPowerPercent);
