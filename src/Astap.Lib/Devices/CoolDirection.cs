using System;

namespace Astap.Lib.Devices;

public enum CoolDirection
{
    Down,
    Up
}

public static class CoolDirectionEx
{

    public static double SetpointTemp(this CoolDirection direction, double ccdTemp, double setpointTemp) =>
        direction switch
        {
            CoolDirection.Up => Math.Min(Math.Round(ccdTemp + 1), setpointTemp),
            CoolDirection.Down => Math.Max(Math.Round(ccdTemp - 1), setpointTemp),
            _ => setpointTemp
        };

    public static bool NeedsFurtherRamping(this CoolDirection direction, double ccdTemp, double setpointTemp) =>
        direction switch
        {
            CoolDirection.Up => ccdTemp < setpointTemp,
            CoolDirection.Down => ccdTemp > setpointTemp,
            _ => false
        };



    public static bool ThresholdPowerReached(this CoolDirection direction, double power, double thresholdPower) =>
        direction switch
        {
            CoolDirection.Up => power <= thresholdPower,
            CoolDirection.Down => power >= thresholdPower,
            _ => true
        };
}