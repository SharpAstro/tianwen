using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.RemoteSessionMirrorDriveTests;

namespace TianWen.Lib.Tests;

/// <summary>#2507: which notes become a desktop alert, how they group, and the two feeds that reach the policy. In memory,
/// on the fake clock, against a fake sink.</summary>
public class DesktopAlertsTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 22, 0, 0, TimeSpan.Zero);

    private sealed class FakeSink : IDesktopAlert
    {
        public List<DesktopAlert> Shown { get; } = [];

        public bool IsAvailable { get; set; } = true;

        public DesktopAlertResult Show(DesktopAlert alert)
        {
            Shown.Add(alert);
            return DesktopAlertResult.Ok;
        }
    }

    private sealed class Rig
    {
        public FakeSink Sink { get; } = new();
        public FakeTimeProviderWrapper Clock { get; } = new(Now);
        public bool InFront { get; set; }
        public DesktopAlerts Alerts { get; }

        public Rig(DesktopAlertLevel level = DesktopAlertLevel.ErrorsAndSafety)
        {
            Alerts = new DesktopAlerts(Sink, Clock, () => InFront) { Level = level };
        }

        public async Task<IReadOnlyList<DesktopAlert>> DrainAsync()
        {
            Alerts.Complete();
            await Alerts.RunAsync(TestContext.Current.CancellationToken);
            return Sink.Shown;
        }
    }

    private static NotificationEntry Note(NotificationSeverity severity, NoteKind kind = NoteKind.None, string message = "something happened")
        => new(Now, severity, message, kind);

    [Fact]
    public void AnErrorAlertsWhileTheWindowIsNotInFrontAndNotWhileItIs()
    {
        var rig = new Rig();
        rig.Alerts.Decide(Note(NotificationSeverity.Error), null, null).ShouldNotBeNull();

        rig.InFront = true;
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed), null, null).ShouldBeNull();
    }

    [Fact]
    public void WithNoHostNothingAlerts()
    {
        var alerts = new DesktopAlerts(new FakeSink(), new FakeTimeProviderWrapper(Now), windowInFront: null);
        alerts.Decide(Note(NotificationSeverity.Error), null, null).ShouldBeNull();
    }

    [Fact]
    public void ASinkWithoutAlertsGetsNone()
    {
        var rig = new Rig();
        rig.Sink.IsAvailable = false;
        rig.Alerts.Decide(Note(NotificationSeverity.Error), null, null).ShouldBeNull();
    }

    [Theory]
    [InlineData(NoteKind.SessionAborted)]
    [InlineData(NoteKind.MountLimit)]
    [InlineData(NoteKind.RunStoppedUnwatched)]
    [InlineData(NoteKind.RunFaulted)]
    public void AnAllowlistedWarningAlertsAtTheDefaultLevel(NoteKind kind)
        => new Rig().Alerts.Decide(Note(NotificationSeverity.Warning, kind), null, null).ShouldNotBeNull();

    [Theory]
    [InlineData(NoteKind.None)]
    [InlineData(NoteKind.GuideStarLost)]
    [InlineData(NoteKind.PlateSolveFailed)]
    public void AnotherWarningDoesNotAlertAtTheDefaultLevelButDoesAtAllWarnings(NoteKind kind)
    {
        new Rig().Alerts.Decide(Note(NotificationSeverity.Warning, kind), null, null).ShouldBeNull();
        new Rig(DesktopAlertLevel.AllWarnings).Alerts.Decide(Note(NotificationSeverity.Warning, kind), null, null).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(DesktopAlertLevel.Off, NotificationSeverity.Error)]
    [InlineData(DesktopAlertLevel.Off, NotificationSeverity.Warning)]
    [InlineData(DesktopAlertLevel.ErrorsAndSafety, NotificationSeverity.Info)]
    [InlineData(DesktopAlertLevel.AllWarnings, NotificationSeverity.Info)]
    public void OffAndInfoNeverAlert(DesktopAlertLevel level, NotificationSeverity severity)
        => new Rig(level).Alerts.Decide(Note(severity, NoteKind.SessionAborted), null, null).ShouldBeNull();

    [Fact]
    public void ARepeatInsideTheWindowIsHeldBackAndTheNextAlertCountsThem()
    {
        var rig = new Rig();
        var first = rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "boom"), "rig-1", "Roof rig");
        first.ShouldNotBeNull().Body.ShouldBe("boom");

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "boom"), "rig-1", "Roof rig").ShouldBeNull();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "boom"), "rig-1", "Roof rig").ShouldBeNull();

        // Another node, and another kind, are their own groups.
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "boom"), "rig-2", "Dome").ShouldNotBeNull();
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.EnhanceFailed, "boom"), "rig-1", "Roof rig").ShouldNotBeNull();

        rig.Clock.Advance(DesktopAlerts.GroupingWindow);
        var next = rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "boom again"), "rig-1", "Roof rig");
        next.ShouldNotBeNull().Body.ShouldBe("boom again (2 similar held back)");

        rig.Clock.Advance(DesktopAlerts.GroupingWindow);
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed, "third"), "rig-1", "Roof rig").ShouldNotBeNull().Body.ShouldBe("third");
    }

    [Fact]
    public void NotesWithNoKindGroupByTheirWording()
    {
        var rig = new Rig();
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.None, "camera lost"), "old-node", "Old rig").ShouldNotBeNull();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        // A different error from the same older node inside the window is not a repeat.
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.None, "mount lost"), "old-node", "Old rig").ShouldNotBeNull();

        // The same wording again is.
        rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.None, "camera lost"), "old-node", "Old rig").ShouldBeNull();
    }

    // An older node that words each error differently takes a slot per wording; past the threshold the slots whose window has
    // passed are swept, and one still inside its window stays (its repeat is still held back).
    [Fact]
    public void SlotsPastTheirWindowAreSweptOnceTheThresholdIsPassed()
    {
        var rig = new Rig();
        DesktopAlert? Error(string message) => rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.None, message), "old-node", "Old rig");

        for (var i = 0; i < DesktopAlerts.GroupSweepThreshold - 1; i++)
        {
            Error($"camera lost at {i}").ShouldNotBeNull();
        }
        rig.Clock.Advance(DesktopAlerts.GroupingWindow - TimeSpan.FromMinutes(1));
        Error("recent").ShouldNotBeNull();
        rig.Alerts.GroupCount.ShouldBe(DesktopAlerts.GroupSweepThreshold);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        // One past the threshold: the slots a whole window old go, the recent one and this one stay.
        Error("one more").ShouldNotBeNull();

        rig.Alerts.GroupCount.ShouldBe(2);
        Error("recent").ShouldBeNull();
        Error("camera lost at 0").ShouldNotBeNull().Body.ShouldBe("camera lost at 0");
    }

    [Fact]
    public void TheGroupingIsTimedOnThisClientAndNotTheNotesStamp()
    {
        var rig = new Rig();
        rig.Alerts.Decide(new NotificationEntry(Now, NotificationSeverity.Error, "a"), "n", "N").ShouldNotBeNull();
        // The node's clock is an hour ahead; this client's has not moved: still a repeat.
        rig.Alerts.Decide(new NotificationEntry(Now.AddHours(1), NotificationSeverity.Error, "a"), "n", "N").ShouldBeNull();
    }

    [Fact]
    public void AnErrorIsUrgentAndAWarningNormalAndATagIsTheKindOrTheSeverityAndWording()
    {
        var rig = new Rig();
        rig.Alerts.Decide(Note(NotificationSeverity.Error), null, null).ShouldNotBeNull().ShouldSatisfyAllConditions(
            a => a.Urgency.ShouldBe(DesktopAlertUrgency.Urgent),
            a => a.Tag.ShouldBe("Error:something happened"));
        rig.Alerts.Decide(Note(NotificationSeverity.Warning, NoteKind.MountLimit), null, null).ShouldNotBeNull().ShouldSatisfyAllConditions(
            a => a.Urgency.ShouldBe(DesktopAlertUrgency.Normal),
            a => a.Tag.ShouldBe("MountLimit"));
    }

    [Fact]
    public void ARigsNoteNamesTheRigAndCarriesItsLinkAndThisComputersDoesNot()
    {
        var rig = new Rig();
        var remote = rig.Alerts.Decide(Note(NotificationSeverity.Error), "node-7", "Observatory").ShouldNotBeNull();
        remote.Title.ShouldBe("Observatory");
        remote.Group.ShouldBe("node-7");
        remote.Link.ShouldBe(DesktopAlertLink.For("node-7"));

        var own = rig.Alerts.Decide(Note(NotificationSeverity.Error, NoteKind.SessionFailed), null, "ignored").ShouldNotBeNull();
        own.Title.ShouldBe("This computer");
        own.Group.ShouldBe(DesktopAlerts.ThisComputerGroup);
        own.Link.ShouldBe(DesktopAlertLink.For(null));
    }

    [Fact]
    public async Task OfferHandsTheAlertToTheConsumerAndNotTheCallersThread()
    {
        var rig = new Rig();
        rig.Alerts.Offer(Note(NotificationSeverity.Error), null, null);
        rig.Sink.Shown.ShouldBeEmpty("Offer only queues");

        (await rig.DrainAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ANoteRecordedThroughTheAppReachesTheSinkAndWithNoAlertsSetNothingChanges()
    {
        var rig = new Rig();
        var app = new GuiAppState { DesktopAlerts = rig.Alerts };
        app.RecordNotification(Now, NotificationSeverity.Error, "Session failed: boom", NoteKind.SessionFailed);
        app.RecordNotification(Now, NotificationSeverity.Info, "fine");
        (await rig.DrainAsync()).Select(a => a.Body).ShouldBe(["Session failed: boom"]);
        app.Notifications.Length.ShouldBe(2);

        var plain = new GuiAppState();
        plain.RecordNotification(Now, NotificationSeverity.Error, "x");
        plain.Notifications.Length.ShouldBe(1);
    }

    private static WebSocketEventDto Pushed(NotificationDto note)
    {
        var json = JsonSerializer.Serialize(new ResponseEnvelope<WebSocketEventDto>(BroadcastEvents.Notification(note), "", 200, true, "Socket"),
            HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
        return JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto)
            .ShouldNotBeNull().Response.ShouldNotBeNull();
    }

    [Fact]
    public async Task AMirrorsNoteOnARigsContextReachesTheSinkWithTheRigAndADetachedMirrorsDoesNot()
    {
        var rig = new Rig();
        var views = new ViewContexts();
        views.AttachAppState(new GuiAppState { DesktopAlerts = rig.Alerts });
        var context = views.GetOrAddRemote("node-9", "Hilltop");

        var (mirror, _) = BuildMirror(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), timeProvider: rig.Clock);
        await using (mirror)
        {
            context.Mirror = mirror;
            mirror.Dispatch(Pushed(new NotificationDto { Severity = "Error", Kind = "SessionFailed", Message = "Session failed: boom", TimestampUtc = Now }));

            context.Mirror = null;
            mirror.Dispatch(Pushed(new NotificationDto { Severity = "Error", Kind = "EnhanceFailed", Message = "after detach", TimestampUtc = Now }));
        }

        var shown = await rig.DrainAsync();
        shown.Count.ShouldBe(1);
        shown[0].Title.ShouldBe("Hilltop");
        shown[0].Tag.ShouldBe("SessionFailed");
        shown[0].Link.ShouldBe("notifications/node-9");
    }

    [Fact]
    public async Task ThisComputersOwnNodesNoteIsThisComputers()
    {
        var rig = new Rig();
        var views = new ViewContexts();
        views.AttachAppState(new GuiAppState { DesktopAlerts = rig.Alerts });
        views.Local.NodeId = "own-node";

        var (mirror, _) = BuildMirror(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), timeProvider: rig.Clock);
        await using (mirror)
        {
            views.Local.Mirror = mirror;
            mirror.Dispatch(Pushed(new NotificationDto { Severity = "Error", Message = "boom", TimestampUtc = Now }));
            views.Local.Mirror = null;
        }

        var shown = await rig.DrainAsync();
        shown.ShouldHaveSingleItem().Group.ShouldBe(DesktopAlerts.ThisComputerGroup);
        shown[0].Link.ShouldBe("notifications");
    }

    [Fact]
    public async Task TheSettingSurvivesASaveAndAReadAndAMissingFileReadsAsTheDefault()
    {
        var external = new FakeExternal(output);
        var ct = TestContext.Current.CancellationToken;

        (await DesktopAlertSettings.LoadAsync(external, ct)).ShouldBe(DesktopAlertLevel.ErrorsAndSafety);

        await DesktopAlertSettings.SaveAsync(external, DesktopAlertLevel.AllWarnings, ct);
        (await DesktopAlertSettings.LoadAsync(external, ct)).ShouldBe(DesktopAlertLevel.AllWarnings);

        await System.IO.File.WriteAllTextAsync(DesktopAlertSettings.PathFor(external), "{ not json", ct);
        (await DesktopAlertSettings.LoadAsync(external, ct)).ShouldBe(DesktopAlertLevel.ErrorsAndSafety);
    }
}
