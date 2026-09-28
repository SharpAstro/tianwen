using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// An idle rig's view lays its devices out as this computer lays out the same devices (P5b part 9 of
/// docs/plans/hardware-in-the-server.md, #935). A real node holds a rig's devices connected and runs nothing. This computer's
/// side reads them through the node's own hub, as this computer's idle poll reads its own; the rig's side reads the node's
/// device states, as a rig's view does (<see cref="RemoteRigConnection.MaybeRefreshDevicesAsync"/>). The two views are
/// compared field by field and drawn (<see cref="TabPictures"/>). Before this a rig with no run showed "No OTAs configured
/// in profile" and a card that said "NotStarted", and the TUI listed this computer's OTAs as the rig's.
/// </summary>
[Collection("Hosting")]
public class IdleRigDevicesTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset DrawnAt = new DateTimeOffset(2025, 12, 15, 21, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One OTA's telemetry as text, its temperatures to the tenth of a degree the tabs show: the two views read the devices at
    /// two instants, and a fake focuser's thermometer drifts in the meantime.
    /// </summary>
    private static string Describe(PreviewOTATelemetry o) => string.Join(", ",
        o.OtaName, o.CameraDisplayName, o.CcdTempC.ToString("F1", CultureInfo.InvariantCulture), o.SetpointC.ToString("F1", CultureInfo.InvariantCulture),
        o.CoolerPowerPct.ToString("F0", CultureInfo.InvariantCulture), o.CoolerOn, o.FocusPosition, o.FocuserTempC.ToString("F1", CultureInfo.InvariantCulture),
        o.FocuserIsMoving, o.FilterName, o.CameraConnected, o.FocuserConnected, o.FilterWheelConnected, o.UsesGainValue, o.UsesGainMode,
        o.GainMin, o.GainMax, o.CurrentGain, string.Join("/", o.GainModes.IsDefault ? [] : o.GainModes), o.SensorWidth, o.SensorHeight,
        o.RoiConstraints);

    [Fact(Timeout = 60_000)]
    public async Task AnIdleRigLaysOutItsDevicesAsThisComputerLaysOutTheSameOnes()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct);

        // Two OTAs, one whole and one a camera alone, and a mount the node does not hold, which both views show as pointing
        // nowhere known.
        var mainCamera = new FakeDevice(DeviceType.Camera, 1);
        var focuser = new FakeDevice(DeviceType.Focuser, 1);
        var filterWheel = new FakeDevice(DeviceType.FilterWheel, 1);
        var wideCamera = new FakeDevice(DeviceType.Camera, 2);
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var data = new ProfileData(mount.DeviceUri, NoneDevice.Instance.DeviceUri,
            [
                new OTAData("Main", 800, mainCamera.DeviceUri, null, focuser.DeviceUri, filterWheel.DeviceUri, null, null),
                new OTAData("Wide", 250, wideCamera.DeviceUri, null, null, null, null, null),
            ],
            SiteLatitude: 48.2, SiteLongitude: 16.3);
        var profile = new Profile(Guid.NewGuid(), "Idle rig", data);
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        foreach (var device in new DeviceBase[] { mainCamera, focuser, filterWheel, wideCamera })
        {
            await hub.ConnectAsync(device, ct);
        }

        // The rig's view, as a GUI connected to the node holds it: its profile read, then its devices, once the node has read
        // each of them (it reads a device only while somebody watches, and the rig's reads are that somebody).
        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "idle-rig-test",
            Alias = "Idle rig",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var contexts = new ViewContexts();
        await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, grants: null, new SystemTimeProvider(), NullLogger.Instance, ct)
            .ShouldNotBeNull();
        (await rig.MaybeRefreshProfileAsync(ct)).ShouldBeTrue();
        var remote = rig.Context.LiveSession;
        await NodeWait.UntilAsync("the rig's view to read every device its node holds", async token =>
        {
            await rig.MaybeRefreshDevicesAsync(token);
            var otas = remote.PreviewOTATelemetry;
            return (otas.Length == 2 && otas.All(o => o.CameraConnected) && otas[0].FocuserConnected && otas[0].FilterWheelConnected,
                string.Join(" | ", otas.Select(Describe)));
        }, ct);

        // This computer's view of the same devices, as its idle poll writes its own (AppSignalHandler.PollPreviewTelemetry):
        // each OTA through the hub's readers, and a mount the hub does not hold as pointing nowhere known.
        var local = new LiveSessionState();
        local.ResizePreviewArrays(data.OTAs.Length);
        var sampled = new PreviewOTATelemetry[data.OTAs.Length];
        for (var i = 0; i < sampled.Length; i++)
        {
            // The node's own readers, as a reading of the devices where they are (DeviceHubReadingExtensions).
            var ota = data.OTAs[i];
            sampled[i] = PreviewOTATelemetry.From(ota,
                await hub.ReadCameraAsync(ota.Camera, NullLogger.Instance, ct),
                ota.Focuser is { } focuserUri ? await hub.ReadFocuserAsync(focuserUri, NullLogger.Instance, ct) : null,
                ota.FilterWheel is { } filterWheelUri ? await hub.ReadFilterWheelAsync(filterWheelUri, NullLogger.Instance, ct) : null);
        }
        local.PreviewOTATelemetry = [.. sampled];
        local.MountState = MountState.Unknown;
        contexts.PollAll();

        remote.PreviewOTATelemetry.Select(Describe).ShouldBe(local.PreviewOTATelemetry.Select(Describe));
        remote.PreviewOTATelemetry[0].CameraDisplayName.ShouldBe(mainCamera.DisplayName, "the rig's OTA is its own, named as its device is");
        double.IsNaN(remote.MountState.RightAscension).ShouldBeTrue("a mount its node does not hold points nowhere known, never at RA 0");
        remote.HasActiveRun.ShouldBeFalse("a node running nothing is idle, as this computer is");

        // Drawn: the idle Live Session lays out the rig's OTAs and its mount, the Guider tab its placeholder, the card its idleness.
        using var localTabs = new TabPictures();
        using var remoteTabs = new TabPictures();
        var clock = new FakeTimeProviderWrapper(DrawnAt);
        var remotePictures = remoteTabs.Draw(remote, clock);
        var differences = localTabs.Draw(local, clock).Zip(remotePictures)
            .SelectMany(pair => TabPictures.Differences(pair.First, pair.Second).Select(d => $"{pair.First.Tab}: {d}"))
            .ToList();
        differences.ShouldBeEmpty();

        // And what they draw alike is the rig: its OTAs, and a pointing it does not know as dashes, never "NaN".
        var liveSession = remotePictures[0].Text;
        liveSession.ShouldContain(t => t.StartsWith($"\"{mainCamera.DisplayName}\"", StringComparison.Ordinal));
        liveSession.ShouldContain(t => t.StartsWith("\"RA --\"", StringComparison.Ordinal));
        liveSession.ShouldNotContain(t => t.Contains("NaN", StringComparison.Ordinal));
    }

    /// <summary>
    /// A view learns what its node holds from the node's pushes alone (P6): a device the node connects reaches the
    /// connection's device model as <c>DEVICE-STATE</c> arrives, with no read asked for, and so does its disconnect. Every
    /// per-frame question the view asks of a device answers from that model, never with a request.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task WhatANodeHoldsReachesItsViewByItsPushesAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct);
        var camera = new FakeDevice(DeviceType.Camera, 1);
        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "rig-device-pushes-test",
            Alias = "Pushing rig",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var contexts = new ViewContexts();
        await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, grants: null, new SystemTimeProvider(), NullLogger.Instance, ct)
            .ShouldNotBeNull();
        await NodeWait.UntilAsync("the rig to answer", _ =>
            ValueTask.FromResult((rig.Mirror.Contact.State is NodeContactState.Answering, rig.Mirror.Contact.State.ToString())), ct);

        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        await hub.ConnectAsync(camera, ct);
        await NodeWait.UntilAsync("the camera's push to reach the rig's view", _ =>
            ValueTask.FromResult((rig.IsConnected(camera.DeviceUri), $"{rig.Devices.Count} device(s)")), ct);
        rig.Device(new UriBuilder(camera.DeviceUri) { Query = "gain=100" }.Uri).ShouldNotBeNull("a device is known by its identity, whatever its query");

        await hub.DisconnectAsync(camera.DeviceUri, force: false, ct);
        await NodeWait.UntilAsync("the camera's disconnect to reach the rig's view", _ =>
            ValueTask.FromResult((!rig.IsConnected(camera.DeviceUri), rig.Device(camera.DeviceUri)?.Connected.ToString() ?? "gone")), ct);
    }

    /// <summary>
    /// A mount the rig's node holds reads on the rig's view as this computer reads it through the same readers: its pointing,
    /// J2000 by the site of the node's active profile, its name, and the node's limit verdict. The hour angle is read at two
    /// instants, so it is compared to the second.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnIdleRigsMountReadsAsThisComputerReadsTheSameOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct);
        var camera = new FakeDevice(DeviceType.Camera, 1);
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var profile = new Profile(Guid.NewGuid(), "Idle rig", new ProfileData(mount.DeviceUri, NoneDevice.Instance.DeviceUri,
            [new OTAData("Main", 800, camera.DeviceUri, null, null, null, null, null)],
            SiteLatitude: 48.2, SiteLongitude: 16.3));
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        await hub.ConnectAsync(camera, ct);
        await hub.ConnectAsync(mount, ct);

        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "idle-rig-mount-test",
            Alias = "Idle rig",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var contexts = new ViewContexts();
        await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, grants: null, new SystemTimeProvider(), NullLogger.Instance, ct)
            .ShouldNotBeNull();
        (await rig.MaybeRefreshProfileAsync(ct)).ShouldBeTrue();
        var remote = rig.Context.LiveSession;
        await NodeWait.UntilAsync("the rig's view to read its mount", async token =>
        {
            await rig.MaybeRefreshDevicesAsync(token);
            return (!double.IsNaN(remote.MountState.RaJ2000), remote.MountState.ToString());
        }, ct);

        var clock = new SystemTimeProvider();
        var read = (await hub.ReadMountAsync(mount.DeviceUri, () => TransformFactory.FromProfile(profile, clock, out _), NullLogger.Instance, ct))
            .ShouldNotBeNull();
        var seen = remote.MountState;
        seen.RightAscension.ShouldBe(read.RightAscension, 1e-6);
        seen.Declination.ShouldBe(read.Declination, 1e-6);
        seen.RaJ2000.ShouldBe(read.RaJ2000, 1e-6);
        seen.DecJ2000.ShouldBe(read.DecJ2000, 1e-6);
        seen.HourAngle.ShouldBe(read.HourAngle, 1.0 / 3600 * 10, "read at two instants, seconds apart");
        seen.PierSide.ShouldBe(read.PierSide);
        seen.IsSlewing.ShouldBe(read.IsSlewing);
        seen.IsTracking.ShouldBe(read.IsTracking);
        remote.MountDisplayName.ShouldBe(mount.DisplayName);
        remote.MountLimitVerdict.ShouldBe(MountLimitVerdict.Clear, "the node's watcher finds nothing wrong with a mount at home");
    }
}
