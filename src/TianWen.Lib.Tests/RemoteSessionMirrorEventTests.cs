using Shouldly;
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.RemoteSessionMirrorDriveTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every event a node sends is one the mirror handles, a frame is fetched only once the node shows a new one, and a rig's
/// view redraws when its mirror has something new (P5b part 6 of docs/plans/hardware-in-the-server.md, #935). Of the
/// events the node sent, the mirror used to act on two; a rig's view repainted only on the window's own tick; and each
/// OTA's frame was asked for on every poll whether or not it had changed.
/// </summary>
public class RemoteSessionMirrorEventTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.Zero);

    /// <summary>An event as the socket delivers it: serialised in its envelope and read back, so its values arrive as JSON.</summary>
    private static WebSocketEventDto OverTheWire(WebSocketEventDto dto)
    {
        var json = JsonSerializer.Serialize(new ResponseEnvelope<WebSocketEventDto>(dto, "", 200, true, "Socket"),
            HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
        return JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto)
            .ShouldNotBeNull().Response.ShouldNotBeNull();
    }

    /// <summary>The node's tokens as its state carries them: OTA 0's and the guide camera's.</summary>
    private static FrameAvailableDto[] Tokens(int ota0, int guider = 0) =>
    [
        new FrameAvailableDto { Source = FrameSources.Ota(0), Number = ota0 },
        new FrameAvailableDto { Source = FrameSources.Guider, Number = guider },
    ];

    private static WebSocketEventDto FrameAvailable(string source, int number) =>
        OverTheWire(BroadcastEvents.FrameAvailable(new FrameAvailableDto { Source = source, Number = number }));

    private static HttpResponseMessage State(FrameAvailableDto[]? frames = null) =>
        Json(ResponseEnvelope<SessionStateDto>.Ok(RemoteSessionMirrorTests.RunningState(frames: frames)));

    // -------------------------------------------------------------------------------------------
    // Every event
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The node's whole broadcast set, as <see cref="BroadcastEventSerializationTests"/> keeps it: an event added to the
    /// broadcaster is added there, and so reaches this test, which fails until the mirror says what the event is to it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BroadcastEventSerializationTests.EveryEvent), MemberType = typeof(BroadcastEventSerializationTests))]
    public async Task EveryEventTheNodeSendsIsOneTheMirrorHandles(WebSocketEventDto dto)
    {
        var (mirror, _) = BuildMirror(_ => State());
        await using (mirror)
        {
            mirror.Dispatch(OverTheWire(dto)).ShouldNotBe(RemoteSessionMirror.NodeEventKind.Unknown, $"{dto.Event} must be handled");
        }
    }

    /// <summary>
    /// A change to the state polls now rather than at the next tick; a new frame fetches that frame and not the state; a
    /// guide step, as frequent as the guide camera, wakes nothing, since its samples come with the next poll.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AStateEventPollsNowAndAFrameEventFetchesOnlyTheFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        // Nothing advances this clock, so the poll's tick never comes by itself.
        var time = new FakeTimeProviderWrapper(Now) { ExternalTimePump = true };
        var (mirror, _) = BuildMirror(_ => State(), timeProvider: time);
        await using (mirror)
        {
            var waiting = mirror.WaitForNextPassAsync(ct);
            mirror.Dispatch(OverTheWire(BroadcastEvents.GuideStep(new GuideErrorSample(Now, 0.4, -0.3, 120, -80, IsDither: false, IsSettling: false))));
            waiting.IsCompleted.ShouldBeFalse("a guide step waits for the poll's own tick");

            mirror.Dispatch(FrameAvailable(FrameSources.Ota(0), 8));
            (await waiting).ShouldBeFalse("a new frame fetches that frame, not the whole state");

            waiting = mirror.WaitForNextPassAsync(ct);
            waiting.IsCompleted.ShouldBeFalse();
            mirror.Dispatch(OverTheWire(BroadcastEvents.PhaseChanged(new SessionPhaseChangedEventArgs(SessionPhase.Observing, SessionPhase.Finalising))));
            (await waiting).ShouldBeTrue("a phase change polls the state now");

            // Raised between two passes, before the loop comes back to wait: the next wait does not sleep past it.
            mirror.Dispatch(OverTheWire(BroadcastEvents.GuiderStateChanged(new Lib.Sequencing.GuiderStateChangedEventArgs("Guiding", "Settling"))));
            (await mirror.WaitForNextPassAsync(ct)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AScoutCrossesWithItsTargetWhole()
    {
        var (mirror, _) = BuildMirror(_ => State());
        await using (mirror)
        {
            ScoutCompletedEventArgs? scouted = null;
            mirror.ScoutCompleted += (_, e) => scouted = e;

            mirror.Dispatch(OverTheWire(BroadcastEvents.ScoutCompleted(new ScoutCompletedEventArgs(
                new Target(22.49, -20.8, "NGC 7293", CatalogIndex.NGC7293),
                ScoutClassification.Obstruction, TimeSpan.FromMinutes(5), ScoutOutcome.Advance, [19, 37]))));

            scouted.ShouldNotBeNull();
            scouted.Target.ShouldBe(new Target(22.49, -20.8, "NGC 7293", CatalogIndex.NGC7293));
            scouted.Classification.ShouldBe(ScoutClassification.Obstruction);
            scouted.EstimatedClearIn.ShouldBe(TimeSpan.FromMinutes(5));
            scouted.Outcome.ShouldBe(ScoutOutcome.Advance);
            scouted.StarCountsPerOTA.ShouldBe([19, 37]);
        }
    }

    /// <summary>A verdict this client cannot read is not raised as some other verdict.</summary>
    [Fact]
    public async Task AScoutWhoseVerdictIsNotUnderstoodIsNotRaised()
    {
        var (mirror, _) = BuildMirror(_ => State());
        await using (mirror)
        {
            var raised = false;
            mirror.ScoutCompleted += (_, _) => raised = true;
            var dto = BroadcastEvents.ScoutCompleted(new ScoutCompletedEventArgs(new Target(22.49, -20.8, "NGC 7293", null),
                ScoutClassification.Healthy, null, ScoutOutcome.Proceed, [19]));
            dto.Data!["Classification"] = "Fog";

            mirror.Dispatch(OverTheWire(dto));

            raised.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ANoteIsRaisedAsTheNodePushedItAndRedrawsTheView()
    {
        var (mirror, _) = BuildMirror(_ => State());
        await using (mirror)
        {
            NotificationDto? note = null;
            var changes = 0;
            mirror.NoteReceived += (_, n) => note = n;
            mirror.Changed += (_, _) => changes++;

            mirror.Dispatch(OverTheWire(BroadcastEvents.Notification(
                new NotificationDto { Severity = "Warning", Message = "Guiding lost on M 42", TimestampUtc = Now })));

            note.ShouldNotBeNull();
            note.Severity.ShouldBe("Warning");
            note.Message.ShouldBe("Guiding lost on M 42");
            note.TimestampUtc.ShouldBe(Now);
            changes.ShouldBe(1);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Frames, by the node's token
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFrameIsFetchedOnlyOnceTheNodeShowsANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = await WireAsync(CameraFrame());
        var token = 7;
        var frameRequests = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (IsFrameRoute(request))
            {
                frameRequests++;
                return FrameResponse(wire, token);
            }
            return State(Tokens(ota0: token));
        });

        await using (mirror)
        {
            mirror.Previews = new PreviewOptions();

            await mirror.PollOnceAsync(ct);
            frameRequests.ShouldBe(1);
            mirror.LastCapturedImageNumber(0).ShouldBe(7);

            await mirror.PollOnceAsync(ct);
            await mirror.PollOnceAsync(ct);
            frameRequests.ShouldBe(1, "a poll whose token is the frame held asks for no frame at all");

            token = 8;
            await mirror.PollOnceAsync(ct);
            frameRequests.ShouldBe(2);
            mirror.LastCapturedImageNumber(0).ShouldBe(8);
        }
    }

    [Fact]
    public async Task AFrameAvailableFetchesThatFrameWithoutPollingTheState()
    {
        var ct = TestContext.Current.CancellationToken;
        var wires = new[] { await WireAsync(CameraFrame(offset: 0)), await WireAsync(CameraFrame(offset: 1)) };
        var shown = 7;
        var stateRequests = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (IsFrameRoute(request))
            {
                return FrameResponse(wires[shown - 7], shown);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/session/state", StringComparison.Ordinal))
            {
                stateRequests++;
            }
            return State(Tokens(ota0: 7));
        });

        await using (mirror)
        {
            mirror.Previews = new PreviewOptions();
            await mirror.PollOnceAsync(ct);
            mirror.LastCapturedImageNumber(0).ShouldBe(7);

            shown = 8;
            mirror.Dispatch(FrameAvailable(FrameSources.Ota(0), 8));
            await mirror.RefreshFramesAsync(ct);

            mirror.LastCapturedImageNumber(0).ShouldBe(8);
            stateRequests.ShouldBe(1, "the frame came on its own, without a state poll");
        }
    }

    /// <summary>
    /// A state polled before the event that brought the frame held carries an older token. The node answers that it still
    /// shows the frame held, and the mirror takes its word, rather than ask again on every pass until the next poll.
    /// </summary>
    [Fact]
    public async Task AnOlderTokenTheNodeAnswersUnchangedIsNotAskedAboutAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = await WireAsync(CameraFrame());
        var frameRequests = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (!IsFrameRoute(request))
            {
                return State(Tokens(ota0: 7));
            }
            frameRequests++;
            return request.RequestUri!.Query.Contains("after=8", StringComparison.Ordinal) ? Unchanged(8) : FrameResponse(wire, 8);
        });

        await using (mirror)
        {
            mirror.Previews = new PreviewOptions();
            mirror.Dispatch(FrameAvailable(FrameSources.Ota(0), 8));
            await mirror.PollOnceAsync(ct);
            mirror.LastCapturedImageNumber(0).ShouldBe(8);

            // The state's 7 is older than the 8 held: asked once, answered unchanged.
            await mirror.PollOnceAsync(ct);
            var afterTheStaleToken = frameRequests;

            await mirror.RefreshFramesAsync(ct);
            frameRequests.ShouldBe(afterTheStaleToken, "the node said it shows the frame held, so a pass asks nothing");
        }
    }

    [Fact]
    public async Task OnlyTheFramesTheViewDrawsAreFetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = await WireAsync(CameraFrame());
        var otaRequests = 0;
        var guiderRequests = 0;
        var (mirror, _) = BuildMirror(request =>
        {
            if (!IsFrameRoute(request))
            {
                return State(Tokens(ota0: 3, guider: 5));
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/frames/guider/latest", StringComparison.Ordinal))
            {
                guiderRequests++;
                return FrameResponse(wire, 5);
            }
            otaRequests++;
            return FrameResponse(wire, 3);
        });

        await using (mirror)
        {
            // The Guider tab draws the guide camera's frame and no OTA's.
            mirror.Previews = new PreviewOptions(IncludeOtas: false, IncludeGuider: true);
            await mirror.PollOnceAsync(ct);

            otaRequests.ShouldBe(0);
            mirror.LastCapturedImages.ShouldBeEmpty();
            guiderRequests.ShouldBe(1);
            mirror.LastGuideFrame.ShouldNotBeNull();
        }
    }

    // -------------------------------------------------------------------------------------------
    // A redraw when the mirror changes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMirrorSaysWhenItHasSomethingNewToDraw()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = HttpStatusCode.OK;
        var (mirror, _) = BuildMirror(_ => answer switch
        {
            HttpStatusCode.OK => State(),
            HttpStatusCode.NotFound => Json(ResponseEnvelope<SessionStateDto>.NotFound("No session"), HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(answer),
        });

        await using (mirror)
        {
            var changes = 0;
            mirror.Changed += (_, _) => changes++;

            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(1, "a state polled");

            answer = HttpStatusCode.NotFound;
            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(2, "the session ended");
            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(2, "an idle node answering idle again is nothing new");

            answer = HttpStatusCode.ServiceUnavailable;
            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(3, "the node went quiet");
            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(3, "and is still quiet");

            answer = HttpStatusCode.NotFound;
            await mirror.PollOnceAsync(ct);
            changes.ShouldBe(4, "and answered again");
        }
    }

    [Fact]
    public async Task ARigsViewRedrawsWhenItsMirrorChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var contexts = new ViewContexts();
        var rig = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        var (mirror, _) = BuildMirror(_ => State());

        await using (mirror)
        {
            rig.Mirror = mirror;
            contexts.ClearNeedsRedraw();

            await mirror.PollOnceAsync(ct);
            rig.LiveSession.NeedsRedraw.ShouldBeTrue("the rig's view has something new to draw");

            rig.Mirror = null;
            contexts.ClearNeedsRedraw();
            await mirror.PollOnceAsync(ct);
            rig.LiveSession.NeedsRedraw.ShouldBeFalse("a mirror the view has let go of no longer redraws it");
        }
    }

    [Fact]
    public async Task ARigOnScreenPullsTheFramesItsTabDraws()
    {
        var contexts = new ViewContexts();
        var app = new GuiAppState { ActiveTab = GuiTab.LiveSession };
        contexts.AttachAppState(app);
        var rig = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        var (mirror, _) = BuildMirror(_ => State());

        await using (mirror)
        {
            rig.LiveSession.ActiveSession = mirror;
            contexts.Activate(rig).ShouldBeTrue();

            contexts.PollAll();
            mirror.Previews.ShouldBe(new PreviewOptions(IncludeOtas: true, IncludeGuider: false));

            app.ActiveTab = GuiTab.Guider;
            contexts.PollAll();
            mirror.Previews.ShouldBe(new PreviewOptions(IncludeOtas: false, IncludeGuider: true));

            app.ActiveTab = GuiTab.Planner;
            contexts.PollAll();
            mirror.Previews.ShouldBeNull("the planner draws no frame of the rig's");

            app.ActiveTab = GuiTab.LiveSession;
            contexts.Activate(contexts.Local).ShouldBeTrue();
            contexts.PollAll();
            mirror.Previews.ShouldBeNull("a rig off screen pulls nothing");
        }
    }
}
