using Astap.Lib.Astrometry.VSOP87;
using System;
using static Astap.Lib.Astrometry.SOFA.Constants;
using static WorldWideAstronomy.WWA;
using System.Collections.Generic;

namespace Astap.Lib.Astrometry.SOFA;

public static class SOFAHelpers
{
    /**
    PURPOSE:
        This function computes atmospheric refraction in zenith
        distance.  This version computes approximate refraction for
        optical wavelengths.

    REFERENCES:
        Explanatory Supplement to the Astronomical Almanac, p. 144.
        Bennett, G. (1982), Journal of Navigation (Royal Institute) 35,
        pp. 255-259.

    INPUT
    ARGUMENTS:
        *location (struct on_surface)
        Pointer to structure containing observer's location.  This
        structure also contains weather data (optional) for the
        observer's location (defined in novas.h).
        ref_option (short int)
        = 1 ... Use 'standard' atmospheric conditions.
        = 2 ... Use atmospheric parameters input in the 'location'
                structure.
        zd_obs (double)
        Observed zenith distance, in degrees.

    OUTPUT
    ARGUMENTS:
        None.

    RETURNED
    VALUE:
        (double)
        Atmospheric refraction, in degrees.

    GLOBALS
    USED:
        DEG2RAD            novascon.c

    FUNCTIONS
    CALLED:
        exp                math.h
        tan                math.h

    VER./DATE/
    PROGRAMMER:
        V1.0/06-98/JAB (USNO/AA)

    NOTES:
        1. This function can be used for planning observations or
        telescope pointing, but should not be used for the reduction
        of precise observations.
        2. This function is the C version of NOVAS Fortran routine
        'refrac'.
    */
    public static double Refract(double elevation, double zd_obs, double pressure = double.NaN, double temp = double.NaN)
    {
        /*
           's' is the approximate scale height of atmosphere in meters.
        */
        const double s = 9.1e3;
        double refr, p, t, h, r;

        /*
           Compute refraction only for zenith distances between 0.1 and
           91 degrees.
        */

        if ((zd_obs < 0.1) || (zd_obs > 91.0))
        {
            refr = 0.0;
        }
        else
        {
            /*
               If observed weather data are available, use them.  Otherwise, use
               crude estimates of average conditions.
            */
            if (!double.IsNaN(pressure) && !double.IsNaN(temp))
            {
                p = pressure;
                t = temp;
            }
            else
            {
                p = 1010.0 * Math.Exp(-elevation / s);
                t = 10.0;
            }

            h = 90.0 - zd_obs;
            r = 0.016667 / Math.Tan((h + 7.31 / (h + 4.4)) * DEG2RAD);
            refr = r * (0.28 * p / (t + 273.0));
        }

        return (refr);
    }

