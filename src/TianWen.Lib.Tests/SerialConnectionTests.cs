using Nerdbank.Streams;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Device")]
public class SerialConnectionTests(ITestOutputHelper testOutputHelper)
{
    private (StreamSerialConnection C1, StreamSerialConnection C2) CreatePair()
    {
        var logger = FakeExternal.CreateLogger(testOutputHelper);

        var (stream1, stream2) = FullDuplexStream.CreatePair();

        var ssc1 = new StreamSerialConnection(stream1, Encoding.Latin1, "SSC1", logger);
        var ssc2 = new StreamSerialConnection(stream2, Encoding.Latin1, "SSC2", logger);

        return (ssc1, ssc2);
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
    [InlineData(SerialConnectionBase.MaxTerminatedResponseBytes - 1)]
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
        var body = new string('x', SerialConnectionBase.MaxTerminatedResponseBytes);

        (await ssc1.TryWriteAsync(Encoding.Latin1.GetBytes(body + "#"), cancellationToken)).ShouldBe(true);

        (await ssc2.TryReadTerminatedAsync("#"u8.ToArray(), cancellationToken)).ShouldBeNull();
    }
}
