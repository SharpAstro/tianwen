using Shouldly;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A node client's request budgets end on the clock it was handed (#1952). The budget is set to an hour, the node accepts
/// the request and never answers, and the fake clock is advanced by that hour ONCE, as soon as the budget's timer exists:
/// on the wall clock the request would hold for the hour (the HTTP client's own backstop is 100 s), and the test's own
/// timeout would end it instead. The expiry must still read as "no answer" and never as the caller's cancellation, which
/// the mirror's tests beside this one hold to (<see cref="RemoteSessionMirrorTests"/>).
/// </summary>
public class NodeClientDeadlineTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    /// <summary>A node that accepts the request and then never answers: the powered-off rig, which black-holes packets.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay above only ends by cancellation");
        }
    }

    private static HttpClient Stalled() => new HttpClient(new StallingHandler()) { BaseAddress = new Uri("http://rig.local:1888/") };

    [Fact(Timeout = 30_000)]
    public async Task AControlRequestNobodyAnswersEndsAtItsBudgetOnTheInjectedClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        using var http = Stalled();
        var client = new TianWenNodeClient(http, NodeTimeouts.Default with { StatePoll = Long }, clock);

        var asked = client.GetNodeAsync(ct);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        asked.IsCompleted.ShouldBeFalse("nobody has answered and the clock has not moved");
        clock.Advance(Long);

        var answer = await asked;
        answer.IsSuccess.ShouldBeFalse();
        answer.StatusCode.ShouldBe(503);
        answer.Error.ShouldBe("No answer within 3600s", "the budget, named: the caller never cancelled");
    }

    [Fact(Timeout = 30_000)]
    public async Task APreviewNobodyAnswersEndsAtItsBudgetOnTheInjectedClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        using var http = Stalled();
        var client = new TianWenNodeClient(http, NodeTimeouts.Default with { Preview = Long }, clock);

        var asked = client.GetPreviewAsync(otaIndex: 0, quality: null, scale: null, ifNotFrameNumber: null, ct);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        asked.IsCompleted.ShouldBeFalse("nobody has answered and the clock has not moved");
        clock.Advance(Long);

        var result = await asked;
        result.HasImage.ShouldBeFalse();
        result.Error.ShouldBe("No answer within 3600s");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALinearFrameNobodyAnswersEndsAtItsBudgetOnTheInjectedClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        using var http = Stalled();
        var client = new TianWenNodeClient(http, NodeTimeouts.Default with { Preview = Long }, clock);

        var asked = client.GetLatestFrameAsync("ota/0", after: null, new TianWen.Lib.Imaging.FrameReader(), ct);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        asked.IsCompleted.ShouldBeFalse("nobody has answered and the clock has not moved");
        clock.Advance(Long);

        var result = await asked;
        result.HasImage.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldBe("No answer within 3600s");
    }
}
