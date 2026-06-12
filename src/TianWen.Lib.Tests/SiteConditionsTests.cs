using System;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Weather;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="SiteConditions"/> -- the two-tier (live weather -> standard
/// atmosphere) refraction-input resolver that replaced the hardcoded <c>SitePressure = 1010</c> /
/// <c>SiteTemperature = 10</c> in <c>IMountDriver.TryGetTransformAsync</c>. Pressure and
/// temperature are varying values: they come live from the weather driver and are never stored in
/// the profile. Also covers <see cref="SiteConditions.ApplyTo"/>'s "standard tier leaves pressure
/// to auto-derive from elevation" contract.
/// </summary>
[Collection("Astrometry")]
public class SiteConditionsTests
{
    private static IWeatherDriver Weather(double pressureHPa, double temperatureC)
    {
        var w = Substitute.For<IWeatherDriver>();
        w.Pressure.Returns(pressureHPa);
        w.Temperature.Returns(temperatureC);
        return w;
    }

    [Fact]
    public void Resolve_WeatherProvidesBoth_WeatherWins()
    {
        // Matches the FakeWeatherDriver values (12.5 C / 1013.25 hPa).
        var conditions = SiteConditions.Resolve(Weather(1013.25, 12.5));

        conditions.PressureHPa.ShouldBe(1013.25);
        conditions.TemperatureCelsius.ShouldBe(12.5);
        conditions.PressureSource.ShouldBe(SiteConditionsSource.Weather);
        conditions.TemperatureSource.ShouldBe(SiteConditionsSource.Weather);
    }

    [Fact]
    public void Resolve_WeatherNaNPressure_PressureFallsToStandard_TemperatureFromWeather()
    {
        // Per-value independence: a station can report temperature but NaN pressure. The NaN
        // pressure falls through to the standard atmosphere while temperature stays from weather.
        var conditions = SiteConditions.Resolve(Weather(double.NaN, 12.5));

        conditions.PressureHPa.ShouldBe(SiteConditions.StandardPressureHPa);
        conditions.PressureSource.ShouldBe(SiteConditionsSource.StandardAtmosphere);
        conditions.TemperatureCelsius.ShouldBe(12.5);
        conditions.TemperatureSource.ShouldBe(SiteConditionsSource.Weather);
    }

    [Fact]
    public void Resolve_NoWeather_StandardAtmosphere()
    {
        var conditions = SiteConditions.Resolve(weather: null);

        conditions.PressureHPa.ShouldBe(SiteConditions.StandardPressureHPa);
        conditions.TemperatureCelsius.ShouldBe(SiteConditions.StandardTemperatureCelsius);
        conditions.PressureSource.ShouldBe(SiteConditionsSource.StandardAtmosphere);
        conditions.TemperatureSource.ShouldBe(SiteConditionsSource.StandardAtmosphere);
    }

    [Theory]
    [InlineData(50.0)]    // far below the 300 hPa floor (garbage station reading)
    [InlineData(2000.0)]  // above the 1100 hPa ceiling
    public void Resolve_WeatherPressureOutOfRange_FallsThroughToStandard(double badWeatherPressure)
    {
        var conditions = SiteConditions.Resolve(Weather(badWeatherPressure, 12.5));

        conditions.PressureHPa.ShouldBe(SiteConditions.StandardPressureHPa);
        conditions.PressureSource.ShouldBe(SiteConditionsSource.StandardAtmosphere);
        // Temperature is still in range, so it stays from weather (per-value resolution).
        conditions.TemperatureCelsius.ShouldBe(12.5);
        conditions.TemperatureSource.ShouldBe(SiteConditionsSource.Weather);
    }

    [Fact]
    public void ApplyTo_StandardAtmosphere_LeavesPressureToAutoDeriveFromElevation()
    {
        // Standard tier must NOT pin a flat 1010: it leaves Transform.SitePressure unset so the
        // transform derives it barometrically from elevation (the point of dropping the hardcode).
        var transform = NewTransform(siteElevationM: 2000);

        SiteConditions.Standard.ApplyTo(transform);
        transform.SiteTemperature.ShouldBe(SiteConditions.StandardTemperatureCelsius);

        // Trigger a conversion so CalculateSitePressureIfRequired runs and materialises the
        // derived pressure (the getter throws while it is still NaN).
        transform.SetJ2000(6.0, 20.0);
        transform.Refresh();
        _ = transform.RAApparent;

        // ~800 hPa at 2000 m -- clearly the elevation-derived value, not the old flat 1010.
        transform.SitePressure.ShouldBeInRange(760.0, 850.0);
        transform.SitePressure.ShouldBeLessThan(1000.0);
    }

    [Fact]
    public void ApplyTo_Weather_PinsTheResolvedPressure()
    {
        var transform = NewTransform(siteElevationM: 2000);

        SiteConditions.Resolve(Weather(1013.25, 12.5)).ApplyTo(transform);

        // Set directly (source != StandardAtmosphere) so it overrides the elevation auto-derive.
        transform.SitePressure.ShouldBe(1013.25);
        transform.SiteTemperature.ShouldBe(12.5);
    }

    private static Transform NewTransform(double siteElevationM)
        // Fixed-instant fake time provider: deterministic, never reads the wall clock. The date is
        // pinned explicitly below so the provider is only a formality here anyway.
        => new Transform(new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero)))
        {
            SiteLatitude = 48.21,
            SiteLongitude = 16.37,
            SiteElevation = siteElevationM,
            Refraction = true,
            DateTimeOffset = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero)
        };
}
