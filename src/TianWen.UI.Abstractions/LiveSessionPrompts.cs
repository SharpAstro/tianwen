using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The ONE wiring of a run's prompts to the live view that shows them (P5b part 5 of docs/plans/hardware-in-the-server.md):
/// a local session's (<see cref="SessionBootstrapper"/>), a flat run's (<see cref="FlatsBootstrapper"/>) and a rig's,
/// through its mirror (<see cref="RemoteRigConnection"/>), whose answer the mirror sends to the rig's node.
/// <para>
/// Only the flat run had it. A local full session subscribed nothing, so the one prompt a session raises (a manual flat
/// panel at the end-of-session flats) was declined unseen; and nothing subscribed a mirror's, while the GUI beats to the
/// node as present, so the node held a rig's prompt for a GUI that never showed it.
/// </para>
/// </summary>
public static class LiveSessionPrompts
{
    /// <summary>
    /// Shows every prompt <paramref name="run"/> raises on <paramref name="view"/> until the returned handle is disposed.
    /// A prompt comes off the view once it settles by any route: answered, or withdrawn by the run (a run cancelled while
    /// it waits, a node that stopped offering it).
    /// </summary>
    /// <param name="app">The app, for a prompt from this computer's own run, which brings the Live Session tab to the
    /// front. Null for a rig's, which waits on its own view and its Home card rather than taking the screen.</param>
    public static IDisposable ShowOn(ISessionTelemetry run, LiveSessionState view, GuiAppState? app)
    {
        // Fires on the run's thread; a reference assignment and a redraw flag are all that cross over.
        void OnPromptRequested(object? _, SessionPromptEventArgs prompt)
        {
            // Nobody can see an overlay once the display is gone: answer as an unattended caller would, rather than hold
            // the run for a frame that will never be drawn.
            if (view.AnswerPromptsUnattended)
            {
                prompt.Respond(prompt.DefaultIfUnanswerable);
                return;
            }
            view.PendingPrompt = prompt;
            view.NeedsRedraw = true;
            if (app is not null)
            {
                app.ActiveTab = GuiTab.LiveSession;
                app.NeedsRedraw = true;
            }

            // Off the view once it settles by any route: a prompt nothing waits on must not stay up.
            _ = prompt.Settled.ContinueWith(_ =>
            {
                if (view.TryClearPendingPrompt(prompt))
                {
                    view.NeedsRedraw = true;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        run.PromptRequested += OnPromptRequested;
        return new Subscription(() => run.PromptRequested -= OnPromptRequested);
    }

    private sealed class Subscription(Action detach) : IDisposable
    {
        private Action? _detach = detach;

        public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
    }
}
