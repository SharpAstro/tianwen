using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// How a client comes to the machine's node (P1 of docs/plans/hardware-in-the-server.md, #917, "Spawn and lifetime"):
/// it finds the node on the socket, or starts one from its own directory and waits for it; replaces an idle node of
/// another wire and leaves a busy one alone; uses a node under another account on TCP; never starts one on a socket
/// it was pointed at; and says plainly why it has no node. The nodes it starts are the real server built beside the
/// tests, on a socket and a data root of the test's own, with the fake devices only.
/// </summary>
[Collection("NodeProcesses")]
public class LocalNodeLauncherTests
{
    private static (LocalNodeOptions Options, string Folder) Isolated(Action<Dictionary<string, string?>>? clock = null)
    {
        var folder = Directory.CreateTempSubdirectory("twl").FullName;
        var clockEnvironment = new Dictionary<string, string?>();
        clock?.Invoke(clockEnvironment);
        return (new LocalNodeOptions
        {
            SocketPath = Path.Combine(folder, "node.sock"),
            AnotherAccountProbe = null,
            ClockEnvironment = clockEnvironment,
            Environment = new Dictionary<string, string?> { [TianWenDataRoot.EnvironmentVariable] = Path.Combine(folder, "data") },
            NodeArguments = ["--local-only", "--fake-devices"],
        }, folder);
    }

    private static LocalNodeLauncher Launcher(LocalNodeOptions options) => new LocalNodeLauncher(options, NullLogger.Instance);

