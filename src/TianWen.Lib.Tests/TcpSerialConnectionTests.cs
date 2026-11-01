using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
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

        var clock = new FakeTimeProviderWrapper();
        await using var connection = await TcpSerialConnection.CreateAsync("127.0.0.1", port, Encoding.Latin1, clock, NullLogger.Instance, ct);
        using var peer = await accepted;

        // The peer never answers, so only the bound ends each read: a caller's bound on the same clock, well inside the
        // connection's own reply deadline.
        (await ReadUntilBoundAsync(clock, bound => connection.TryReadTerminatedAsync("#"u8.ToArray(), bound).AsTask(), ct)).ShouldBeNull();
        (await ReadUntilBoundAsync(clock, bound => connection.TryReadExactlyAsync(1, bound).AsTask(), ct)).ShouldBeNull();
        (await ReadUntilBoundAsync(clock, bound => connection.TryReadTerminatedRawAsync(new byte[16], "#"u8.ToArray(), bound).AsTask(), ct)).ShouldBe(-1);

        connection.IsOpen.ShouldBeTrue();
    }

    private static async Task<T> ReadUntilBoundAsync<T>(FakeTimeProviderWrapper clock, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var step = TimeSpan.FromMilliseconds(100);
        using var timer = clock.CancelAfter(step);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var pending = read(bound.Token);
        pending.IsCompleted.ShouldBeFalse("nothing has answered and the clock has not moved");
        clock.Advance(step);
        return await pending;
    }
}
