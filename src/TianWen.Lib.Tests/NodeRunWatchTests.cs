using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The detach grace (P5 part 4 of docs/plans/hardware-in-the-server.md, #934): an interactive run nobody has watched for
/// the grace is stopped, once, and only it; a client back within the grace keeps it; a run that finishes on its own is
/// never stopped. Driven one look at a time over a fake clock, so each boundary is exact.
/// </summary>
public class NodeRunWatchTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private sealed class Rig
    {
        public readonly FakeTimeProviderWrapper Clock = new FakeTimeProviderWrapper();
        public readonly IHostedSession Node = Substitute.For<IHostedSession>();
        public readonly EventHub Clients;
        public readonly NodeRunWatch Watch;

        public Rig()
        {
            Clients = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), Clock);
            Watch = new NodeRunWatch(Node, Clients, Clock, new NodeRunWatchOptions(Grace, TimeSpan.FromSeconds(1)), NullLogger<NodeRunWatch>.Instance);
        }

        /// <summary>Makes <paramref name="run"/> the node's run going on.</summary>
        public INodeRun Running(INodeRun run)
        {
            // Read before it configures another: NSubstitute takes the last call it saw as the one being configured.
            var kind = run.Kind;
            Node.RunningKind.Returns(kind);
            Node.CurrentRun.Returns(run);
            Node.TryAbort(run).Returns(Task.CompletedTask);
            return run;
        }

        /// <summary>A client that beats now, as a window does from the loop that draws it.</summary>
        public void ABeatNow()
        {
            var socket = Substitute.For<WebSocket>();
            socket.State.Returns(WebSocketState.Open);
            Clients.RecordBeat(Clients.AddClient(socket));
        }

        public void After(TimeSpan elapsed)
        {
            Clock.Advance(elapsed);
            Watch.Check();
        }
    }

    private static INodeRun Run(NodeRunKind kind, bool endsUnwatched)
    {
        var run = Substitute.For<INodeRun>();
        run.Kind.Returns(kind);
        run.EndsUnwatched.Returns(endsUnwatched);
        return run;
    }

    [Fact]
    public void AnInteractiveRunNobodyWatchesStopsAtTheGraceAndOnlyOnce()
    {
        var rig = new Rig();
        var polar = rig.Running(Run(NodeRunKind.Polar, endsUnwatched: true));

        rig.Watch.Check();
        rig.After(Grace - TimeSpan.FromSeconds(1));
        rig.Node.DidNotReceive().TryAbort(Arg.Any<INodeRun>());

        rig.After(TimeSpan.FromSeconds(1));
        rig.Node.Received(1).TryAbort(polar);
        rig.Node.Received(1).AddNotification(Arg.Is<NotificationDto>(n => n.Message.Contains("Polar alignment stopped")));

        // It restores the mount for a while yet, still the run going on: the next looks leave it to end.
        rig.After(Grace);
        rig.After(Grace);
        rig.Node.Received(1).TryAbort(Arg.Any<INodeRun>());
        rig.Node.DidNotReceive().TryAbort();
    }

    [Fact]
    public void AClientBackWithinTheGraceKeepsTheRunAndTheGraceStartsAgainWhenItGoes()
    {
        var rig = new Rig();
        var polar = rig.Running(Run(NodeRunKind.Polar, endsUnwatched: true));

        rig.Watch.Check();
        rig.After(TimeSpan.FromSeconds(50));
        rig.ABeatNow();
        rig.After(TimeSpan.Zero);

        // Its beat lapses: nobody is watching again from here, and the grace is whole again.
        rig.After(TimeSpan.FromSeconds(10));
        rig.After(Grace - TimeSpan.FromSeconds(1));
        rig.Node.DidNotReceive().TryAbort(Arg.Any<INodeRun>());

        rig.After(TimeSpan.FromSeconds(1));
        rig.Node.Received(1).TryAbort(polar);
    }

    [Fact]
    public void ARunThatFinishesOnItsOwnIsNeverStopped()
    {
        var rig = new Rig();
        rig.Running(Run(NodeRunKind.Darks, endsUnwatched: false));

        for (var look = 0; look < 30; look++)
        {
            rig.After(Grace);
        }

        rig.Node.DidNotReceive().TryAbort(Arg.Any<INodeRun>());
        rig.Node.DidNotReceive().TryAbort();
    }

    [Fact]
    public void ANewRunHasAGraceOfItsOwn()
    {
        var rig = new Rig();
        rig.Running(Run(NodeRunKind.Polar, endsUnwatched: true));
        rig.Watch.Check();
        rig.After(TimeSpan.FromSeconds(50));

        // That one ended (a client stopped it) and another began, unwatched from its start.
        var next = rig.Running(Run(NodeRunKind.Polar, endsUnwatched: true));
        rig.After(TimeSpan.Zero);
        rig.After(Grace - TimeSpan.FromSeconds(1));
        rig.Node.DidNotReceive().TryAbort(Arg.Any<INodeRun>());

        rig.After(TimeSpan.FromSeconds(1));
        rig.Node.Received(1).TryAbort(next);
    }

    [Fact]
    public void NothingIsStoppedWhileNoRunIsGoingOn()
    {
        // The last run ended: CurrentRun still names it, for its state, but it is not going on.
        var rig = new Rig();
        var ended = Run(NodeRunKind.Polar, endsUnwatched: true);
        rig.Node.RunningKind.Returns((NodeRunKind?)null);
        rig.Node.CurrentRun.Returns(ended);

        rig.Watch.Check();
        rig.After(Grace * 2);

        rig.Node.DidNotReceive().TryAbort(Arg.Any<INodeRun>());
    }
}
