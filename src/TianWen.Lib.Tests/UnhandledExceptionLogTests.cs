using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #1811: what a TianWen program writes down about an exception nobody handled, and that every program is hooked
/// to write it.
/// </summary>
public class UnhandledExceptionLogTests
{
    [Fact]
    public void AnUnhandledExceptionIsWrittenWithItsExceptionAndWhetherTheProcessIsEnding()
    {
        var written = new System.Collections.Generic.List<(string Text, Exception? Exception)>();
        var boom = new InvalidOperationException("boom");

        UnhandledExceptions.OnCrash((text, exception) => written.Add((text, exception)), boom, terminating: true);
        UnhandledExceptions.OnCrash((text, exception) => written.Add((text, exception)), boom, terminating: false);
        // Not everything thrown is an Exception: code that is not C# can throw anything.
        UnhandledExceptions.OnCrash((text, exception) => written.Add((text, exception)), "a thrown string", terminating: true);

        written.ShouldBe(
        [
            ("Unhandled exception, the process is ending", boom),
            ("Unhandled exception", boom),
            ("Unhandled exception, the process is ending (a thrown string)", null),
        ]);
    }

    [Fact]
    public void ATasksOneExceptionIsWrittenAsItselfAndSeveralAsTheirAggregate()
    {
        var one = new InvalidOperationException("one");
        UnhandledExceptions.Unwrapped(new AggregateException(one)).ShouldBeSameAs(one);
        // Nested, as a task that awaited a task leaves it.
        UnhandledExceptions.Unwrapped(new AggregateException(new AggregateException(one))).ShouldBeSameAs(one);

        var several = UnhandledExceptions.Unwrapped(new AggregateException(one, new AggregateException(new FormatException("two"))));
        several.ShouldBeOfType<AggregateException>().InnerExceptions.Select(static e => e.Message).ShouldBe(["one", "two"]);
    }

    // A handler runs while the process is already going down for another reason: a writer that fails must not
    // replace the exception being reported with its own.
    [Fact]
    public void AWriterThatFailsDoesNotThrowOutOfTheHandler()
    {
        Should.NotThrow(() => UnhandledExceptions.OnCrash((_, _) => throw new IOException("the disk is full"), new InvalidOperationException("boom"), terminating: true));
        Should.NotThrow(() => UnhandledExceptions.OnUnobserved((_, _) => throw new IOException("the disk is full"), new AggregateException(new InvalidOperationException("boom"))));
    }

    // The hook itself, end to end for the kind that does not need a dying process: a task that faulted and that
    // nobody awaited is reported when it is collected, through the logger, as an error with its exception.
    [Fact]
    public void ATaskThatFaultedWithNobodyAwaitingItIsLoggedOnceItIsCollectedAndNoLongerOnceTheHookIsOff()
    {
        var log = new RecordingLogger<UnhandledExceptionLogTests>();
        var first = "unobserved " + Guid.NewGuid().ToString("N");
        using (UnhandledExceptionLog.Hook(log))
        {
            LeaveAFaultedTaskBehind(first);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var said = log.Drain();
            said.ShouldContain("A task ended with an exception that nobody awaited");
            said.ShouldContain(first);
        }

        var second = "unobserved " + Guid.NewGuid().ToString("N");
        LeaveAFaultedTaskBehind(second);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        log.Drain().ShouldNotContain(second);
    }

    // In a method of its own, never inlined, so that no local of the test keeps the task alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeaveAFaultedTaskBehind(string message) => _ = Task.FromException(new InvalidOperationException(message));

    /// <summary>
    /// Every program hooks its unhandled exceptions. A program is a <c>Program.cs</c> beside a project that builds an
    /// executable and is not a test, a benchmark or a one-off: a new one fails here until it is hooked, or is listed
    /// as one that need not be, with the reason.
    /// </summary>
    [Fact]
    public void EveryProgramLogsItsUnhandledExceptions()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        string[] notPrograms =
        [
            "ShaderTest",                   // a developer's one-off for a shader
            "TianWen.UI.Benchmarks",        // BenchmarkDotNet's own host
        ];
        // A project sits in a folder of its own directly under src: no walk into anyone's bin or obj.
        var programs = Directory.EnumerateDirectories(root).SelectMany(static folder => Directory.EnumerateFiles(folder, "*.csproj"))
            .Where(static project => File.ReadAllText(project).Contains("<OutputType>Exe", StringComparison.Ordinal)
                || File.ReadAllText(project).Contains("<OutputType>WinExe", StringComparison.Ordinal))
            .Select(static project => (Name: Path.GetFileNameWithoutExtension(project), Program: Path.Combine(Path.GetDirectoryName(project) ?? "", "Program.cs")))
            .Where(p => File.Exists(p.Program) && !p.Name.Contains(".Tests", StringComparison.Ordinal) && !p.Name.EndsWith(".E2E", StringComparison.Ordinal) && !notPrograms.Contains(p.Name))
            .OrderBy(static p => p.Name, StringComparer.Ordinal)
            .ToArray();

        programs.Select(static p => p.Name).ShouldBe(
            ["TianWen.AI.MCP", "TianWen.AscomHost", "TianWen.Cli", "TianWen.Server", "TianWen.UI.FitsViewer", "TianWen.UI.Gui"],
            "a new program hooks its unhandled exceptions and is listed here; one that need not goes into notPrograms with its reason");
        foreach (var (name, program) in programs)
        {
            File.ReadAllText(program).ShouldContain(".HookForTheProcess(", customMessage: $"{name}'s Program.cs hooks its unhandled exceptions (#1811)");
        }
    }
}
