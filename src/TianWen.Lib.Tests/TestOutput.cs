using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace TianWen.Lib.Tests;

/// <summary>
/// The dated test-output folders (<c>%TEMP%/TianWen.Lib.Tests/&lt;yyyyMMdd&gt;/&lt;sub&gt;_&lt;random&gt;</c>) and who owns them (#2035):
/// a folder made while a test runs lives until that test ends, one made with no test running (a class or assembly fixture)
/// until the run ends, and <c>TIANWEN_KEEP_TEST_OUTPUT=1</c> keeps all of them for a human to look at.
/// </summary>
/// <remarks>
/// The registry itself is <see cref="TestOutputFolders"/>; this is the process's one instance of it, plus the environment
/// (the keep switch, the root under <c>%TEMP%</c>) and the run-start sweep. Every test process (unit, functional, GPU)
/// registers <see cref="TestOutputCleanupAttribute"/> and <see cref="TestOutputRun"/>.
/// </remarks>
internal static class TestOutput
{
    internal const string KeepVariable = "TIANWEN_KEEP_TEST_OUTPUT";

    /// <summary>The run's own owner key, for a folder made with no test running.</summary>
    internal const string RunOwner = "";

    private static readonly TestOutputFolders Shared = new();

    private static int _swept;

    internal static bool KeepRequested => Environment.GetEnvironmentVariable(KeepVariable) == "1";

    /// <summary>The root every dated folder lives under: <c>%TEMP%/TianWen.Lib.Tests</c>.</summary>
    internal static string DefaultRoot { get; } = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests");

    /// <summary>A folder for <paramref name="sub"/>, owned by the test now running (the run when none is).</summary>
    internal static string Create(string sub)
    {
        SweepOnce();
        return Shared.Create(DefaultRoot, DateTimeOffset.Now.Date, TestContext.Current.Test?.UniqueID ?? RunOwner, sub);
    }

    /// <summary>A folder for <paramref name="sub"/> owned by the run, which lives until the process's last test has ended.</summary>
    internal static string CreateForRun(string sub)
    {
        SweepOnce();
        return Shared.Create(DefaultRoot, DateTimeOffset.Now.Date, RunOwner, sub);
    }

    /// <summary>The run-start sweep, once per process: the fixture runs it first, a folder asked for early runs it otherwise.</summary>
    internal static void SweepOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref _swept, 1) == 0)
        {
            SweepStale(DefaultRoot, DateTimeOffset.Now);
        }
    }

    /// <summary>Deletes every folder <paramref name="owner"/> made, unless a human asked to keep them.</summary>
    internal static void Release(string owner) => Shared.Release(owner, KeepRequested);

    /// <summary>Deletes the run's own folders, retries any a test's end could not delete, and removes the empty date folders.</summary>
    internal static void ReleaseAll() => Shared.ReleaseAll(KeepRequested);

    /// <summary>
    /// Deletes each dated folder under <paramref name="root"/> whose NAME is a date before yesterday. The name is the day
    /// <see cref="TestOutputFolders.Create"/> made the folder, so a run in flight, or one that crossed midnight, is never touched,
    /// and nothing else decides it: a directory's last-write time moves only when an entry directly in it is added or removed
    /// (never for a file written deeper down, which is all a running test does to its date folder), and a copy or a restore sets it
    /// as it likes, so it says nothing about a run still going that the name does not.
    /// </summary>
    internal static void SweepStale(string root, DateTimeOffset now)
    {
        if (KeepRequested || !Directory.Exists(root))
        {
            return;
        }

        var oldestKept = now.Date.AddDays(-1);
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (DateTime.TryParseExact(folder.Name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < oldestKept)
            {
                TestOutputFolders.TryDelete(folder.FullName, TestOutputFolders.DeleteTree);
            }
        }
    }
}

/// <summary>
/// Which dated output folders exist and whose they are: made on request, deleted when their owner (a test, or the run) ends.
/// </summary>
/// <remarks>
/// <para>
/// A test asking twice for the same sub name gets the same folder (a later step of one test reads what an earlier one
/// wrote); two tests asking for the same name get folders of their own, so one's end never deletes the other's live files.
/// </para>
/// <para>
/// Deletion is best effort as in <see cref="TempFolders"/>: a folder a test's end cannot delete (a process the test started
/// still holds a file) is remembered, tried once more when the run ends, and left for the next run's sweep after that.
/// </para>
/// <para>
/// <b>The date folder is removed only when the run ends</b>, never as a test's last folder in it goes: a sibling test creating
/// its folder is two steps (the date folder, then its own below it), and a delete between them failed that test's
/// <c>CreateDirectory</c>. At the run's end no test of this process is left to create one. A second test PROCESS sharing the
/// root could still do so at the very instant this one ends (the suites are meant to run one after another, as CI does);
/// the CI step that fails on what a green run left behind is why the empty date folder is removed at all.
/// </para>
/// <para>
/// An instance, not a static, so a test of the registry owns its own and never reaches another test's live folders.
/// </para>
/// </remarks>
internal sealed class TestOutputFolders(Action<string>? deleteFolder = null)
{
    // The seam a test uses to make a delete fail; production deletes the tree.
    private readonly Action<string> _deleteFolder = deleteFolder ?? DeleteTree;

