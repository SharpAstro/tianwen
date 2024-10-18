using System;

namespace Astap.Lib.Devices;

public enum SetupointDirection
{
    Down,
    Up
}

public static class CoolDirectionEx
{
    public static double SetpointTemp(this SetupointDirection direction, double ccdTemp, double setpointTemp) =>
        direction switch
        {
            SetupointDirection.Up => Math.Min(Math.Round(ccdTemp + 1), setpointTemp),
            SetupointDirection.Down => Math.Max(Math.Round(ccdTemp - 1), setpointTemp),
            _ => setpointTemp
        };

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