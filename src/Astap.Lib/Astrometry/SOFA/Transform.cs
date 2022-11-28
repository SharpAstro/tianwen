using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.NOVA;
using nom.tam.fits;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    public class Transform
    {
        private double RAJ2000Value, RATopoValue, DECJ2000Value, DECTopoValue, SiteElevValue, SiteLatValue, SiteLongValue, SiteTempValue, SitePressureValue;
        private double RAApparentValue, DECApparentValue, AzimuthTopoValue, ElevationTopoValue, JulianDateTTValue, JulianDateUTCValue;
        private bool RefracValue, RequiresRecalculate;
        private SetBy LastSetBy;

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
            RAJ2000Value = double.NaN;
            DECJ2000Value = double.NaN;
            RATopoValue = double.NaN;
            DECTopoValue = double.NaN;
            SiteElevValue = double.NaN;
            SiteLatValue = double.NaN;
            SiteLongValue = double.NaN;
            SitePressureValue = double.NaN;

            RefracValue = false;
            LastSetBy = SetBy.Never;
            RequiresRecalculate = true;
            JulianDateTTValue = 0d; // Initialise to a value that forces the current PC date time to be used in determining the TT Julian date of interest
        }

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
                CheckSet(SiteLatValue, "Site latitude has not been set");
                return SiteLatValue;
            }
            set
            {
                if (value < -90.0d | value > 90.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"{value} should be within -90.0 degrees and +90.0 degrees");
                }
                if (SiteLatValue != value)
                {
                    RequiresRecalculate = true;
                }
                SiteLatValue = value;
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
                CheckSet(SiteLongValue, "Site longitude has not been set");
                return SiteLongValue;
            }
            set
            {
                if (value < -180.0d | value > 180.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -180.0 degrees to +180.0 degrees");
                }
                if (SiteLongValue != value)
                {
                    RequiresRecalculate = true;
                }
                SiteLongValue = value;
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
                CheckSet(SiteElevValue, "Site elevation has not been set");
                return SiteElevValue;
            }
            set
            {
                if (value < -300.0d | value > 10000.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -300.0 metres to +10000.0 metres");
                }
                if (SiteElevValue != value)
                {
                    RequiresRecalculate = true;
                }
                SiteElevValue = value;
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
                CheckSet(SiteTempValue, "Site temperature has not been set");
                return SiteTempValue;
            }
            set
            {
                if (value < -273.15d | value > 100.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from -273.15 Celsius to +100.0 Celsius");
                }
                if (SiteTempValue != value)
                {
                    RequiresRecalculate = true;
                }
                SiteTempValue = value;
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
                CheckSet(SitePressureValue, "Site atmospheric pressure has not been set");
                return SitePressureValue;
            }
            set
            {
                if (value < 0.0d | value > 1200.0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Range from 0.0hPa (mbar) to +1200.0hPa (mbar)");
                }
                if (SitePressureValue != value)
                {
                    RequiresRecalculate = true;
                }
                SitePressureValue = value;
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
                return RefracValue;
            }
            set
            {
                if (RefracValue != value)
                {
                    RequiresRecalculate = true;
                }
                RefracValue = value;
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
        /// <param name="RA">RA in J2000 co-ordinates (0.0 to 23.999 hours)</param>
        /// <param name="DEC">DEC in J2000 co-ordinates (-90.0 to +90.0)</param>
        /// <remarks></remarks>
        public void SetJ2000(double RA, double DEC)
        {

            if (RA != RAJ2000Value | DEC != DECJ2000Value)
            {
                RAJ2000Value = ValidateRA(RA);
                DECJ2000Value = ValidateDec(DEC);
                RequiresRecalculate = true;
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

            if (RA != RAApparentValue | DEC != DECApparentValue)
            {
                RAApparentValue = ValidateRA(RA);
                DECApparentValue = ValidateDec(DEC);
                RequiresRecalculate = true;
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

            if (RA != RATopoValue | DEC != DECTopoValue)
            {
                RATopoValue = ValidateRA(RA);
                DECTopoValue = ValidateDec(DEC);
                RequiresRecalculate = true;
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

            AzimuthTopoValue = Azimuth;
            ElevationTopoValue = Elevation;
            RequiresRecalculate = true;

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
                CheckSet(RAJ2000Value, "RA J2000 can not be derived from the information provided. Are site parameters set?");
                return RAJ2000Value;
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
                CheckSet(DECJ2000Value, "DEC J2000 can not be derived from the information provided. Are site parameters set?");
                return DECJ2000Value;
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
                CheckSet(RATopoValue, "RA topocentric can not be derived from the information provided. Are site parameters set?");
                return RATopoValue;
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
                CheckSet(DECTopoValue, "DEC topocentric can not be derived from the information provided. Are site parameters set?");
                return DECTopoValue;
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
                return RAApparentValue;
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
                return DECApparentValue;
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
                RequiresRecalculate = true; // Force a recalculation of Azimuth
                Recalculate();
                CheckSet(AzimuthTopoValue, "Azimuth topocentric can not be derived from the information provided. Are site parameters set?");
                return AzimuthTopoValue;
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
                RequiresRecalculate = true; // Force a recalculation of Elevation
                Recalculate();
                CheckSet(ElevationTopoValue, "Elevation topocentric can not be derived from the information provided. Are site parameters set?");
                return ElevationTopoValue;
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
                return JulianDateTTValue;
            }
            set
            {
                double tai1 = default, tai2 = default, utc1 = default, utc2 = default;

                // Validate the supplied value, it must be 0.0 or within the permitted range
                if (value != 0.0d & (value < JULIAN_DATE_MINIMUM_VALUE | value > JULIAN_DATE_MAXIMUM_VALUE))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"Range from {JULIAN_DATE_MINIMUM_VALUE} to {JULIAN_DATE_MAXIMUM_VALUE}");
                }

                JulianDateTTValue = value;
                RequiresRecalculate = true; // Force a recalculation because the Julian date has changed

                if (JulianDateTTValue != 0.0d)
                {
                    // Calculate UTC
                    if (wwaTttai(JulianDateTTValue, 0.0d, ref tai1, ref tai2) != 0)
                    {
                        throw new InvalidOperationException("TtTai - Bad return code");
                    }
                    if (wwaTaiutc(tai1, tai2, ref utc1, ref utc2) != 0)
                    {
                        throw new InvalidOperationException("TaiUtc - Bad return code");
                    }
                    JulianDateUTCValue = utc1 + utc2;
                }
                else // Handle special case of 0.0
                {
                    JulianDateUTCValue = 0.0d;
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
                return JulianDateUTCValue;
            }
            set
            {
                double tai1 = default, tai2 = default, tt1 = default, tt2 = default;

                // Validate the supplied value, it must be 0.0 or within the permitted range
                if (value != 0.0d & (value < JULIAN_DATE_MINIMUM_VALUE | value > JULIAN_DATE_MAXIMUM_VALUE))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"Range from {JULIAN_DATE_MINIMUM_VALUE} to {JULIAN_DATE_MAXIMUM_VALUE}");
                }

                JulianDateUTCValue = value;
                RequiresRecalculate = true; // Force a recalculation because the Julian date has changed

                if (JulianDateUTCValue != 0.0d)
                {
                    // Calculate Terrestrial Time equivalent
                    if (wwaUtctai(JulianDateUTCValue, 0.0d, ref tai1, ref tai2) != 0)
                    {
                        throw new InvalidOperationException("UtcTai - Bad return code");
                    }
                    if (wwaTaitt(tai1, tai2, ref tt1, ref tt2) != 0)
                    {
                        throw new InvalidOperationException("TaiTt - Bad return code");
                    }
                    JulianDateTTValue = tt1 + tt2;
                }
                else // Handle special case of 0.0
                {
                    JulianDateTTValue = 0.0d;
                }
            }
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
            double DUT1, JDUTCSofa;
            double aob = default, zob = default, hob = default, dob = default, rob = default, eo = default;

            CheckSet(SiteElevValue, "Site elevation has not been set");
            CheckSet(SiteLatValue, "Site latitude has not been set");
            CheckSet(SiteLongValue, "Site longitude has not been set");
            CheckSet(SiteTempValue, "Site temperature has not been set");

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();

            JDUTCSofa = GetJDUTCSofa();
            DUT1 = LeapSecondsTable.DeltaTCalc(JDUTCSofa);

            if (RefracValue) // Include refraction
            {
                wwaAtco13(RAJ2000Value * HOURS2RADIANS, DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, SitePressureValue, SiteTempValue, 0.8d, 0.57d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }
            else // No refraction
            {
                wwaAtco13(RAJ2000Value * HOURS2RADIANS, DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }

            RATopoValue = wwaAnp(rob - eo) * RADIANS2HOURS; // // Convert CIO RA to equinox of date RA by subtracting the equation of the origins and convert from radians to hours
            DECTopoValue = dob * RADIANS2DEGREES; // Convert Dec from radians to degrees
            AzimuthTopoValue = aob * RADIANS2DEGREES;
            ElevationTopoValue = 90.0d - zob * RADIANS2DEGREES;
        }

        private void J2000ToApparent()
        {
            double ri = default, di = default, eo = default;
            double JDTTSofa = GetJDTTSofa();

            wwaAtci13(RAJ2000Value * HOURS2RADIANS, DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, JDTTSofa, 0.0d, ref ri, ref di, ref eo);
            RAApparentValue = wwaAnp(ri - eo) * RADIANS2HOURS; // // Convert CIO RA to equinox of date RA by subtracting the equation of the origins and convert from radians to hours
            DECApparentValue = di * RADIANS2DEGREES; // Convert Dec from radians to degrees
        }

        private void TopoToJ2000()
        {
            double RACelestrial = default, DecCelestial = default, JDTTSofa, JDUTCSofa, DUT1;
            double aob = default, zob = default, hob = default, dob = default, rob = default, eo = default;

            if (double.IsNaN(SiteElevValue))
            {
                throw new InvalidOperationException("Site elevation has not been set");
            }
            if (double.IsNaN(SiteLatValue))
            {
                throw new InvalidOperationException("Site latitude has not been set");
            }
            if (double.IsNaN(SiteLongValue))
            {
                throw new InvalidOperationException("Site longitude has not been set");
            }
            if (double.IsNaN(SiteTempValue))
            {
                throw new InvalidOperationException("Site temperature has not been set");
            }

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();

            JDUTCSofa = GetJDUTCSofa();
            JDTTSofa = GetJDTTSofa();
            DUT1 = LeapSecondsTable.DeltaTCalc(JDUTCSofa);

            var type = 'R';
            int RetCode;
            if (RefracValue) // Refraction is required
            {
                RetCode = wwaAtoc13(ref type, wwaAnp(RATopoValue * HOURS2RADIANS + wwaEo06a(JDTTSofa, 0.0d)), DECTopoValue * DEGREES2RADIANS, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, SitePressureValue, SiteTempValue, 0.85d, 0.57d, ref RACelestrial, ref DecCelestial);
            }
            else
            {
                RetCode = wwaAtoc13(ref type, wwaAnp(RATopoValue * HOURS2RADIANS + wwaEo06a(JDTTSofa, 0.0d)), DECTopoValue * DEGREES2RADIANS, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref RACelestrial, ref DecCelestial);
            }

            if (RetCode != 0)
            {
                throw new InvalidOperationException($"Atoc13: Invalid return code: {RetCode}");
            }

            RAJ2000Value = RACelestrial * RADIANS2HOURS;
            DECJ2000Value = DecCelestial * RADIANS2DEGREES;

            // Now calculate the corresponding AzEl values from the J2000 values
            if (RefracValue) // Include refraction
            {
                wwaAtco13(RAJ2000Value * HOURS2RADIANS, DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, SitePressureValue, SiteTempValue, 0.8d, 0.57d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }
            else // No refraction
            {
                wwaAtco13(RAJ2000Value * HOURS2RADIANS, DECJ2000Value * DEGREES2RADIANS, 0.0d, 0.0d, 0.0d, 0.0d, JDUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);
            }

            AzimuthTopoValue = aob * RADIANS2DEGREES;
            ElevationTopoValue = 90.0d - zob * RADIANS2DEGREES;
        }

        private void ApparentToJ2000()
        {
            double JulianDateTTSofa, RACelestial = default, DecCelestial = default, JulianDateUTCSofa, eo = default;

            JulianDateTTSofa = GetJDTTSofa();
            JulianDateUTCSofa = GetJDUTCSofa();

            wwaAtic13(wwaAnp(RAApparentValue * HOURS2RADIANS + wwaEo06a(JulianDateUTCSofa, 0.0d)), DECApparentValue * DEGREES2RADIANS, JulianDateTTSofa, 0.0d, ref RACelestial, ref DecCelestial, ref eo);
            RAJ2000Value = RACelestial * RADIANS2HOURS;
            DECJ2000Value = DecCelestial * RADIANS2DEGREES;
        }

        private void Recalculate() // Calculate values for derived co-ordinates
        {
            if (RequiresRecalculate | RefracValue == true)
            {
                switch (LastSetBy)
                {
                    case SetBy.J2000: // J2000 coordinates have bee set so calculate apparent and topocentric coordinates
                        {
                            // Check whether required topo values have been set
                            if (!double.IsNaN(SiteLatValue) & !double.IsNaN(SiteLongValue) & !double.IsNaN(SiteElevValue) & !double.IsNaN(SiteTempValue))
                            {
                                J2000ToTopo(); // All required site values present so calculate Topo values
                            }
                            else // Set to NaN
                            {
                                RATopoValue = double.NaN;
                                DECTopoValue = double.NaN;
                                AzimuthTopoValue = double.NaN;
                                ElevationTopoValue = double.NaN;
                            }
                            J2000ToApparent();
                            break;
                        }
                    case SetBy.Topocentric: // Topocentric co-ordinates have been set so calculate J2000 and apparent coordinates
                        {
                            // Check whether required topo values have been set
                            if (!double.IsNaN(SiteLatValue) & !double.IsNaN(SiteLongValue) & !double.IsNaN(SiteElevValue) & !double.IsNaN(SiteTempValue)) // They have so calculate remaining values
                            {
                                TopoToJ2000();
                                J2000ToApparent();
                            }
                            else // Set the topo and apparent values to NaN
                            {
                                RAJ2000Value = double.NaN;
                                DECJ2000Value = double.NaN;
                                RAApparentValue = double.NaN;
                                DECApparentValue = double.NaN;
                                AzimuthTopoValue = double.NaN;
                                ElevationTopoValue = double.NaN;
                            }

                            break;
                        }
                    case SetBy.Apparent: // Apparent values have been set so calculate J2000 values and topo values if appropriate
                        {
                            ApparentToJ2000(); // Calculate J2000 value
                                               // Check whether required topo values have been set
                            if (!double.IsNaN(SiteLatValue) & !double.IsNaN(SiteLongValue) & !double.IsNaN(SiteElevValue) & !double.IsNaN(SiteTempValue))
                            {
                                J2000ToTopo(); // All required site values present so calculate Topo values
                            }
                            else
                            {
                                RATopoValue = double.NaN;
                                DECTopoValue = double.NaN;
                                AzimuthTopoValue = double.NaN;
                                ElevationTopoValue = double.NaN;
                            }

                            break;
                        }
                    case SetBy.AzimuthElevation:
                        {
                            if (!double.IsNaN(SiteLatValue) & !double.IsNaN(SiteLongValue) & !double.IsNaN(SiteElevValue) & !double.IsNaN(SiteTempValue))
                            {
                                AzElToJ2000();
                                J2000ToTopo();
                                J2000ToApparent();
                            }
                            else
                            {
                                RAJ2000Value = double.NaN;
                                DECJ2000Value = double.NaN;
                                RAApparentValue = double.NaN;
                                DECApparentValue = double.NaN;
                                RATopoValue = double.NaN;
                                DECTopoValue = double.NaN;
                            } // Neither SetJ2000 nor SetTopocentric nor SetApparent have been called, so throw an exception

                            break;
                        }

                    default:
                        {
                            throw new InvalidOperationException("Can not recalculate Transform object values because neither SetJ2000 nor SetTopocentric nor SetApparent have been called");
                        }
                }
                RequiresRecalculate = false; // Reset the recalculate flag
            }
        }

        private void AzElToJ2000()
        {
            double JulianDateUTCSofa, RACelestial = default, DecCelestial = default, DUT1;

            if (double.IsNaN(SiteElevValue))
            {
                throw new InvalidOperationException("Site elevation has not been set");
            }
            if (double.IsNaN(SiteLatValue))
            {
                throw new InvalidOperationException("Site latitude has not been set");
            }
            if (double.IsNaN(SiteLongValue))
            {
                throw new InvalidOperationException("Site longitude has not been set");
            }
            if (double.IsNaN(SiteTempValue))
            {
                throw new InvalidOperationException("Site temperature has not been set");
            }

            // Calculate site pressure at site elevation if this has not been provided
            CalculateSitePressureIfRequired();

            JulianDateUTCSofa = GetJDUTCSofa();
            DUT1 = LeapSecondsTable.DeltaTCalc(JulianDateUTCSofa);


            int RetCode;
            var type = 'A';
            if (RefracValue) // Refraction is required
            {
                RetCode = wwaAtoc13(ref type, AzimuthTopoValue * DEGREES2RADIANS, (90.0d - ElevationTopoValue) * DEGREES2RADIANS, JulianDateUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, SitePressureValue, SiteTempValue, 0.85d, 0.57d, ref RACelestial, ref DecCelestial);
            }
            else
            {
                RetCode = wwaAtoc13(ref type, AzimuthTopoValue * DEGREES2RADIANS, (90.0d - ElevationTopoValue) * DEGREES2RADIANS, JulianDateUTCSofa, 0.0d, DUT1, SiteLongValue * DEGREES2RADIANS, SiteLatValue * DEGREES2RADIANS, SiteElevValue, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d, ref RACelestial, ref DecCelestial);
            }

            if (RetCode != 0)
            {
                throw new InvalidOperationException($"Atoc13: Return code is {RetCode}");
            }

            RAJ2000Value = RACelestial * RADIANS2HOURS;
            DECJ2000Value = DecCelestial * RADIANS2DEGREES;
        }

        private double GetJDUTCSofa()
        {
            if (JulianDateUTCValue == 0.0d) // No specific UTC date / time has been set so use the current date / time
            {
                var (utc1, utc2, _, _) = GetJDUtcTTSofa(DateTime.UtcNow);
                return utc1 + utc2;
            }
            else // A specific UTC date / time has been set so use it
            {
                return JulianDateUTCValue;
            }
        }

        private double GetJDTTSofa()
        {
            if (JulianDateTTValue == 0.0d) // No specific TT date / time has been set so use the current date / time
            {
                var (_, _, tt1, tt2) = GetJDUtcTTSofa(DateTime.UtcNow);
                return tt1 + tt2;
            }
            else // A specific TT date / time has been set so use it
            {
                return JulianDateTTValue;
            }
        }

        private static (double utc1, double utc2, double tt1, double tt2) GetJDUtcTTSofa(DateTime Now)
        {
            double utc1 = default, utc2 = default, tai1 = default, tai2 = default, tt1 = default, tt2 = default;

            // First calculate the UTC Julian date, then convert this to the equivalent TAI Julian date then convert this to the equivalent TT Julian date
            if (wwaDtf2d("UTC", Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, Now.Second + Now.Millisecond / 1000.0d, ref utc1, ref utc2) != 0)
            {
                throw new InvalidOperationException("Dtf2d: Bad return code");
            }
            if (wwaUtctai(utc1, utc2, ref tai1, ref tai2) != 0)
            {
                throw new InvalidOperationException("UtcTai: Bad return code");
            }
            if (wwaTaitt(tai1, tai2, ref tt1, ref tt2) != 0)
            {
                throw new InvalidOperationException("TaiTt: Bad return code");
            }

            return (utc1, utc2, tt1, tt2);
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
            if (double.IsNaN(SitePressureValue)) // Site pressure has not been set so derive a value based on the supplied observatory height and temperature
            {
                // phpa = 1013.25 * exp ( −hm / ( 29.3 * tsl ) ); NOTE this equation calculates the site pressure and uses the site temperature REDUCED TO SEA LEVEL MESURED IN DEGREES KELVIN
                // tsl = tSite − 0.0065(0 − hsite);  NOTE this equation reduces the site temperature to sea level
                SitePressureValue = STANDARD_PRESSURE * Math.Exp(-SiteElevValue / (29.3d * (SiteTempValue + 0.0065d * SiteElevValue - ABSOLUTE_ZERO_CELSIUS)));
            }
        }

        #endregion

        #region Additional functionality
        public static double LocalSiderealTime(DateTimeOffset dateTimeOffset, double siteLongitude)
        {
            var (utc1, utc2, tt1, tt2) = GetJDUtcTTSofa(dateTimeOffset.UtcDateTime);

            double ut11 = default, ut12 = default;
            var dut1 = LeapSecondsTable.DeltaUT1(utc1 + utc2);
            if (wwaUtcut1(utc1, utc2, dut1, ref ut11, ref ut12) != 0)
            {
                throw new InvalidOperationException($"Cannot convert {dateTimeOffset} to UT1");
            }

            var gmst = wwaGmst00(ut11, ut12, tt1, tt2) * RADIANS2HOURS;

            // Allow for the longitude
            gmst += siteLongitude / 360.0 * 24.0;

            // Reduce to the range 0 to 24 hours
            return CoordinateUtils.ConditionRA(gmst);
        }

        public IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> CalculateObjElevation(in CelestialObject obj, DateTimeOffset astroDark, DateTimeOffset astroTwilight, double siderealTimeAtAstroDark)
        {
            SetJ2000(obj.RA, obj.Dec);

            var raDecEventTimes = new Dictionary<RaDecEventTime, RaDecEventInfo>(4);

            var hourAngle = TimeSpan.FromHours(CoordinateUtils.ConditionHA(siderealTimeAtAstroDark - obj.RA));
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
                JulianDateUTC = dt.ToJulian();
                if (ElevationTopocentric is double alt)
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
