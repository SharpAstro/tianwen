using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A node's feed words a run as the GUI does for the same run in-process (P5b part 4b of
/// docs/plans/hardware-in-the-server.md, #935): both go through <see cref="SessionNotes"/>, the ONE mapping, where a
/// remote rig's feed used to read "Cooling -> RoughFocus" and noted a failure before the run's Finalise had run. Driven over
/// real HTTP against a real host, with a session that ends when the test says so.
/// </summary>
[Collection("Hosting")]
public class NodeRunNotesTests(ITestOutputHelper output)
{
    [Fact(Timeout = 30_000)]
    public async Task ANodesFeedWordsARunAsTheGuiDoesAndNotesItsEndOnceItHasEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NodeHarness.StartAsync(output, ct);
        harness.Factory.Initialised.SetResult();
        var run = await harness.StartSessionAsync(ct);

        // A phase, raised as the session raises it.
        run.Session.PhaseChanged += Raise.EventWith(run.Session, new SessionPhaseChangedEventArgs(SessionPhase.Cooling, SessionPhase.RoughFocus));

        // The run ends failed, and its Finalise is still to come: the failure is noted once that is done too.
        run.Session.Phase.Returns(SessionPhase.Failed);
        run.Session.FailureReason.Returns("The camera stopped answering");
        run.EndsOnItsOwn.SetResult();
        harness.Node.Notifications.Select(n => n.Message).ShouldNotContain(m => m.StartsWith("Session failed", StringComparison.Ordinal),
            "a run is not over before its Finalise");
        run.Finalise.SetResult();

        var failed = SessionNotes.ForRunEnd(SessionPhase.Failed, "The camera stopped answering").ShouldNotBeNull();
        await UntilAsync("the node to note the run's end", _ => ValueTask.FromResult(
            (harness.Node.Notifications.Any(n => n.Message == failed.Message), $"{harness.Node.Notifications.Length} notes")), ct);

        harness.Node.Notifications.Select(n => (n.Severity, n.Message)).ShouldBe(
        [
            ("Info", SessionNotes.ForRunStart(flatRun: false).Message),
            ("Info", SessionNotes.ForPhase(SessionPhase.RoughFocus).ShouldNotBeNull().Message),
            ("Error", failed.Message),
        ]);
    }
}
