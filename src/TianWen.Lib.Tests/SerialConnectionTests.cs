using Nerdbank.Streams;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

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
}
