using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TianWen.Lib.Tests;

/// <summary>
/// A test process's own temp folder (#2083): TMP, TEMP and TMPDIR point at <c>&lt;original temp&gt;/TianWen.Lib.Tests/r&lt;8 hex&gt;</c> for the
/// life of the run, so everything a test writes through <see cref="Path.GetTempPath"/> (87 files did, with a dozen prefixes of their own),
/// and everything a process a test starts writes, lands in one folder that is deleted when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>It needs no change in a test</b>: <see cref="Path.GetTempPath"/> reads the variables on every call and a child process inherits
/// them. One narrow Defender exclusion, <c>%TEMP%\TianWen.Lib.Tests</c>, then covers all of a run's writes instead of all of %TEMP%.
/// </para>
/// <para>
/// <b>The roots <see cref="TempFolders"/> and <see cref="TestOutput"/> keep come from the ORIGINAL temp folder</b> (<see cref="Root"/>), never
/// from <see cref="Path.GetTempPath"/> after the redirect: kept output (<c>TIANWEN_KEEP_TEST_OUTPUT=1</c>) must survive the run folder's
/// delete, and a node's socket lives under <see cref="TempFolders.Root"/>, where a Unix-domain socket path must stay under about 104
/// characters (a macOS TMPDIR is long already, so the run folder is not put in front of it). The run folder's name is short for the same reason.
/// </para>
/// <para>
/// <b>A live run holds <see cref="LockName"/> open with no sharing</b> in its folder; the next run's start deletes every run folder whose lock
/// it can open (a run that was killed) and keeps the rest. A test host a test starts finds <see cref="RunVariable"/> and reuses the outer
/// run's folder: it makes none, sweeps none and deletes none.
/// </para>
/// <para>
/// Each test assembly (unit, functional, GPU, simulators) runs <see cref="Redirect"/> from a module initializer (<c>TestRunTempInit.cs</c>,
/// linked into the three that are not this one) and ends the run from <see cref="TestOutputRun"/>, so the pure pieces below are tested on
/// folders the test owns and not on the process's own.
/// </para>
/// </remarks>
internal static class TestRunTemp
{
    /// <summary>The run's folder, set for the processes the tests start: a test host started by a test reuses it and does not own it.</summary>
    internal const string RunVariable = "TIANWEN_TEST_RUN";

    /// <summary>The temp folder the run started with, set for the same processes so their roots agree with this one's.</summary>
    internal const string OriginalTempVariable = "TIANWEN_TEST_ORIGINAL_TEMP";

    /// <summary>The file a live run holds open, with no sharing, in its folder.</summary>
    internal const string LockName = ".run.lock";

    /// <summary>The folder under the original temp that holds every test-owned folder: runs, <see cref="TempFolders"/> and the dated output.</summary>
    internal const string RootName = "TianWen.Lib.Tests";

    /// <summary>How long a run folder with no lock file is left alone: a run may be between making its folder and taking its lock.</summary>
    internal static readonly TimeSpan LockGrace = TimeSpan.FromHours(1);

    private static FileStream? _lock;

    /// <summary>The temp folder the run started with: what <see cref="OriginalTempVariable"/> says, else the one now in force.</summary>
    internal static string OriginalTemp => Environment.GetEnvironmentVariable(OriginalTempVariable) is { Length: > 0 } original
        ? original
        : Path.GetTempPath();

    /// <summary>The root every test-owned folder lives under: <c>&lt;original temp&gt;/TianWen.Lib.Tests</c>.</summary>
    internal static string Root => Path.Combine(OriginalTemp, RootName);

    /// <summary>This process's run folder, or empty before <see cref="Redirect"/> ran.</summary>
    internal static string Run { get; private set; } = "";

    /// <summary>True when this process made <see cref="Run"/> and deletes it at the end; false when it reuses an outer run's.</summary>
    internal static bool OwnsRun { get; private set; }

    /// <summary>Where a run is: its folder, the root it sits in, the temp folder it started from, and whether this process owns it.</summary>
    internal readonly record struct RunPlan(string OriginalTemp, string Root, string Run, bool OwnsRun);

    /// <summary>
    /// Decides the run's folder. With <see cref="RunVariable"/> naming a folder that exists, that run is reused and not owned; otherwise a
    /// new folder named <paramref name="runName"/> under <paramref name="currentTemp"/>'s <c>TianWen.Lib.Tests</c> (or under the original
    /// temp a stale <see cref="OriginalTempVariable"/> still names, when that folder exists) is the plan. Nothing is created.
    /// </summary>
    internal static RunPlan Plan(Func<string, string?> env, string currentTemp, string runName)
    {
        if (env(RunVariable) is { Length: > 0 } outer && Directory.Exists(outer))
        {
            var run = Path.TrimEndingDirectorySeparator(outer);
            var root = Path.GetDirectoryName(run) ?? run;
            var original = env(OriginalTempVariable) is { Length: > 0 } inherited
                ? inherited
                : Path.GetDirectoryName(root) ?? root;
            return new RunPlan(original, root, run, OwnsRun: false);
        }

        var temp = env(OriginalTempVariable) is { Length: > 0 } stale && Directory.Exists(stale) ? stale : currentTemp;
        var testsRoot = Path.Combine(temp, RootName);
        return new RunPlan(temp, testsRoot, Path.Combine(testsRoot, runName), OwnsRun: true);
    }

