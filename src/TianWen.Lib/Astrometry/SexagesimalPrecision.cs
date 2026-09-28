namespace TianWen.Lib.Astrometry;

/// <summary>
/// The last field a sexagesimal string is rounded to (<see cref="CoordinateUtils.HoursToHMS"/>,
/// <see cref="CoordinateUtils.DegreesToDMS"/>). Each value is the number of those units in one whole
/// hour or degree, which is what the formatter rounds in.
/// </summary>
public enum SexagesimalPrecision
{
    /// <summary><c>DD:MM</c>, the LX200 low-precision declination and both site coordinates.</summary>
    Minute = 60,

    /// <summary><c>HH:MM.T</c>, the LX200 low-precision right ascension.</summary>
    TenthMinute = 600,

    /// <summary><c>DD:MM:SS</c>, the LX200 high-precision forms.</summary>
    Second = 3600,

    /// <summary><c>DD:MM:SS.fff</c>, the fraction written only when it is not zero.</summary>
    Millisecond = 3_600_000
}
