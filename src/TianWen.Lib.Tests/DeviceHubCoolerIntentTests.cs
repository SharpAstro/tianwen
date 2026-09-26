using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What each camera's cooler is being ASKED to do, as the hub keeps it for the node's crash journal (P1 of
/// docs/plans/hardware-in-the-server.md, #917): the target of a ramp, never a step on the way, and a warm-up as a warm-up.
/// The session's ramp records its target (<c>SessionCoolingTests</c>) and the Alpaca plane what it commanded
/// (<c>AlpacaServerRoundTripTests</c>).
/// </summary>
public class DeviceHubCoolerIntentTests(ITestOutputHelper output)
{
    private (FakeExternal External, IDeviceHub Hub) Build()
    {
        var external = new FakeExternal(output);
        return (external, external.BuildServiceProvider().GetRequiredService<IDeviceHub>());
    }

    [Fact]
    public async Task AnIntentIsKeptPerCameraAndForgottenWhenTheCameraGoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub) = Build();
        var camera = new FakeDevice(DeviceType.Camera, 1);
        await hub.ConnectAsync(camera, ct);
        var changes = 0;
        hub.CoolerIntentChanged += (_, _) => changes++;

        hub.TryGetCoolerIntent(camera.DeviceUri, out _).ShouldBeFalse("nothing has asked anything of it");
        hub.SetCoolerIntent(camera.DeviceUri, CoolerIntent.CoolTo(-10));
        hub.SetCoolerIntent(camera.DeviceUri, CoolerIntent.CoolTo(-10));

        hub.TryGetCoolerIntent(new Uri(camera.DeviceUri + "?port=elsewhere"), out var intent).ShouldBeTrue("the device, not its query");
        intent.ShouldBe(CoolerIntent.CoolTo(-10));
        changes.ShouldBe(1, "the same intent again is no change, and no journal write");

        await hub.DisconnectAsync(camera.DeviceUri, cancellationToken: ct);
        hub.TryGetCoolerIntent(camera.DeviceUri, out _).ShouldBeFalse("a camera the hub no longer holds is not the node's to re-establish");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheHubsWarmUpIsRecordedAsAWarmUpThenOff()
    {
        var ct = TestContext.Current.CancellationToken;
        var (external, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        var seen = new List<CoolerIntent>();
        hub.CoolerIntentChanged += (_, _) =>
        {
            if (hub.TryGetCoolerIntent(device.DeviceUri, out var now))
            {
                seen.Add(now);
            }
        };

        await hub.WarmAndCoolerOffAsync(device.DeviceUri, external.TimeProvider, NullLogger.Instance, ct);

        seen.ShouldBe([CoolerIntent.Warm, CoolerIntent.Off], "a node that crashed mid-ramp goes on warming, never cools back down");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheHubCoolsThroughTheSessionsRampAndRecordsTheTarget()
    {
        // What a node re-establishes after a crash: the session's own cool-down, never a jump to the setpoint.
        var ct = TestContext.Current.CancellationToken;
        var (external, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);

        var reached = await hub.CoolToSetpointAsync(device.DeviceUri, -10.4, TimeSpan.FromMinutes(1), external.TimeProvider, NullLogger.Instance, ct);

        reached.ShouldBeTrue();
        (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue();
        (await camera.GetCCDTemperatureAsync(ct)).ShouldBe(-10, 1.0);
        hub.TryGetCoolerIntent(device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.CoolTo(-10), "a whole degree, as the ramp takes it");
    }

    [Fact]
    public async Task TheHubCoolsNothingItDoesNotHold()
    {
        var (external, hub) = Build();

        (await hub.CoolToSetpointAsync(new FakeDevice(DeviceType.Camera, 1).DeviceUri, -10, TimeSpan.FromMinutes(1), external.TimeProvider,
            NullLogger.Instance, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task ACommandedCoolerIsRecordedAsWhatTheCommandLeftItDoing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);

        await camera.SetSetCCDTemperatureAsync(-5, ct);
        await hub.RecordCommandedCoolerAsync(device.DeviceUri, NullLogger.Instance, ct);
        hub.TryGetCoolerIntent(device.DeviceUri, out var off).ShouldBeTrue();
        off.ShouldBe(CoolerIntent.Off, "a setpoint with the cooler off asks nothing of it yet");

        await camera.SetCoolerOnAsync(true, ct);
        await hub.RecordCommandedCoolerAsync(device.DeviceUri, NullLogger.Instance, ct);
        hub.TryGetCoolerIntent(device.DeviceUri, out var cooling).ShouldBeTrue();
        cooling.ShouldBe(CoolerIntent.CoolTo(-5));
    }

    [Fact]
    public async Task ACoolerThatCannotBeReadBackKeepsItsIntentAndFailsNothing()
    {
        // The command has succeeded by then: a failed read-back must not turn it into a fault.
        var ct = TestContext.Current.CancellationToken;
        var (_, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = Substitute.For<ICameraDriver>();
        camera.Connected.Returns(true);
        camera.CanGetCoolerOn.Returns(true);
        camera.GetCoolerOnAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException<bool>(new InvalidOperationException("USB gone")));
        await hub.AdoptAsync(device, camera, ct);
        hub.SetCoolerIntent(device.DeviceUri, CoolerIntent.CoolTo(-10));

        await Should.NotThrowAsync(hub.RecordCommandedCoolerAsync(device.DeviceUri, NullLogger.Instance, ct).AsTask());

        hub.TryGetCoolerIntent(device.DeviceUri, out var kept).ShouldBeTrue();
        kept.ShouldBe(CoolerIntent.CoolTo(-10));
    }

    [Fact]
    public async Task ACoolerThatNamesNoSetpointRecordsNoIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = Substitute.For<ICameraDriver>();
        camera.Connected.Returns(true);
        camera.CanGetCoolerOn.Returns(true);
        camera.CanSetCCDTemperature.Returns(true);
        camera.GetCoolerOnAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetSetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(double.NaN));
        await hub.AdoptAsync(device, camera, ct);

        await hub.RecordCommandedCoolerAsync(device.DeviceUri, NullLogger.Instance, ct);

        hub.TryGetCoolerIntent(device.DeviceUri, out _).ShouldBeFalse("a cool-down to nothing is no intent");
    }

    [Theory]
    [InlineData(SetpointTempKind.Normal, CoolerIntentKind.Cool)]
    [InlineData(SetpointTempKind.Ambient, CoolerIntentKind.Warm)]
    public void ASessionRampRecordsItsTarget(SetpointTempKind kind, CoolerIntentKind expected)
    {
        var intent = Session.IntentOf(new SetpointTemp(-10, kind)).ShouldNotBeNull();

        intent.Kind.ShouldBe(expected);
        if (expected is CoolerIntentKind.Cool)
        {
            intent.SetpointC.ShouldBe(-10);
        }
    }

    [Fact]
    public void HoldingAtTheSensorsOwnTemperatureNamesNoTarget()
    {
        Session.IntentOf(new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD)).ShouldBeNull();
    }
}
