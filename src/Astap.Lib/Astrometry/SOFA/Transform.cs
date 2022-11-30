using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.VSOP87;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using static Astap.Lib.Astrometry.SOFA.Constants;
using static WorldWideAstronomy.WWA;

namespace Astap.Lib.Astrometry.SOFA
{
    /// <summary>
    /// Coordinate transform component; J2000 - apparent - topocentric
    /// </summary>
    /// <remarks>Use this component to transform between J2000, apparent and topocentric (JNow) coordinates or
    /// vice versa. To use the component, instantiate it, then use one of SetJ2000 or SetJNow or SetApparent to
    /// initialise with known values. Now use the RAJ2000, DECJ200, RAJNow, DECJNow, RAApparent and DECApparent etc.
    /// properties to read off the required transformed values.
    /// <para>The component can be reused simply by setting new co-ordinates with a Set command, there
    /// is no need to create a new component each time a transform is required.</para>
    /// <para>Transforms are effected through the ASCOM NOVAS.Net engine that encapsulates the USNO NOVAS 3.1 library.
    /// The USNO NOVAS reference web page is:
    /// <href>http://www.usno.navy.mil/USNO/astronomical-applications/software-products/novas</href>
    /// and the NOVAS 3.1 user guide is included in the ASCOM Developer Components install.
    /// </para>
    /// </remarks>
    public sealed class Transform
    {
        private double _RAJ2000Value, _RATopoValue, _DECJ2000Value, _DECTopoValue;
        private double _SiteElevValue, _SiteLatValue, _SiteLongValue;
        private TimeSpan _SiteTimeZoneValue;
        private double _SiteTempValue, _SitePressureValue;
        private double _RAApparentValue, _DECApparentValue, _AzimuthTopoValue, _ElevationTopoValue;
        private double _jdTTValue1, _jdTTValue2, _jdUTCValue1, _jdUTCValue2;
        private bool _RefracValue, _RequiresRecalculate;
        private SetBy LastSetBy { get; set; }

        private enum SetBy
        {
            Never,
            J2000,
            Apparent,
            Topocentric,
            AzimuthElevation,
            Refresh
        }

        public Transform()
        {
            // Initialise to invalid values in case these are read before they are set
            _RAJ2000Value = double.NaN;
            _DECJ2000Value = double.NaN;
            _RATopoValue = double.NaN;
            _DECTopoValue = double.NaN;
            _SiteElevValue = double.NaN;
            _SiteLatValue = double.NaN;
            _SiteLongValue = double.NaN;
            _SiteTimeZoneValue = TimeSpan.MinValue;
            _SitePressureValue = double.NaN;

            _RefracValue = false;
            _RequiresRecalculate = true;
            // Initialise to a value that forces the current PC date time to be used in determining the TT Julian date of interest
            _jdUTCValue1 = 0d;
            _jdUTCValue2 = 0d;
            LastSetBy = SetBy.Never;
        }

