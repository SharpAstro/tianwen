using System;

namespace TianWen.Lib.Devices;

public enum SetupointDirection
{
    Down,
    Up
}

public static class CoolDirectionEx
{
    /// <summary>
    /// The setpoint of one ramp step: <paramref name="maxStepC"/> past the sensor's own reading, never past
    /// <paramref name="setpointTemp"/>. A whole-degree step (the default) lands on a whole degree as it always has; a
    /// fractional one, which a rate ceiling asks for of a camera that takes a fraction, is kept as it is. A camera that
    /// takes only whole degrees (<paramref name="wholeDegreeCamera"/>) is always given a whole one, rounded to the sensor's
    /// side of a half degree, so it moves toward the target and the SDK's truncation has nothing to undo; the ramp holds
    /// each such setpoint for as long as the rate ceiling asks.
    /// </summary>
    public static double SetpointTemp(this SetupointDirection direction, double ccdTemp, double setpointTemp, double maxStepC = 1d, bool wholeDegreeCamera = false)
    {
        var whole = wholeDegreeCamera || maxStepC >= 1d;
        if (wholeDegreeCamera)
        {
            maxStepC = 1d;
        }
        var upTemp = whole ? Math.Round(ccdTemp + maxStepC) : ccdTemp + maxStepC;
        var downTemp = whole ? Math.Round(ccdTemp - maxStepC) : ccdTemp - maxStepC;

        return direction switch
        {
            SetupointDirection.Up => Math.Min(upTemp, setpointTemp),
            SetupointDirection.Down => Math.Max(downTemp, setpointTemp),
            _ => setpointTemp
        };
    }

    public static bool NeedsFurtherRamping(this SetupointDirection direction, double ccdTemp, double setpointTemp, double tolerance = 0.1d) =>
        direction switch
        {
            SetupointDirection.Up => ccdTemp + tolerance < setpointTemp,
            SetupointDirection.Down => ccdTemp - tolerance > setpointTemp,
            _ => false
        };

    public static bool ThresholdPowerReached(this SetupointDirection direction, double power, double thresholdPower) =>
        direction switch
        {
            SetupointDirection.Up => power <= thresholdPower,
            SetupointDirection.Down => power >= thresholdPower,
            _ => true
        };
}