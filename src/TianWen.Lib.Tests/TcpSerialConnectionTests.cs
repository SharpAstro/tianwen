using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests;

public class TcpSerialConnectionTests
{
    [Fact(Timeout = 30_000)]
    public async Task AReadThatItsBoundCancelsReturnsNothingAndLeavesTheLinkOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync(ct).AsTask();

        await using var connection = await TcpSerialConnection.CreateAsync("127.0.0.1", port, Encoding.Latin1, NullLogger.Instance, ct);
        using var peer = await accepted;

        // The peer never answers, so only the bound ends each read.
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bound.CancelAfter(TimeSpan.FromMilliseconds(100));
            (await connection.TryReadTerminatedAsync("#"u8.ToArray(), bound.Token)).ShouldBeNull();
        }
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bound.CancelAfter(TimeSpan.FromMilliseconds(100));
            (await connection.TryReadExactlyAsync(1, bound.Token)).ShouldBeNull();
        }
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bound.CancelAfter(TimeSpan.FromMilliseconds(100));
            (await connection.TryReadTerminatedRawAsync(new byte[16], "#"u8.ToArray(), bound.Token)).ShouldBe(-1);
        }

        connection.IsOpen.ShouldBeTrue();
    }
}
