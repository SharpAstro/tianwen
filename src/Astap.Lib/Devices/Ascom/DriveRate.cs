namespace Astap.Lib.Devices.Ascom
{
    internal enum DriveRate
    {
        Sidereal = 0, // Sidereal tracking rate (15.041 arcseconds per second).
        Lunar  = 1, // Lunar tracking rate (14.685 arcseconds per second).
        Solar = 2, // Solar tracking rate (15.0 arcseconds per second).
        King = 3 // King tracking rate (15.0369 arcseconds per second).
    }
}