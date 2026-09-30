using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The idle exit (decision 2 of docs/plans/hardware-in-the-server.md, amended 2026-09-30): a node a client started exits once
/// nothing has used it for the grace, once; never while a client is present, a device is connected, a run is going on or a
/// job is running; never one started by hand or sharing the rig. Driven one look at a time over a fake clock, so each boundary
/// is exact.
/// </summary>
public class NodeIdleExitTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private sealed class Rig
    {
        public readonly FakeTimeProviderWrapper Clock = new FakeTimeProviderWrapper();
        public readonly IHostedSession Node = Substitute.For<IHostedSession>();
        public readonly IDeviceHub Hub = Substitute.For<IDeviceHub>();
        public readonly IHostApplicationLifetime Lifetime = Substitute.For<IHostApplicationLifetime>();
        public readonly EventHub Clients;
        public readonly NodeJobs Jobs;
        public readonly NodeIdleExit Exit;

        public Rig(bool spawned = true, int? lanPort = null)
        {
            Hub.ConnectedDevices.Returns(new List<(Uri, IDeviceDriver)>());
            Clients = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), Clock);
            Jobs = new NodeJobs(Lifetime, Clock, NullLogger<NodeJobs>.Instance);
            Exit = new NodeIdleExit(new NodeRole(spawned), new NodeListening("node.sock", lanPort), Hub, Node, Jobs, Clients, Lifetime, Clock,
                new NodeIdleExitOptions(Grace, TimeSpan.FromSeconds(1)), NullLogger<NodeIdleExit>.Instance);
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
            Exit.Check();
        }

        public int Exits => Lifetime.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IHostApplicationLifetime.StopApplication));
    }

    [Fact]
    public void A_node_a_client_started_exits_once_nothing_has_used_it_for_the_grace_and_only_once()
    {
        var rig = new Rig();

        rig.After(TimeSpan.Zero);
        rig.After(Grace - TimeSpan.FromSeconds(1));
        rig.Exits.ShouldBe(0, "a second short of the grace");

        rig.After(TimeSpan.FromSeconds(1));
        rig.Exits.ShouldBe(1);

        rig.After(Grace);
        rig.Exits.ShouldBe(1, "it asked the host to stop, once");
    }

    [Fact]
    public void A_node_started_by_hand_never_exits_by_itself()
    {
        var rig = new Rig(spawned: false);

        rig.After(TimeSpan.Zero);
        rig.After(Grace * 3);

        rig.Exits.ShouldBe(0);
    }

    [Fact]
    public void A_node_sharing_the_rig_never_exits_by_itself()
    {
        // It serves LAN clients it cannot count.
        var rig = new Rig(lanPort: 1888);

        rig.After(TimeSpan.Zero);
        rig.After(Grace * 3);

        rig.Exits.ShouldBe(0);
    }

    [Fact]
    public void A_connected_device_keeps_it_and_the_grace_starts_once_it_is_disconnected()
    {
        var rig = new Rig();
        rig.Hub.ConnectedDevices.Returns(new List<(Uri, IDeviceDriver)> { (new Uri("Camera://CanonDevice/2977d924"), Substitute.For<IDeviceDriver>()) });

        rig.After(TimeSpan.Zero);
        rig.After(Grace * 3);
        rig.Exits.ShouldBe(0, "a node never exits while it holds hardware");

        rig.Hub.ConnectedDevices.Returns(new List<(Uri, IDeviceDriver)>());
        rig.After(TimeSpan.Zero);
        rig.After(Grace - TimeSpan.FromSeconds(1));
        rig.Exits.ShouldBe(0, "the grace runs from the disconnect, not from the start");
        rig.After(TimeSpan.FromSeconds(1));
        rig.Exits.ShouldBe(1);
    }

    [Fact]
    public void A_client_present_keeps_it_and_one_that_stops_beating_leaves_it_idle()
    {
        var rig = new Rig();
        rig.ABeatNow();

        rig.After(TimeSpan.Zero);
        rig.After(NodeWire.PresenceLapse - TimeSpan.FromSeconds(1));
        rig.Exits.ShouldBe(0);

        // The window went away: its last beat lapses, and the grace starts then.
        rig.After(TimeSpan.FromSeconds(2));
        rig.After(Grace);
        rig.Exits.ShouldBe(1);
    }

    [Fact]
    public void A_run_keeps_it()
    {
        var rig = new Rig();
        rig.Node.RunningKind.Returns(NodeRunKind.Darks);

        rig.After(TimeSpan.Zero);
        rig.After(Grace * 3);

        rig.Exits.ShouldBe(0);
    }

    [Fact]
    public async Task A_running_job_keeps_it_and_the_grace_starts_once_it_has_ended()
    {
        var rig = new Rig();
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Jobs.StartOrJoin("warm-and-disconnect", (_, _) => finish.Task);

        rig.After(TimeSpan.Zero);
        rig.After(Grace * 3);
        rig.Exits.ShouldBe(0, "a warm-up is a job, and the node never exits while one runs");

        finish.SetResult("Warmed");
        await Task.Yield();
        for (var i = 0; i < 100 && rig.Exit.InUse() is not null; i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        rig.After(TimeSpan.Zero);
        rig.After(Grace);
        rig.Exits.ShouldBe(1);
    }
}
