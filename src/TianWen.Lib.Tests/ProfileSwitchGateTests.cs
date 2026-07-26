using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="ProfileSwitchGate"/> -- the shared "may I change the active profile right now?"
/// check used by the GUI dropdown, the TUI profile picker, and the hosted
/// <c>PUT /api/v1/session/profile</c>. The invariant it protects: no host may swap the profile out
/// from under connected hardware (the drivers would be stranded in the hub with no UI surface
/// referencing their URIs) or a running session.
/// </summary>
public class ProfileSwitchGateTests
{
    private static IDeviceHub HubWith(params (string Uri, DeviceType Type)[] connected)
    {
        var hub = Substitute.For<IDeviceHub>();
        var list = new List<(Uri DeviceUri, IDeviceDriver Driver)>(connected.Length);
        foreach (var (uri, type) in connected)
        {
            var driver = Substitute.For<IDeviceDriver>();
            driver.DriverType.Returns(type);
            list.Add((new Uri(uri), driver));
        }
        hub.ConnectedDevices.Returns(list);
        return hub;
    }

    [Fact]
    public void Allows_WhenNothingConnectedAndNoSession()
    {
        var verdict = ProfileSwitchGate.Evaluate(HubWith(), sessionActive: false);

        verdict.Allowed.ShouldBeTrue();
        verdict.Blocker.ShouldBe(ProfileSwitchBlocker.None);
        verdict.ConnectedDevices.ShouldBeEmpty();
        verdict.Describe().ShouldBeEmpty();
    }

    [Fact]
    public void Allows_WhenNoHubComposed()
    {
        // A host with no device hub (nothing can be connected) still switches freely.
        var verdict = ProfileSwitchGate.Evaluate(null, sessionActive: false);

        verdict.Allowed.ShouldBeTrue();
        verdict.ConnectedDevices.ShouldBeEmpty();
    }

    [Fact]
    public void Blocks_WhenDevicesConnected_AndNamesThem()
    {
        var verdict = ProfileSwitchGate.Evaluate(
            HubWith(("Mount://FakeDevice/FakeMount1?port=SkyWatcher", DeviceType.Mount),
                    ("Camera://FakeDevice/FakeCamera1", DeviceType.Camera)),
            sessionActive: false);

        verdict.Allowed.ShouldBeFalse();
        verdict.Blocker.ShouldBe(ProfileSwitchBlocker.DevicesConnected);
        verdict.ConnectedDevices.ShouldBe(["Mount (FakeMount1)", "Camera (FakeCamera1)"]);

        // The message must name what is in the way and what to do about it.
        var described = verdict.Describe();
        described.ShouldContain("Disconnect");
        described.ShouldContain("Mount (FakeMount1)");
        described.ShouldContain("Camera (FakeCamera1)");
    }

    [Fact]
    public void Blocks_WhenSessionActive_EvenWithNoConnectedDevices()
    {
        // Defence in depth: a session should always hold drivers, but if the hub view were empty the
        // session flag alone still has to stop the switch.
        var verdict = ProfileSwitchGate.Evaluate(HubWith(), sessionActive: true);

        verdict.Allowed.ShouldBeFalse();
        verdict.Blocker.ShouldBe(ProfileSwitchBlocker.SessionActive);
        verdict.Describe().ShouldContain("session is running");
    }

    [Fact]
    public void SessionActive_TakesPrecedenceOverDevicesConnected()
    {
        // Both are true during a real session; "stop the session" is the actionable instruction,
        // not "disconnect the mount".
        var verdict = ProfileSwitchGate.Evaluate(
            HubWith(("Mount://FakeDevice/FakeMount1", DeviceType.Mount)),
            sessionActive: true);

        verdict.Blocker.ShouldBe(ProfileSwitchBlocker.SessionActive);
        // The device list is still carried for context even though the message talks about the session.
        verdict.ConnectedDevices.ShouldHaveSingleItem();
    }

    [Fact]
    public void Describe_ElidesBeyondFourDevices()
    {
        var verdict = ProfileSwitchGate.Evaluate(
            HubWith(("Mount://FakeDevice/M1", DeviceType.Mount),
                    ("Camera://FakeDevice/C1", DeviceType.Camera),
                    ("Focuser://FakeDevice/F1", DeviceType.Focuser),
                    ("FilterWheel://FakeDevice/W1", DeviceType.FilterWheel),
                    ("Guider://FakeDevice/G1", DeviceType.Guider),
                    ("Weather://FakeDevice/T1", DeviceType.Weather)),
            sessionActive: false);

        verdict.ConnectedDevices.Length.ShouldBe(6);
        verdict.Describe().ShouldContain("and 2 more");
        // The elided ones must not be spelled out (the dialog card has a fixed message band).
        verdict.Describe().ShouldNotContain("Weather");
    }

    [Fact]
    public void DefaultVerdict_IsAllowedAndDescribesWithoutThrowing()
    {
        // A record struct's `new()` bypasses the primary ctor, leaving ConnectedDevices *default*
        // (not empty) -- Describe must not touch .Length on it.
        var verdict = new ProfileSwitchVerdict();

        verdict.Allowed.ShouldBeTrue();
        verdict.Describe().ShouldBeEmpty();
    }
}
