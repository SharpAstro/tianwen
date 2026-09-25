using NSubstitute;
using Shouldly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// What a remote client hears from a node's event stream (P0b item 5 of docs/plans/hardware-in-the-server.md,
/// #752). The node sends each event inside a <c>ResponseEnvelope</c>, and <see cref="TianWenEventStream"/>
/// decoded a bare <see cref="WebSocketEventDto"/>, whose <c>Event</c> is required. So every frame failed to
/// parse and was dropped at Debug level while the stream reported itself connected: FrameWritten,
/// PlateSolve and GUIDE-STEP never reached a mirror. Nothing had ever sent a real server event through the
/// real client; these do, from a session event through the broadcaster, the hub and the socket.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeEventStreamTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness? _harness;

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ASessionsPhaseChangeReachesTheRealClient()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var session = await _harness.StartSessionAsync(ct);

        var received = new TaskCompletionSource<WebSocketEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new TianWenEventStream(
            _harness.Client.BaseAddress ?? throw new InvalidOperationException("the harness client has no base address"),
            new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.EventReceived += (_, e) =>
        {
            if (e.Event == "SESSION-PHASE-CHANGED")
            {
                received.TrySetResult(e);
            }
        };
        stream.Start(ct);

        // The broadcaster finds the session on its own poll, and the socket connects on its own time, so
        // raise the change until one lands rather than guess when both are ready.
        for (var i = 0; i < 100 && !received.Task.IsCompleted; i++)
        {
            session.Session.PhaseChanged += Raise.EventWith(session.Session,
                new SessionPhaseChangedEventArgs(SessionPhase.Initialising, SessionPhase.Cooling));
            await Task.WhenAny(received.Task, Task.Delay(100, ct));
        }

        var phase = await received.Task.WaitAsync(ct);
        phase.Data.ShouldNotBeNull()["NewPhase"]?.ToString().ShouldBe("Cooling");
    }
}
