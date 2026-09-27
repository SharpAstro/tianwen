using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.IO;
using System.Threading.Tasks;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A client stops the machine's node through <see cref="TianWenNodeClient.ShutdownNodeAsync"/> (P6 part 1 of
/// docs/plans/hardware-in-the-server.md, #936), the one way every client asks: only over the node's socket, since only a
/// client on this machine may stop the machine's node ("Stop the rig and quit" from the last window, decision 1).
/// </summary>
[Collection("Hosting")]
public class NodeShutdownClientTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ANodeRefusesAStopOverTcpAndStopsWhenAskedOverItsSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var overTcp = await NodeHarness.StartAsync(output, ct))
        {
            var client = new TianWenNodeClient(overTcp.Client);
            (await client.ShutdownNodeAsync(ct)).StatusCode.ShouldBe(403);
            (await client.GetNodeAsync(ct)).IsSuccess.ShouldBeTrue("a refused stop stops nothing");
        }

        await using var onItsSocket = await NodeHarness.StartAsync(output, ct,
            socketPath: Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));
        var stopping = onItsSocket.App.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        (await new TianWenNodeClient(onItsSocket.Client).ShutdownNodeAsync(ct)).IsSuccess.ShouldBeTrue();

        await NodeWait.UntilAsync("the node to start stopping", _ => ValueTask.FromResult((stopping.IsCancellationRequested, "running")), ct);
    }
}
