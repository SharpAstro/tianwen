using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The processing app's runs without a window (#1642, W2): one at a time, off the render thread, cancellable, with the running
/// step, its fraction and its lines in one immutable state, and an end that is always reported so the screen asks the runner afresh.
/// A step waits at a gate the test holds, so no test waits on a clock.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchJobTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");
    private readonly GateOperation _gate = new();
    private readonly BackgroundTaskTracker _tracker = new();
    private readonly CancellationTokenSource _app = new();
    private readonly ConcurrentQueue<WorkbenchJobSettled> _settled = new();
    private int _redraws;

    public void Dispose()
    {
        _app.Cancel();
        _app.Dispose();
        _folders.Dispose();
    }

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    private WorkbenchJobs Jobs()
    {
        var catalog = new OperationCatalog([_add, _gate]);
        return new WorkbenchJobs(new WorkbenchRunner(catalog), catalog, _tracker, NullLogger.Instance, _app.Token,
            () => Interlocked.Increment(ref _redraws), _settled.Enqueue);
    }

    private async Task<string> ProjectAsync(params ProjectEdit[] edits)
    {
        var folder = _folders.Create("job-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, edits)), TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact(Timeout = 120_000)]
    public async Task AStartWhileARunGoesIsRefusedAndTheFirstRunIsUnharmed()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("gate"));
        var jobs = Jobs();

        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _gate.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        jobs.IsRunning.ShouldBeTrue();
        jobs.TryStart(folder, "m", through: null).ShouldBeFalse("only one run at a time");

        _gate.Release.SetResult();
        await _tracker.DrainAsync();

        jobs.IsRunning.ShouldBeFalse();
        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Finished);
        _settled.Count.ShouldBe(1, "the one run that ran was reported once");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheRunningStepItsFractionAndItsLinesAreInTheState()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("gate"));
        var jobs = Jobs();

        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _gate.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var state = jobs.State;
        state.Running.ShouldBeTrue();
        state.StepIndex.ShouldBe(1, "the second step is the one running");
        state.Title.ShouldBe("Gate");
        state.Fraction.ShouldBe(0.5);
        state.Words.ShouldBe("Step 2, Gate: 50 percent", "the fraction in words");
        state.Lines.ShouldContain(line => line.Contains("at the gate", StringComparison.Ordinal), "the run's own line");
        state.Lines.ShouldContain(line => line.Contains("Step 1", StringComparison.Ordinal) && line.Contains("ran", StringComparison.Ordinal),
            "the step that ran before it");
        _redraws.ShouldBeGreaterThan(0, "every change asks for a frame");

        _gate.Release.SetResult();
        await _tracker.DrainAsync();
        jobs.State.Fraction.ShouldBeNull("a run that ended reports no fraction");
    }

    [Fact(Timeout = 120_000)]
    public async Task CancelEndsTheRunAndTheEndIsReported()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("gate"));
        var jobs = Jobs();
        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _gate.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        jobs.Cancel();
        await _tracker.DrainAsync();

        jobs.IsRunning.ShouldBeFalse();
        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Cancelled);
        _settled.Single().Outcome.ShouldBe(WorkbenchJobOutcome.Cancelled, "the screen is told, so it asks the runner afresh");
        jobs.TryStart(folder, "m", through: null).ShouldBeTrue("a cancelled run frees the slot");
        jobs.Cancel();
        await _tracker.DrainAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task AStartTheMomentTheFirstRunEndedCanBeCancelledAndKeepsItsOwnSource()
    {
        var second = new GateOperation("gate2");
        var catalog = new OperationCatalog([_add, _gate, second]);
        var first = await ProjectAsync(WorkbenchFixtures.Edit("gate"));
        var next = await ProjectAsync(WorkbenchFixtures.Edit("gate2"));
        WorkbenchJobs? jobs = null;
        var again = 0;
        var runningWithoutSource = 0;
        var startedInsideTheEnd = false;
        jobs = new WorkbenchJobs(new WorkbenchRunner(catalog), catalog, _tracker, NullLogger.Instance, _app.Token,
            () =>
            {
                Interlocked.Increment(ref _redraws);
                if (jobs?.State is not { } state)
                {
                    return;
                }

                if (state is { Running: true, Owner: null })
                {
                    Interlocked.Increment(ref runningWithoutSource);
                }

                // The frame the first run's end asks for, drawn while that run is still ending: Finish has said Running is
                // false, and the run has not yet disposed its own source nor told the screen it settled. A start pressed on
                // that frame is the one that once lost its source to the ending run.
                if (state is { Running: false, Outcome: WorkbenchJobOutcome.Finished } && _settled.IsEmpty
                    && Interlocked.Exchange(ref again, 1) == 0)
                {
                    startedInsideTheEnd = jobs.TryStart(next, "m", through: null);
                }
            },
            _settled.Enqueue);

        jobs.TryStart(first, "m", through: null).ShouldBeTrue();
        await _gate.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        _gate.Release.SetResult();
        await second.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        startedInsideTheEnd.ShouldBeTrue("the first run had said it was over, so the slot was free on the frame its end asked for");
        jobs.IsRunning.ShouldBeTrue("the second run is going");
        jobs.Cancel();
        await _tracker.DrainAsync();

        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Cancelled, "the second run could be cancelled; it held its own source");
        jobs.IsRunning.ShouldBeFalse();
        runningWithoutSource.ShouldBe(0, "a state that says a run is going always carries that run's source");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunOfOneImageIsNotStoppedByAnotherImagesRefusal()
    {
        var folder = _folders.Create("two-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add", Amount(1))),
            WorkbenchFixtures.Picture("n", master, WorkbenchFixtures.Edit("narrowband-from-a-newer-version"))), TestContext.Current.CancellationToken);
        var jobs = Jobs();

        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _tracker.DrainAsync();

        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Finished, "the other image's unknown operation is not this run's");
        _settled.Single().SelectStep.ShouldBe(-1);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppShutdownEndsARunThroughTheLinkedToken()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("gate"));
        var jobs = Jobs();
        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _gate.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _app.CancelAsync();
        await _tracker.DrainAsync();

        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Cancelled);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunStopsAtARefusedStepAndSaysWhyAndWhichStepToSelect()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("ghost"), WorkbenchFixtures.Edit("add", Amount(5)));
        var jobs = Jobs();

        jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await _tracker.DrainAsync();

        var state = jobs.State;
        state.Outcome.ShouldBe(WorkbenchJobOutcome.Stopped);
        state.Text.ShouldStartWith("Stopped at step 2", Case.Sensitive);
        state.Text.ShouldContain("ghost", Case.Insensitive, "the runner's reason");
        _settled.Single().SelectStep.ShouldBe(1, "the step that stopped the run is the one to look at");
        _add.Runs.ShouldBe(1, "the step behind the refused one did not run");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunThroughAStepMakesNothingAfterItAndSelectsWhatTheCallerAsked()
    {
        var folder = await ProjectAsync(WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("add", Amount(3)));
        var jobs = Jobs();

        jobs.TryStart(folder, "m", through: 1, selectAfter: 1).ShouldBeTrue();
        await _tracker.DrainAsync();

        jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Finished);
        _add.Runs.ShouldBe(2, "steps 1 and 2 only");
        _settled.Single().SelectStep.ShouldBe(1);
    }
}