        #region EventTimes Astroutils implemtation
        /// <summary>
        ///
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="dto"></param>
        /// <param name="siteLatitude"></param>
        /// <param name="siteLongitude"></param>
        /// <returns></returns>
        public (bool aboveHorizon, IReadOnlyList<TimeSpan> riseEvents, IReadOnlyList<TimeSpan> setEvents) EventTimes(EventType eventType)
        {
            var celestialBody = eventType.CelestialBody();

            bool doesRise, doesSet, aboveHorizon = default;
            double centreTime, altitiudeMinus1, altitiude0, altitiudePlus1, a, b, c, xSymmetry, yExtreme, discriminant;
            double deltaX, zero1, zero2;
            int nZeros;
            List<TimeSpan> bodyRises = new(2), bodySets = new(2);

            doesRise = false;
            doesSet = false;

            InitFromUtcNowIfRequired();

            // Iterate over the day in two hour periods

            // Start at 01:00 as the centre time i.e. then time range will be 00:00 to 02:00
            centreTime = 1.0d;

            do
            {
                // Calculate body positional information
                altitiudeMinus1 = BodyAltitude(centreTime - 1d);
                altitiude0 = BodyAltitude(centreTime);
                altitiudePlus1 = BodyAltitude(centreTime + 1d);

                // Correct alititude for body's apparent size, parallax, required distance below horizon and refraction
                switch (eventType)
                {
                    case EventType.SunRiseSunset:
                        {
                            altitiudeMinus1 -= - SUN_RISE;
                            altitiude0 -= SUN_RISE;
                            altitiudePlus1 -= SUN_RISE;
                            break;
                        }
                    case EventType.CivilTwilight:
                        {
                            altitiudeMinus1 -= CIVIL_TWILIGHT;
                            altitiude0 -= CIVIL_TWILIGHT;
                            altitiudePlus1 -= CIVIL_TWILIGHT;
                            break;
                        }
                    case EventType.NauticalTwilight:
                        {
                            altitiudeMinus1 -= NAUTICAL_TWILIGHT;
                            altitiude0 -= NAUTICAL_TWILIGHT;
                            altitiudePlus1 -= NAUTICAL_TWILIGHT;
                            break;
                        }
                    case EventType.AmateurAstronomicalTwilight:
                        {
                            altitiudeMinus1 -= AMATEUR_ASRONOMICAL_TWILIGHT;
                            altitiude0 = AMATEUR_ASRONOMICAL_TWILIGHT;
                            altitiudePlus1 -= AMATEUR_ASRONOMICAL_TWILIGHT;
                            break;
                        }
                    case EventType.AstronomicalTwilight:
                        {
                            altitiudeMinus1 -= ASTRONOMICAL_TWILIGHT;
                            altitiude0 -= ASTRONOMICAL_TWILIGHT;
                            altitiudePlus1 -= ASTRONOMICAL_TWILIGHT; // Planets so correct for radius of plant and refraction
                            break;
                        }
                }

                if (centreTime == 1.0d)
                {
                    if (altitiudeMinus1 < 0d)
                    {
                        aboveHorizon = false;
                    }
                    else
                    {
                        aboveHorizon = true;
                    }
                }

                // Assess quadratic equation
                c = altitiude0;
                b = 0.5d * (altitiudePlus1 - altitiudeMinus1);
                a = 0.5d * (altitiudePlus1 + altitiudeMinus1) - altitiude0;

                xSymmetry = -b / (2.0d * a);
                yExtreme = (a * xSymmetry + b) * xSymmetry + c;
                discriminant = b * b - 4.0d * a * c;

                deltaX = double.NaN;
                zero1 = double.NaN;
                zero2 = double.NaN;
                nZeros = 0;

                if (discriminant > 0.0d)                 // there are zeros
                {
                    deltaX = 0.5d * Math.Sqrt(discriminant) / Math.Abs(a);
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
                    case 1:                      // simple rise / set event
                        {
                            if (altitiudeMinus1 < 0.0d)       // The body is set at start of event so this must be a rising event
                            {
                                doesRise = true;
                                bodyRises.Add(TimeSpan.FromHours(centreTime + zero1));
                            }
                            else                    // must be setting
                            {
                                doesSet = true;
                                bodySets.Add(TimeSpan.FromHours(centreTime + zero1));
                            }

                            break;
                        }
                    case 2:                      // rises and sets within interval
                        {
                            if (altitiudeMinus1 < 0.0d) // The body is set at start of event so it must rise first then set
                            {
                                bodyRises.Add(TimeSpan.FromHours(centreTime + zero1));
                                bodySets.Add(TimeSpan.FromHours(centreTime + zero2));
                            }
                            else                    // The body is risen at the start of the event so it must set first then rise
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
            while (!(doesRise && doesSet && Math.Abs(SiteLatitude) < 60.0d || centreTime == 25.0d));

            return (aboveHorizon, bodyRises, bodySets);

            double BodyAltitude(double hour)
            {
                VSOP87a.Reduce(celestialBody, _jdUTCValue1, _jdUTCValue2 + (hour / 24.0), _jdTTValue1, _jdTTValue2 + (hour / 24.0), SiteLatitude, SiteLongitude, out _, out _, out _, out var alt);
                return alt;
            }
        }
        #endregion

        #region ITransform Implementation
        /// <summary>
        /// Gets or sets the site latitude
        /// </summary>
        /// <value>Site latitude (-90.0 to +90.0)</value>
        /// <returns>Latitude in degrees</returns>
        /// <remarks>Positive numbers north of the equator, negative numbers south.</remarks>
        public double SiteLatitude
        {
            get
            {
                CheckSet(_SiteLatValue, "Site latitude has not been set");
                return _SiteLatValue;
            }
            set
            {
                if (value < -90.0d | value > 90.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"{value} should be within -90.0 degrees and +90.0 degrees");
                }
                if (_SiteLatValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _SiteLatValue = value;
            }
        }

        /// <summary>
        /// Gets or sets the site longitude
        /// </summary>
        /// <value>Site longitude (-180.0 to +180.0)</value>
        /// <returns>Longitude in degrees</returns>
        /// <remarks>Positive numbers east of the Greenwich meridian, negative numbers west of the Greenwich meridian.</remarks>
        public double SiteLongitude
        {
            get
            {
                CheckSet(_SiteLongValue, "Site longitude has not been set");
                return _SiteLongValue;
            }
            set
            {
                if (value < -180.0d | value > 180.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -180.0 degrees to +180.0 degrees");
                }
                if (_SiteLongValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _SiteLongValue = value;
            }
        }

        /// <summary>
        /// Gets or sets the site elevation above sea level
        /// </summary>
        /// <value>Site elevation (-300.0 to +10,000.0 metres)</value>
        /// <returns>Elevation in metres</returns>
        /// <remarks></remarks>
        public double SiteElevation
        {
            get
            {
                CheckSet(_SiteElevValue, "Site elevation has not been set");
                return _SiteElevValue;
            }
            set
            {
                if (value < -300.0d | value > 10000.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -300.0 metres to +10000.0 metres");
                }
                if (_SiteElevValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _SiteElevValue = value;
            }
        }

        /// <summary>
        /// Gets or sets the site ambient temperature (not reduced to sea level)
        /// </summary>
        /// <value>Site ambient temperature (-273.15 to 100.0 Celsius)</value>
        /// <returns>Temperature in degrees Celsius</returns>
        /// <remarks>This property represents the air temperature as measured by a thermometer at the observing site. It must not be a "reduced to sea level" value.</remarks>
        public double SiteTemperature
        {
            get
            {
                CheckSet(_SiteTempValue, "Site temperature has not been set");
                return _SiteTempValue;
            }
            set
            {
                if (value < -273.15d | value > 100.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -273.15 Celsius to +100.0 Celsius");
                }
                if (_SiteTempValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _SiteTempValue = value;
            }
        }

        /// <summary>
        /// Gets or sets the site atmospheric pressure (not reduced to sea level)
        /// </summary>
        /// <value>Site atmospheric pressure (0.0 to 1200.0 hPa (mbar))</value>
        /// <returns>Atmospheric pressure (hPa)</returns>
        /// <remarks>This property represents the atmospheric pressure as measured by a barometer at the observing site. It must not be a "reduced to sea level" value.</remarks>
        public double SitePressure
        {
            get
            {
                CheckSet(_SitePressureValue, "Site atmospheric pressure has not been set");
                return _SitePressureValue;
            }
            set
            {
                if (value < 0.0d | value > 1200.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from 0.0hPa (mbar) to +1200.0hPa (mbar)");
                }
                if (_SitePressureValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _SitePressureValue = value;
            }
        }

        /// <summary>
        /// Gets or sets a flag indicating whether refraction is calculated for topocentric co-ordinates
        /// </summary>
        /// <value>True / false flag indicating refraction is included / omitted from topocentric co-ordinates</value>
        /// <returns>Boolean flag</returns>
        /// <remarks></remarks>
        public bool Refraction
        {
            get
            {
                return _RefracValue;
            }
            set
            {
                if (_RefracValue != value)
                {
                    _RequiresRecalculate = true;
                }
                _RefracValue = value;
            }
        }

        /// <summary>
        /// Causes the transform component to recalculate values derived from the last Set command
        /// </summary>
        /// <remarks>Use this when you have set J2000 co-ordinates and wish to ensure that the mount points to the same
        /// co-ordinates allowing for local effects that change with time such as refraction.
        /// <para><b style="color:red">Note:</b> As of Platform 6 SP2 use of this method is not required, refresh is always performed automatically when required.</para></remarks>
        public void Refresh()
        {
            Recalculate();
        }

        /// <summary>
        /// Sets the known J2000 Right Ascension and Declination coordinates that are to be transformed
        /// </summary>
        /// <param name="ra">RA in J2000 co-ordinates (0.0 to 23.999 hours)</param>
        /// <param name="dec">DEC in J2000 co-ordinates (-90.0 to +90.0)</param>
        /// <remarks></remarks>
        public void SetJ2000(double ra, double dec)
        {

            if (ra != _RAJ2000Value | dec != _DECJ2000Value)
            {
                _RAJ2000Value = ValidateRA(ra);
                _DECJ2000Value = ValidateDec(dec);
                _RequiresRecalculate = true;
            }

            LastSetBy = SetBy.J2000;
        }

        /// <summary>
        /// Sets the known apparent Right Ascension and Declination coordinates that are to be transformed
        /// </summary>
        /// <param name="RA">RA in apparent co-ordinates (0.0 to 23.999 hours)</param>
        /// <param name="DEC">DEC in apparent co-ordinates (-90.0 to +90.0)</param>
        /// <remarks></remarks>
        public void SetApparent(double RA, double DEC)
        {

            if (RA != _RAApparentValue | DEC != _DECApparentValue)
            {
                _RAApparentValue = ValidateRA(RA);
                _DECApparentValue = ValidateDec(DEC);
                _RequiresRecalculate = true;
            }

            LastSetBy = SetBy.Apparent;
        }

        /// <summary>
        /// Sets the known topocentric Right Ascension and Declination coordinates that are to be transformed
        /// </summary>
        /// <param name="RA">RA in topocentric co-ordinates (0.0 to 23.999 hours)</param>
        /// <param name="DEC">DEC in topocentric co-ordinates (-90.0 to +90.0)</param>
        /// <remarks></remarks>
        public void SetTopocentric(double RA, double DEC)
        {

            if (RA != _RATopoValue | DEC != _DECTopoValue)
            {
                _RATopoValue = ValidateRA(RA);
                _DECTopoValue = ValidateDec(DEC);
                _RequiresRecalculate = true;
            }

            LastSetBy = SetBy.Topocentric;
        }

        /// <summary>
        /// Sets the topocentric azimuth and elevation
        /// </summary>
        /// <param name="Azimuth">Topocentric Azimuth in degrees (0.0 to 359.999999 - north zero, east 90 deg etc.)</param>
        /// <param name="Elevation">Topocentric elevation in degrees (-90.0 to +90.0)</param>
        /// <remarks></remarks>
        public void SetAzimuthElevation(double Azimuth, double Elevation)
        {

            if (Azimuth < 0.0d | Azimuth >= 360.0d)
            {
                throw new ArgumentOutOfRangeException(nameof(Azimuth), Azimuth, "Valid range from 0.0 hours to 23.9999999... hours");
            }
            if (Elevation < -90.0d | Elevation > 90.0d)
            {
                throw new ArgumentOutOfRangeException(nameof(Elevation), Elevation, "Valid range from -90.0 degrees to +90.0 degrees");
            }

            _AzimuthTopoValue = Azimuth;
            _ElevationTopoValue = Elevation;
            _RequiresRecalculate = true;

            LastSetBy = SetBy.AzimuthElevation;
        }

        /// <summary>
        /// Returns the Right Ascension in J2000 co-ordinates
        /// </summary>
        /// <value>J2000 Right Ascension</value>
        /// <returns>Right Ascension in hours</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double RAJ2000
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read RAJ2000 before a SetXX method has been called");
                }
                Recalculate();
                CheckSet(_RAJ2000Value, "RA J2000 can not be derived from the information provided. Are site parameters set?");
                return _RAJ2000Value;
            }
        }

        /// <summary>
        /// Returns the Declination in J2000 co-ordinates
        /// </summary>
        /// <value>J2000 Declination</value>
        /// <returns>Declination in degrees</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double DecJ2000
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read DECJ2000 before a SetXX method has been called");
                }
                Recalculate();
                CheckSet(_DECJ2000Value, "DEC J2000 can not be derived from the information provided. Are site parameters set?");
                return _DECJ2000Value;
            }
        }

        /// <summary>
        /// Returns the Right Ascension in topocentric co-ordinates
        /// </summary>
        /// <value>Topocentric Right Ascension</value>
        /// <returns>Topocentric Right Ascension in hours</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double RATopocentric
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read RATopocentric before a SetXX method  has been called");
                }
                Recalculate();
                CheckSet(_RATopoValue, "RA topocentric can not be derived from the information provided. Are site parameters set?");
                return _RATopoValue;
            }
        }

        /// <summary>
        /// Returns the Declination in topocentric co-ordinates
        /// </summary>
        /// <value>Topocentric Declination</value>
        /// <returns>Declination in degrees</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double DECTopocentric
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read DECTopocentric before a SetXX method has been called");
                }
                Recalculate();
                CheckSet(_DECTopoValue, "DEC topocentric can not be derived from the information provided. Are site parameters set?");
                return _DECTopoValue;
            }
        }

        /// <summary>
        /// Returns the Right Ascension in apparent co-ordinates
        /// </summary>
        /// <value>Apparent Right Ascension</value>
        /// <returns>Right Ascension in hours</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double RAApparent
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read DECApparent before a SetXX method has been called");
                }
                Recalculate();
                return _RAApparentValue;
            }
        }

        /// <summary>
        /// Returns the Declination in apparent co-ordinates
        /// </summary>
        /// <value>Apparent Declination</value>
        /// <returns>Declination in degrees</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double DECApparent
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read DECApparent before a SetXX method has been called");
                }
                Recalculate();
                return _DECApparentValue;
            }
        }

        /// <summary>
        /// Returns the topocentric azimuth angle of the target
        /// </summary>
        /// <value>Topocentric azimuth angle</value>
        /// <returns>Azimuth angle in degrees</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double AzimuthTopocentric
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read AzimuthTopocentric before a SetXX method has been called");
                }
                _RequiresRecalculate = true; // Force a recalculation of Azimuth
                Recalculate();
                CheckSet(_AzimuthTopoValue, "Azimuth topocentric can not be derived from the information provided. Are site parameters set?");
                return _AzimuthTopoValue;
            }
        }

        /// <summary>
        /// Returns the topocentric elevation of the target
        /// </summary>
        /// <value>Topocentric elevation angle</value>
        /// <returns>Elevation angle in degrees</returns>
        /// <exception cref="InvalidOperationException">Exception thrown if an attempt is made
        /// to read a value before any of the Set methods has been used or if the value can not be derived from the
        /// information in the last Set method used. E.g. topocentric values will be unavailable if the last Set was
        /// a SetApparent and one of the Site properties has not been set.</exception>
        /// <remarks></remarks>
        public double ElevationTopocentric
        {
            get
            {
                if (LastSetBy == SetBy.Never)
                {
                    throw new InvalidOperationException("Attempt to read ElevationTopocentric before a SetXX method has been called");
                }
                _RequiresRecalculate = true; // Force a recalculation of Elevation
                Recalculate();
                CheckSet(_ElevationTopoValue, "Elevation topocentric can not be derived from the information provided. Are site parameters set?");
                return _ElevationTopoValue;
            }
        }

        /// <summary>
        /// Sets or returns the Julian date on the Terrestrial Time timescale for which the transform will be made
        /// </summary>
        /// <value>Julian date (Terrestrial Time) of the transform (1757583.5 to 5373484.499999 = 00:00:00 1/1/0100 to 23:59:59.999 31/12/9999)</value>
        /// <returns>Terrestrial Time Julian date that will be used by Transform or zero if the PC's current clock value will be used to calculate the Julian date.</returns>
        /// <remarks>This method was introduced in May 2012. Previously, Transform used the current date-time of the PC when calculating transforms;
        /// this remains the default behaviour for backward compatibility.
        /// The initial value of this parameter is 0.0, which is a special value that forces Transform to replicate original behaviour by determining the
        /// Julian date from the PC's current date and time. If this property is non zero, that particular terrestrial time Julian date is used in preference
        /// to the value derived from the PC's clock.
        /// <para>Only one of JulianDateTT or JulianDateUTC needs to be set. Use whichever is more readily available, there is no
        /// need to set both values. Transform will use the last set value of either JulianDateTT or JulianDateUTC as the basis for its calculations.</para></remarks>
        public double JulianDateTT
        {
            get
            {
                return _jdTTValue1 + _jdTTValue2;
            }
            set
            {
                double tai1 = default, tai2 = default;

                // Validate the supplied value, it must be 0.0 or within the permitted range
                if (value != 0.0d & (value < JULIAN_DATE_MINIMUM_VALUE | value > JULIAN_DATE_MAXIMUM_VALUE))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"Range from {JULIAN_DATE_MINIMUM_VALUE} to {JULIAN_DATE_MAXIMUM_VALUE}");
                }

                _jdTTValue1 = value;
                _jdTTValue2 = 0.0d;
                _RequiresRecalculate = true; // Force a recalculation because the Julian date has changed

                if (_jdTTValue1 != 0.0d)
                {
                    // Calculate UTC
                    _ = wwaTttai(_jdTTValue1, _jdTTValue2, ref tai1, ref tai2);
                    _ = wwaTaiutc(tai1, tai2, ref _jdUTCValue1, ref _jdUTCValue2);
                }
                else // Handle special case of 0.0
                {
                    _jdUTCValue1 = 0.0d;
                    _jdUTCValue2 = 0.0d;
                }
            }
        }

        /// <summary>
        /// Sets or returns the Julian date on the UTC timescale for which the transform will be made
        /// </summary>
        /// <value>Julian date (UTC) of the transform (1757583.5 to 5373484.499999 = 00:00:00 1/1/0100 to 23:59:59.999 31/12/9999)</value>
        /// <returns>UTC Julian date that will be used by Transform or zero if the PC's current clock value will be used to calculate the Julian date.</returns>
        /// <remarks>Introduced in April 2014 as an alternative to JulianDateTT. Only one of JulianDateTT or JulianDateUTC needs to be set. Use whichever is more readily available, there is no
        /// need to set both values. Transform will use the last set value of either JulianDateTT or JulianDateUTC as the basis for its calculations.</remarks>
        public double JulianDateUTC
        {
            get
            {
                return _jdUTCValue1 + _jdUTCValue2;
            }
            set
            {
                double tai1 = default, tai2 = default;

                // Validate the supplied value, it must be 0.0 or within the permitted range
                if (value != 0.0d & (value < JULIAN_DATE_MINIMUM_VALUE | value > JULIAN_DATE_MAXIMUM_VALUE))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"Range from {JULIAN_DATE_MINIMUM_VALUE} to {JULIAN_DATE_MAXIMUM_VALUE}");
                }

                _jdUTCValue1 = value;
                _jdUTCValue2 = 0.0;
                _RequiresRecalculate = true; // Force a recalculation because the Julian date has changed

                if (_jdUTCValue1 != 0.0d)
                {
                    // Calculate Terrestrial Time equivalent
                    _ = wwaUtctai(_jdUTCValue1, _jdUTCValue2, ref tai1, ref tai2);
                    _ = wwaTaitt(tai1, tai2, ref _jdTTValue1, ref _jdTTValue2);
                }
                else // Handle special case of 0.0
                {
                    _jdTTValue1 = 0.0d;
                    _jdTTValue2 = 0.0d;
                }
            }
        }

        /// <summary>
        /// Initialises time via <see cref="TimeUtils.ToSOFAUtcJdTT(DateTimeOffset, out double, out double, out double, out double)"/>.
        /// </summary>
        public DateTimeOffset DateTimeOffset
        {
            set
            {
                value.ToSOFAUtcJdTT(out _jdUTCValue1, out _jdUTCValue2, out _jdTTValue1, out _jdTTValue2);
                _SiteTimeZoneValue = value.Offset;
            }
        }

        /// <summary>
        /// Timezone offset, will <see cref="TimeSpan.MinValue"/> if not provided via <see cref="DateTimeOffset"/>.
        /// Is used to calculate <see cref="EventTimes(EventType)"/>.
        /// </summary>
        public TimeSpan SiteTimeZone
        {
            get => _SiteTimeZoneValue;
            set => _SiteTimeZoneValue = value;
        }

        #endregion

        #region Support Code

        private static void CheckSet(double Value, string ErrMsg)
        {
            if (double.IsNaN(Value))
            {
                throw new InvalidOperationException(ErrMsg);
            }
        }

        private void J2000ToTopo()
        {
            double dut1;
            double aob = default, zob = default, hob = default, dob = default, rob = default, eo = default;

            CheckSet(_SiteElevValue, "Site elevation has not been set");
            CheckSet(_SiteLatValue, "Site latitude has not been set");
            CheckSet(_SiteLongValue, "Site longitude has not been set");
            CheckSet(_SiteTempValue, "Site temperature has not been set");

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();
            InitFromUtcNowIfRequired();

            dut1 = LeapSecondsTable.DeltaTCalc(_jdUTCValue1 + _jdUTCValue2);

            if (_RefracValue) // Include refraction
            {
                _ = wwaAtco13(_RAJ2000Value * HOURS2RADIANS, _DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, _SitePressureValue, _SiteTempValue, 0.8d, 0.57d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }
            else // No refraction
            {
                _ = wwaAtco13(_RAJ2000Value * HOURS2RADIANS, _DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }

            _RATopoValue = wwaAnp(rob - eo) * RADIANS2HOURS; // // Convert CIO RA to equinox of date RA by subtracting the equation of the origins and convert from radians to hours
            _DECTopoValue = dob * RADIANS2DEGREES; // Convert Dec from radians to degrees
            _AzimuthTopoValue = aob * RADIANS2DEGREES;
            _ElevationTopoValue = 90.0d - zob * RADIANS2DEGREES;
        }

        private void J2000ToApparent()
        {
            double ri = default, di = default, eo = default;
            InitFromUtcNowIfRequired();

            wwaAtci13(_RAJ2000Value * HOURS2RADIANS, _DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, _jdTTValue1, _jdTTValue2, ref ri, ref di, ref eo);
            _RAApparentValue = wwaAnp(ri - eo) * RADIANS2HOURS; // // Convert CIO RA to equinox of date RA by subtracting the equation of the origins and convert from radians to hours
            _DECApparentValue = di * RADIANS2DEGREES; // Convert Dec from radians to degrees
        }

        private void TopoToJ2000()
        {
            double raCelestrial = default, decCelestial = default, dut1;
            double aob = default, zob = default, hob = default, dob = default, rob = default, eo = default;

            if (double.IsNaN(_SiteElevValue))
            {
                throw new InvalidOperationException("Site elevation has not been set");
            }
            if (double.IsNaN(_SiteLatValue))
            {
                throw new InvalidOperationException("Site latitude has not been set");
            }
            if (double.IsNaN(_SiteLongValue))
            {
                throw new InvalidOperationException("Site longitude has not been set");
            }
            if (double.IsNaN(_SiteTempValue))
            {
                throw new InvalidOperationException("Site temperature has not been set");
            }

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();
            InitFromUtcNowIfRequired();

            dut1 = LeapSecondsTable.DeltaTCalc(_jdUTCValue1 + _jdUTCValue2);

            var type = 'R';
            var ob1 = wwaAnp(Math.FusedMultiplyAdd(_RATopoValue, HOURS2RADIANS, wwaEo06a(_jdTTValue1, _jdTTValue2)));
            if (_RefracValue) // Refraction is required
            {
                _ = wwaAtoc13(ref type, ob1, _DECTopoValue * DEGREES2RADIANS, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, _SitePressureValue, _SiteTempValue, 0.85d, 0.57d, ref raCelestrial, ref decCelestial);
            }
            else
            {
                _ = wwaAtoc13(ref type, ob1, _DECTopoValue * DEGREES2RADIANS, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref raCelestrial, ref decCelestial);
            }

            _RAJ2000Value = raCelestrial * RADIANS2HOURS;
            _DECJ2000Value = decCelestial * RADIANS2DEGREES;

            // Now calculate the corresponding AzEl values from the J2000 values
            if (_RefracValue) // Include refraction
            {
                _ = wwaAtco13(_RAJ2000Value * HOURS2RADIANS, _DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, _SitePressureValue, _SiteTempValue, 0.8d, 0.57d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }
            else // No refraction
            {
                _ = wwaAtco13(_RAJ2000Value * HOURS2RADIANS, _DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }

            _AzimuthTopoValue = aob * RADIANS2DEGREES;
            _ElevationTopoValue = 90.0d - zob * RADIANS2DEGREES;
        }

        private void ApparentToJ2000()
        {
            double raCelestial = default, decCelestial = default, eo = default;
            InitFromUtcNowIfRequired();

            var ri = wwaAnp(Math.FusedMultiplyAdd(_RAApparentValue, HOURS2RADIANS, wwaEo06a(_jdUTCValue1, _jdUTCValue1)));
            wwaAtic13(ri, _DECApparentValue * DEGREES2RADIANS, _jdTTValue1, _jdTTValue2, ref raCelestial, ref decCelestial, ref eo);
            _RAJ2000Value = raCelestial * RADIANS2HOURS;
            _DECJ2000Value = decCelestial * RADIANS2DEGREES;
        }

        private void Recalculate() // Calculate values for derived co-ordinates
        {
            if (_RequiresRecalculate | _RefracValue == true)
            {
                switch (LastSetBy)
                {
                    case SetBy.J2000: // J2000 coordinates have bee set so calculate apparent and topocentric coordinates
                        {
                            // Check whether required topo values have been set
                            if (!double.IsNaN(_SiteLatValue) & !double.IsNaN(_SiteLongValue) & !double.IsNaN(_SiteElevValue) & !double.IsNaN(_SiteTempValue))
                            {
                                J2000ToTopo(); // All required site values present so calculate Topo values
                            }
                            else // Set to NaN
                            {
                                _RATopoValue = double.NaN;
                                _DECTopoValue = double.NaN;
                                _AzimuthTopoValue = double.NaN;
                                _ElevationTopoValue = double.NaN;
                            }
                            J2000ToApparent();
                            break;
                        }
                    case SetBy.Topocentric: // Topocentric co-ordinates have been set so calculate J2000 and apparent coordinates
                        {
                            // Check whether required topo values have been set
                            if (!double.IsNaN(_SiteLatValue) & !double.IsNaN(_SiteLongValue) & !double.IsNaN(_SiteElevValue) & !double.IsNaN(_SiteTempValue)) // They have so calculate remaining values
                            {
                                TopoToJ2000();
                                J2000ToApparent();
                            }
                            else // Set the topo and apparent values to NaN
                            {
                                _RAJ2000Value = double.NaN;
                                _DECJ2000Value = double.NaN;
                                _RAApparentValue = double.NaN;
                                _DECApparentValue = double.NaN;
                                _AzimuthTopoValue = double.NaN;
                                _ElevationTopoValue = double.NaN;
                            }

                            break;
                        }
                    case SetBy.Apparent: // Apparent values have been set so calculate J2000 values and topo values if appropriate
                        {
                            ApparentToJ2000(); // Calculate J2000 value
                                               // Check whether required topo values have been set
                            if (!double.IsNaN(_SiteLatValue) & !double.IsNaN(_SiteLongValue) & !double.IsNaN(_SiteElevValue) & !double.IsNaN(_SiteTempValue))
                            {
                                J2000ToTopo(); // All required site values present so calculate Topo values
                            }
                            else
                            {
                                _RATopoValue = double.NaN;
                                _DECTopoValue = double.NaN;
                                _AzimuthTopoValue = double.NaN;
                                _ElevationTopoValue = double.NaN;
                            }

                            break;
                        }
                    case SetBy.AzimuthElevation:
                        {
                            if (!double.IsNaN(_SiteLatValue) & !double.IsNaN(_SiteLongValue) & !double.IsNaN(_SiteElevValue) & !double.IsNaN(_SiteTempValue))
                            {
                                AzElToJ2000();
                                J2000ToTopo();
                                J2000ToApparent();
                            }
                            else
                            {
                                _RAJ2000Value = double.NaN;
                                _DECJ2000Value = double.NaN;
                                _RAApparentValue = double.NaN;
                                _DECApparentValue = double.NaN;
                                _RATopoValue = double.NaN;
                                _DECTopoValue = double.NaN;
                            } // Neither SetJ2000 nor SetTopocentric nor SetApparent have been called, so throw an exception

                            break;
                        }

                    default:
                        {
                            throw new InvalidOperationException("Can not recalculate Transform object values because neither SetJ2000 nor SetTopocentric nor SetApparent have been called");
                        }
                }
                _RequiresRecalculate = false; // Reset the recalculate flag
            }
        }

        private void AzElToJ2000()
        {
            double raCelestial = default, decCelestial = default, dut1;

            if (double.IsNaN(_SiteElevValue))
            {
                throw new InvalidOperationException("Site elevation has not been set");
            }
            if (double.IsNaN(_SiteLatValue))
            {
                throw new InvalidOperationException("Site latitude has not been set");
            }
            if (double.IsNaN(_SiteLongValue))
            {
                throw new InvalidOperationException("Site longitude has not been set");
            }
            if (double.IsNaN(_SiteTempValue))
            {
                throw new InvalidOperationException("Site temperature has not been set");
            }

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();

            InitFromUtcNowIfRequired();
            dut1 = LeapSecondsTable.DeltaTCalc(_jdUTCValue1 + _jdUTCValue2);


            var type = 'A';
            if (_RefracValue) // Refraction is required
            {
                _ = wwaAtoc13(ref type, _AzimuthTopoValue * DEGREES2RADIANS, (90.0d - _ElevationTopoValue) * DEGREES2RADIANS, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, _SitePressureValue, _SiteTempValue, 0.85d, 0.57d, ref raCelestial, ref decCelestial);
            }
            else
            {
                _ = wwaAtoc13(ref type, _AzimuthTopoValue * DEGREES2RADIANS, (90.0d - _ElevationTopoValue) * DEGREES2RADIANS, _jdUTCValue1, _jdUTCValue2, dut1, _SiteLongValue * DEGREES2RADIANS, _SiteLatValue * DEGREES2RADIANS, _SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref raCelestial, ref decCelestial);
            }

            _RAJ2000Value = raCelestial * RADIANS2HOURS;
            _DECJ2000Value = decCelestial * RADIANS2DEGREES;
        }

        private void InitFromUtcNowIfRequired()
        {
            if (_jdUTCValue1 == 0.0d && _jdTTValue1 == 0.0d) // No specific TT date / time has been set so use the current date / time
            {
                DateTimeOffset = DateTimeOffset.Now;
            }
        }

        private static double ValidateRA(double RA)
        {
            if (RA < 0.0d | RA >= 24.0d)
            {
                throw new ArgumentOutOfRangeException(nameof(RA), RA, "0 to 23.9999");
            }
            return RA;
        }

        private static double ValidateDec(double Dec)
        {
            if (Dec < -90.0d | Dec > 90.0d)
            {
                throw new ArgumentOutOfRangeException(nameof(Dec), Dec, "-90.0 to 90.0");
            }
            return Dec;
        }

        private void CalculateSitePressureIfRequired()
        {
            // Derive the site pressure from the site elevation if the pressure has not been set explicitly
            if (double.IsNaN(_SitePressureValue)) // Site pressure has not been set so derive a value based on the supplied observatory height and temperature
            {
                // phpa = 1013.25 * exp ( −hm / ( 29.3 * tsl ) ); NOTE this equation calculates the site pressure and uses the site temperature REDUCED TO SEA LEVEL MESURED IN DEGREES KELVIN
                // tsl = tSite − 0.0065(0 − hsite);  NOTE this equation reduces the site temperature to sea level
                _SitePressureValue = STANDARD_PRESSURE * Math.Exp(-_SiteElevValue / (29.3d * (_SiteTempValue + 0.0065d * _SiteElevValue - ABSOLUTE_ZERO_CELSIUS)));
            }
        }

        #endregion

        #region Additional functionality
        public double LocalSiderealTime()
        {
            InitFromUtcNowIfRequired();
            return LocalSiderealTime(_jdUTCValue1, _jdUTCValue2, _jdTTValue1, _jdTTValue1, SiteLongitude);
        }

        public double LocalSiderealTime(DateTimeOffset dateTimeOffset) => LocalSiderealTime(dateTimeOffset, SiteLongitude);

        public static double LocalSiderealTime(DateTimeOffset dateTimeOffset, double siteLongitude)
        {
            dateTimeOffset.ToSOFAUtcJdTT(out var utc1, out var utc2, out var tt1, out var tt2);

            return LocalSiderealTime(utc1, utc2, tt1, tt2, siteLongitude);
        }

        public static double LocalSiderealTime(double utc1, double utc2, double tt1, double tt2, double siteLongitude)
        {
            double ut11 = default, ut12 = default;
            var dut1 = LeapSecondsTable.DeltaUT1(utc1 + utc2);
            _ = wwaUtcut1(utc1, utc2, dut1, ref ut11, ref ut12);

            var gmst = wwaGmst00(ut11, ut12, tt1, tt2) * RADIANS2HOURS;

            // Allow for the longitude
            gmst += siteLongitude / 360.0 * 24.0;

            // Reduce to the range 0 to 24 hours
            return CoordinateUtils.ConditionRA(gmst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> CalculateObjElevation(in CelestialObject obj, DateTimeOffset astroDark, DateTimeOffset astroTwilight, double siderealTimeAtAstroDark)
            => CalculateObjElevation(obj.Index, obj.ObjectType, obj.RA, obj.Dec, astroDark, astroTwilight, siderealTimeAtAstroDark);

        public IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> CalculateObjElevation(CatalogIndex idx, ObjectType objectType, double ra, double dec, DateTimeOffset astroDark, DateTimeOffset astroTwilight, double siderealTimeAtAstroDark)
        {
            if (double.IsNaN(ra))
            {
                throw new ArgumentException("Ra should not be NaN", nameof(ra));
            }

            if (double.IsNaN(dec))
            {
                throw new ArgumentException("Dec should not be NaN", nameof(dec));
            }

            SetJ2000(ra, dec);

            var isFixed = idx != CatalogIndex.Sol && objectType != ObjectType.Planet;

            var raDecEventTimes = new Dictionary<RaDecEventTime, RaDecEventInfo>(4);

            var hourAngle = TimeSpan.FromHours(CoordinateUtils.ConditionHA(siderealTimeAtAstroDark - ra));
            var crossMeridianTime = astroDark - hourAngle;

            var darkEvent = raDecEventTimes[RaDecEventTime.AstroDark] = CalcRaDecEventInfo(astroDark);
            var twilightEvent = raDecEventTimes[RaDecEventTime.AstroTwilight] = CalcRaDecEventInfo(astroTwilight);
            var meridianEvent = raDecEventTimes[RaDecEventTime.Meridian] = CalcRaDecEventInfo(crossMeridianTime);

            raDecEventTimes[RaDecEventTime.MeridianL1] = CalcRaDecEventInfo(crossMeridianTime - TimeSpan.FromHours(0.2));
            raDecEventTimes[RaDecEventTime.MeridianL2] = CalcRaDecEventInfo(crossMeridianTime - TimeSpan.FromHours(12));
            raDecEventTimes[RaDecEventTime.MeridianR1] = CalcRaDecEventInfo(crossMeridianTime + TimeSpan.FromHours(0.2));
            raDecEventTimes[RaDecEventTime.MeridianR2] = CalcRaDecEventInfo(crossMeridianTime + TimeSpan.FromHours(12));

            TimeSpan duration;
            DateTimeOffset start;
            if (TryBalanceTimeAroundMeridian(meridianEvent.Time, darkEvent.Time, twilightEvent.Time, out var maybeBalance) && maybeBalance is DateTimeOffset balance)
            {
                raDecEventTimes[RaDecEventTime.Balance] = CalcRaDecEventInfo(balance);
                var absHours = Math.Abs((balance - meridianEvent.Time).TotalHours);
                duration = TimeSpan.FromHours(absHours * 2);
                start = meridianEvent.Time.AddHours(-absHours);
            }
            else
            {
                duration = astroTwilight - astroDark;
                start = astroDark;
            }

            const int iterations = 10;
            var step = duration / 10;
            for (var it = 1; it < iterations; it++)
            {
                start += step;
                raDecEventTimes[RaDecEventTime.Balance + it] = CalcRaDecEventInfo(start);
            }

            return raDecEventTimes;

            RaDecEventInfo CalcRaDecEventInfo(in DateTimeOffset dt)
            {
                if (isFixed)
                {
                    JulianDateUTC = dt.ToJulian();
                    if (ElevationTopocentric is double alt)
                    {
                        return new(dt, alt);
                    }
                }
                else if (VSOP87a.Reduce(idx, dt, SiteLatitude, SiteLongitude, out _, out _, out _, out var alt))
                {
                    return new(dt, alt);
                }

                return new(dt, double.NaN);
            }
        }


        internal static bool TryBalanceTimeAroundMeridian(in DateTimeOffset m, in DateTimeOffset d, in DateTimeOffset t, [NotNullWhen(true)] out DateTimeOffset? b)
        {
            var dm = Math.Abs((d - m).TotalHours);
            var tm = Math.Abs((t - m).TotalHours);

            if (dm > tm)
            {
                b = m.AddHours((m > d ? 1 : -1) * dm);
                return true;
            }
            else if (dm == tm)
            {
                b = null;
                return false;
            }
            else
            {
                b = m.AddHours((m > t ? 1 : -1) * tm);
                return true;
            }
        }
        #endregion
    }
}
