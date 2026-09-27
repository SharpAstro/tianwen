using SharpAstro.Serial;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SerialConnection"/>, the adapter from Serial.Lib to <see cref="ISerialConnection"/>, driven over the
/// library's own loopback pair: two real ports wired to each other in memory, so every exchange here goes through
/// the same library code (framing, carry-over, deadlines) a COM port does.
/// </summary>
[Collection("Device")]
public class SerialConnectionTests(ITestOutputHelper testOutputHelper)
{
    private (SerialConnection C1, SerialConnection C2) CreatePair()
    {
        var logger = FakeExternal.CreateLogger(testOutputHelper);
        var (first, second) = SerialLoopback.CreatePair(new SerialSettings(9600) { ReadTimeout = Timeout.InfiniteTimeSpan });
        return (new SerialConnection(first, Encoding.Latin1, logger), new SerialConnection(second, Encoding.Latin1, logger));
    }

    [Fact]
    public async ValueTask TestRoundtripTerminatedMessage()
    {
        var terminators = "#\0"u8.ToArray();

        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();

        (await ssc1.TryWriteAsync(":test1#"u8.ToArray(), cancellationToken)).ShouldBe(true);

        // terminator is not part of the response
        (await ssc2.TryReadTerminatedAsync(terminators, cancellationToken)).ShouldBe(":test1");
    }

    [Fact]
    public async ValueTask TestRoundtripReadExactlyMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();

        (await ssc1.TryWriteAsync("1234567890abcdef#"u8.ToArray(), cancellationToken)).ShouldBe(true);

        (await ssc2.TryReadExactlyAsync(4, cancellationToken)).ShouldBe("1234");
        (await ssc2.TryReadExactlyAsync(4, cancellationToken)).ShouldBe("5678");
        (await ssc2.TryReadExactlyAsync(2, cancellationToken)).ShouldBe("90");
        (await ssc2.TryReadTerminatedAsync("#\0"u8.ToArray(), cancellationToken)).ShouldBe("abcdef");
    }

    // The window is a stated constant because it used to be an accident of ArrayPool's rounding:
    // the read asked for 100 bytes, got the 128-byte bucket and scanned all of it, until the
    // ArrayPoolHelper migration handed on exactly 100. The 100 and 127 bodies (101 and 128 bytes
    // with the terminator) are the replies that cut took away, and both fail against a 100-byte window.
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(SerialConnection.MaxTerminatedResponseBytes - 1)]
    public async ValueTask ATerminatedReplyThatFitsTheWindowIsRead(int bodyLength)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();
        var body = new string('x', bodyLength);

        (await ssc1.TryWriteAsync(Encoding.Latin1.GetBytes(body + "#"), cancellationToken)).ShouldBe(true);

        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBe(body);
    }

    [Fact]
    public async ValueTask ATerminatedReplyLongerThanTheWindowIsRefusedNotTruncated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();
        var body = new string('x', SerialConnection.MaxTerminatedResponseBytes);

        (await ssc1.TryWriteAsync(Encoding.Latin1.GetBytes(body + "#"), cancellationToken)).ShouldBe(true);

        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBeNull();
    }

    [Fact(Timeout = 10_000)]
    public async ValueTask ACancelledReadIsNullAndTheNextReadGetsItsReply()
    {
        // The CH34x trap, through the adapter: after a read the caller's budget ended, nothing may still be waiting
        // to eat the next reply.
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(150));

        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), budget.Token)).ShouldBeNull();

        (await ssc1.TryWriteAsync("EOK#"u8.ToArray(), cancellationToken)).ShouldBe(true);
        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBe("EOK");
    }

    [Fact]
    public async ValueTask BytesAfterATerminatorWaitForTheNextRead()
    {
        // Two replies in one arrival: the old transport kept only the first and dropped what followed its terminator.
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, ssc2) = CreatePair();

        (await ssc1.TryWriteAsync("P7820#Z17.06#"u8.ToArray(), cancellationToken)).ShouldBe(true);

        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBe("P7820");
        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBe("Z17.06");
    }

    [Fact]
    public async ValueTask AClosedConnectionRefusesAWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (ssc1, _) = CreatePair();

        (await ssc1.TryCloseAsync()).ShouldBeTrue();

        ssc1.IsOpen.ShouldBeFalse();
        (await ssc1.TryWriteAsync(":00#"u8.ToArray(), cancellationToken)).ShouldBeFalse();
    }
}
