using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>How a run ended.</summary>
public enum WorkbenchJobOutcome
{
    /// <summary>No run has ended yet.</summary>
    None,

    /// <summary>The run made what it was asked for.</summary>
    Finished,

    /// <summary>The run stopped at a step that is refused or blocked (or the project was refused); the text says why.</summary>
    Stopped,

    /// <summary>The person cancelled the run.</summary>
    Cancelled,

    /// <summary>The run failed with an exception.</summary>
    Failed,
}

/// <summary>
/// What the job panel shows, as one immutable value replaced whole: the run's worker thread writes it and the render thread reads
/// it, with nothing to lock.
/// </summary>
/// <param name="Running">True while a run is going.</param>
/// <param name="Image">The image the run is making.</param>
/// <param name="StepIndex">The running step's place in the edit list, from 0; -1 before the first step starts.</param>
/// <param name="Title">The running step's title.</param>
/// <param name="Fraction">How far the running step is, where its operation reports it; null otherwise.</param>
/// <param name="Lines">Every line the run said, oldest first.</param>
/// <param name="Outcome">How the last run ended.</param>
/// <param name="Text">The last run's end in words (why it stopped); empty while running or before any run.</param>
public sealed record WorkbenchJobState(
    bool Running,
    string Image,
    int StepIndex,
    string Title,
    double? Fraction,
    ImmutableArray<string> Lines,
    WorkbenchJobOutcome Outcome,
    string Text)
{
    /// <summary>No run yet.</summary>
    public static WorkbenchJobState Idle { get; } = new(false, string.Empty, -1, string.Empty, null, [], WorkbenchJobOutcome.None, string.Empty);

    /// <summary>The running step in words: its number and title, and how far it is where that is known.</summary>
    public string Words => !Running ? Text
        : StepIndex < 0 ? "Starting"
        : Fraction is { } fraction ? $"Step {StepIndex + 1}, {Title}: {Math.Round(Math.Clamp(fraction, 0d, 1d) * 100d):0} percent"
        : $"Step {StepIndex + 1}, {Title}: running";
}

/// <summary>What a run left behind, handed to whoever refreshes the screen.</summary>
/// <param name="Folder">The project.</param>
/// <param name="Image">The image the run made.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="SelectStep">The step to select next: where the run stopped, or the step an off switch made; -1 for none.</param>
public sealed record WorkbenchJobSettled(string Folder, string Image, WorkbenchJobOutcome Outcome, int SelectStep);

