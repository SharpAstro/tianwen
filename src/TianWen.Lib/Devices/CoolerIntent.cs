namespace TianWen.Lib.Devices;

/// <summary>What a camera's cooler is being asked to do (<see cref="CoolerIntent"/>).</summary>
public enum CoolerIntentKind
{
    /// <summary>The cooler is off, and asked to stay off.</summary>
    Off,

    /// <summary>The cooler is asked to hold <see cref="CoolerIntent.SetpointC"/>, or is ramping towards it.</summary>
    Cool,

    /// <summary>A warm-up ramp is running: the sensor is being brought back to ambient before the cooler goes off.</summary>
    Warm,
}

/// <summary>
/// What a camera's cooler is being ASKED to do, as opposed to the state it is in: the target of a ramp, never a step
/// on the way. A cool-down part-way from 20 to -10 °C holds a 5 °C setpoint at that moment, and a warm-up is a
/// process no setpoint describes, so the state a camera reports cannot answer what to re-establish after the node that
/// drove it crashed; this does (the crash journal, P1 of docs/plans/hardware-in-the-server.md, #917). Recorded on the
/// hub (<see cref="IDeviceHub.SetCoolerIntent"/>) by whatever commands the cooler.
/// </summary>
/// <param name="Kind">What the cooler is asked to do.</param>
/// <param name="SetpointC">The target in degrees Celsius for <see cref="CoolerIntentKind.Cool"/>; NaN otherwise.</param>
public readonly record struct CoolerIntent(CoolerIntentKind Kind, double SetpointC)
{
    /// <summary>Off, and staying off.</summary>
    public static CoolerIntent Off => new CoolerIntent(CoolerIntentKind.Off, double.NaN);

    /// <summary>A warm-up ramp towards ambient, after which the cooler goes off.</summary>
    public static CoolerIntent Warm => new CoolerIntent(CoolerIntentKind.Warm, double.NaN);

    /// <summary>Cooling to, and then holding, <paramref name="setpointC"/>.</summary>
    public static CoolerIntent CoolTo(double setpointC) => new CoolerIntent(CoolerIntentKind.Cool, setpointC);
}
