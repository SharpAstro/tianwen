using NSubstitute;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Linear frames over the node's socket (P4 part 2 of docs/plans/hardware-in-the-server.md, #931): an OTA's frame and the
/// guider's served bit for bit, nothing sent while the client holds the frame shown, a push when a source shows a new one,
/// and a 26 MP frame's transfer measured.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeFrameTests(ITestOutputHelper output) : IAsyncLifetime
{
    private NodeHarness? _node;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_node))]
    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken,
        socketPath: Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
    }

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    private static Image Frame(int width, int height, Func<int, int, float> sample)
    {
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = sample(x, y);
            }
        }
        return new Image([plane], BitDepth.Int16, maxValue: 65535, minValue: 0, pedestal: 0, default);
    }

    private static void ShouldBeBitExact(Image read, Image sent)
    {
        (read.Width, read.Height, read.ChannelCount).ShouldBe((sent.Width, sent.Height, sent.ChannelCount));
        read.GetChannelSpan(0).SequenceEqual(sent.GetChannelSpan(0)).ShouldBeTrue("the frame read is not the frame sent");
    }

    /// <summary>A running session whose OTA 0 slot and guider show the frames the test gives them.</summary>
    private async Task<ISession> RunningAsync(Image? ota, int otaNumber, Image? guide, int guideNumber)
    {
        _node.Factory.OnCreated = controlled =>
        {
            controlled.Session.LastCapturedImages.Returns([ota]);
            controlled.Session.LastCapturedImageNumber(0).Returns(otaNumber);
            controlled.Session.LastGuideFrame.Returns(guide);
            controlled.Session.LastGuideFrameNumber.Returns(guideNumber);
        };
        _node.Factory.Initialised.TrySetResult();
        return (await _node.StartSessionAsync(TestContext.Current.CancellationToken)).Session;
    }

    [Fact(Timeout = 30_000)]
    public async Task AnOtasFrameComesOverTheSocketBitExactAndNotAgainWhileItIsShown()
    {
        var ct = TestContext.Current.CancellationToken;
        var sent = Frame(97, 61, (x, y) => x * 7 + y);
        await RunningAsync(sent, otaNumber: 3, guide: null, guideNumber: 0);
        var reader = new FrameReader();

        var got = await Client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, reader, ct);

        got.Error.ShouldBeNull();
        got.FrameNumber.ShouldBe(3);
        ShouldBeBitExact(got.Image.ShouldNotBeNull(), sent);
        got.Image.Release();

        var again = await Client.GetLatestFrameAsync(FrameSources.Ota(0), after: 3, reader, ct);
        again.IsUnchanged.ShouldBeTrue("the frame the client holds was sent again");
        sent.TryLease(out var stillShown).ShouldBeTrue("serving the frame took it from the slot");
        stillShown.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheGuidersFrameComesTheSameWay()
    {
        var ct = TestContext.Current.CancellationToken;
        var sent = Frame(64, 48, (x, y) => 1000 + x);
        await RunningAsync(ota: null, otaNumber: 0, sent, guideNumber: 12);

        var got = await Client.GetLatestFrameAsync(FrameSources.Guider, after: null, new FrameReader(), ct);

        got.FrameNumber.ShouldBe(12);
        ShouldBeBitExact(got.Image.ShouldNotBeNull(), sent);
        got.Image.Release();
    }

    [Fact(Timeout = 30_000)]
    public async Task WithNoRunThereIsNoFrame()
    {
        var got = await Client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), TestContext.Current.CancellationToken);

        (got.HasImage, got.Error, got.IsUnchanged).ShouldBe((false, null, false));
    }

    [Fact(Timeout = 30_000)]
    public async Task ANewFrameInASlotIsPushed()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushed = new ConcurrentQueue<FrameAvailableDto>();
        await using var stream = _node.Transport.CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(output));
        stream.EventReceived += (_, e) =>
        {
            if (FrameAvailableDto.TryFromEvent(e, out var frame))
            {
                pushed.Enqueue(frame);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, "not yet")), ct);
        var session = await RunningAsync(ota: null, otaNumber: 0, guide: null, guideNumber: 0);

        session.LastCapturedImages.Returns([Frame(8, 8, (x, y) => x)]);
        session.LastCapturedImageNumber(0).Returns(5);

        await UntilAsync("FRAME-AVAILABLE for OTA 0's new frame", _ => ValueTask.FromResult((
            pushed.Any(f => f.Source == FrameSources.Ota(0) && f.Number == 5), $"{pushed.Count} pushed")), ct);
        pushed.ShouldNotContain(f => f.Number == 0, "an empty slot was announced");
    }

    // The plan's measurement: a 26 MP frame (6248 x 4176, an IMX571's) over the socket on this machine. Timed for the
    // record, never asserted on: a loaded machine is slower, not wrong.
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATwentySixMegapixelFrameCrossesTheSocket(bool wholeAdu)
    {
        var ct = TestContext.Current.CancellationToken;
        var sent = Frame(6248, 4176, wholeAdu ? (x, y) => (x * 13 + y * 7) % 65536 : (x, y) => (x * 13 + y * 7) % 65536 + 0.25f);
        await RunningAsync(sent, otaNumber: 1, guide: null, guideNumber: 0);
        var reader = new FrameReader();
        // The first read allocates the plane the timed one is read into, as a client showing frame after frame would find it.
        if ((await Client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, reader, ct)).Image is { } warmup)
        {
            warmup.Release();
        }

        var clock = Stopwatch.StartNew();
        var got = await Client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, reader, ct);
        clock.Stop();

        var image = got.Image.ShouldNotBeNull(got.Error);
        output.WriteLine($"26 MP {(wholeAdu ? "whole-ADU (16-bit on the wire)" : "fractional (float on the wire)")}: {clock.Elapsed.TotalMilliseconds:F0} ms "
            + $"over the socket, into a recycled plane");
        ShouldBeBitExact(image, sent);
        image.Release();
    }
}
