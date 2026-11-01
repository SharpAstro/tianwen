using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
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
/// The deadline runs on the injected clock: an hour's deadline on a fake clock ends the read the moment the clock is
/// advanced past it, which a deadline on the wall clock would not do inside the test's own timeout.
/// </summary>
public class ConnectionReplyTimeoutTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    [Fact(Timeout = 30_000)]
    public async Task ATcpDeviceThatNeverAnswersEndsTheReadAtTheReplyTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(ct);
        await using var connection = await TcpSerialConnection.CreateAsync("127.0.0.1", port, Encoding.ASCII, clock, NullLogger.Instance, ct);
        using var silent = await accept;
        connection.ReplyTimeout = Long;

        (await connection.TryWriteAsync(":GVP#"u8.ToArray(), ct)).ShouldBeTrue();
        var read = connection.TryReadTerminatedAsync("#"u8.ToArray(), ct).AsTask();
        read.IsCompleted.ShouldBeFalse("nothing has answered and the clock has not moved");

        clock.Advance(Long);

        (await read).ShouldBeNull("nothing answered");
        connection.IsOpen.ShouldBeTrue("a silent device is not a broken link");
    }

    [Fact(Timeout = 30_000)]
    public async Task AUdpMountThatNeverAnswersEndsTheReadAtTheReplyTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        await using var connection = new SkywatcherUdpConnection("127.0.0.1", port, Encoding.ASCII, clock, NullLogger.Instance) { ReplyTimeout = Long };

        (await connection.TryWriteAsync(":e1\r"u8.ToArray(), ct)).ShouldBeTrue();
        var read = connection.TryReadTerminatedAsync("\r"u8.ToArray(), ct).AsTask();
        read.IsCompleted.ShouldBeFalse("nothing has answered and the clock has not moved");

        clock.Advance(Long);

        (await read).ShouldBeNull("nothing answered");
    }
}
