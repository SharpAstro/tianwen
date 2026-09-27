using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Equipment tab acts through this computer's node (P6 part 2 of docs/plans/hardware-in-the-server.md, #936): a device
/// connects and disconnects as the node's job, a profile edit is written by the node's one profile writer at the revision it
/// was read at, and the node's own gates answer, in their own words. Through the GUI's real signal handler over a real node
/// on its socket (<see cref="GuiNodeHarness"/>).
/// </summary>
[Collection("NodeProcesses")]
public class EquipmentThroughTheNodeTests(ITestOutputHelper output)
{
    private async Task<StoredProfile> StoredAsync(GuiNodeHarness h) =>
        (await Profile.TryReadStoredAsync(h.Node.External, h.Profile.ProfileId, TestContext.Current.CancellationToken)).ShouldNotBeNull();

    [Fact(Timeout = 60_000)]
    public async Task ADeviceConnectsAsTheNodesJob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        await h.Hub.DisconnectAsync(h.FocuserUri, force: true, ct);
        await h.Local.RefreshDevicesNowAsync(ct);
        h.Local.IsConnected(h.FocuserUri).ShouldBeFalse();

        h.Post(new ConnectDeviceSignal(h.FocuserUri));
        h.Equipment.PendingTransitions.ContainsKey(h.FocuserUri).ShouldBeTrue("the row shows the connect in flight");
        await h.UntilAsync(() => !h.Equipment.PendingTransitions.ContainsKey(h.FocuserUri), ct);

