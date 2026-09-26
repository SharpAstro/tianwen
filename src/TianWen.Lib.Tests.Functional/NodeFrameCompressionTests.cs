using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A frame compressed over TCP only, and only when the client asks (P4 part 3 of docs/plans/hardware-in-the-server.md,
/// #931): Brotli, or gzip for a client that takes only gzip, and never over the local socket, where a copy is cheaper than
/// any codec. The node's own client asks over TCP and reads the frame back bit for bit.
/// </summary>
[Collection("Hosting")]
public class NodeFrameCompressionTests(ITestOutputHelper output)
{
    /// <summary>A whole-ADU frame smooth enough to compress, as a sky background does a little.</summary>
    private static Image Frame()
    {
        var plane = new float[480, 640];
        for (var y = 0; y < 480; y++)
        {
            for (var x = 0; x < 640; x++)
            {
                plane[y, x] = 1000 + (x + y) % 64;
            }
        }
        return new Image([plane], BitDepth.Int16, maxValue: 65535, minValue: 0, pedestal: 0, default);
    }

    private static async Task<NodeHarness> RunningAsync(ITestOutputHelper output, Image frame, string? socketPath)
    {
        var node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken, socketPath: socketPath);
        node.Factory.OnCreated = controlled =>
        {
            controlled.Session.LastCapturedImages.Returns([frame]);
            controlled.Session.LastCapturedImageNumber(0).Returns(1);
        };
        node.Factory.Initialised.TrySetResult();
        await node.StartSessionAsync(TestContext.Current.CancellationToken);
        return node;
    }

    /// <summary>The frame route asked directly, with <paramref name="acceptEncoding"/>, by a client that decompresses nothing.</summary>
    private static async Task<(string? Encoding, long Bytes)> AskAsync(HttpClient http, string acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/frames/{FrameSources.Ota(0)}/latest");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        return (response.Content.Headers.ContentEncoding.SingleOrDefault(), body.Length);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("br, gzip", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("identity", null)]
    public async Task OverTcpAFrameIsCompressedOnlyAsAsked(string acceptEncoding, string? expected)
    {
        var frame = Frame();
        await using var node = await RunningAsync(output, frame, socketPath: null);
        using var plain = new HttpClient { BaseAddress = node.Transport.BaseAddress };

        var (encoding, bytes) = await AskAsync(plain, acceptEncoding);

        encoding.ShouldBe(expected);
        var uncompressed = 640 * 480 * sizeof(ushort);
        if (expected is null)
        {
            bytes.ShouldBeGreaterThan(uncompressed);
        }
        else
        {
            bytes.ShouldBeLessThan(uncompressed / 2, $"{expected} did not compress a smooth frame");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TheNodesClientAsksOverTcpAndReadsTheFrameBitExact()
    {
        var frame = Frame();
        await using var node = await RunningAsync(output, frame, socketPath: null);

        var got = await new TianWenNodeClient(node.Client).GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(),
            TestContext.Current.CancellationToken);

        var image = got.Image.ShouldNotBeNull(got.Error);
        image.GetChannelSpan(0).SequenceEqual(frame.GetChannelSpan(0)).ShouldBeTrue();
        image.Release();
    }

    [Fact(Timeout = 30_000)]
    public async Task OverTheSocketAFrameIsNeverCompressed()
    {
        await using var node = await RunningAsync(output, Frame(), Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));
        using var plain = NodeTransport.OverSocket(node.Transport.SocketPath!).CreateHttpClient();

        var (encoding, _) = await AskAsync(plain, "br, gzip");

        encoding.ShouldBeNull("the socket compressed a frame, where a copy is cheaper than any codec");
    }
}
