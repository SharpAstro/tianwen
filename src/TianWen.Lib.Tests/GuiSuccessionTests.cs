using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A window whose GPU cannot draw again is replaced by a fresh process (P7 of docs/plans/hardware-in-the-server.md, #937):
/// the successor carries this process's arguments and is marked by the environment, a boot loop is bounded by a generation
/// cap that a healthy uptime resets, and the successor waits for its predecessor to exit before it claims the gate.
/// </summary>
public class GuiSuccessionTests
{
    private static Func<string, string?> Environment(Dictionary<string, string?> variables)
        => name => variables.TryGetValue(name, out var value) ? value : null;

    [Fact]
    public void TheSuccessorCarriesTheArgumentsAndIsMarkedByTheEnvironment()
    {
        var start = GuiSuccession.SuccessorFor("C:/apps/tianwen-gui.exe", ["--active", "Rig"], processId: 4242, generation: 0, TimeSpan.FromSeconds(30))
            .ShouldNotBeNull();

        start.FileName.ShouldBe("C:/apps/tianwen-gui.exe");
        start.ArgumentList.ShouldBe(["--active", "Rig"], "a user's own arguments pass through as they were");
        start.UseShellExecute.ShouldBeFalse();
        start.Environment[GuiSuccession.PredecessorVariable].ShouldBe("4242");
        start.Environment[GuiSuccession.GenerationVariable].ShouldBe("1");
    }

    [Fact]
    public void ABootLoopIsCappedAndAHealthyUptimeStartsTheCountAgain()
    {
        GuiSuccession.SuccessorFor("gui", [], 1, generation: GuiSuccession.MaxGeneration - 1, TimeSpan.FromSeconds(10))
            .ShouldNotBeNull().Environment[GuiSuccession.GenerationVariable].ShouldBe(GuiSuccession.MaxGeneration.ToString());
        GuiSuccession.SuccessorFor("gui", [], 1, generation: GuiSuccession.MaxGeneration, TimeSpan.FromSeconds(10))
            .ShouldBeNull("a GPU that fails every window it is given is not one to keep starting windows on");
        GuiSuccession.SuccessorFor("gui", [], 1, generation: GuiSuccession.MaxGeneration, GuiSuccession.HealthyUptime)
            .ShouldNotBeNull("a window that drew for a while was a one-off loss, and the count starts again")
            .Environment[GuiSuccession.GenerationVariable].ShouldBe("1");
    }

    /// <summary>
    /// #1414: a process id is handed out again, so the successor is told when its predecessor started and holds the
    /// process it finds to that. The rule is its own function so that every case can be read without a process.
    /// </summary>
    [Fact]
    public void AProcessFoundUnderThePredecessorsIdIsHeldToThePredecessorsStart()
    {
        var started = new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);

        GuiSuccession.IsTheProcess(started, () => started).ShouldBeTrue();
        GuiSuccession.IsTheProcess(started, () => started + GuiSuccession.StartTolerance).ShouldBeTrue("two readings of one start may differ a little");
        GuiSuccession.IsTheProcess(started, () => started - GuiSuccession.StartTolerance).ShouldBeTrue();
        GuiSuccession.IsTheProcess(started, () => started + GuiSuccession.StartTolerance + TimeSpan.FromSeconds(1)).ShouldBeFalse("another process, started later");
        GuiSuccession.IsTheProcess(started, () => started - TimeSpan.FromHours(1)).ShouldBeFalse();
        // Nothing to hold it to, or nothing to read: the id is trusted, as it was before the start was passed.
        GuiSuccession.IsTheProcess(null, () => started).ShouldBeTrue();
        GuiSuccession.IsTheProcess(started, () => null).ShouldBeTrue();
    }

    [Fact]
    public void TheSuccessorIsToldWhenItsPredecessorStarted()
    {
        var started = new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.FromHours(11));
        var start = GuiSuccession.SuccessorFor("gui", [], processId: 4242, generation: 0, TimeSpan.Zero, started).ShouldNotBeNull();

        start.Environment[GuiSuccession.PredecessorStartedVariable].ShouldBe(started.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        GuiSuccession.PredecessorStartedOf(name => start.Environment.TryGetValue(name, out var value) ? value : null).ShouldBe(started);

        // Not told: no variable. (That a start it INHERITED is taken out is AStartThisProcessWasHandedIsNotPassedOn's:
        // nothing in a test host's environment is there to inherit, so this line cannot show it.)
        GuiSuccession.SuccessorFor("gui", [], 4242, 0, TimeSpan.Zero).ShouldNotBeNull()
            .Environment.ContainsKey(GuiSuccession.PredecessorStartedVariable).ShouldBeFalse();
        GuiSuccession.PredecessorStartedOf(Environment([])).ShouldBeNull();
        GuiSuccession.PredecessorStartedOf(Environment(new() { [GuiSuccession.PredecessorStartedVariable] = "x" })).ShouldBeNull();
        GuiSuccession.PredecessorStartedOf(Environment(new() { [GuiSuccession.PredecessorStartedVariable] = "0" })).ShouldBeNull();
        GuiSuccession.PredecessorStartedOf(Environment(new() { [GuiSuccession.PredecessorStartedVariable] = "-5" })).ShouldBeNull();
    }

    /// <summary>
    /// #2381: the successor's log line says how its wait ended, and a process taken for another one gets words of its
    /// own, so that a window that started beside a dying one can be explained from the log.
    /// </summary>
    [Fact]
    public void TheLogSaysHowTheWaitEndedAndSaysNothingWhenThePredecessorHadGone()
    {
        GuiSuccession.Says(PredecessorWait.Exited).ShouldBe("");
        GuiSuccession.Says(PredecessorWait.StillRunning).ShouldBe("; it had not exited in time");
        GuiSuccession.Says(PredecessorWait.TakenForAnother)
            .ShouldBe("; a process under its id that was started at another time was still running after 2 s and was taken for another process");
    }

    /// <summary>
    /// #2362: a successor's environment starts as a copy of this process's own, so the start this process was itself
    /// handed (its predecessor's) would go on to the successor with the rest. With no start of its own to give, it is
    /// taken out, and the other two are written over.
    /// </summary>
    [Fact]
    public void AStartThisProcessWasHandedIsNotPassedOn()
    {
        var environment = new Dictionary<string, string?>
        {
            [GuiSuccession.PredecessorVariable] = "77",
            [GuiSuccession.PredecessorStartedVariable] = "638990000000000000",
            [GuiSuccession.GenerationVariable] = "2",
            ["TIANWEN_SOMETHING_ELSE"] = "kept",
        };

        GuiSuccession.Describe(environment, processId: 4242, processStarted: null, generation: 3);

        environment.ContainsKey(GuiSuccession.PredecessorStartedVariable).ShouldBeFalse("that start is its predecessor's, not its own");
        environment[GuiSuccession.PredecessorVariable].ShouldBe("4242");
        environment[GuiSuccession.GenerationVariable].ShouldBe("3");
        environment["TIANWEN_SOMETHING_ELSE"].ShouldBe("kept");
    }

    /// <summary>
    /// #1414: once read, the succession's variables are taken out of this process's environment, so that nothing it
    /// starts inherits them and reads as a successor.
    /// </summary>
    [Fact]
    public void TheVariablesAreTakenOutOnceRead()
    {
        var environment = new Dictionary<string, string?>
        {
            [GuiSuccession.PredecessorVariable] = "77",
            [GuiSuccession.PredecessorStartedVariable] = "638990000000000000",
            [GuiSuccession.GenerationVariable] = "2",
            ["TIANWEN_SOMETHING_ELSE"] = "kept",
        };

        GuiSuccession.ClearFrom((name, value) =>
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        });

        environment.Keys.ShouldBe(["TIANWEN_SOMETHING_ELSE"]);
        GuiSuccession.PredecessorOf(Environment(environment)).ShouldBeNull();
        GuiSuccession.GenerationOf(Environment(environment)).ShouldBe(0);
    }

    [Fact]
    public void AProcessAUserStartedIsGenerationNoughtWithNoPredecessor()
    {
        var none = Environment([]);
        GuiSuccession.GenerationOf(none).ShouldBe(0);
        GuiSuccession.PredecessorOf(none).ShouldBeNull();

        var successor = Environment(new() { [GuiSuccession.GenerationVariable] = "2", [GuiSuccession.PredecessorVariable] = "77" });
        GuiSuccession.GenerationOf(successor).ShouldBe(2);
        GuiSuccession.PredecessorOf(successor).ShouldBe(77);

        var garbage = Environment(new() { [GuiSuccession.GenerationVariable] = "-3", [GuiSuccession.PredecessorVariable] = "x" });
        GuiSuccession.GenerationOf(garbage).ShouldBe(0);
        GuiSuccession.PredecessorOf(garbage).ShouldBeNull();

        // Process 0 is the system's idle process, never a window this one replaces.
        GuiSuccession.PredecessorOf(Environment(new() { [GuiSuccession.PredecessorVariable] = "0" })).ShouldBeNull();
    }

    [Fact]
    public void TheWindowStartsItsSuccessorAndReleasesTheGateBeforeItExits()
    {
        var steps = new List<string>();
        var successor = GuiSuccession.SuccessorFor("gui", [], 1, 0, TimeSpan.Zero).ShouldNotBeNull();

        GuiSuccession.LeaveFor(successor, "Display lost", new Step(steps, "gate released"), NullLogger.Instance,
            start => { steps.Add("successor started"); start.ShouldBeSameAs(successor); return 4243; },
            code => steps.Add($"exit {code}"));

        steps.ShouldBe(["successor started", "gate released", $"exit {GuiSuccession.ReplacedExitCode}"],
            "an exit can hang on a hung driver, and a gate it still held would hand every later start to that window");
    }

    [Fact]
    public void ASuccessorThatDoesNotStartStillLetsTheWindowLeave()
    {
        var steps = new List<string>();
        var successor = GuiSuccession.SuccessorFor("gui", [], 1, 0, TimeSpan.Zero).ShouldNotBeNull();

        GuiSuccession.LeaveFor(successor, "Display lost", new Step(steps, "gate released"), NullLogger.Instance,
            _ => throw new Win32Exception(2, "The system cannot find the file specified"),
            code => steps.Add($"exit {code}"));

        steps.ShouldBe(["gate released", $"exit {GuiSuccession.ReplacedExitCode}"],
            "a window that cannot draw leaves either way, and the rig goes on in the node");
    }

    private sealed class Step(List<string> steps, string name) : IDisposable
    {
        public void Dispose() => steps.Add(name);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheSuccessorWaitsForItsPredecessorToExit()
    {
        var ct = TestContext.Current.CancellationToken;
        // A predecessor that lives until the test ends it, so no step races its exit. On Unix it is not this process's
        // child, as a GUI's predecessor never is: a shell starts it in the background, reports its pid and exits.
        var predecessor = await StartPredecessorAsync(ct);
        try
        {
            (await GuiSuccession.WaitForPredecessorAsync(predecessor, started: null, TimeSpan.FromMilliseconds(50), SystemTimeProvider.Instance, ct))
                .ShouldBe(PredecessorWait.StillRunning, "it was still running when the budget ran out");

            // #1414: with its start given, the same process is still the one waited for...
            DateTimeOffset started;
            using (var found = Process.GetProcessById(predecessor))
            {
                started = new DateTimeOffset(found.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            (await GuiSuccession.WaitForPredecessorAsync(predecessor, started, TimeSpan.FromMilliseconds(50), SystemTimeProvider.Instance, ct))
                .ShouldBe(PredecessorWait.StillRunning, "its own start names it, so it is waited for");
            // ...and a process under that id with ANOTHER start is taken for a stranger, given the id since: the whole
            // budget is not spent on it (#1414), but it is still waited for a short grace, in case it is the predecessor
            // after all (#2362). On a fake clock, which only the waiting moves: the process runs throughout, so where
            // the clock stops is how long was waited, and no wall-clock bar is needed to tell the three apart.
            var fake = new FakeTimeProviderWrapper();
            var before = fake.GetUtcNow();
            (await GuiSuccession.WaitForPredecessorAsync(predecessor, started - TimeSpan.FromHours(1), GuiSuccession.PredecessorExitBudget, fake, ct))
                .ShouldBe(PredecessorWait.TakenForAnother, "a process started at another time is not the one this window replaces, and the answer says so");
            (fake.GetUtcNow() - before).ShouldBe(GuiSuccession.StrangerGrace, "the grace: not the whole budget, and not no wait at all");

            // A caller with less than the grace to spend gets its own budget and no more (#2381): half a second is
            // waited for a stranger, not the two seconds of the grace.
            var shortBudget = TimeSpan.FromMilliseconds(500);
            var beforeShort = fake.GetUtcNow();
            (await GuiSuccession.WaitForPredecessorAsync(predecessor, started - TimeSpan.FromHours(1), shortBudget, fake, ct))
                .ShouldBe(PredecessorWait.TakenForAnother);
            (fake.GetUtcNow() - beforeShort).ShouldBe(shortBudget, "a budget under the grace is the wait, the grace does not stretch it");

            var waiting = GuiSuccession.WaitForPredecessorAsync(predecessor, started, GuiSuccession.PredecessorExitBudget, SystemTimeProvider.Instance, ct);
            End(predecessor);
            (await waiting).ShouldBe(PredecessorWait.Exited, "it exited within the budget");

            (await GuiSuccession.WaitForPredecessorAsync(predecessor, started, TimeSpan.FromMilliseconds(50), SystemTimeProvider.Instance, ct))
                .ShouldBe(PredecessorWait.Exited, "a predecessor gone already needs no waiting for");
        }
        finally
        {
            End(predecessor);
        }
    }

    private static async Task<int> StartPredecessorAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            using var ping = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }).ShouldNotBeNull();
            return ping.Id;
        }

        using var shell = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 60 >/dev/null 2>&1 & echo $!\"")
            { UseShellExecute = false, RedirectStandardOutput = true }).ShouldNotBeNull();
        var pid = int.Parse((await shell.StandardOutput.ReadLineAsync(ct)).ShouldNotBeNull());
        await shell.WaitForExitAsync(ct);
        return pid;
    }

    private static void End(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone already.
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task APredecessorThatExitedWhileTheWaitWasStarvedIsAnsweredAsExited()
    {
        // #1245: the wait raced the runtime's exit notification, which runs on the thread pool, against its budget's
        // timer, and on a starved pool the timer won, so a predecessor gone for 18 s was reported running. A wait
        // whose sleep overruns its budget must answer by LOOKING once it wakes.
        var time = new FakeTimeProvider();
        var exited = false;
        var sleeps = 0;

        var answer = await GuiSuccession.WaitForExitAsync(() => exited, TimeSpan.FromSeconds(1), time,
            (delay, _) =>
            {
                sleeps++;
                time.Advance(delay + TimeSpan.FromSeconds(18));
                exited = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        answer.ShouldBeTrue("it had exited by the time the wait looked, however late that look was");
        sleeps.ShouldBe(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task APredecessorStillRunningIsAnsweredOnlyByALookAtTheEndOfTheBudget()
    {
        var time = new FakeTimeProvider();
        var start = time.GetTimestamp();
        var looks = new List<TimeSpan>();

        var answer = await GuiSuccession.WaitForExitAsync(() => { looks.Add(time.GetElapsedTime(start)); return false; },
            TimeSpan.FromSeconds(1), time,
            (delay, _) =>
            {
                delay.ShouldBeLessThanOrEqualTo(GuiSuccession.PredecessorPollInterval);
                time.Advance(delay);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        answer.ShouldBeFalse();
        looks[^1].ShouldBe(TimeSpan.FromSeconds(1), "the false is a look taken once the budget has run, never before it");
    }

    [Fact(Timeout = 10_000)]
    public async Task AWaitEndsAtOnceWhenItsTokenIsCancelled()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(GuiSuccession.WaitForExitAsync(() => false, TimeSpan.FromMinutes(5),
            TimeProvider.System, static (delay, ct) => Task.Delay(delay, ct), cancelled.Token));
    }
}
