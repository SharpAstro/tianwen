using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
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
            (await GuiSuccession.WaitForPredecessorAsync(predecessor, TimeSpan.FromMilliseconds(50), ct))
                .ShouldBeFalse("it was still running when the budget ran out");

            var waiting = GuiSuccession.WaitForPredecessorAsync(predecessor, GuiSuccession.PredecessorExitBudget, ct);
            End(predecessor);
            (await waiting).ShouldBeTrue("it exited within the budget");

            (await GuiSuccession.WaitForPredecessorAsync(predecessor, TimeSpan.FromMilliseconds(50), ct))
                .ShouldBeTrue("a predecessor gone already needs no waiting for");
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