    private readonly ConcurrentDictionary<(string Owner, string Sub), string> _folders = new();

    // Folders whose delete at an owner's end failed; the set is a dictionary because ConcurrentDictionary is the lock-free one.
    private readonly ConcurrentDictionary<string, byte> _undeleted = new();

    private readonly ConcurrentDictionary<string, byte> _dateFolders = new();

    internal string Create(string root, DateTime today, string owner, string sub)
    {
        var folder = _folders.GetOrAdd((owner, sub), _ =>
        {
            var dateFolder = Path.Combine(root, today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            _dateFolders[dateFolder] = 0;
            return Path.Combine(dateFolder, sub + "_" + TempFolders.RandomName());
        });
        return Directory.CreateDirectory(folder).FullName;
    }

    /// <summary>Deletes every folder <paramref name="owner"/> made, unless <paramref name="keep"/>.</summary>
    internal void Release(string owner, bool keep)
    {
        if (keep)
        {
            return;
        }

        // Keys is a snapshot, so removing while walking it is fine.
        foreach (var key in _folders.Keys)
        {
            if (key.Owner == owner && _folders.TryRemove(key, out var folder))
            {
                DeleteOrRemember(folder);
            }
        }
    }

    /// <summary>The run's end: every folder still held, every earlier failed delete once more, then each empty date folder.</summary>
    internal void ReleaseAll(bool keep)
    {
        if (keep)
        {
            return;
        }

        foreach (var key in _folders.Keys)
        {
            if (_folders.TryRemove(key, out var folder))
            {
                DeleteOrRemember(folder);
            }
        }

        foreach (var folder in _undeleted.Keys)
        {
            if (TryDelete(folder, _deleteFolder))
            {
                _undeleted.TryRemove(folder, out _);
            }
        }

        foreach (var dateFolder in _dateFolders.Keys)
        {
            RemoveIfEmpty(dateFolder);
        }
    }

    internal static void DeleteTree(string folder) => Directory.Delete(folder, recursive: true);

    /// <summary>Deletes <paramref name="folder"/> through <paramref name="delete"/>; false when something still holds it.</summary>
    internal static bool TryDelete(string folder, Action<string> delete)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                delete(folder);
            }

            return true;
        }
        catch (IOException)
        {
            // Held for a moment by a process the test started; left for the run's end and then the sweep, never failing a passed test.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only file in it; the same.
            return false;
        }
    }

    private void DeleteOrRemember(string folder)
    {
        if (!TryDelete(folder, _deleteFolder))
        {
            _undeleted[folder] = 0;
        }
    }

    private static void RemoveIfEmpty(string dateFolder)
    {
        try
        {
            if (Directory.Exists(dateFolder) && !Directory.EnumerateFileSystemEntries(dateFolder).Any())
            {
                Directory.Delete(dateFolder, recursive: false);
            }
        }
        catch (IOException)
        {
            // Another process put a folder in it between the look and the delete; it stays.
        }
        catch (UnauthorizedAccessException)
        {
            // The same, on a platform that answers that way.
        }
    }
}

/// <summary>
/// Ends a test's dated output folders when the test ends, pass or fail. Registered once per test assembly.
/// </summary>
/// <remarks>
/// xunit runs <c>After</c> whether the test method returned or threw, so a failed test's folder goes too, and it runs BEFORE the
/// test class's own <c>Dispose</c> (both measured on this xunit version, 2026-11-07, with a test that wrote a folder and then
/// threw, and one that returned: the folder was already gone when <c>Dispose</c> ran in each). There is no separate case to keep
/// a failed test's output, which is what <c>TIANWEN_KEEP_TEST_OUTPUT=1</c> is for. That order is what lets
/// <c>TestOutputEndToEndTests</c> assert from its <c>Dispose</c> that the real registration, not a hand-made call, ended a folder.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestOutputCleanupAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test) => TestOutput.Release(test.UniqueID);
}

/// <summary>An assembly fixture: its disposal, after the last test, ends the run's own output folders.</summary>
public sealed class TestOutputRun : IDisposable
{
    public TestOutputRun() => TestOutput.SweepOnce();

    public void Dispose() => TestOutput.ReleaseAll();
}