    /// <summary>
    /// Stops whatever node answers on each socket when the test ends, pass or fail: a node the launcher starts
    /// outlives its client by design, so a test that failed before stopping it would leave it running.
    /// </summary>
    private sealed class StopsNodes(params string[] socketPaths) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var socketPath in socketPaths)
            {
                await StopAsync(socketPath);
            }
        }
    }

    /// <summary>Stops a node a test started, over its socket, and waits until it no longer answers.</summary>
    private static async Task StopAsync(string socketPath)
    {
        using var http = NodeTransport.OverSocket(socketPath).CreateHttpClient();
        try
        {
            using var answer = await http.PostAsync("api/v1/node/shutdown", content: null);
        }
        catch (HttpRequestException)
        {
            return;
        }

        var client = new TianWenNodeClient(http);
        for (var i = 0; i < 300 && (await client.GetNodeAsync(CancellationToken.None)).IsSuccess; i++)
        {
            await Task.Delay(100);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task AClientWithNoNodeStartsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, folder) = Isolated();
        await using var stop = new StopsNodes(options.SocketPath);
        var node = await Launcher(options).FindOrStartAsync(ct);

        node.Outcome.ShouldBeOneOf(LocalNodeOutcome.Started, LocalNodeOutcome.StartedWithTheClient);
        node.Node.ShouldNotBeNull().WireVersion.ShouldBe(NodeWire.Version);
        node.Transport.ShouldNotBeNull().SocketPath.ShouldBe(options.SocketPath);
        node.Node.ProcessId.ShouldNotBe(Environment.ProcessId, "the node is a process of its own");
        File.Exists(Path.Combine(folder, "data", NodeIdentity.IdFileName)).ShouldBeTrue("under the data root it was given");
    }

    [Fact(Timeout = 120_000)]
    public async Task AClientFindsTheRunningNodeRatherThanStartingAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var running = await kept.WaitForNodeAsync(static _ => true, ct);

        var node = await Launcher(Isolated().Options with { SocketPath = kept.SocketPath }).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.Found);
        node.Node.ShouldNotBeNull().ProcessId.ShouldBe(running.ProcessId);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStartedNodeRunsOnItsClientsClock()
    {
        // A GUI on a simulated night (TIANWEN_NOW) starts a node that must run on the same night, not re-anchor the
        // simulated instant at its own, later, start.
        var ct = TestContext.Current.CancellationToken;
        var ahead = TimeSpan.FromDays(3);
        var (options, _) = Isolated(clock =>
        {
            clock[StartupTimeOverride.OffsetEnvVarName] = ahead.ToString("c", CultureInfo.InvariantCulture);
            clock[StartupTimeOverride.EnvVarName] = null;
        });
        await using var stop = new StopsNodes(options.SocketPath);
        var node = await Launcher(options).FindOrStartAsync(ct);

        var drift = node.Node.ShouldNotBeNull().NowUtc - (DateTimeOffset.UtcNow + ahead);
        drift.Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANamedSocketWithNoNodeIsReportedAndNoNodeIsStartedThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, folder) = Isolated();
        var named = Path.Combine(folder, "named.sock");
        await using var stop = new StopsNodes(named, options.SocketPath);

        var node = await Launcher(options with { NamedSocket = named }).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.NamedNodeUnreachable);
        node.Transport.ShouldBeNull();
        node.Message.ShouldContain(named);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        File.Exists(named).ShouldBeFalse("a client pointed at a socket starts no node there");
        File.Exists(options.SocketPath).ShouldBeFalse("nor anywhere else");
    }

    [Fact(Timeout = 60_000)]
    public async Task AMissingServerIsReportedWithWhereItWasLookedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, folder) = Isolated();
        var empty = Directory.CreateDirectory(Path.Combine(folder, "no-server")).FullName;
        await using var stop = new StopsNodes(options.SocketPath);

        var node = await Launcher(options with { ServerDirectory = empty }).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.ServerMissing);
        node.Message.ShouldContain(Path.Combine(empty, LocalNodeLauncher.ServerFileName));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnIdleNodeOfAnotherWireIsStoppedAndReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, _) = Isolated();
        await using var stop = new StopsNodes(options.SocketPath);
        await using var old = await ImpostorNode.StartAsync(options.SocketPath, NodeWire.Version - 1, holdsHardware: false, ct);
        var node = await Launcher(options).FindOrStartAsync(ct);

        old.ShutdownRequests.ShouldBe(1);
        node.Outcome.ShouldBeOneOf(LocalNodeOutcome.Started, LocalNodeOutcome.StartedWithTheClient);
        node.Node.ShouldNotBeNull().WireVersion.ShouldBe(NodeWire.Version);
        node.Node.NodeId.ShouldNotBe("impostor");
    }

    [Fact(Timeout = 60_000)]
    public async Task ABusyNodeOfAnotherWireIsLeftRunning()
    {
        // An older node still running a night after an update: stopping it would end the night.
        var ct = TestContext.Current.CancellationToken;
        var (options, _) = Isolated();
        await using var old = await ImpostorNode.StartAsync(options.SocketPath, NodeWire.Version - 1, holdsHardware: true, ct);
        await using var stop = new StopsNodes(options.SocketPath);

        var node = await Launcher(options).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.BusyWithAnotherWire);
        old.ShutdownRequests.ShouldBe(0);
        node.Transport.ShouldNotBeNull("the client can still watch it");
        (await new TianWenNodeClient(node.Transport.CreateHttpClient()).GetNodeAsync(ct)).Value.ShouldNotBeNull().NodeId.ShouldBe("impostor");
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeUnderAnotherAccountIsUsedOverTcp()
    {
        // A service on the machine runs the node under its own account, invisible to this user's socket and lock.
        var ct = TestContext.Current.CancellationToken;
        await using var service = await ImpostorNode.StartAsync(socketPath: null, NodeWire.Version, holdsHardware: false, ct);
        var (options, _) = Isolated();
        await using var stop = new StopsNodes(options.SocketPath);

        var node = await Launcher(options with { AnotherAccountProbe = service.TcpAddress }).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.AnotherAccount);
        node.Transport.ShouldNotBeNull().SocketPath.ShouldBeNull();
        File.Exists(options.SocketPath).ShouldBeFalse("nothing was started");
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeThatCannotStartIsReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, _) = Isolated();
        await using var stop = new StopsNodes(options.SocketPath);

        var node = await Launcher(options with { NodeArguments = ["--port", "0"] }).FindOrStartAsync(ct);

        node.Outcome.ShouldBe(LocalNodeOutcome.CouldNotStart);
        node.Message.ShouldContain($"ended with {NodeExitCodes.InvalidArguments}");
    }
}
