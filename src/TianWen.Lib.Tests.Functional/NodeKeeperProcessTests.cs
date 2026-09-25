using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A real keeper and the node it keeps (P1 of docs/plans/hardware-in-the-server.md, #917, decision 10): it starts the
/// node again when the node crashes, leaves it down after a second crash in a few minutes, ends when the node is
/// stopped over its socket, and ends at once when another node already holds the socket. On Unix it has left the
/// session and the terminal of whoever started it. Each test runs the server built beside the tests, on a socket
/// and a data root of its own, with the fake devices only (<see cref="KeptNode"/>).
/// </summary>
[Collection("NodeProcesses")]
public class NodeKeeperProcessTests
{
    [Fact(Timeout = 120_000)]
    public async Task AKeeperStartsItsNodeAgainAfterTheNodeCrashes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var first = await kept.WaitForNodeAsync(static _ => true, ct);

        using (var crashing = Process.GetProcessById(first.ProcessId))
        {
            crashing.Kill();
        }

        var second = await kept.WaitForNodeAsync(node => node.ProcessId != first.ProcessId, ct);
        second.NodeId.ShouldBe(first.NodeId, "the node started again is the same rig");
        kept.Keeper.HasExited.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task AKeeperLeavesANodeThatCrashesTwiceInAFewMinutesDown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var first = await kept.WaitForNodeAsync(static _ => true, ct);
        using (var crashing = Process.GetProcessById(first.ProcessId))
        {
            crashing.Kill();
        }
        var second = await kept.WaitForNodeAsync(node => node.ProcessId != first.ProcessId, ct);

        using (var crashingAgain = Process.GetProcessById(second.ProcessId))
        {
            crashingAgain.Kill();
        }

        (await kept.KeeperExitAsync(ct)).ShouldBe(NodeExitCodes.CrashLoop);
        (await kept.Client.GetNodeAsync(ct)).IsSuccess.ShouldBeFalse("nothing started a third node");
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeStoppedOverItsSocketEndsCleanlyAndTakesItsKeeperWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var node = await kept.WaitForNodeAsync(static _ => true, ct);
        using var nodeProcess = Process.GetProcessById(node.ProcessId);

        using var http = TianWen.RemoteClient.NodeTransport.OverSocket(kept.SocketPath).CreateHttpClient();
        (await NodeHarness.EnvelopeStatusAsync(http.PostAsync("api/v1/node/shutdown", null, ct), ct)).ShouldBe(202);

        // The keeper ends with Stopped only when its node did: the node's exit code, which a process this test did not
        // start cannot be asked for.
        (await kept.KeeperExitAsync(ct)).ShouldBe(NodeExitCodes.Stopped);
        await nodeProcess.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        nodeProcess.HasExited.ShouldBeTrue();
    }

    [Fact(Timeout = 120_000)]
    public async Task ASecondKeeperOnARunningNodesSocketEndsWithoutStartingAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var running = await kept.WaitForNodeAsync(static _ => true, ct);

        using var second = KeptNode.StartKeeper(kept.SocketPath, kept.DataRoot);
        await second.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        second.ExitCode.ShouldBe(NodeExitCodes.AlreadyRunning, "its node found the socket held, which is not a crash to start again");
        (await kept.WaitForNodeAsync(static _ => true, ct)).ProcessId.ShouldBe(running.ProcessId);
    }

    [Fact(Timeout = 120_000)]
    public async Task AKeptNodeKeepsItsDataUnderTheRootItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var node = await kept.WaitForNodeAsync(static _ => true, ct);

        (await File.ReadAllTextAsync(Path.Combine(kept.DataRoot, NodeIdentity.IdFileName), ct)).Trim().ShouldBe(node.NodeId);
        var logs = Directory.GetFiles(Path.Combine(kept.DataRoot, "Logs"), "*.log", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();
        logs.ShouldContain(name => name!.StartsWith("Server_"), "the node logs under the root it was given");
        logs.ShouldContain(name => name!.StartsWith("Keeper_"), "and so does its keeper");
    }

    [Fact(Timeout = 120_000)]
    public async Task OnUnixAKeeperHasLeftItsClientsSessionAndTerminal()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc; macOS and Windows detach differently");
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var node = await kept.WaitForNodeAsync(static _ => true, ct);

        // A session of its own, which the node it started shares: a closed terminal or an ended desktop session signals
        // the client's session, not this one.
        SessionOf(kept.Keeper.Id).ShouldBe(kept.Keeper.Id, "the keeper leads a session of its own");
        SessionOf(node.ProcessId).ShouldBe(kept.Keeper.Id);
        SessionOf(Environment.ProcessId).ShouldNotBe(kept.Keeper.Id);

        // And standard streams on the null device, never the terminal of the client that started it.
        foreach (var pid in (ReadOnlySpan<int>)[kept.Keeper.Id, node.ProcessId])
        {
            for (var stream = 0; stream <= 2; stream++)
            {
                new FileInfo($"/proc/{pid}/fd/{stream}").LinkTarget.ShouldBe("/dev/null", $"pid {pid}, stream {stream}");
            }
        }
    }

    /// <summary>A process's session id, the sixth field of <c>/proc/pid/stat</c> (after the name in parentheses).</summary>
    private static int SessionOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var afterName = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(afterName[3]);
    }
}
