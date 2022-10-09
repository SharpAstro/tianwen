namespace Astap.Lib.Astrometry.Ascom;

public class AscomTransform : DynamicComObject, ICoordinateTransform
{
    public AscomTransform() : base("ASCOM.Astrometry.Transform.Transform") { }

    public double? AzimuthTopocentric => _comObject?.AzimuthTopocentric is double value ? value : null;

    public double? DECApparent => _comObject?.DECApparent is double value ? value : null;

    public double? DecJ2000 => _comObject?.DecJ2000 is double value ? value : null;

    public double? DECTopocentric => _comObject?.DECTopocentric is double value ? value : null;

    public double? ElevationTopocentric => _comObject?.ElevationTopocentric is double value ? value : null;

    public double JulianDateTT
    {
        get => _comObject?.JulianDateTT is double value ? value : 0.0;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.JulianDateTT = value;
            }
        }
    }

    public double JulianDateUTC
    {
        get => _comObject?.JulianDateUTC is double value ? value : 0.0;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.JulianDateUTC = value;
            }
        }
    }

    public double? RAApparent => _comObject?.RAApparent is double value ? value : null;

    public double? RA2000 => _comObject?.RA2000 is double value ? value : null;

    public double? RATopocentric => _comObject?.RATopocentric is double value ? value : null;

    public bool? Refraction
    {
        get => _comObject?.Refraction is bool value ? value : null;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.Refraction = value;
            }
        }
    }

    public double? SiteElevation
    {
        get => _comObject?.SiteElevation is double value ? value : null;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.SiteElevation = value;
            }
        }
    }

    public double? SiteLatitude
    {
        get => _comObject?.SiteLatitude is double value ? value : null;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.SiteLatitude = value;
            }
        }
    }

    public double? SiteLongitude
    {
        get => _comObject?.SiteLongitude is double value ? value : null;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.SiteLongitude = value;
            }
        }
    }

    public double? SiteTemperature
    {
        get => _comObject?.SiteTemperature is double value ? value : null;
        set
        {
            if (_comObject is var obj and not null)
            {
                obj.SiteTemperature = value;
            }
        }
    }

    public void SetApparent(double ra, double dec) => _comObject?.SetApparent(ra, dec);

    public void SetAzimuthElevation(double azimuth, double elevation) => _comObject?.SetAzimuthElevation(azimuth, elevation);

    public void SetJ2000(double ra, double dec) => _comObject?.SetJ2000(ra, dec);

    public void SetTopocentric(double ra, double dec) => _comObject?.SetTopocentric(ra, dec);

    public void Refresh() => _comObject?.Refresh();
}