/// <summary>
/// One run at a time off the render thread, for the processing app: starts <see cref="WorkbenchRunner.RunAsync"/> on the tracker
/// with a token linked to the app's, takes its progress and lines into <see cref="State"/>, and says when it ended. No window is
/// needed to drive it.
/// </summary>
/// <remarks>
/// A second start while one runs is refused (<see cref="TryStart"/> answers false), so no screen offers it. What a run did to the
/// steps is never read from here: the settled callback asks the runner afresh.
/// </remarks>
public sealed class WorkbenchJobs(
    WorkbenchRunner runner,
    OperationCatalog catalog,
    BackgroundTaskTracker tracker,
    ILogger logger,
    CancellationToken appToken,
    Action requestRedraw,
    Action<WorkbenchJobSettled> onSettled)
{
    private const int MostLines = 2000;

    private WorkbenchJobState _state = WorkbenchJobState.Idle;
    private CancellationTokenSource? _cts;

    /// <summary>What the job panel shows now.</summary>
    public WorkbenchJobState State => Volatile.Read(ref _state);

    /// <summary>True while a run is going.</summary>
    public bool IsRunning => State.Running;

    /// <summary>
    /// Starts a run of one image, up to <paramref name="through"/> or to the end of its list. False, and nothing started, while a
    /// run is going.
    /// </summary>
    /// <param name="folder">The project folder.</param>
    /// <param name="image">The image to make.</param>
    /// <param name="through">The step to make it up to, from 0; null for the whole image.</param>
    /// <param name="selectAfter">The step to select when the run ended without stopping at a refused one; -1 for none.</param>
    public bool TryStart(string folder, string image, int? through, int selectAfter = -1)
    {
        var started = new WorkbenchJobState(true, image, -1, string.Empty, null, [], WorkbenchJobOutcome.None, string.Empty);
        while (true)
        {
            var seen = State;
            if (seen.Running)
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, started, seen), seen))
            {
                break;
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        Volatile.Write(ref _cts, cts);
        var outcome = WorkbenchJobOutcome.Cancelled;
        var stoppedAt = -1;
        requestRedraw();
        tracker.RunGuarded(async ct =>
            {
                var report = await runner.RunAsync(folder, new WorkbenchRunOptions
                {
                    Image = image,
                    Through = through,
                    Say = line => Update(s => s with { Lines = Append(s.Lines, line) }),
                    Progress = new Sink(Progressed),
                }, ct);
                ct.ThrowIfCancellationRequested();
                (outcome, stoppedAt) = Judge(report, through);
            },
            cts.Token, logger, "Run the workbench project",
            onError: ex =>
            {
                outcome = WorkbenchJobOutcome.Failed;
                Update(s => s with { Lines = Append(s.Lines, $"The run failed: {ex.Message}") });
            },
            onCancel: () => outcome = WorkbenchJobOutcome.Cancelled,
            onFinally: () =>
            {
                Finish(outcome);
                Interlocked.Exchange(ref _cts, null)?.Dispose();
                requestRedraw();
                onSettled(new WorkbenchJobSettled(folder, image, outcome, outcome is WorkbenchJobOutcome.Stopped ? stoppedAt : selectAfter));
            });
        return true;
    }

    /// <summary>Cancels the run that is going, if any; it ends at its next check of the token.</summary>
    public void Cancel()
    {
        if (Volatile.Read(ref _cts) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended between the read and the cancel: there is nothing left to cancel.
            }
        }
    }

    private (WorkbenchJobOutcome, int) Judge(WorkbenchReport report, int? through)
    {
        if (report.Refusal is { } refusal)
        {
            Update(s => s with { Text = refusal });
            return (WorkbenchJobOutcome.Stopped, -1);
        }

        foreach (var image in report.Images)
        {
            if (image.Refusal is { } imageRefusal)
            {
                Update(s => s with { Text = imageRefusal });
                return (WorkbenchJobOutcome.Stopped, -1);
            }

            var steps = through is { } last ? image.Steps.Where(step => step.Index <= last) : image.Steps;
            if (steps.FirstOrDefault(static step => step.State is StepState.Refused or StepState.Blocked) is { } stopped)
            {
                var title = TitleOf(stopped.Operation);
                var why = stopped.Message is { Length: > 0 } message ? message : stopped.Words;
                Update(s => s with { Text = $"Stopped at step {stopped.Index + 1}, {title} ({stopped.Words}): {why}" });
                return (WorkbenchJobOutcome.Stopped, stopped.Index);
            }
        }

        return (WorkbenchJobOutcome.Finished, -1);
    }

    private void Progressed(WorkbenchProgress progress)
    {
        var title = TitleOf(progress.Operation);
        switch (progress.Kind)
        {
            case WorkbenchProgressKind.Started:
                Update(s => s with { StepIndex = progress.StepIndex, Title = title, Fraction = null });
                break;
            case WorkbenchProgressKind.Fraction:
                Update(s => s with { StepIndex = progress.StepIndex, Title = title, Fraction = progress.Fraction });
                break;
            default:
                var words = progress.State is { } state ? StepStates.Words(state) : "ended";
                var line = $"Step {progress.StepIndex + 1}, {title}: {words}{(progress.Message is { Length: > 0 } m ? $". {m}" : string.Empty)}";
                Update(s => s with { StepIndex = progress.StepIndex, Title = title, Fraction = null, Lines = Append(s.Lines, line) });
                break;
        }
    }

    private void Finish(WorkbenchJobOutcome outcome)
    {
        var text = outcome switch
        {
            WorkbenchJobOutcome.Finished => "The run finished.",
            WorkbenchJobOutcome.Cancelled => "The run was cancelled.",
            WorkbenchJobOutcome.Failed => "The run failed; its last line says why.",
            _ => null,
        };
        Update(s => s with { Running = false, Fraction = null, Outcome = outcome, Text = text ?? s.Text });
    }

    private string TitleOf(string operation) => catalog.TryGet(operation, out var op) ? op.Description.Title : operation;

    private static ImmutableArray<string> Append(ImmutableArray<string> lines, string line)
        => (lines.Length >= MostLines ? lines.RemoveRange(0, lines.Length - MostLines + 1) : lines).Add(line);

    // Replaces the state with a change of it, by compare-and-swap, so the worker's writes and a reader never see half of one.
    private void Update(Func<WorkbenchJobState, WorkbenchJobState> change)
    {
        while (true)
        {
            var seen = State;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, change(seen), seen), seen))
            {
                break;
            }
        }

        requestRedraw();
    }

    private sealed class Sink(Action<WorkbenchProgress> report) : IProgress<WorkbenchProgress>
    {
        public void Report(WorkbenchProgress value) => report(value);
    }
}
