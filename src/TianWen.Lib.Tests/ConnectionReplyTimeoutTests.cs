using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A device that stops answering ends a read at the connection's <see cref="ISerialConnection.ReplyTimeout"/>, whatever the
/// caller's token (2026-10-31: an AZ-GTi power-cycled while connected held a read, its port lock and the disconnect queued
/// behind it for 14 minutes). Over loopback sockets that take the request and never reply, as a powered-off device would.
/// </summary>
public class ConnectionReplyTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    [Fact(Timeout = 30_000)]
    public async Task ATcpDeviceThatNeverAnswersEndsTheReadAtTheReplyTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(ct);
        await using var connection = await TcpSerialConnection.CreateAsync("127.0.0.1", port, Encoding.ASCII, NullLogger.Instance, ct);
        using var silent = await accept;
        connection.ReplyTimeout = Short;

        (await connection.TryWriteAsync(":GVP#"u8.ToArray(), ct)).ShouldBeTrue();
        var watch = Stopwatch.StartNew();
        var reply = await connection.TryReadTerminatedAsync("#"u8.ToArray(), ct);

        reply.ShouldBeNull("nothing answered");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "ended by the reply timeout, not the caller's token");
        connection.IsOpen.ShouldBeTrue("a silent device is not a broken link");
    }

    [Fact(Timeout = 30_000)]
    public async Task AUdpMountThatNeverAnswersEndsTheReadAtTheReplyTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        await using var connection = new SkywatcherUdpConnection("127.0.0.1", port, Encoding.ASCII, NullLogger.Instance) { ReplyTimeout = Short };

        (await connection.TryWriteAsync(":e1\r"u8.ToArray(), ct)).ShouldBeTrue();
        var watch = Stopwatch.StartNew();
        var reply = await connection.TryReadTerminatedAsync("\r"u8.ToArray(), ct);

        reply.ShouldBeNull("nothing answered");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "ended by the reply timeout, not the caller's token");
    }
}
