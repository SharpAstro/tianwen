using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.RemoteSessionMirrorDriveTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// A session's histories cross once (P5b part 7 of docs/plans/hardware-in-the-server.md, #935): a client names where its
/// copy ends, the node sends only what follows, and the mirror appends it. Every poll used to carry the whole night, about
/// 0.8 MB at four OTAs over ten hours, twice a second, and the mirror re-mapped every history on every frame it was read.
/// </summary>
public class IncrementalSessionStateTests
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000935");
    private static readonly Guid SessionB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000935");

    private static ExposureLogEntry Frame(int n) =>
        new ExposureLogEntry(Start.AddMinutes(n), "M42", "L", TimeSpan.FromSeconds(120), n, 2.4f, 300 + n);

    private static CoolingSample Cooling(int n) => new CoolingSample(Start.AddSeconds(15 * n), 0, 20 - n, -10, 50);

    private static GuideErrorSample Step(int n) =>
        new GuideErrorSample(Start.AddSeconds(2 * n), 0.1 * n, -0.1 * n, 10, -10, IsDither: false, IsSettling: false);

    /// <summary>A running session holding <paramref name="frames"/> frames, cooling samples and phases.</summary>
    private static ISessionTelemetry Holding(int frames, int cooling = 3, int phases = 3)
    {
        var session = RemoteSessionMirrorTests.Observing(Substitute.For<ISessionTelemetry>());
        session.ExposureLog.Returns([.. Enumerable.Range(1, frames).Select(Frame)]);
        session.CoolingSamples.Returns([.. Enumerable.Range(1, cooling).Select(Cooling)]);
        session.PhaseTimeline.Returns([.. Enumerable.Range(0, phases).Select(i => new PhaseTimestamp((SessionPhase)i, Start.AddMinutes(i)))]);
        return session;
    }

    // -------------------------------------------------------------------------------------------
    // The node sends what follows the client's copy
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ACursorOfThisSessionGetsOnlyWhatFollowsItsCopy()
    {
        var session = Holding(frames: 10, cooling: 4, phases: 3);

        var state = SessionStateDto.FromSession(session, sessionId: SessionA,
            cursor: new SessionStateCursor(SessionA, ExposureLog: 7, FocusHistory: 0, CoolingSamples: 4, PhaseTimeline: 2, GuideSteps: 0));

        state.SessionId.ShouldBe(SessionA);
        state.ExposureLog.Select(e => e.FrameNumber).ShouldBe([8, 9, 10]);
        state.CoolingSamples.ShouldBeEmpty("the client holds all four");
        state.PhaseTimeline.ShouldHaveSingleItem();
        var from = state.HistoryFrom.ShouldNotBeNull();
        (from.ExposureLog, from.CoolingSamples, from.PhaseTimeline).ShouldBe((7, 4, 2));
    }

    [Fact]
    public void ACursorOfAnotherSessionGetsEveryHistoryWhole()
    {
        var session = Holding(frames: 10);

        var state = SessionStateDto.FromSession(session, sessionId: SessionB,
            cursor: new SessionStateCursor(SessionA, ExposureLog: 7, FocusHistory: 0, CoolingSamples: 2, PhaseTimeline: 2, GuideSteps: 0));

        state.ExposureLog.Length.ShouldBe(10, "a copy of another session is not continued with this one's");
        state.HistoryFrom.ShouldNotBeNull().ExposureLog.ShouldBe(0);
    }

    [Fact]
    public void ACopyLongerThanAHistoryIsNotOfItAndGetsItWhole()
    {
        var state = SessionStateDto.FromSession(Holding(frames: 3), sessionId: SessionA,
            cursor: new SessionStateCursor(SessionA, ExposureLog: 9, FocusHistory: 0, CoolingSamples: 0, PhaseTimeline: 0, GuideSteps: 0));

        state.ExposureLog.Length.ShouldBe(3);
        state.HistoryFrom.ShouldNotBeNull().ExposureLog.ShouldBe(0);
    }

    [Fact]
    public void WithNoSessionNamedEveryHistoryIsWholeAndNoStartIsGiven()
    {
        // As a node before part 7 answered, which a client reads as whole.
        var state = SessionStateDto.FromSession(Holding(frames: 5));

        state.ExposureLog.Length.ShouldBe(5);
        state.SessionId.ShouldBeNull();
        state.HistoryFrom.ShouldBeNull();
    }

    [Fact]
    public void TheGuideStepsAreThoseAfterTheClientsWithinTheRingTheSessionHolds()
    {
        var session = Holding(frames: 0);
        // A ring of the latest 4 of 10 steps taken: steps 6 to 9 (numbering from 0).
        session.GuideSampleWindow.Returns(([.. Enumerable.Range(6, 4).Select(Step)], 10L));

        var guider = GuiderStateDto.FromSession(session, guideCursor: 8, out var first);

        first.ShouldBe(8L);
        guider.RecentSteps.Select(s => s.Timestamp).ShouldBe([Step(8).Timestamp, Step(9).Timestamp]);

        // A client further behind than the ring gets the whole ring, from the oldest step still held.
        GuiderStateDto.FromSession(session, guideCursor: 2, out first).RecentSteps.Length.ShouldBe(4);
        first.ShouldBe(6L);
    }

    // -------------------------------------------------------------------------------------------
    // The mirror appends
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AMirrorAppendsEachPartWhereItsCopyEnds()
    {
        var held = RemoteSessionMirror.Histories.Continue(RemoteSessionMirror.Histories.None,
            SessionStateDto.FromSession(Holding(frames: 5), sessionId: SessionA));
        held.ExposureLog.Length.ShouldBe(5);
        held.Cursor.ShouldBe(new SessionStateCursor(SessionA, 5, 0, 3, 3, 0));

        var next = RemoteSessionMirror.Histories.Continue(held,
            SessionStateDto.FromSession(Holding(frames: 8, cooling: 5), sessionId: SessionA, cursor: held.Cursor));

        next.ExposureLog.Select(e => e.FrameNumber).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
        next.CoolingSamples.Length.ShouldBe(5);
        next.PhaseTimeline.Length.ShouldBe(3, "nothing new, nothing added");
    }

    [Fact]
    public void ANewSessionReplacesTheCopyOfTheLast()
    {
        var held = RemoteSessionMirror.Histories.Continue(RemoteSessionMirror.Histories.None,
            SessionStateDto.FromSession(Holding(frames: 5), sessionId: SessionA));

        var next = RemoteSessionMirror.Histories.Continue(held,
            SessionStateDto.FromSession(Holding(frames: 2), sessionId: SessionB, cursor: held.Cursor));

        next.ExposureLog.Length.ShouldBe(2);
        next.SessionId.ShouldBe(SessionB);
    }

    [Fact]
    public void APartThatDoesNotContinueTheCopyIsTakenAsItIsAndTheNextPollAsksForEverything()
    {
        var held = RemoteSessionMirror.Histories.Continue(RemoteSessionMirror.Histories.None,
            SessionStateDto.FromSession(Holding(frames: 5), sessionId: SessionA));

        // A part from 3 of a copy of 5: not what this client asked for.
        var odd = SessionStateDto.FromSession(Holding(frames: 6), sessionId: SessionA,
            cursor: new SessionStateCursor(SessionA, 3, 0, 3, 3, 0));
        var next = RemoteSessionMirror.Histories.Continue(held, odd);

        next.Cursor.ShouldBeNull("the copy is not trusted to continue, so the next poll names no session");
    }

    [Fact]
    public void AMirrorOfANodeBeforeThisTakesEachHistoryWhole()
    {
        var held = RemoteSessionMirror.Histories.Continue(RemoteSessionMirror.Histories.None, SessionStateDto.FromSession(Holding(frames: 5)));
        var next = RemoteSessionMirror.Histories.Continue(held, SessionStateDto.FromSession(Holding(frames: 6)));

        next.ExposureLog.Length.ShouldBe(6, "replaced, not appended");
        next.Cursor.ShouldBeNull("such a node reads no cursor");
    }

    [Fact]
    public void TheGuideStepsAreAppendedAndKeptToTheRingsSize()
    {
        var session = Holding(frames: 0);
        var taken = ISessionTelemetry.GuideSampleCapacity;
        session.GuideSampleWindow.Returns(([.. Enumerable.Range(0, taken).Select(Step)], (long)taken));
        var held = RemoteSessionMirror.Histories.Continue(RemoteSessionMirror.Histories.None,
            SessionStateDto.FromSession(session, sessionId: SessionA));
        held.GuideSamples.Length.ShouldBe(taken);

        // Five more: the ring drops its five oldest, and so does the copy.
        session.GuideSampleWindow.Returns(([.. Enumerable.Range(5, taken).Select(Step)], (long)taken + 5));
        var next = RemoteSessionMirror.Histories.Continue(held, SessionStateDto.FromSession(session, sessionId: SessionA, cursor: held.Cursor));

        next.GuideSamples.Length.ShouldBe(taken);
        next.GuideSamples[0].Timestamp.ShouldBe(Step(5).Timestamp);
        next.GuideSamples[^1].Timestamp.ShouldBe(Step(taken + 4).Timestamp);
        next.Cursor.ShouldNotBeNull().GuideSteps.ShouldBe((long)taken + 5);
    }

    [Fact]
    public async Task TheMirrorsNextPollNamesWhereItsCopyEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = 4;
        var (mirror, handler) = BuildMirror(request =>
        {
            var cursor = SessionStateCursor.FromQuery(key => System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[key]);
            return Json(ResponseEnvelope<SessionStateDto>.Ok(
                SessionStateDto.FromSession(Holding(frames), sessionId: SessionA, cursor: cursor)));
        });

        await using (mirror)
        {
            await mirror.PollOnceAsync(ct);
            frames = 6;
            await mirror.PollOnceAsync(ct);

            handler.Requests.ShouldContain(r => r.Contains($"session={SessionA:D}&exposures=4&", StringComparison.Ordinal));
            mirror.ExposureLog.Select(e => e.FrameNumber).ShouldBe([1, 2, 3, 4, 5, 6]);
        }
    }
}
