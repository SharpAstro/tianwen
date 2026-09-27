using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A node's <c>/session/state</c> sends each history from where a client's cursor says its copy ends (P5b part 7 of
/// docs/plans/hardware-in-the-server.md, #935), over its real route: the first poll brings the log whole and names the
/// session, and the next, naming it back, brings only what was written since. <c>MirrorParityTests</c> shows the copy a
/// mirror builds this way is the session's own; this shows the node really sends less, which a node sending everything
/// every time would pass there.
/// </summary>
[Collection("Hosting")]
public class IncrementalStateNodeTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);

    private static ExposureLogEntry Frame(int n) =>
        new ExposureLogEntry(Start.AddMinutes(n), "M42", "L", TimeSpan.FromSeconds(120), n, 2.4f, 300 + n);

    [Fact(Timeout = 30_000)]
    public async Task ASecondPollNamingTheSessionGetsOnlyTheFramesWrittenSince()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct);
        node.Factory.OnCreated = controlled =>
        {
            RemoteSessionMirrorTests.Observing(controlled.Session);
            controlled.Session.ExposureLog.Returns([.. Enumerable.Range(1, 10).Select(Frame)]);
        };
        node.Factory.Initialised.TrySetResult();
        var session = (await node.StartSessionAsync(ct)).Session;
        var client = new TianWenNodeClient(node.Client);

        var first = (await client.GetSessionStateAsync(ct)).Value.ShouldNotBeNull();
        var id = first.SessionId.ShouldNotBeNull("a node sending in part names the session");
        first.ExposureLog.Length.ShouldBe(10);

        session.ExposureLog.Returns([.. Enumerable.Range(1, 12).Select(Frame)]);
        var next = (await client.GetSessionStateAsync(new SessionStateCursor(id, first.ExposureLog.Length, 0, 0, 0, 0), ct))
            .Value.ShouldNotBeNull();

        next.SessionId.ShouldBe(id);
        next.HistoryFrom.ShouldNotBeNull().ExposureLog.ShouldBe(10);
        next.ExposureLog.Select(e => e.FrameNumber).ShouldBe([11, 12], "only the frames written since the client's copy");

        // Naming a session that is not the one on show gets the log whole.
        var other = (await client.GetSessionStateAsync(new SessionStateCursor(Guid.NewGuid(), 12, 0, 0, 0, 0), ct)).Value.ShouldNotBeNull();
        other.ExposureLog.Length.ShouldBe(12);
    }
}
