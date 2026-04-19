using Shouldly;
using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Verifies that <see cref="ActiveProfilePinnedSerialPortsProvider"/> walks every URI
/// slot on the active profile (mount, guider, optional extras, every OTA) and only
/// returns port values that normalise to real OS ports — sentinel values like
/// <c>wifi</c> / <c>wpd</c> / fake-mount names must not block discovery.
/// </summary>
public class ActiveProfilePinnedSerialPortsProviderTests
{
    [Fact]
    public void NoActiveProfileReturnsEmptySet()
    {
        var appState = new GuiAppState { ActiveProfile = null };
        var provider = new ActiveProfilePinnedSerialPortsProvider(appState);

        provider.GetPinnedPorts().ShouldBeEmpty();
    }

    [Fact]
    public void MountAndOtaPortsAreAllIncluded()
    {
        var profile = MakeProfile(new ProfileData(
            Mount: new Uri("Mount://OnStepDevice/onstep?port=COM5"),
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [
                new OTAData("Main", 1000,
                    Camera: new Uri("Camera://ZwoDevice/asi?gain=100"),
                    Cover: null,
                    Focuser: new Uri("Focuser://QHYDevice/qfoc?port=COM7"),
                    FilterWheel: new Uri("FilterWheel://QHYDevice/cfw?port=COM8"),
                    PreferOutwardFocus: null,
                    OutwardIsPositive: null)
            ]));
        var provider = new ActiveProfilePinnedSerialPortsProvider(new GuiAppState { ActiveProfile = profile });

        var pins = provider.GetPinnedPorts();

        pins.ShouldContain("serial:COM5");
        pins.ShouldContain("serial:COM7");
        pins.ShouldContain("serial:COM8");
        pins.Count.ShouldBe(3);
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

        provider.GetPinnedPorts().ShouldContain("serial:COM9");
    }

    private static Profile MakeProfile(ProfileData data)
        => new Profile(Guid.NewGuid(), "Test", data);
}
