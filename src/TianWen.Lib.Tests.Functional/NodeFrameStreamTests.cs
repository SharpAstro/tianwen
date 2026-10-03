using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A planetary capture's frames as streams (P5 part 5c of docs/plans/hardware-in-the-server.md, #934): one WebSocket per
/// source, the newest frame for each ask, so a client that falls behind skips frames rather than queueing them; an open
/// stream never holds the node from stopping; and over this machine's socket the frames come through shared memory (P4b,
/// #932), pixel for pixel.
/// </summary>
[Collection("Hosting")]
public class NodeFrameStreamTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    /// <summary>
    /// A node streaming the fake camera's planet, watched by a window so the capture goes on: on loopback TCP, or with
    /// <paramref name="onItsSocket"/> on a socket of its own, as this machine's node listens.
    /// </summary>
    private async Task<(NodeHarness Node, NodeWindow Window)> CapturingAsync(CancellationToken ct, bool onItsSocket = false)
    {
        var node = await NodeHarness.StartAsync(outputHelper, ct,
            onItsSocket: onItsSocket);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var window = await NodeWindow.OpenAsync(node, outputHelper, ct);
        (await new TianWenNodeClient(node.Client).StartPlanetaryAsync(new PlanetaryRequestDto(), ct)).IsSuccess.ShouldBeTrue();
        return (node, window);
    }

    /// <summary>The next frame's number and when it arrived at the node (its start time: the live frame is dated so).</summary>
    private static async Task<(int Number, DateTimeOffset Arrived)> ReadAsync(NodeFrameStream stream, FrameReader reader,
        (int Width, int Height, int Channels) shape, CancellationToken ct)
    {
        var (number, frame) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull("the stream is open while the capture runs");
        (frame.Width, frame.Height, frame.ChannelCount).ShouldBe(shape);
        var arrived = frame.ImageMeta.ExposureStartTime;
        frame.Release();
        return (number, arrived);
    }

    [Fact(Timeout = 90_000)]
    public async Task TheLiveFrameStreamsNewestFirstAndAReaderThatFallsBehindSkipsWhatItMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, window) = await CapturingAsync(ct);
        await using var _ = node;
        await using var __ = window;
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, ct);
        var reader = new FrameReader();

        // Read as it comes: every frame newer than the last, none twice.
        var frames = new List<(int Number, DateTimeOffset Arrived)>();
        for (var i = 0; i < 5; i++)
        {
            frames.Add(await ReadAsync(stream, reader, (640, 320, 1), ct));
        }
        frames.Select(f => f.Number).ShouldBe(frames.Select(f => f.Number).Distinct(), "a stream never sends a frame twice");
        frames.Select(f => f.Arrived).ShouldBe(frames.Select(f => f.Arrived).Order(), "nor an older one after a newer");

        // Fall behind for about sixty live frames (one every 33 ms): the next frame read is the newest, not the first of
        // those missed. A socket buffers megabytes, so a node that sent whenever its last send had gone fed a paused reader
        // every one, the first of them 33 ms after the frame before the pause. The frame's number cannot say: it is the
        // node's token for the source, moved only when a reader looks.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var after = await ReadAsync(stream, reader, (640, 320, 1), ct);
        (after.Arrived - frames[^1].Arrived).ShouldBeGreaterThan(TimeSpan.FromSeconds(1.5),
            "a reader that fell behind is sent the newest frame, not every frame it missed");
    }

    /// <summary>
    /// Over this machine's socket a stream asks for shared memory, and every frame comes out of a slot (P4b, #932): the
    /// newest for each ask as before, and bit for bit the frame the node holds. A stream that does not ask gets bytes.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task OverThisMachinesSocketTheLiveFrameComesThroughSharedMemoryPixelForPixel()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, window) = await CapturingAsync(ct, onItsSocket: true);
        await using var _ = node;
        await using var __ = window;
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, ct);
        var reader = new FrameReader();

        var numbers = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            numbers.Add((await ReadAsync(stream, reader, (640, 320, 1), ct)).Number);
        }
        numbers.ShouldBe(numbers.Distinct(), "a stream never sends a frame twice, whatever carries it");
        stream.FramesFromSharedMemory.ShouldBe(10, "every frame came out of a slot");

        // Pixel for pixel: the frame a slot gave against the one the node shows, leased, while it still shows it. The stream
        // answers with the newest and never the same frame twice, so the frame is read first; the live frame moves on at
        // display rate, so read until the node has not replaced it yet.
        var frames = node.App.Services.GetRequiredService<NodeFrames>();
        var compared = false;
        while (!compared)
        {
            var (number, frame) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull();
            try
            {
                var shown = frames.Named(FrameSources.PlanetaryLive);
                if (shown.Number == number && shown.Frame is { } published && published.TryLease(out var lease))
                {
                    using (lease)
                    {
                        ShouldBeBitExact(frame, lease.Image);
                    }
                    compared = true;
                }
            }
            finally
            {
                frame.Release();
            }
        }

        await using var bytes = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, sharedMemory: false, ct);
        await ReadAsync(bytes, reader, (640, 320, 1), ct);
        bytes.FramesFromSharedMemory.ShouldBe(0, "a stream that does not ask for shared memory is sent the frame itself");
    }

    /// <summary>A client that goes mid-read, its stream dropped without a close, leaves the node streaming to the rest.</summary>
    [Fact(Timeout = 90_000)]
    public async Task AClientGoneMidReadLeavesTheNodeStreamingToTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, window) = await CapturingAsync(ct, onItsSocket: true);
        await using var _ = node;
        await using var __ = window;
        var gone = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, ct);
        await using var staying = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, ct);
        var reader = new FrameReader();
        await ReadAsync(gone, reader, (640, 320, 1), ct);

        gone.Abort();

        for (var i = 0; i < 5; i++)
        {
            await ReadAsync(staying, reader, (640, 320, 1), ct);
        }
        staying.FramesFromSharedMemory.ShouldBe(5);
        await gone.DisposeAsync();
    }

    private static void ShouldBeBitExact(Image read, Image sent)
    {
        (read.Width, read.Height, read.ChannelCount).ShouldBe((sent.Width, sent.Height, sent.ChannelCount));
        for (var c = 0; c < sent.ChannelCount; c++)
        {
            var got = read.GetChannelSpan(c);
            var want = sent.GetChannelSpan(c);
            for (var i = 0; i < want.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(got[i]) != BitConverter.SingleToInt32Bits(want[i]))
                {
                    throw new ShouldAssertException($"channel {c} sample {i}: the node holds {want[i]:R}, the slot gave {got[i]:R}");
                }
            }
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task TheMasterStreamsAsItIsStacked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, window) = await CapturingAsync(ct);
        await using var _ = node;
        await using var __ = window;
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryMaster, ct);
        var reader = new FrameReader();

        var first = await ReadAsync(stream, reader, (640, 320, 3), ct);
        var second = await ReadAsync(stream, reader, (640, 320, 3), ct);

        second.Number.ShouldNotBe(first.Number, "each ask is answered by a master newer than the last");
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOpenStreamNeverHoldsTheNodeFromStopping()
    {
        // The host waits for its open requests for as long as its shutdown budget allows, 30 minutes on a node: a stream
        // that ended only with its client would hold every stop that long.
        var ct = TestContext.Current.CancellationToken;
        var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        (await new TianWenNodeClient(node.Client).StartPlanetaryAsync(new PlanetaryRequestDto(), ct)).IsSuccess.ShouldBeTrue();
        await using var stream = await node.Transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, ct);
        await ReadAsync(stream, new FrameReader(), (640, 320, 1), ct);

        await node.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamIsAWebSocketOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);

        using var response = await node.Client.GetAsync(FrameStreamWire.PathOf(FrameSources.PlanetaryLive), ct);

        ((int)response.StatusCode).ShouldBe(400);
    }
}
