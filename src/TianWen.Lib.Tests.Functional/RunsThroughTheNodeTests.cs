using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// This computer's runs are its node's (P6 part 2 of docs/plans/hardware-in-the-server.md, #936): a session starts there with
/// the schedule the planner built here and the session tab's configuration, a flat run from that same configuration, polar
/// alignment claims the node's mount and camera, and an abort or a cancel goes to the node, whose feed this computer's
/// Notifications tab now shows. Through the GUI's real signal handler over a real node on its socket.
/// </summary>
[Collection("NodeProcesses")]
public class RunsThroughTheNodeTests(ITestOutputHelper output)
{
    /// <summary>M 81: circumpolar at the harness's site (48.2 N), so it is schedulable whatever night the test runs on.</summary>
    private static readonly Target M81 = new Target(9.926, 69.07, "M 81", null);

    /// <summary>A session the planner here planned, started on the harness's node and running there.</summary>
    internal static async Task<ControlledSession> StartedSessionAsync(GuiNodeHarness h, int setpointC)
    {
        var ct = TestContext.Current.CancellationToken;
        h.Node.Factory.Initialised.TrySetResult();
        // The planner's start, which the connect made, finds tonight's night: a schedule is built within it.
        await h.UntilSettledAsync(ct);
        h.Planner.AstroDark.ShouldNotBe(default, "premise: the planner has tonight's night");
        h.Planner.Proposals = [new ProposedObservation(M81)];
        h.Session.InitializeFromProfile(h.AppState.ActiveProfile, h.AppState.CameraCapabilitiesOf);
        h.Session.CameraSettings[0].SetpointTempC = (sbyte)setpointC;

        h.Post(new StartSessionSignal());
        // Until the node made the session, or the view said why it did not start one.
        await h.UntilAsync(() => !h.Node.Factory.Created.IsEmpty
            || h.AppState.Notifications.Any(static n => n.Severity is NotificationSeverity.Warning or NotificationSeverity.Error), ct);
        h.AppState.Notifications.ShouldNotContain(static n => n.Severity == NotificationSeverity.Error,
            string.Join("; ", h.AppState.Notifications.Select(static n => n.Message)));
        await h.UntilAsync(() => !h.Node.Factory.Created.IsEmpty, ct);
        var session = h.Node.Factory.Created.Last();
        await session.Started.Task.WaitAsync(ct);
        return session;
    }

    [Fact(Timeout = 60_000)]
    public async Task ASessionRunsOnTheNodeWithTheSchedulePlannedHereAndTheSessionTabsSetpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        var session = await StartedSessionAsync(h, setpointC: -7);

        session.Observations.ShouldHaveSingleItem().Target.Name.ShouldBe("M 81", "the planner's schedule, pushed whole");
        session.Configuration.SetpointCCDTemperature.TempC.ShouldBe((sbyte)-7, "the session tab's setpoint");
        session.Configuration.SiteLatitude.ShouldBe(48.2, 1e-9, "the site the plan was made at");