        h.Hub.IsConnected(h.FocuserUri).ShouldBeTrue("the node holds it");
        h.Local.IsConnected(h.FocuserUri).ShouldBeTrue("and the view knows at once, without waiting for its next read");
        h.AppState.Notifications.ShouldContain(n => n.Severity == NotificationSeverity.Info);
    }

    [Fact(Timeout = 60_000)]
    public async Task AProfileEditIsWrittenByTheNodeAtTheRevisionItWasReadAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var before = (await StoredAsync(h)).Revision;
        var profile = h.AppState.ActiveProfile.ShouldNotBeNull();

        h.Post(new UpdateProfileSignal(profile.Data.ShouldNotBeNull() with { GuiderFocalLength = 240 }));
        await h.UntilSettledAsync(ct);

        var stored = await StoredAsync(h);
        stored.Profile.Data.ShouldNotBeNull().GuiderFocalLength.ShouldBe(240);
        stored.Revision.ShouldNotBe(before);
        h.Local.ProfileRevision.ShouldBe(stored.Revision, "the view adopted the write, so its next edit names this revision");
        h.AppState.ActiveProfile.ShouldNotBeNull().Data.ShouldNotBeNull().GuiderFocalLength.ShouldBe(240);
    }

    /// <summary>
    /// Another client (or the node's own write after a connect) changed the profile after this view read it: the node refuses
    /// the view's edit as stale (412), and the view makes it again onto the profile as it is now, so neither change is lost.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnEditOfAProfileThatMovedOnIsMadeAgainOntoItAndLosesNeitherChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var read = h.AppState.ActiveProfile.ShouldNotBeNull();
        var elsewhere = await StoredAsync(h);
        var other = await new TianWenNodeClient(h.Node.Client).UpdateProfileAsync(h.Profile.ProfileId,
            elsewhere.Profile.Data.ShouldNotBeNull() with { SiteElevation = 1234 }, elsewhere.Revision, null, ct);
        other.IsSuccess.ShouldBeTrue(other.Error);

        h.Post(new UpdateProfileSignal(read.Data.ShouldNotBeNull() with { GuiderFocalLength = 240 }));
        await h.UntilSettledAsync(ct);

        var stored = (await StoredAsync(h)).Profile.Data.ShouldNotBeNull();
        stored.GuiderFocalLength.ShouldBe(240, "the view's edit");
        stored.SiteElevation.ShouldBe(1234, "the change made elsewhere survives it");
        h.AppState.Notifications.ShouldNotContain(n => n.Severity == NotificationSeverity.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task SwitchingTheProfileWhileTheRigIsConnectedIsRefusedByTheNodeInItsWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var another = new Profile(Guid.NewGuid(), "Another rig", ProfileData.Empty);
        await another.SaveAsync(h.Node.External, ct);
        h.Post(new DiscoverDevicesSignal());
        await h.UntilAsync(() => h.Equipment.AllProfiles.Any(p => p.ProfileId == another.ProfileId), ct);

        h.Post(new SwitchProfileSignal(another.ProfileId));
        await h.UntilSettledAsync(ct);

        h.Equipment.ProfileSwitchBlocked.ShouldNotBeNull().ShouldContain("connected");
        h.ShouldHaveRefused("Cannot switch to 'Another rig'");
        h.AppState.ActiveProfile.ShouldNotBeNull().ProfileId.ShouldBe(h.Profile.ProfileId);
        h.Node.Node.ActiveProfileId.ShouldBe(h.Profile.ProfileId);
    }

    [Fact(Timeout = 60_000)]
    public async Task EditingThisComputersSiteWhileARigIsOnShowLeavesTheRigsSite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var rig = h.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        rig.RigProfile = new Profile(Guid.NewGuid(), "Observatory rig", new ProfileData(
            Mount: new FakeDevice(DeviceType.Mount, 7).DeviceUri, Guider: new FakeDevice(DeviceType.Guider, 7).DeviceUri,
            OTAs: [new OTAData("Rig scope", 1000, new FakeDevice(DeviceType.Camera, 7).DeviceUri, null, null, null, null, null)],
            SiteLatitude: -33.87, SiteLongitude: 151.21));
        h.Contexts.Activate(rig).ShouldBeTrue();
        h.Handler.CheckRecompute();
        await h.Tracker.DrainAsync();

        // The Equipment tab edits this computer's profile, which a rig's view is not planned with.
        h.Equipment.LatitudeInput.Text = "51.5";
        h.Equipment.LongitudeInput.Text = "-0.1";
        h.Equipment.ElevationInput.Text = "20";
        await h.Equipment.LatitudeInput.OnCommit.ShouldNotBeNull()("51.5");

        h.AppState.ActiveProfile.ShouldNotBeNull().Data.ShouldNotBeNull().SiteLatitude.ShouldBe(51.5, "this computer's profile took the edit");
        (await StoredAsync(h)).Profile.Data.ShouldNotBeNull().SiteLatitude.ShouldBe(51.5, "written by the node");
        h.Planner.SiteLatitude.ShouldBe(-33.87, 1e-9, "the rig on show is still planned at its own site");
    }

    /// <summary>
    /// A cooled camera a run holds is refused as the run's, and never offered the [Warm &amp; Off] [Force Off] strip, which
    /// would invite the user to break their own run: the node's word on the holder is asked before its word on the cooler.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACooledCameraARunHoldsIsRefusedAsTheRunsBeforeAnyWarmUpIsOffered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Hub.TryGetConnectedDriver<ICameraDriver>(h.CameraUri, out var camera).ShouldBeTrue();
        await camera.ShouldNotBeNull().SetCoolerOnAsync(true, ct);
        h.Hub.TryAcquireLease(h.CameraUri, "the imaging session", out var lease).ShouldBeTrue();
        using var _ = lease;

        h.Post(new DisconnectDeviceSignal(h.CameraUri));
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("the imaging session");
        h.Equipment.PendingDisconnectConfirm.ShouldBeNull("a camera a run holds was offered the warm/force strip");
        h.Hub.IsConnected(h.CameraUri).ShouldBeTrue();
    }

    /// <summary>Another client writes the profile: its push asks this view to read it again now, not in two minutes.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AProfileWrittenElsewhereIsReadAgainAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Local.ProfileRefreshDue.ShouldBeFalse("read at the connect");
        var stored = await StoredAsync(h);

        (await new TianWenNodeClient(h.Node.Client).UpdateProfileAsync(h.Profile.ProfileId,
            stored.Profile.Data.ShouldNotBeNull() with { SiteElevation = 1234 }, stored.Revision, null, ct)).IsSuccess.ShouldBeTrue();

        await h.UntilAsync(() => h.Local.ProfileRefreshDue, ct);
        await h.Local.MaybeRefreshProfileAsync(ct);
        h.AppState.ActiveProfile.ShouldNotBeNull().Data.ShouldNotBeNull().SiteElevation.ShouldBe(1234);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACoolerSetpointCoolsThroughTheNodesRamp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Post(new SetCoolerSetpointSignal(h.CameraUri, -5));

        // The ramp runs on for minutes, on the node: what the node records is its target, never a step it reached.
        await h.UntilAsync(() => h.Hub.TryGetCoolerIntent(h.CameraUri, out var intent) && intent == CoolerIntent.CoolTo(-5), ct);
        h.AppState.Notifications.ShouldContain(n => n.Message.Contains("Cooling to -5.0"));
        h.AppState.Notifications.ShouldNotContain(n => n.Severity == NotificationSeverity.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheDeviceListIsWhatTheNodeListedAndTheCamerasCapabilitiesComeWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Equipment.DiscoveredDevices.ShouldAllBe(d => d is NodeDevice);
        h.Equipment.DiscoveredDevices.ShouldContain(d => DeviceBase.SameDevice(d.DeviceUri, h.CameraUri));
        h.AppState.CameraCapabilitiesOf(h.CameraUri).ShouldNotBeNull().CanCool.ShouldBeTrue();

        h.Post(new DiscoverDevicesSignal(IncludeFake: false));
        await h.UntilSettledAsync(ct);
        h.Equipment.DiscoveredDevices.ShouldNotContain(d => DeviceBase.SameDevice(d.DeviceUri, h.CameraUri),
            "the fake devices are listed only when asked for");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCoolerSparklineSamplesWhatTheNodeReadOncePerReading()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.AppState.ActiveTab = GuiTab.Equipment;
        await h.Local.RefreshDevicesNowAsync(ct);
        var key = h.CameraUri.GetLeftPart(UriPartial.Path);

        h.Handler.PollCameraTelemetry();
        h.Handler.PollCameraTelemetry();

        h.Equipment.CameraTelemetry.TryGetValue(key, out var buffer).ShouldBeTrue("a connected camera is sampled from the node's reading");
        buffer.ShouldNotBeNull().Count.ShouldBe(1, "the same reading twice is one sample");
    }
}