    /// <summary>The variables a plan sets for this process and, through it, for every process the tests start.</summary>
    internal static IReadOnlyDictionary<string, string> Variables(RunPlan plan) => new Dictionary<string, string>
    {
        ["TMP"] = plan.Run,
        ["TEMP"] = plan.Run,
        ["TMPDIR"] = plan.Run,
        [RunVariable] = plan.Run,
        [OriginalTempVariable] = plan.OriginalTemp,
    };

    /// <summary>A new run folder's name: <c>r</c> and eight hex digits.</summary>
    internal static string NewRunName() => "r" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>True for a name <see cref="NewRunName"/> could have made, so the sweep never touches <c>t</c> or a dated folder.</summary>
    internal static bool IsRunFolderName(string name) =>
        name.Length == 9 && name[0] == 'r' && name.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>
    /// Points the process's temp folder at a run folder, once, before any test code runs. Called from each assembly's module initializer.
    /// </summary>
    internal static void Redirect()
    {
        if (Run.Length > 0)
        {
            return;
        }

        var plan = Plan(Environment.GetEnvironmentVariable, Path.GetTempPath(), NewRunName());
        if (plan.OwnsRun)
        {
            Directory.CreateDirectory(plan.Root);
            SweepDead(plan.Root, DateTime.UtcNow);
            Directory.CreateDirectory(plan.Run);
            _lock = new FileStream(Path.Combine(plan.Run, LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }

        Run = plan.Run;
        OwnsRun = plan.OwnsRun;
        foreach (var (name, value) in Variables(plan))
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        // The assembly fixture ends the run, but only when xUnit makes it, and a run whose filter picks few or no tests of the assembly
        // (the Debug job's Category=DebugOnly) left the run folder and its lock behind on CI. The process's exit ends it too; End is a
        // no-op once it has run.
        if (plan.OwnsRun)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
        }
    }

    /// <summary>
    /// Deletes each run folder under <paramref name="root"/> that no live run holds: its lock file opens with no sharing, or it has none and
    /// was last written over <see cref="LockGrace"/> ago (a run that died before it took its lock). <paramref name="except"/> is never touched
    /// (the caller's own). Returns the folders deleted.
    /// </summary>
    internal static List<string> SweepDead(string root, DateTime utcNow, string? except = null)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(root))
        {
            return deleted;
        }

        var keep = except is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(except));
        foreach (var folder in Directory.EnumerateDirectories(root).Where(f => IsRunFolderName(Path.GetFileName(f))))
        {
            if (keep is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsDead(folder, utcNow))
            {
                continue;
            }

            if (DeleteTree(folder))
            {
                deleted.Add(folder);
            }
        }

        return deleted;
    }

    private static bool IsDead(string folder, DateTime utcNow)
    {
        var lockPath = Path.Combine(folder, LockName);
        if (!File.Exists(lockPath))
        {
            return utcNow - Directory.GetLastWriteTimeUtc(folder) >= LockGrace;
        }

        try
        {
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false; // a live run holds it
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ends the run this process owns: its folder's contents, then its lock, then the folder. A reused outer run is left alone.</summary>
    internal static void End()
    {
        if (!OwnsRun || Run.Length == 0)
        {
            return;
        }

        // The lock file is held open, so it cannot be deleted with the rest; released last, then the (now nearly empty) folder goes.
        DeleteTree(Run, keepName: LockName);
        _lock?.Dispose();
        _lock = null;
        DeleteTree(Run);
        OwnsRun = false;
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> and all under it, clearing read-only marks first (git writes its objects read-only, which defeats a
    /// plain recursive delete), leaving the file named <paramref name="keepName"/> in the folder itself when given. Best effort: what a stray
    /// process still holds stays for the next run's sweep, never retried with a wait. True when the folder is gone (or, with
    /// <paramref name="keepName"/>, holds nothing else).
    /// </summary>
    internal static bool DeleteTree(string folder, string? keepName = null)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (keepName is not null && Path.GetDirectoryName(file) == Path.TrimEndingDirectorySeparator(folder)
                    && Path.GetFileName(file) == keepName)
                {
                    continue;
                }

                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (IOException)
                {
                    // Held; the delete below reports it.
                }
                catch (UnauthorizedAccessException)
                {
                    // The same.
                }
            }

            if (keepName is null)
            {
                Directory.Delete(folder, recursive: true);
                return true;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).Where(e => Path.GetFileName(e) != keepName))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
