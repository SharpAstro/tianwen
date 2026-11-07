using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
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
/// A test asking twice for the same sub name gets the same folder (a later step of one test reads what an earlier one
/// wrote); two tests asking for the same name get folders of their own, so one's end never deletes the other's live files.
/// Deletion is best effort as in <see cref="TempFolders"/>; what it misses the run-start sweep takes after a day.
/// Every test process (unit, functional, GPU) registers <see cref="TestOutputCleanupAttribute"/> and <see cref="TestOutputRun"/>.
/// </remarks>
internal static class TestOutput
{
    internal const string KeepVariable = "TIANWEN_KEEP_TEST_OUTPUT";

    /// <summary>The run's own owner key, for a folder made with no test running.</summary>
    internal const string RunOwner = "";

    private static readonly ConcurrentDictionary<(string Owner, string Sub), string> Folders = new();

    private static int _swept;

    internal static bool KeepRequested => Environment.GetEnvironmentVariable(KeepVariable) == "1";

    /// <summary>The root every dated folder lives under: <c>%TEMP%/TianWen.Lib.Tests</c>.</summary>
    internal static string DefaultRoot { get; } = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests");

    /// <summary>A folder for <paramref name="sub"/>, owned by the test now running (the run when none is).</summary>
    internal static string Create(string sub)
    {
        SweepOnce();
        return Create(DefaultRoot, DateTimeOffset.Now.Date, TestContext.Current.Test?.UniqueID ?? RunOwner, sub);
    }

    /// <summary>The run-start sweep, once per process: the fixture runs it first, a folder asked for early runs it otherwise.</summary>
    internal static void SweepOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref _swept, 1) == 0)
        {
            SweepStale(DefaultRoot, DateTimeOffset.Now);
        }
    }

    internal static string Create(string root, DateTime today, string owner, string sub)
    {
        var folder = Folders.GetOrAdd((owner, sub), _ =>
            Path.Combine(root, today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), sub + "_" + TempFolders.RandomName()));
        return Directory.CreateDirectory(folder).FullName;
    }

    /// <summary>Deletes every folder <paramref name="owner"/> made, unless a human asked to keep them.</summary>
    internal static void Release(string owner) => Release(owner, KeepRequested);

    internal static void Release(string owner, bool keep)
    {
        if (keep)
        {
            return;
        }

        List<(string Owner, string Sub)> mine = [];
        foreach (var key in Folders.Keys)
        {
            if (key.Owner == owner)
            {
                mine.Add(key);
            }
        }

        foreach (var key in mine)
        {
            if (Folders.TryRemove(key, out var folder))
            {
                TryDelete(folder);
            }
        }
    }

    /// <summary>Deletes the run's own folders and any a test forgot to end.</summary>
    internal static void ReleaseAll()
    {
        if (KeepRequested)
        {
            return;
        }

        foreach (var key in Folders.Keys)
        {
            if (Folders.TryRemove(key, out var folder))
            {
                TryDelete(folder);
            }
        }
    }

    /// <summary>
    /// Deletes each dated folder under <paramref name="root"/> whose NAME is a date before yesterday AND whose last write is
    /// over a day old, so another run in flight (or one past midnight) is never touched.
    /// </summary>
    internal static void SweepStale(string root, DateTimeOffset now)
    {
        if (KeepRequested || !Directory.Exists(root))
        {
            return;
        }

        var oldestKept = now.Date.AddDays(-1);
        var lastWriteCutoff = now.UtcDateTime - TempFolders.StaleAfter;
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (DateTime.TryParseExact(folder.Name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < oldestKept
                && folder.LastWriteTimeUtc < lastWriteCutoff)
            {
                TryDelete(folder.FullName);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            // The date folder above it goes once empty; a non-empty one (another test's) is left alone.
            if (Path.GetDirectoryName(folder) is { } dateFolder && Directory.Exists(dateFolder)
                && !Directory.EnumerateFileSystemEntries(dateFolder).GetEnumerator().MoveNext())
            {
                Directory.Delete(dateFolder, recursive: false);
            }
        }
        catch (IOException)
        {
            // Held for a moment by a process the test started; left for the sweep, never failing a passed test.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only file in it; the same.
        }
    }
}

/// <summary>Ends a test's dated output folders when the test ends, pass or fail. Registered once per test assembly.</summary>
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
