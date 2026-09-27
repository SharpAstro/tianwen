using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.RemoteSessionMirrorDriveTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// A rig says when its node has gone quiet, in ONE wording for its Home card and its tabs, and its feed is its node's:
/// the ring, then what it pushes (P5b part 6b of docs/plans/hardware-in-the-server.md, #935). Before this a rig's tabs went
/// on showing the last thing a dark node said as though it were live, and a rig's notes reached nothing but its card.
/// </summary>
public class RigContactAndFeedTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);

    private static bool IsRoute(HttpRequestMessage request, string path) =>
        request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal);

    private static HttpResponseMessage State() =>
        Json(ResponseEnvelope<SessionStateDto>.Ok(RemoteSessionMirrorTests.RunningState()));

    private static HttpResponseMessage Ring(params NotificationDto[] notes) =>
        Json(ResponseEnvelope<NotificationDto[]>.Ok(notes));

    private static NotificationDto Note(int minute, string message, string severity = "Info") =>
        new NotificationDto { Severity = severity, Message = message, TimestampUtc = Now.AddMinutes(minute) };

    private static WebSocketEventDto Pushed(NotificationDto note)
    {
        var json = JsonSerializer.Serialize(new ResponseEnvelope<WebSocketEventDto>(BroadcastEvents.Notification(note), "", 200, true, "Socket"),
            HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
        return JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto)
            .ShouldNotBeNull().Response.ShouldNotBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Whether the node is answering
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARigIsConnectingUntilItsNodeAnswersAndNotAnsweringOnceItStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var answering = true;
        var (mirror, _) = BuildMirror(request => answering
            ? IsRoute(request, "/session/notifications") ? Ring() : State()
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await using (mirror)
        {
            mirror.Contact.ShouldBe(new NodeContact(NodeContactState.Connecting, null), "no poll has come back: neither live nor quiet yet");

            await mirror.PollOnceAsync(ct);
            mirror.Contact.State.ShouldBe(NodeContactState.Answering);
            var answered = mirror.Contact.LastAnsweredUtc.ShouldNotBeNull();

            answering = false;
            await mirror.PollOnceAsync(ct);
            mirror.Contact.ShouldBe(new NodeContact(NodeContactState.NotAnswering, answered), "quiet, and when it last answered");
        }
    }

    [Fact]
    public void TheCardAndTheTabsSayItInOneWording()
    {
        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "node-a",
            Alias = "Observatory Pi",
            LastAddress = "http://10.0.0.7:1888/",
            LastSeenUtc = Now.AddHours(-3),
        };

        RemoteRigActions.DescribeContact(NodeContact.InProcess, binding, Now).ShouldBeNull("an answering node shows its activity");
        RemoteRigActions.DescribeContact(new NodeContact(NodeContactState.Connecting, null), binding, Now).ShouldBe("Connecting");
        RemoteRigActions.DescribeContact(new NodeContact(NodeContactState.NotAnswering, Now.AddMinutes(-3)), binding, Now)
            .ShouldBe("Not answering (last seen 3 min ago)", "this run's own last answer beats a previous run's");
        RemoteRigActions.DescribeContact(new NodeContact(NodeContactState.NotAnswering, null), binding, Now)
            .ShouldBe("Not answering (last seen 3 h ago)", "the card falls back to a previous run's");
        RemoteRigActions.DescribeContact(new NodeContact(NodeContactState.NotAnswering, null), binding: null, Now)
            .ShouldBe("Not answering", "a tab with no answer this run has nothing to add");
    }

    /// <summary>
    /// The card reads the same rule as the tabs: a rig whose first poll has not come back is connecting, where the card
    /// used to call it not answering. The node here is a loopback socket that takes the connection and never answers.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARigStillConnectingSaysSoOnItsCard()
    {
        var ct = TestContext.Current.CancellationToken;
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var binding = new RemoteRigBinding
            {
                BindingId = Guid.NewGuid(),
                NodeId = "node-silent",
                Alias = "Silent rig",
                LastAddress = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/",
            };
            var contexts = new ViewContexts();
            var rigs = new RemoteRigRegistry();
            rigs.Upsert(binding);
            await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, new SystemTimeProvider(),
                NullLogger.Instance, ct).ShouldNotBeNull();
            rigs.Attach(rig);

            var card = HomeBoard.BuildCards(contexts, rigs, new GuiAppState(), DateTimeOffset.UtcNow)[1];

            card.Status.ShouldBe("Connecting");
            card.IsOnline.ShouldBeFalse();
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task TheLiveViewCarriesItsRigsContactAndLetsItGoWithTheRig()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, _) = BuildMirror(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using (mirror)
        {
            var view = new LiveSessionState { ActiveSession = mirror };
            await mirror.PollOnceAsync(ct);

            view.PollSession();
            view.Contact.State.ShouldBe(NodeContactState.NotAnswering);

            view.ActiveSession = null;
            view.PollSession();
            view.Contact.ShouldBe(NodeContact.InProcess, "a rig let go leaves no word of it on the view");
        }
    }

    // -------------------------------------------------------------------------------------------
    // The rig's feed
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARigsFeedIsItsNodesRingThenWhatItPushesEachNoteOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var ring = new[] { Note(0, "Session started"), Note(5, "Cooling to -10 C") };
        var ringReads = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (IsRoute(request, "/session/notifications"))
            {
                ringReads++;
                return Ring(ring);
            }
            return State();
        });

        await using (mirror)
        {
            var changes = 0;
            mirror.Changed += (_, _) => changes++;

            await mirror.PollOnceAsync(ct);
            mirror.Notes.Select(n => n.Message).ShouldBe(["Session started", "Cooling to -10 C"]);
            changes.ShouldBe(2, "the state, then the ring");

            // A note the ring already holds, pushed as well (the socket and the read raced), is one note; a new one joins.
            var raised = 0;
            mirror.NoteReceived += (_, _) => raised++;
            mirror.Dispatch(Pushed(Note(5, "Cooling to -10 C")));
            mirror.Dispatch(Pushed(Note(9, "Rough focus")));
            mirror.Notes.Select(n => n.Message).ShouldBe(["Session started", "Cooling to -10 C", "Rough focus"]);
            raised.ShouldBe(1, "the note the ring brought is not raised again when it is pushed as well");

            await mirror.PollOnceAsync(ct);
            ringReads.ShouldBe(1, "the ring is read once, at the first answer, not every poll");
        }
    }

    [Fact]
    public async Task ARingThatCouldNotBeReadIsReadAtTheNextAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var ringAnswers = true;
        var ringReads = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (!IsRoute(request, "/session/notifications"))
            {
                return State();
            }
            ringReads++;
            return ringAnswers ? Ring(Note(0, "Session started")) : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await using (mirror)
        {
            ringAnswers = false;
            await mirror.PollOnceAsync(ct);
            mirror.Notes.ShouldBeEmpty();

            ringAnswers = true;
            await mirror.PollOnceAsync(ct);
            mirror.Notes.ShouldHaveSingleItem().Message.ShouldBe("Session started");
            ringReads.ShouldBe(2);
        }
    }

    [Fact]
    public async Task AReconnectedSocketReadsTheRingAgainForWhatItMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ring = new[] { Note(0, "Session started") };
        var (mirror, _) = BuildMirror(request => IsRoute(request, "/session/notifications") ? Ring(ring) : State());

        await using (mirror)
        {
            await mirror.PollOnceAsync(ct);
            mirror.Notes.ShouldHaveSingleItem();

            // Pushed while the socket was down: only the ring has it.
            ring = [Note(0, "Session started"), Note(7, "Guiding lost", "Warning")];
            mirror.OnEventStreamConnectedChanged(this, connected: true);
            await mirror.PollOnceAsync(ct);

            mirror.Notes.Select(n => n.Message).ShouldBe(["Session started", "Guiding lost"]);
        }
    }

    [Fact]
    public async Task TheFeedKeepsTheNewestNotesInTheOrderTheyWereRecorded()
    {
        var (mirror, _) = BuildMirror(_ => State());
        await using (mirror)
        {
            var many = Enumerable.Range(0, RemoteSessionMirror.MaxNotes + 20).Select(i => Note(i, $"note {i}")).Reverse().ToArray();

            mirror.MergeNotes(many).ShouldBeTrue();
            mirror.MergeNotes(many).ShouldBeFalse("nothing new");

            mirror.Notes.Length.ShouldBe(RemoteSessionMirror.MaxNotes);
            mirror.Notes[0].Message.ShouldBe("note 20");
            mirror.Notes[^1].Message.ShouldBe($"note {RemoteSessionMirror.MaxNotes + 19}");
        }
    }

    [Fact]
    public async Task TheNotificationsTabShowsTheFeedOfTheViewOnShow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, _) = BuildMirror(request => IsRoute(request, "/session/notifications")
            ? Ring(Note(0, "Session started"), Note(7, "Guiding lost", "Warning"))
            : State());

        await using (mirror)
        {
            var app = new GuiAppState();
            app.RecordNotification(Now, NotificationSeverity.Info, "Profile saved");
            var contexts = new ViewContexts();
            var rig = contexts.GetOrAddRemote("node-a", "Observatory Pi");
            rig.Mirror = mirror;
            await mirror.PollOnceAsync(ct);

            var local = NotificationFeed.Of(contexts.Local, app);
            local.IsLocal.ShouldBeTrue();
            local.Entries.ShouldHaveSingleItem().Message.ShouldBe("Profile saved");
            local.Header.ShouldBe("Notifications (1)");

            var remote = NotificationFeed.Of(rig, app);
            remote.IsLocal.ShouldBeFalse("a rig's notes are its node's, which this computer cannot clear");
            remote.Entries.Select(e => (e.Message, e.Severity))
                .ShouldBe([("Guiding lost", NotificationSeverity.Warning), ("Session started", NotificationSeverity.Info)], "newest first");
            remote.Header.ShouldBe("Notifications from Observatory Pi (2)");

            rig.NodeNotes.Equals(remote.Entries).ShouldBeTrue("the same entries, mapped once while the notes are unchanged");
        }
    }
}
