namespace Astap.Lib.Devices;

public enum TrackingSpeed
{
    None = 0,
    Sidereal = 1,
    Lunar = 2,
    Solar = 3
}

public interface IMountDriver : IDeviceDriver
{
    TrackingSpeed TrackingSpeed { get; set; }

    bool AtHome { get; }

    bool IsSlewing { get; }

    bool SlewAsync(double ra, double dec);
}