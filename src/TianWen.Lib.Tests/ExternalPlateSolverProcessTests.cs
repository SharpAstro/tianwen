using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An external solver's tool must never outlive the call that started it. On 2026-09-27 a wedged WSL
/// service made <c>wsl solve-field -h</c> hang for good: the probe had no bound, so every plate solve in the
/// process waited on it (the factory awaits every probe first), and every process that probed left its
/// <c>wsl.exe</c> pair behind (2,444 of them). These tests stand a tool that never exits in for the real
/// one, started one process down as <c>wsl.exe</c> and <c>bash -l -c</c> start theirs.
/// </summary>
[Collection("Astrometry")]
public class ExternalPlateSolverProcessTests
{
    [Fact(Timeout = 60_000)]
    public async Task AProbeThatNeverExitsCountsAsAbsentAndLeavesNothingRunning()
    {
        var solver = new StandInToolSolver(hang: true);

        // Bounded by the test's own timeout, never a stopwatch (CLAUDE.md): the stand-in sleeps 120 s, so a probe that did not stop it
        // fails at 60. A budget of the probe's bound plus 10 read 15.5 on a CI runner whose cores a parallel test held.
        var supported = await solver.CheckSupportAsync(TestContext.Current.CancellationToken);

        supported.ShouldBeFalse();
        await ShouldAllExitAsync(solver);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACancelledSolveKillsTheToolAndRethrows()
    {
        var solver = new StandInToolSolver(hang: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(StandInToolSolver.Timeout);
        var fits = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");

        await Should.ThrowAsync<OperationCanceledException>(
            () => solver.SolveFileAsync(fits, cancellationToken: cts.Token));

        await ShouldAllExitAsync(solver);
    }

    [Fact(Timeout = 60_000)]
    public async Task AToolThatAnswersIsStillFound()
    {
        var solver = new StandInToolSolver(hang: false);

        (await solver.CheckSupportAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>
    /// The tool and the process it started are both gone. The wrapper prints the inner process's id first,
    /// and the probe's bound is long enough for it to arrive, so a missing id is a failure, not a skip.
    /// </summary>
    private static async Task ShouldAllExitAsync(StandInToolSolver solver)
    {
        var outer = solver.StartedPid.ShouldNotBeNull();
        var inner = solver.InnerPid.ShouldNotBeNull();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((IsRunning(outer) || IsRunning(inner)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        IsRunning(outer).ShouldBeFalse("the tool's own process is still running");
        IsRunning(inner).ShouldBeFalse("the process the tool started is still running");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A solver whose "tool" is a shell that starts a second process and prints its id: one that never
    /// exits (<paramref name="hang"/>) or one that exits 0 at once.
    /// </summary>
    private sealed class StandInToolSolver(bool hang) : ExternalProcessPlateSolverBase
    {
        // Long enough for the hanging stand-in to print its inner process's id before the probe or the cancel kills it: pwsh's cold
        // start alone passed 5 s under a loaded full suite, and a 5 s bound then killed the tool before the id arrived, failing
        // ShouldAllExitAsync on a null InnerPid (2026-10-03). Still well inside the tests' 60 s, so a tool left running fails them.
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        public int? StartedPid { get; private set; }

        public int? InnerPid { get; private set; }

        public override string Name => "stand-in tool";

        public override float Priority => 0f;

        protected override PlatformID CommandPlatform => Environment.OSVersion.Platform;

        protected override string? CommandFolder => null;

        protected override string CommandFile => "stand-in";

        // The shorter bound is for the tool that never exits. One that answers must not race it: pwsh's cold start alone took more
        // than 5 s under a loaded full suite (2026-09-30) and read an answering tool as absent, so it gets a bound well inside
        // the test's own timeout.
        protected override TimeSpan ProbeTimeout => hang ? Timeout : TimeSpan.FromSeconds(45);

        protected override string FormatImageDimenstions(ImageDim? imageDim, float range) => "";

        protected override string FormatSearchPosition(WCS? searchOrigin, double? searchRadius) => "";

        protected override string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt) => "";

        protected override Process? StartRedirectedProcess(string proc, string arguments, PlatformID? executionPlatform = default)
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("pwsh")
                {
                    ArgumentList =
                    {
                        "-NoProfile", "-Command",
                        hang
                            ? "$c = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -NoNewWindow -PassThru; [Console]::Out.WriteLine($c.Id); [Console]::Out.Flush(); $c.WaitForExit()"
                            : "$c = Start-Process cmd -ArgumentList '/c','exit 0' -NoNewWindow -PassThru; [Console]::Out.WriteLine($c.Id); $c.WaitForExit(); exit 0",
                    },
                }
                : new ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { "-c", hang ? "sleep 120 & echo $!; wait" : "true & echo $!; wait; exit 0" },
                };
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var process = Process.Start(startInfo);
            if (process is not null)
            {
                StartedPid = process.Id;
                // Subscribed before the base calls BeginOutputReadLine, so the first line is not missed.
                process.OutputDataReceived += (_, e) =>
                {
                    if (InnerPid is null && int.TryParse(e.Data?.Trim(), out var pid))
                    {
                        InnerPid = pid;
                    }
                };
            }
            return process;
        }
    }
}
