using System;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Astrometry;

/// <summary>Which tier of the resolution chain supplied a <see cref="SiteConditions"/> value.</summary>
public enum SiteConditionsSource
{
    /// <summary>A connected <see cref="IWeatherDriver"/> reported a usable (non-NaN, in-range) value.</summary>
    Weather,
    /// <summary>No usable weather value was available -- the standard-atmosphere fallback.</summary>
    StandardAtmosphere
}

/// <summary>
/// Resolved atmospheric refraction inputs (site-level pressure + temperature) for a
/// <see cref="Transform"/>, with the tier each value came from. Replaces the hardcoded
/// <c>SitePressure = 1010</c> / <c>SiteTemperature = 10</c> that used to live in
/// <see cref="Devices.IMountDriver.TryGetTransformAsync(System.Threading.CancellationToken)"/>.
/// <para>
/// Pressure and temperature are <em>varying</em> environmental values, so they are deliberately
/// NOT persisted in the profile (unlike the static site geometry: latitude/longitude/elevation).
/// They are read live from a connected weather driver each time conditions are resolved -- the
/// driver already caches its last reading and refreshes on its own cadence, so resolving per call
/// is a cheap field read, not a network hit. When no weather device is connected both values fall
/// back to the standard atmosphere; <see cref="ApplyTo"/> then leaves pressure to be derived
/// barometrically from the site's static (profile-stored) elevation. Resolution is per-value -- a
/// station can report temperature but NaN pressure. Pure + static so the session, the
/// polar-alignment handler, and tests all share one implementation.
/// </para>
/// </summary>
public readonly record struct SiteConditions(
    double PressureHPa,
    double TemperatureCelsius,
    SiteConditionsSource PressureSource,
    SiteConditionsSource TemperatureSource)
{
    /// <summary>Standard-atmosphere site pressure (hPa). Matches the value the old hardcode used.</summary>
    public const double StandardPressureHPa = 1010.0;

    /// <summary>Standard-atmosphere site temperature (Celsius). Matches the old hardcode.</summary>
    public const double StandardTemperatureCelsius = 10.0;

    // Plausibility ranges. Out-of-range weather values are treated as unset and fall through to
    // the standard atmosphere (a garbage station reading must not poison refraction). Generous
    // enough for any real observatory (~330 hPa at Everest).
    private const double MinPressureHPa = 300.0;
    private const double MaxPressureHPa = 1100.0;
    private const double MinTemperatureCelsius = -80.0;
    private const double MaxTemperatureCelsius = 60.0;

    /// <summary>
    /// The standard-atmosphere fallback (1010 hPa / 10 C), both tiers
    /// <see cref="SiteConditionsSource.StandardAtmosphere"/>. <see cref="ApplyTo"/> on this value
    /// leaves <see cref="Transform.SitePressure"/> unset so the transform auto-derives pressure
    /// from elevation -- which beats a flat 1010 at altitude.
    /// </summary>
    public static readonly SiteConditions Standard = new(
        StandardPressureHPa, StandardTemperatureCelsius,
        SiteConditionsSource.StandardAtmosphere, SiteConditionsSource.StandardAtmosphere);

    /// <summary>
    /// Resolves pressure and temperature independently from a connected weather driver (the only
    /// live source for these varying values), falling back to the standard atmosphere when no
    /// usable reading is available.
    /// </summary>
    /// <param name="weather">A <em>connected</em> weather driver, or null. Callers pass null when
    /// no weather device is assigned or connected; NaN properties are treated as unsupported.</param>
    public static SiteConditions Resolve(IWeatherDriver? weather)
    {
        var weatherPressure = weather is not null && !double.IsNaN(weather.Pressure) ? weather.Pressure : (double?)null;
        var weatherTemperature = weather is not null && !double.IsNaN(weather.Temperature) ? weather.Temperature : (double?)null;

        var (pressure, pressureSource) = ResolveValue(weatherPressure, StandardPressureHPa, MinPressureHPa, MaxPressureHPa);
        var (temperature, temperatureSource) = ResolveValue(weatherTemperature, StandardTemperatureCelsius, MinTemperatureCelsius, MaxTemperatureCelsius);

        return new SiteConditions(pressure, temperature, pressureSource, temperatureSource);
    }

    private static (double Value, SiteConditionsSource Source) ResolveValue(double? weatherValue, double standard, double min, double max)
    {
        if (weatherValue is { } w && w >= min && w <= max)
        {
            return (w, SiteConditionsSource.Weather);
        }
        return (standard, SiteConditionsSource.StandardAtmosphere);
    }

    /// <summary>
    /// Applies the resolved conditions to a <see cref="Transform"/>: always sets
    /// <see cref="Transform.SiteTemperature"/>; sets <see cref="Transform.SitePressure"/> only when
    /// pressure came from weather. For the standard-atmosphere tier the pressure is deliberately
    /// left unset (NaN) so the transform derives it barometrically from
    /// <see cref="Transform.SiteElevation"/> -- more accurate at altitude than a flat 1010 hPa.
    /// </summary>
    public void ApplyTo(Transform transform)
    {
        transform.SiteTemperature = TemperatureCelsius;
        if (PressureSource != SiteConditionsSource.StandardAtmosphere)
        {
            transform.SitePressure = PressureHPa;
        }
    }
}
