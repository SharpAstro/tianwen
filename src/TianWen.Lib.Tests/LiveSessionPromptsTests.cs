using NSubstitute;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="LiveSessionPrompts"/>, the ONE wiring of a run's prompts to the live view that shows them: a local session's,
/// a flat run's and a rig's through its mirror (P5b part 5).
/// </summary>
public class LiveSessionPromptsTests
{
    private static (SessionPromptEventArgs Prompt, TaskCompletionSource<bool> Answer) Prompt(bool defaultIfUnanswerable = false)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (new SessionPromptEventArgs("Manual flat panel", "Switch the panel on, then Continue.", "Continue", "Cancel", answer,
            requiresPhysicalPresence: true, defaultIfUnanswerable), answer);
    }

    [Fact(Timeout = 10_000)]
    public async Task APromptShowsOnTheViewAndComesOffOnceItSettles()
    {
        var run = Substitute.For<ISessionTelemetry>();
        var view = new LiveSessionState();
        var app = new GuiAppState { ActiveTab = GuiTab.Planner };
        using var _ = LiveSessionPrompts.ShowOn(run, view, app);
        var (prompt, answer) = Prompt();

        run.PromptRequested += Raise.EventWith(run, prompt);

        view.PendingPrompt.ShouldBeSameAs(prompt);
        app.ActiveTab.ShouldBe(GuiTab.LiveSession, "this computer's own run brings its question to the front");

        // Withdrawn by the run (cancelled while it waited): nothing is left waiting on the view.
        answer.TrySetCanceled();
        await UntilClearedAsync(view);
    }

    [Fact]
    public void ARigsPromptWaitsOnItsOwnViewWithoutTakingTheScreen()
    {
        var run = Substitute.For<ISessionTelemetry>();
        var view = new LiveSessionState();
        using var _ = LiveSessionPrompts.ShowOn(run, view, app: null);
        var (prompt, _) = Prompt();

        run.PromptRequested += Raise.EventWith(run, prompt);

        view.PendingPrompt.ShouldBeSameAs(prompt);
    }

    [Fact]
    public async Task WithTheDisplayGoneAPromptGetsTheUnattendedAnswerAtOnce()
    {
        var run = Substitute.For<ISessionTelemetry>();
        var view = new LiveSessionState { AnswerPromptsUnattended = true };
        using var _ = LiveSessionPrompts.ShowOn(run, view, app: null);
        var (prompt, answer) = Prompt(defaultIfUnanswerable: true);

        run.PromptRequested += Raise.EventWith(run, prompt);

        (await answer.Task).ShouldBeTrue();
        view.PendingPrompt.ShouldBeNull();
    }

    [Fact]
    public void OnceDetachedARunsPromptNoLongerShows()
    {
        var run = Substitute.For<ISessionTelemetry>();
        var view = new LiveSessionState();
        LiveSessionPrompts.ShowOn(run, view, app: null).Dispose();
        var (prompt, _) = Prompt();

        run.PromptRequested += Raise.EventWith(run, prompt);

        view.PendingPrompt.ShouldBeNull();
    }

    private static async Task UntilClearedAsync(LiveSessionState view)
    {
        while (view.PendingPrompt is not null)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
