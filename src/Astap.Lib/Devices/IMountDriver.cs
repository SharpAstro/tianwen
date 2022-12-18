using System;

namespace Astap.Lib.Devices;

public interface IMountDriver : IDeviceDriver
{
    bool CanSetTracking { get; }

    bool CanSetSideOfPier { get; }

    bool CanPark { get; }

    bool CanSetPark { get; }

    bool CanSlew { get; }

    bool CanSlewAsync { get; }

    TrackingSpeed TrackingSpeed { get; set; }

    bool Tracking { get; set; }

    bool AtHome { get; }

    bool IsSlewing { get; }

    bool SlewAsync(double ra, double dec);

    DateTime? UTCDate { get; set; }

    PierSide SideOfPier { get; set; }
}