using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
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
        // A predecessor that lives a moment: a shell asked to wait, the one process every test box has.
        using var predecessor = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 2\"") { UseShellExecute = false }).ShouldNotBeNull();

        (await GuiSuccession.WaitForPredecessorAsync(predecessor.Id, TimeSpan.FromMilliseconds(50), ct))
            .ShouldBeFalse("it was still running when the budget ran out");
        (await GuiSuccession.WaitForPredecessorAsync(predecessor.Id, TimeSpan.FromSeconds(20), ct))
            .ShouldBeTrue("it exited within the budget");
        (await GuiSuccession.WaitForPredecessorAsync(predecessor.Id, TimeSpan.FromMilliseconds(50), ct))
            .ShouldBeTrue("a predecessor gone already needs no waiting for");
    }
}
