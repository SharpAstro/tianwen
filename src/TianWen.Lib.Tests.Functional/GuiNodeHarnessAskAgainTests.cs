using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// What <see cref="GuiNodeHarness.ActUntilAsync"/> promises the tests that click through the GUI's handler: a gesture the
/// node did not answer is made again, as a user would, and nothing else is. CI run 41939984127 hung 30 s in a click test
/// because a call outran its client's budget on a runner whose thread pool was starved, the handler noted it and gave up,
/// and the test waited for what the click would have done. A refusal asked again would hide the very thing the tests are
/// for, so the second half matters as much as the first (#2023).
/// </summary>
[Collection("NodeProcesses")]
public class GuiNodeHarnessAskAgainTests(ITestOutputHelper output)
{
    [Fact(Timeout = 30_000)]
    public async Task AGestureTheNodeDidNotAnswerIsMadeAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var asked = 0;

        await h.ActUntilAsync("the second ask to be made", () =>
        {
            if (++asked == 1)
            {
                // What the handler notes for a call that outran its budget: the click is lost, nothing else comes.
                h.AppState.AppendNotification(DateTimeOffset.UtcNow, NotificationSeverity.Warning, $"{TianWenNodeClient.NoAnswerText} 10s");
            }
        }, () => asked >= 2, ct);

        asked.ShouldBe(2, "asked once more for the one no answer, and not again once it was done");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARefusalIsNeverAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var asked = 0;
        var looks = 0;

        // The wait looks every 20 ms; 50 looks are a second in which a second ask had every chance.
        await h.ActUntilAsync("a second of looks at a refused gesture", () =>
        {
            asked++;
            h.AppState.AppendNotification(DateTimeOffset.UtcNow, NotificationSeverity.Warning, "The imaging session holds Fake Mount 1");
        }, () => ++looks >= 50, ct);

        asked.ShouldBe(1, "a refusal says something a second click would not change");
    }

    [Fact(Timeout = 30_000)]
    public async Task ANoAnswerWhileATransitionIsInFlightIsNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var asked = 0;
        var looks = 0;
        // A connect or a disconnect the handler is still running: its own no-answer (a job poll) is not a lost click.
        h.Equipment.PendingTransitions.TryAdd(h.MountUri, 0).ShouldBeTrue();

        await h.ActUntilAsync("a second of looks at a gesture whose transition is running", () =>
        {
            asked++;
            h.AppState.AppendNotification(DateTimeOffset.UtcNow, NotificationSeverity.Error, $"Disconnecting Fake Mount 1: {TianWenNodeClient.NoAnswerText} 5s");
        }, () => ++looks >= 50, ct);

        asked.ShouldBe(1);
        h.Equipment.PendingTransitions.TryRemove(h.MountUri, out _);
    }
}
