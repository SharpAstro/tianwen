using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace TianWen.Lib.Tests;

/// <summary>
/// A test's temporary folders: each made on request under the tests' common root, and all of them deleted, with
/// everything in them, when this is disposed. Own one per test (a field of a test class that is
/// <see cref="IDisposable"/>, which xUnit disposes after each test, or a <c>using</c> in the test) or per harness that
/// outlives a test's body (<c>NodeHarness</c>, <c>KeptNode</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists (#1197):</b> <see cref="Directory.CreateTempSubdirectory(string?)"/> makes a folder at the top of
/// the system temp directory and nothing ever removed it, and tests made one per test, per node, per socket. A
/// developer machine had 1,253 such folders scattered through its temp directory, a few hundred more after every full
/// run.
/// </para>
/// <para>
/// <b>One common root</b>, <see cref="Root"/>, beside the dated test-output folders under the same
/// <c>TianWen.Lib.Tests</c> folder: whatever a deletion misses is in one known place rather than scattered, and the
/// first <see cref="TempFolders"/> in a test process sweeps the root of folders older than <see cref="StaleAfter"/>,
/// which no test run lasts, so a miss cleans itself up. The root's name is one letter on purpose: a node's socket
/// lives in one of these folders, and a Unix-domain socket path must stay under about 104 characters.
/// </para>
/// <para>
/// <b>Deletion is best effort</b>: a process the test started may still hold a file for a moment (a spawned node's
/// lock, its socket), and a cleanup that throws would fail a test that passed. What cannot be deleted now is left for
/// the sweep, never retried with a wait.
/// </para>
/// <para>
/// Not for the dated test-output folders (<c>SharedTestData.CreateTempTestOutputDir</c>), which are kept on purpose for
/// a human to look at and trimmed by the <c>test-output-prune</c> skill.
/// </para>
/// </remarks>
public sealed class TempFolders : IDisposable
{
    /// <summary>Where every test's temporary folders are made: <c>%TEMP%/TianWen.Lib.Tests/t</c>.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", "t");

    /// <summary>How old a folder under <see cref="Root"/> must be before the sweep takes it: longer than any run.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private static int _swept;

    private readonly ConcurrentQueue<DirectoryInfo> _folders = new();

    /// <summary>A new, empty folder under <see cref="Root"/>, named <paramref name="prefix"/> plus random characters.</summary>
    public DirectoryInfo Create(string prefix)
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            SweepStale();
        }

        Directory.CreateDirectory(Root);
        DirectoryInfo folder;
        do
        {
            folder = new DirectoryInfo(Path.Combine(Root, prefix + Path.GetRandomFileName()));
        }
        while (folder.Exists);

        folder.Create();
        _folders.Enqueue(folder);
        return folder;
    }

    public void Dispose()
    {
        while (_folders.TryDequeue(out var folder))
        {
            TryDelete(folder);
        }
    }

    /// <summary>Deletes what earlier runs left under <see cref="Root"/>, once per test process.</summary>
    private static void SweepStale()
    {
        var root = new DirectoryInfo(Root);
        if (!root.Exists)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var folder in root.EnumerateDirectories())
        {
            if (folder.LastWriteTimeUtc < cutoff)
            {
                TryDelete(folder);
            }
        }
    }

    private static void TryDelete(DirectoryInfo folder)
    {
        try
        {
            if (Directory.Exists(folder.FullName))
            {
                Directory.Delete(folder.FullName, recursive: true);
            }
        }
        catch (IOException)
        {
            // Still held by a process the test started; left for the sweep rather than failing a passed test.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only file in it; the same.
        }
    }
}
