namespace Astap.Lib.Astrometry.Ascom;

public class AscomTransform : DynamicComObject, ICoordinateTransform
{
    public AscomTransform() : base("ASCOM.Astrometry.Transform.Transform") { }

    /// <inheritdoc/>
    public double? AzimuthTopocentric => _comObject?.AzimuthTopocentric is double value ? value : null;

    /// <inheritdoc/>
    public double? DECApparent => _comObject?.DECApparent is double value ? value : null;

    /// <inheritdoc/>
    public double? DecJ2000 => _comObject?.DecJ2000 is double value ? value : null;

    /// <inheritdoc/>
    public double? DECTopocentric => _comObject?.DECTopocentric is double value ? value : null;

    /// <inheritdoc/>
    public double? ElevationTopocentric => _comObject?.ElevationTopocentric is double value ? value : null;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public double? RAApparent => _comObject?.RAApparent is double value ? value : null;

    /// <inheritdoc/>
    public double? RA2000 => _comObject?.RA2000 is double value ? value : null;

    /// <inheritdoc/>
    public double? RATopocentric => _comObject?.RATopocentric is double value ? value : null;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void SetApparent(double ra, double dec) => _comObject?.SetApparent(ra, dec);

    /// <inheritdoc/>
    public void SetAzimuthElevation(double azimuth, double elevation) => _comObject?.SetAzimuthElevation(azimuth, elevation);

    /// <inheritdoc/>
    public void SetJ2000(double ra, double dec) => _comObject?.SetJ2000(ra, dec);

    /// <inheritdoc/>
    public void SetTopocentric(double ra, double dec) => _comObject?.SetTopocentric(ra, dec);

    /// <inheritdoc/>
    public void Refresh() => _comObject?.Refresh();
}