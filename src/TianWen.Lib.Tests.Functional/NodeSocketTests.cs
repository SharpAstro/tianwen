using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The machine's node on its socket (P1 of docs/plans/hardware-in-the-server.md, #917): a client reaches it there
/// with the same client and event stream a remote rig uses, the node says which node it is, one node holds the
/// socket's lock, and only that holder clears a socket a dead node left.
/// </summary>
[Collection("Hosting")]
public class NodeSocketTests(ITestOutputHelper outputHelper)
{
    private static string NewSocketPath() => Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock");

    [Fact(Timeout = 30_000)]
    public async Task ANodeOnItsSocketSaysWhichNodeItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: NewSocketPath());
        var client = new TianWenNodeClient(node.Client);

        var answer = await client.GetNodeAsync(ct);

        answer.IsSuccess.ShouldBeTrue(answer.Error);
        var info = answer.Value.ShouldNotBeNull();
        // The id is the one kept in AppData, which the LAN announcement carries too: one rig, however it is reached.
        info.NodeId.ShouldBe((await File.ReadAllTextAsync(NodeIdentity.IdFilePath(node.External), ct)).Trim());
        info.NodeId.ShouldNotBeNullOrWhiteSpace();
        info.WireVersion.ShouldBe(NodeWire.Version);
        info.ProcessId.ShouldBe(Environment.ProcessId);
        info.IsShared.ShouldBeFalse("a node on its socket alone is not reachable from the LAN");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASessionStartedOverTheSocketIsHeardOnTheEventStreamOverTheSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: NewSocketPath());
        node.Factory.Initialised.SetResult();

        var received = new TaskCompletionSource<WebSocketEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = node.Transport.CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.EventReceived += (_, e) =>
        {
            if (e.Event == "SESSION-PHASE-CHANGED")
            {
                received.TrySetResult(e);
            }
        };
        stream.Start(ct);

        var client = new TianWenNodeClient(node.Client);
        var attached = 0;
        for (var i = 0; i < 250 && attached == 0; i++)
        {
            await Task.Delay(20, ct);
            attached = (await client.GetNodeAsync(ct)).Value?.ClientsAttached ?? 0;
        }
        attached.ShouldBe(1, "the stream attached over the socket, as a TianWen client");

        (await client.StartSessionAsync(NodeHarness.ProfileId, configuration: null, ct)).IsSuccess.ShouldBeTrue();
        var session = node.Factory.Created.ShouldHaveSingleItem();
        await session.Started.Task.WaitAsync(ct);
        for (var i = 0; i < 100 && !received.Task.IsCompleted; i++)
        {
            session.Session.PhaseChanged += Raise.EventWith(session.Session,
                new SessionPhaseChangedEventArgs(SessionPhase.Initialising, SessionPhase.Cooling));
            await Task.WhenAny(received.Task, Task.Delay(100, ct));
        }

        (await received.Task.WaitAsync(ct)).Data.ShouldNotBeNull()["NewPhase"]?.ToString().ShouldBe("Cooling");
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeRefusedTheLockNamesTheRunningOneAndLeavesItsSocketAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        var running = (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull();

        NodeLock.TryAcquire(socketPath, out var second, out var refusal).ShouldBeFalse("one node per socket");
        second.ShouldBeNull();

        var described = await NodeLock.DescribeHolderAsync(socketPath, refusal.ShouldNotBeNull(), ct);
        described.ShouldContain($"pid {running.ProcessId}");
        described.ShouldContain(running.NodeId);

        // Refused, it touched nothing: the running node still answers on its socket.
        File.Exists(socketPath).ShouldBeTrue();
        (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ASocketADeadNodeLeftIsClearedByTheNextNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        // What a node that died leaves: the socket FILE, with nothing listening, and binding onto it fails. A socket
        // disposed in an orderly way removes its own file, so a crash is what leaves one; here the bound socket is
        // simply never disposed until the node is done (declared first, it is disposed last).
        using var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.Exists(socketPath).ShouldBeTrue("a dead node's socket file stays behind");

        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);

        (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeThatStopsTakesItsSocketWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        File.Exists(socketPath).ShouldBeTrue();

        await node.DisposeAsync();

        // A client then finds no socket, rather than one nobody answers. The runtime deletes a socket file when the
        // socket that bound it is disposed, so nothing of ours does; this pins the behaviour the node relies on.
        File.Exists(socketPath).ShouldBeFalse();
        // And the lock went with the node, so the next one starts.
        NodeLock.TryAcquire(socketPath, out var next, out _).ShouldBeTrue();
        next.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeStopsWhenAskedOverItsSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: NewSocketPath());
        var stopping = node.App.Lifetime.ApplicationStopping;

        (await NodeHarness.EnvelopeStatusAsync(node.Client.PostAsync("api/v1/node/shutdown", null, ct), ct)).ShouldBe(202);

        await Task.Delay(Timeout.InfiniteTimeSpan, stopping).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10), ct);
        stopping.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeReachedOverTcpCannotBeStopped()
    {
        // Only a client on this machine's socket may stop the machine's node.
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);

        (await NodeHarness.EnvelopeStatusAsync(node.Client.PostAsync("api/v1/node/shutdown", null, ct), ct)).ShouldBe(403);

        node.App.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeSaysWhenItHoldsHardware()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: NewSocketPath());
        var client = new TianWenNodeClient(node.Client);
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().HoldsHardware.ShouldBeFalse("an idle node holds nothing");

        node.Factory.Initialised.SetResult();
        await node.StartSessionAsync(ct);

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().HoldsHardware.ShouldBeTrue("a run is going on");
    }

    [Fact(Timeout = 30_000)]
    [UnsupportedOSPlatform("windows")]
    public async Task OnUnixTheSocketIsItsOwnersAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows keeps the AppData folder's ACL, which the socket inherits");
        }
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);

        // Connecting takes write permission on the file, so without this the umask decides who may drive the rig.
        File.GetUnixFileMode(socketPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
