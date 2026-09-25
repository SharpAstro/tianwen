using NSubstitute;
using Shouldly;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The live preview's conditional GET over real HTTP, the real client against a real node (P0b item 15 of
/// docs/plans/hardware-in-the-server.md, #752). A picture carries its frame's token; a client that names the
/// current one is answered 304 before the node so much as reads the frame, where the node used to encode a
/// full frame per poll for the client to compare and throw away; and the client turns the 304 into
/// <see cref="PreviewResult.Unchanged"/>.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodePreviewTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness? _harness;

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private async Task<ControlledSession> RunningSessionShowingAsync(int frameNumber)
    {
        _harness.Factory.Initialised.SetResult();
        var controlled = await _harness.StartSessionAsync(TestContext.Current.CancellationToken);
        controlled.Session.LastCapturedImages.Returns([TestFrames.BufferedMono(out _)]);
        controlled.Session.LastCapturedImageNumber(0).Returns(frameNumber);
        return controlled;
    }

    [Fact(Timeout = 30_000)]
    public async Task AClientHoldingTheCurrentFrameIsAnswered304WithoutTheNodeReadingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var controlled = await RunningSessionShowingAsync(frameNumber: 3);
        var client = new TianWenNodeClient(_harness.Client);

        var first = await client.GetPreviewAsync(otaIndex: 0, quality: null, scale: null, ifNotFrameNumber: null, ct);
        first.HasImage.ShouldBeTrue(first.Error);
        first.FrameNumber.ShouldBe(3);

        controlled.Session.ClearReceivedCalls();
        var again = await client.GetPreviewAsync(otaIndex: 0, quality: null, scale: null, ifNotFrameNumber: first.FrameNumber, ct);

        again.IsUnchanged.ShouldBeTrue(again.Error);
        _ = controlled.Session.DidNotReceive().LastCapturedImages;

        // And as plain HTTP sees it, so a client that is not ours (curl, a browser) gets the standard answer.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/preview/0");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"3\""));
        using var response = await _harness.Client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe("\"3\"");
        (await response.Content.ReadAsByteArrayAsync(ct)).ShouldBeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task AClientHoldingTheLastFrameIsSentTheNewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var controlled = await RunningSessionShowingAsync(frameNumber: 3);
        var client = new TianWenNodeClient(_harness.Client);

        (await client.GetPreviewAsync(otaIndex: 0, quality: null, scale: null, ifNotFrameNumber: null, ct)).FrameNumber.ShouldBe(3);

        // The session publishes its next frame.
        controlled.Session.LastCapturedImageNumber(0).Returns(4);
        var next = await client.GetPreviewAsync(otaIndex: 0, quality: null, scale: null, ifNotFrameNumber: 3, ct);

        next.HasImage.ShouldBeTrue(next.Error);
        next.FrameNumber.ShouldBe(4);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheGuidePreviewTakesTheSameConditionalGet()
    {
        var ct = TestContext.Current.CancellationToken;
        var controlled = await RunningSessionShowingAsync(frameNumber: 1);
        controlled.Session.LastGuideFrame.Returns(TestFrames.BufferedMono(out _));
        controlled.Session.LastGuideFrameNumber.Returns(9);
        var client = new TianWenNodeClient(_harness.Client);

        (await client.GetGuidePreviewAsync(quality: null, scale: null, ifNotFrameNumber: null, ct)).FrameNumber.ShouldBe(9);

        controlled.Session.ClearReceivedCalls();
        var again = await client.GetGuidePreviewAsync(quality: null, scale: null, ifNotFrameNumber: 9, ct);

        again.IsUnchanged.ShouldBeTrue(again.Error);
        _ = controlled.Session.DidNotReceive().LastGuideFrame;
    }
}
