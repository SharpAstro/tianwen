using TianWen.Lib.Sequencing;
using System;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit coverage for the pure helpers extracted out of <see cref="AppSignalHandler"/> during the
/// "route, don't implement" refactor: <see cref="EquipmentActions.TryParseSite"/>,
/// <see cref="EquipmentActions.CommitDeviceSetting"/>, and <see cref="GuiAppState.GateSunSlew"/>.
/// Each was inline lambda logic before; testing them directly is the point of the extraction.
/// </summary>
public class RouteOnlyExtractionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("48.2", "16.4", "180", true, 48.2, 16.4, 180.0)]
    [InlineData("-33.87", "151.21", "", true, -33.87, 151.21, null)]      // no elevation -> null
    [InlineData("90", "-180", "0", true, 90.0, -180.0, 0.0)]              // range extremes accepted
    [InlineData("91", "0", "", false, 0, 0, null)]                        // lat out of range
    [InlineData("0", "181", "", false, 0, 0, null)]                       // lon out of range
    [InlineData("abc", "16", "", false, 0, 0, null)]                      // lat unparseable
    public void TryParseSite_validates_range_and_optional_elevation(
        string lat, string lon, string elev, bool expected, double eLat, double eLon, double? eElev)
    {
        var ok = EquipmentActions.TryParseSite(lat, lon, elev, out var pLat, out var pLon, out var pElev);

        ok.ShouldBe(expected);
        if (expected)
        {
            pLat.ShouldBe(eLat, 1e-9);
            pLon.ShouldBe(eLon, 1e-9);
            pElev.ShouldBe(eElev);
        }
    }

    [Theory]
    [InlineData("20", "20", "10", "5", true)]
    [InlineData("0", "0", "0", "0", true)]          // extremes accepted: warn and act coincide, floor at the horizon
    [InlineData("360", "360", "60", "60", true)]
    [InlineData("361", "0", "10", "5", false)]      // meridian out of range
    [InlineData("20", "20", "61", "5", false)]      // horizon out of range
    [InlineData("-1", "20", "10", "5", false)]      // a negative threshold
    [InlineData("twenty", "20", "10", "5", false)]  // unparseable
    public void TryParseMountLimits_validates_ranges_and_keeps_the_switch_and_responses(
        string warn, string extra, string floor, string above, bool expected)
    {
        var current = new MountLimitConfiguration(Enabled: true, MeridianResponse: MountLimitResponse.Park, HorizonResponse: MountLimitResponse.Warn);

        var ok = EquipmentActions.TryParseMountLimits(warn, extra, floor, above, current, out var parsed);

        ok.ShouldBe(expected);
        if (expected)
        {
            parsed.MeridianWarnMinutes.ShouldBe(double.Parse(warn, System.Globalization.CultureInfo.InvariantCulture));
            parsed.MeridianActionExtraMinutes.ShouldBe(double.Parse(extra, System.Globalization.CultureInfo.InvariantCulture));
            parsed.HorizonActionDeg.ShouldBe(double.Parse(floor, System.Globalization.CultureInfo.InvariantCulture));
            parsed.HorizonWarnExtraDeg.ShouldBe(double.Parse(above, System.Globalization.CultureInfo.InvariantCulture));
            // The buttons own these; the text fields must not reset them.
            parsed.Enabled.ShouldBeTrue();
            parsed.MeridianResponse.ShouldBe(MountLimitResponse.Park);
            parsed.HorizonResponse.ShouldBe(MountLimitResponse.Warn);
        }
        else
        {
            parsed.ShouldBe(current, "a rejected edit leaves the configuration alone");
        }
    }

    [Fact]
    public void DescribeMountLimits_reads_as_absolute_thresholds_or_off()
    {
        EquipmentActions.DescribeMountLimits(null).ShouldBe("Limits: off");
        EquipmentActions.DescribeMountLimits(new MountLimitConfiguration(Enabled: false)).ShouldBe("Limits: off");

        // Warn 20 + extra 20 = act at 40; floor 10 + extra 5 = warn from 15. The stored EXTRAS never show.
        var text = EquipmentActions.DescribeMountLimits(new MountLimitConfiguration(Enabled: true));
        text.ShouldContain("stop at 40 min (warn 20)");
        text.ShouldContain("stop at 10\u00b0 (warn 15\u00b0)");
    }

    [Fact]
    public void SetMountLimits_round_trips_every_other_profile_field()
    {
        var data = ProfileData.Empty with { SiteLatitude = 48.2, SiteLongitude = 16.3 };
        var limits = new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 30.0);

        var updated = EquipmentActions.SetMountLimits(data, limits);

        updated.MountLimits.ShouldBe(limits);
        updated.SiteLatitude.ShouldBe(48.2);
        EquipmentActions.SetMountLimits(updated, null).MountLimits.ShouldBeNull();
    }

    [Fact]
    public void CommitDeviceSetting_masked_setting_writes_credential_store_and_reports_weather()
    {
        ICredentialStore store = new FileCredentialStore(new FakeExternal(output));
        var owm = new OpenWeatherMapDevice();

        var result = EquipmentActions.CommitDeviceSetting(owm.DeviceUri, "apiKey", "sk-123", store);

        result.Kind.ShouldBe(EquipmentActions.DeviceSettingCommitKind.StoredSecret);
        result.NewUri.ShouldBeNull();
        result.IsWeatherSecret.ShouldBeTrue();
        // The secret landed in the store under the device-keyed slot, NOT on the URI.
        store.Get(owm.CredentialKey).ShouldBe("sk-123");
    }

    [Fact]
    public void CommitDeviceSetting_nonmasked_setting_returns_uri_with_query_param()
    {
        ICredentialStore store = new FileCredentialStore(new FakeExternal(output));
        // A key that is not a masked setting on the OWM device -> non-secret URI path.
        var owm = new OpenWeatherMapDevice();

        var result = EquipmentActions.CommitDeviceSetting(owm.DeviceUri, "units", "metric", store);

        result.Kind.ShouldBe(EquipmentActions.DeviceSettingCommitKind.UriParam);
        result.IsWeatherSecret.ShouldBeFalse();
        result.NewUri.ShouldNotBeNull();
        result.NewUri!.Query.ShouldContain("units=metric");
        // Nothing was written to the credential store.
        store.Get(ICredentialStore.KeyFor(owm.DeviceId, "units")).ShouldBeNull();
    }

    [Fact]
    public void GateSunSlew_first_click_arms_second_within_window_confirms()
    {
        var appState = new GuiAppState();
        var t0 = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromSeconds(5);

        // First click arms and stashes the pending index + expiry.
        appState.GateSunSlew(CatalogIndex.Sol, t0, window).ShouldBe(GuiAppState.SunSlewGate.Armed);
        appState.PendingSunSlewIndex.ShouldBe(CatalogIndex.Sol);

        // Second click 2s later (within the 5s window) confirms and clears the pending state.
        appState.GateSunSlew(CatalogIndex.Sol, t0 + TimeSpan.FromSeconds(2), window)
            .ShouldBe(GuiAppState.SunSlewGate.Confirmed);
        appState.PendingSunSlewIndex.ShouldBeNull();
        appState.PendingSunSlewExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void GateSunSlew_second_click_after_window_re_arms()
    {
        var appState = new GuiAppState();
        var t0 = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromSeconds(5);

        appState.GateSunSlew(CatalogIndex.Sol, t0, window).ShouldBe(GuiAppState.SunSlewGate.Armed);

        // A click 6s later is past the window -> re-arms rather than confirming.
        appState.GateSunSlew(CatalogIndex.Sol, t0 + TimeSpan.FromSeconds(6), window)
            .ShouldBe(GuiAppState.SunSlewGate.Armed);
        appState.PendingSunSlewIndex.ShouldBe(CatalogIndex.Sol);
        appState.PendingSunSlewExpiresAt.ShouldBe(t0 + TimeSpan.FromSeconds(6) + window);
    }
}