        // The view learns of the run from its node, as a rig's view does.
        await h.UntilAsync(() =>
        {
            h.Contexts.PollAll();
            return h.Contexts.Local.LiveSession.IsRunning;
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAbortOfThisComputersSessionGoesToItsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var session = await StartedSessionAsync(h, setpointC: -10);

        h.Post(new ConfirmAbortSessionSignal());

        await session.Cancelled.Task.WaitAsync(ct);
        session.Finalise.TrySetResult();
        await h.UntilSettledAsync(ct);
        h.AppState.Notifications.ShouldContain(n => n.Message.Contains("sent to this computer's node"));
    }

    /// <summary>
    /// The run's notes are the node's now, in the words a rig's feed uses (SessionNotes): this computer's Notifications tab
    /// shows them beside its own.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ThisComputersFeedShowsItsNodesNotesOfTheRunBesideItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        await StartedSessionAsync(h, setpointC: -10);
        var started = SessionNotes.ForRunStart(flatRun: false).Message;

        await h.UntilAsync(() => NotificationFeed.Of(h.Contexts.Local, h.AppState).Entries.Any(n => n.Message == started), ct);
        var feed = NotificationFeed.Of(h.Contexts.Local, h.AppState).Entries;
        feed.ShouldContain(n => n.Message.StartsWith("Found "), "this computer's own notes are there too");
        feed.Select(static n => n.When).ShouldBeInOrder(SortDirection.Descending, "newest first");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFlatRunRunsOnTheNodeFromTheSessionTabsConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Node.Factory.Initialised.TrySetResult();
        h.Session.InitializeFromProfile(h.AppState.ActiveProfile, h.AppState.CameraCapabilitiesOf);
        h.Session.CameraSettings[0].SetpointTempC = -12;

        h.Post(new StartFlatsSignal(FlatIlluminationChoice.Calibrator, FlatsPerFilter: 7));
        await h.UntilAsync(() => !h.Node.Factory.Created.IsEmpty, ct);
        var run = h.Node.Factory.Created.Last();
        await run.Started.Task.WaitAsync(ct);

        run.Configuration.SetpointCCDTemperature.TempC.ShouldBe((sbyte)-12, "the flats cool to the lights' setpoint");
        run.Configuration.FlatsPerFilter.ShouldBe(7);
        await h.UntilAsync(() =>
        {
            h.Contexts.PollAll();
            return h.Contexts.Local.LiveSession.IsFlatRunGoingOn;
        }, ct);

        h.Post(new CancelFlatsSignal());
        h.Contexts.Local.LiveSession.FlatCancelRequested.ShouldBeTrue("the panel reads Cancelling until the run has ended");
        await run.Cancelled.Task.WaitAsync(ct);
        run.Finalise.TrySetResult();
        await h.UntilAsync(() =>
        {
            h.Contexts.PollAll();
            return !h.Contexts.Local.LiveSession.IsFlatRunGoingOn && !h.Contexts.Local.LiveSession.FlatCancelRequested;
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task PolarAlignmentIsTheNodesRunAndItsDevicesAreTheNodesClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Post(new StartPolarAlignmentSignal(OtaIndex: 0));
        await h.UntilAsync(() => h.Claimed(h.MountUri) && h.Claimed(h.CameraUri), ct);
        h.Claimed(h.FocuserUri).ShouldBeFalse("it only reads the focuser, for the frames' cards");

        // Another action on the mount it rotates: the node refuses it, in the polar run's name.
        h.Post(new JogMountSignal(GuideDirection.North, Arcsec: 10));
        await h.UntilAsync(() => h.AppState.Notifications.Any(n => n.Message.Contains("polar alignment")), ct);

        h.Post(new CancelPolarAlignmentSignal());
        await h.UntilAsync(() => h.Contexts.Local.LiveSession.PolarRunEnded.IsCompleted, ct);
        h.Claimed(h.MountUri).ShouldBeFalse("the run gave the mount back once it had restored it");
        h.Claimed(h.CameraUri).ShouldBeFalse();
        h.Contexts.Local.LiveSession.PolarAlignmentCts.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task APolarStartIsRefusedByTheNodeWhileAnotherRunHoldsItsMount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Hub.TryAcquireLease(h.MountUri, "the imaging session", out var sessionClaim).ShouldBeTrue();
        using var _ = sessionClaim;

        h.Post(new StartPolarAlignmentSignal(OtaIndex: 0));
        await h.UntilAsync(() => h.Contexts.Local.LiveSession.PolarRunEnded.IsCompleted, ct);

        h.ShouldHaveRefused("the imaging session");
        h.Contexts.Local.LiveSession.PolarAlignmentCts.ShouldBeNull("polar never ran");
        h.Claimed(h.CameraUri).ShouldBeFalse("a refused start leaves ownership as it found it");
    }
}
