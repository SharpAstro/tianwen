using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Verifies that <see cref="ActiveProfilePinnedSerialPortsProvider"/> walks every URI
/// slot on the active profile (mount, guider, optional extras, every OTA) and returns
/// <see cref="TianWen.Lib.Devices.Discovery.PinnedSerialPort"/> pairs for every
/// <c>?port=…</c> that normalises to a real OS port. Sentinel values like <c>wifi</c> /
/// <c>wpd</c> / fake-mount names must not block discovery.
/// </summary>
public class ActiveProfilePinnedSerialPortsProviderTests
{
    [Fact]
    public void NoActiveProfileReturnsEmptyList()
    {
        var appState = new GuiAppState { ActiveProfile = null };
        var provider = new ActiveProfilePinnedSerialPortsProvider(appState);

        provider.GetPinnedPorts().ShouldBeEmpty();
    }

    [Fact]
    public void MountAndOtaPortsAreAllIncludedWithExpectedUri()
    {
        var mountUri = new Uri("Mount://OnStepDevice/onstep?port=COM5");
        var focUri = new Uri("Focuser://QHYDevice/qfoc?port=COM7");
        var cfwUri = new Uri("FilterWheel://QHYDevice/cfw?port=COM8");

        var profile = MakeProfile(new ProfileData(
            Mount: mountUri,
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [
                new OTAData("Main", 1000,
                    Camera: new Uri("Camera://ZwoDevice/asi?gain=100"),
                    Cover: null,
                    Focuser: focUri,
                    FilterWheel: cfwUri,
                    PreferOutwardFocus: null,
                    OutwardIsPositive: null)
            ]));
        var provider = new ActiveProfilePinnedSerialPortsProvider(new GuiAppState { ActiveProfile = profile });

        var pins = provider.GetPinnedPorts();

        pins.Count.ShouldBe(3);
        pins.ShouldContain(p => p.Port == "serial:COM5" && p.ExpectedUri == mountUri);
        pins.ShouldContain(p => p.Port == "serial:COM7" && p.ExpectedUri == focUri);
        pins.ShouldContain(p => p.Port == "serial:COM8" && p.ExpectedUri == cfwUri);
    }

    [Fact]
    public void SentinelPortValuesAreIgnored()
    {
        // ?port=wifi (Canon), ?port=SkyWatcher (fake mount) — not OS ports, must not filter.
        var profile = MakeProfile(new ProfileData(
            Mount: new Uri("Mount://FakeDevice/fake?port=SkyWatcher"),
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [
                new OTAData("Main", 1000,
                    Camera: new Uri("Camera://CanonDevice/wifi-cam?port=wifi&host=192.168.0.42"),
                    Cover: null,
                    Focuser: null,
                    FilterWheel: null,
                    PreferOutwardFocus: null,
                    OutwardIsPositive: null)
            ]));
        var provider = new ActiveProfilePinnedSerialPortsProvider(new GuiAppState { ActiveProfile = profile });

        provider.GetPinnedPorts().ShouldBeEmpty();
    }

    [Fact]
    public void MissingPortQueryIsIgnored()
    {
        var profile = MakeProfile(new ProfileData(
            Mount: new Uri("Mount://NoneDevice/none"),
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: []));
        var provider = new ActiveProfilePinnedSerialPortsProvider(new GuiAppState { ActiveProfile = profile });

        provider.GetPinnedPorts().ShouldBeEmpty();
    }

    [Fact]
    public void ChangingActiveProfileReflectsImmediately()
    {
        var appState = new GuiAppState();
        var provider = new ActiveProfilePinnedSerialPortsProvider(appState);

        provider.GetPinnedPorts().ShouldBeEmpty();

        appState.ActiveProfile = MakeProfile(new ProfileData(
            Mount: new Uri("Mount://OnStepDevice/onstep?port=COM9"),
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: []));

        provider.GetPinnedPorts().Any(p => p.Port == "serial:COM9").ShouldBeTrue();
    }

    private static Profile MakeProfile(ProfileData data)
        => new Profile(Guid.NewGuid(), "Test", data);
}