    public static (bool aboveHorizon, IReadOnlyList<TimeSpan> riseEvents, IReadOnlyList<TimeSpan> setEvents) RiseSetEventTimes(
        EventType eventType,
        double utc1,
        double utc2,
        double tt1,
        double tt2,
        double lat,
        double @long,
        double siteElevation,
        double pressure,
        double temp
    )
    {
        var (celestialBody, bodyRadius) = eventType.CelestialBodyAndRadius();

        bool doesRise = false, doesSet = false, aboveHorizon = false;
        List<TimeSpan> bodyRises = new(2), bodySets = new(2);
        var refractionCorrection = Refract(siteElevation, 90.0, pressure, temp);

        // Iterate over the day in two hour periods

        // Start at 01:00 as the centre time i.e. then time range will be 00:00 to 02:00
        var centreTime = 1.0d;

        do
        {
            // Calculate body positional information
            var (altitiudeMinus1, distMinus1) = BodyAltitudeAndDistanceKM(centreTime - 1d);
            var (altitiude0, distAlt0) = BodyAltitudeAndDistanceKM(centreTime);
            var (altitiudePlus1, distPlus1) = BodyAltitudeAndDistanceKM(centreTime + 1d);

            // Correct alititude for body's apparent size, parallax, required distance below horizon and refraction
            switch (eventType)
            {
                case EventType.MoonRiseMoonSet:
                    altitiudeMinus1 = altitiudeMinus1 - (EARTH_RADIUS * RAD2DEG / distMinus1) + (bodyRadius * RAD2DEG / distMinus1) + refractionCorrection;
                    altitiude0 = altitiude0 - (EARTH_RADIUS * RAD2DEG / distAlt0) + (bodyRadius * RAD2DEG / distAlt0) + refractionCorrection;
                    altitiudePlus1 = altitiudePlus1 - (EARTH_RADIUS * RAD2DEG / distPlus1) + (bodyRadius * RAD2DEG / distPlus1) + refractionCorrection;
                    break;

                case EventType.SunRiseSunset:

                    altitiudeMinus1 -= SUN_RISE;
                    altitiude0 -= SUN_RISE;
                    altitiudePlus1 -= SUN_RISE;
                    break;

                case EventType.CivilTwilight:
                    altitiudeMinus1 -= CIVIL_TWILIGHT;
                    altitiude0 -= CIVIL_TWILIGHT;
                    altitiudePlus1 -= CIVIL_TWILIGHT;
                    break;

                case EventType.NauticalTwilight:
                    altitiudeMinus1 -= NAUTICAL_TWILIGHT;
                    altitiude0 -= NAUTICAL_TWILIGHT;
                    altitiudePlus1 -= NAUTICAL_TWILIGHT;
                    break;

                case EventType.AmateurAstronomicalTwilight:
                    altitiudeMinus1 -= AMATEUR_ASRONOMICAL_TWILIGHT;
                    altitiude0 = AMATEUR_ASRONOMICAL_TWILIGHT;
                    altitiudePlus1 -= AMATEUR_ASRONOMICAL_TWILIGHT;
                    break;

                case EventType.AstronomicalTwilight:
                    altitiudeMinus1 -= ASTRONOMICAL_TWILIGHT;
                    altitiude0 -= ASTRONOMICAL_TWILIGHT;
                    altitiudePlus1 -= ASTRONOMICAL_TWILIGHT; // Planets so correct for radius of plant and refraction
                    break;

                default: // Planets so correct for radius of plant and refraction
                    altitiudeMinus1 = altitiudeMinus1 + RAD2DEG * bodyRadius / distMinus1 + refractionCorrection;
                    altitiude0 = altitiude0 + RAD2DEG * bodyRadius / distAlt0 + refractionCorrection;
                    altitiudePlus1 = altitiudePlus1 + RAD2DEG * bodyRadius / distPlus1 + refractionCorrection;
                    break;
            }

            if (centreTime == 1.0d)
            {
                aboveHorizon = altitiudeMinus1 >= 0d;
            }

            // Assess quadratic equation
            var c = altitiude0;
            var b = 0.5d * (altitiudePlus1 - altitiudeMinus1);
            var a = 0.5d * (altitiudePlus1 + altitiudeMinus1) - altitiude0;

            var xSymmetry = -b / (2.0d * a);
            // yExtreme = (a * xSymmetry + b) * xSymmetry + c;
            var discriminant = b * b - 4.0d * a * c;

            var zero1 = double.NaN;
            var zero2 = double.NaN;
            var nZeros = 0;

            if (discriminant > 0.0d)                 // there are zeros
            {
                var deltaX = 0.5d * Math.Sqrt(discriminant) / Math.Abs(a);
                zero1 = xSymmetry - deltaX;
                zero2 = xSymmetry + deltaX;
                if (Math.Abs(zero1) <= 1.0d)
                {
                    nZeros++; // This zero is in interval
                }
                if (Math.Abs(zero2) <= 1.0d)
                {
                    nZeros++; // This zero is in interval
                }

                if (zero1 < -1.0d)
                {
                    zero1 = zero2;
                }
            }

            switch (nZeros)
            {
                // cases depend on values of discriminant - inner part of STEP 4
                case 0: // nothing  - go to next time slot
                    {
                        break;
                    }
                case 1: // simple rise / set event
                    {
                        if (altitiudeMinus1 < 0.0d) // The body is set at start of event so this must be a rising event
                        {
                            doesRise = true;
                            bodyRises.Add(TimeSpan.FromHours(centreTime + zero1));
                        }
                        else // must be setting
                        {
                            doesSet = true;
                            bodySets.Add(TimeSpan.FromHours(centreTime + zero1));
                        }

                        break;
                    }
                case 2: // rises and sets within interval
                    {
                        if (altitiudeMinus1 < 0.0d) // The body is set at start of event so it must rise first then set
                        {
                            bodyRises.Add(TimeSpan.FromHours(centreTime + zero1));
                            bodySets.Add(TimeSpan.FromHours(centreTime + zero2));
                        }
                        else // The body is risen at the start of the event so it must set first then rise
                        {
                            bodyRises.Add(TimeSpan.FromHours(centreTime + zero2));
                            bodySets.Add(TimeSpan.FromHours(centreTime + zero1));
                        }
                        doesRise = true;
                        doesSet = true;
                        break;
                    }
                    // Zero2 = 1
            }
            centreTime += 2.0d; // Increment by 2 hours to get the next 2 hour slot in the day
        }
        while (!((doesRise && doesSet && Math.Abs(lat) < 60.0d) || centreTime == 25.0d));

        return (aboveHorizon, bodyRises, bodySets);

        (double alt, double dist) BodyAltitudeAndDistanceKM(double hour)
        {
            var utc2_hrs = utc2 + (hour / 24.0);
            VSOP87a.Reduce(celestialBody, utc1, utc2_hrs, tt1, tt2 + (hour / 24.0), lat, @long, out _, out _, out _, out var alt, out var dist);
            return (alt, dist * 0.001);
        }
    }

    public static (double raTop, double decTop, double az, double alt) J2000ToTopo(double ra, double dec, double utc1, double utc2, double siteLat, double siteLong, double siteElevation, double sitePressure = double.NaN, double siteTemp = double.NaN)
    {
        double dut1;
        double aob = default, zob = default, hob = default, dob = default, rob = default, eo = default;

        dut1 = LeapSecondsTable.DeltaTCalc(utc1 + utc2);

        if (!double.IsNaN(sitePressure) && !double.IsNaN(siteTemp)) // Include refraction
        {
            _ = wwaAtco13(ra * HOURS2RADIANS, dec * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, utc1, utc2, dut1, siteLong * DEGREES2RADIANS, siteLat * DEGREES2RADIANS, siteElevation, 0.0d, 0.0d, sitePressure, siteTemp, 0.8d, 0.57d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
        }
        else // No refraction
        {
            _ = wwaAtco13(ra * HOURS2RADIANS, dec * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, utc1, utc2, dut1, siteLong * DEGREES2RADIANS, siteLat * DEGREES2RADIANS, siteElevation, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
        }

        return (
            wwaAnp(rob - eo) * RADIANS2HOURS, // // Convert CIO RA to equinox of date RA by subtracting the equation of the origins and convert from radians to hours
            dob * RADIANS2DEGREES, // Convert Dec from radians to degrees
            aob * RADIANS2DEGREES,
            90.0d - zob * RADIANS2DEGREES
        );
    }
}
