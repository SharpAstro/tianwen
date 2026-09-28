using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Live Session's and the sky map's device actions go to this computer's node (P6 part 2 of
/// docs/plans/hardware-in-the-server.md, #936): a preview, a solve, a focuser move, a mount nudge and a goto are the node's
/// jobs, refused by it on a device a run holds in the run's name; and the frame on show is the node's, which the view's mirror
/// fetches, as a rig's view does. Through the GUI's real signal handler over a real node on its socket.
/// </summary>
[Collection("NodeProcesses")]
public class LiveSessionThroughTheNodeTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task APreviewIsTheNodesAndItsFrameReachesTheViewThroughItsMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.AppState.ActiveTab = GuiTab.LiveSession;
        var view = h.Contexts.Local.LiveSession;

        h.Post(new TakePreviewSignal(OtaIndex: 0, ExposureSeconds: 0.5));
        view.PreviewCapturing[0].ShouldBeTrue("the panel shows the exposure in flight");

        // The host's per-frame poll: the view on show asks its mirror for the frames its tab draws.
        await h.UntilAsync(() =>
        {
            h.Contexts.PollAll();
            return view.LastCapturedImages is [{ }, ..];
        }, ct);
        await h.UntilAsync(() => !view.PreviewCapturing[0], ct);
        h.AppState.Notifications.ShouldContain(n => n.Message.Contains("Preview captured"));

        // The node runs no session, so each state poll is its 404: the frame the node shows stays on the view through them.
        var shown = h.Local.Mirror.LastCapturedImages[0].ShouldNotBeNull();
        await h.Local.Mirror.PollOnceAsync(ct);
        await h.Local.Mirror.PollOnceAsync(ct);
        h.Local.Mirror.LastCapturedImages.ShouldHaveSingleItem().ShouldBeSameAs(shown, "an idle node's frame is kept, not blanked each poll");
    }

    [Fact(Timeout = 60_000)]
    public async Task APreviewOnACameraARunHoldsIsRefusedByTheNodeInTheRunsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Hub.TryAcquireLease(h.CameraUri, "the imaging session", out var lease).ShouldBeTrue();
        using var _ = lease;

        h.Post(new TakePreviewSignal(OtaIndex: 0, ExposureSeconds: 0.5));
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("the imaging session");
        h.Contexts.Local.LiveSession.PreviewCapturing[0].ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AFocuserJogMovesTheNodesFocuser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Hub.TryGetConnectedDriver<IFocuserDriver>(h.FocuserUri, out var focuser).ShouldBeTrue();
        var before = await focuser.ShouldNotBeNull().GetPositionAsync(ct);

        h.Post(new JogFocuserSignal(OtaIndex: 0, Steps: 50));
        await h.UntilSettledAsync(ct);

        (await focuser.GetPositionAsync(ct)).ShouldBe(before + 50);
        h.AppState.Notifications.ShouldNotContain(n => n.Severity == NotificationSeverity.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task AMountNudgeIsTheNodesPulse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Post(new JogMountSignal(GuideDirection.North, Arcsec: 10));
        await h.UntilSettledAsync(ct);

        h.AppState.Notifications.ShouldContain(n => n.Message.Contains("nudged North"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASolveOfAnOtaWithNoFrameIsRefusedByTheNodeAndTheButtonComesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Post(new PlateSolvePreviewSignal(OtaIndex: 0));
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("no frame");
        h.Contexts.Local.LiveSession.PreviewPlateSolving[0].ShouldBeFalse();
    }

    /// <summary>
    /// The control of <c>GuiContextGatingTests</c>: with this computer's rig on screen a Goto does drive it, as the node's
    /// slew job, so a refusal there is the context's doing and not a rig the harness failed to wire. Whether the mount can
    /// reach M 42 at the test's hour is the node's to answer; that it was asked is what this shows.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AGotoWithTheLocalRigOnScreenIsTheNodesSlew()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        // A goto the mount lands in seconds at any hour: beside where it points, near the pole, which is always up at the
        // harness's 48.2 N. M 42 was a slew of up to 150 degrees at the fake mount's 1.5 a second once it had risen past the
        // planner's floor, which outlasted the test's timeout at some hours of the day and not others.
        h.Hub.TryGetConnectedDriver<IMountDriver>(h.MountUri, out var mount).ShouldBeTrue();
        var ra = await mount.GetRightAscensionAsync(ct);
        h.Post(new SkyMapSlewToObjectSignal("Near the pole", ra, 85, Index: null, ObjectType.Unknown));
        await h.UntilSettledAsync(ct);

        var jobs = (await new TianWenNodeClient(h.Node.Client).GetJobsAsync(ct)).Value.ShouldNotBeNull();
        jobs.ShouldContain(j => j.Kind == "slew" && DeviceBase.SameDevice(new Uri(j.DeviceUri!), h.MountUri));
        h.AppState.Notifications.ShouldNotContain(n => n.Message.Contains("runs on this computer"));
        h.SkyMap.ActiveSlewTarget.ShouldBeNull("the marker goes once the slew has ended, however it ended");
    }
}
