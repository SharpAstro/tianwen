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
/// <b>One common root</b>, <see cref="Root"/>, beside the dated test-output folders and the run's own temp folder (<see cref="TestRunTemp"/>,
/// where a bare <see cref="Path.GetTempPath"/> lands, #2083) under the same <c>TianWen.Lib.Tests</c> folder, in the temp folder the
/// process STARTED with: whatever a deletion misses is in one known place rather than scattered, and the
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
/// Not for the dated test-output folders (<c>SharedTestData.CreateTempTestOutputDir</c>): those live as long as their test
/// (<see cref="TestOutput"/>) and <c>TIANWEN_KEEP_TEST_OUTPUT=1</c> keeps them for a human to look at.
/// </para>
/// </remarks>
public sealed class TempFolders : IDisposable
{
    /// <summary>
    /// Where every test's temporary folders are made: <c>&lt;original temp&gt;/TianWen.Lib.Tests/t</c>. The ORIGINAL temp folder, never
    /// <see cref="Path.GetTempPath"/> as it reads after the run's redirect (<see cref="TestRunTemp"/>), which would put this inside the run's
    /// folder, a few characters longer for a node's socket path and gone when the run ends.
    /// </summary>
    public static string Root { get; } = Path.Combine(TestRunTemp.Root, "t");

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
            folder = new DirectoryInfo(Path.Combine(Root, prefix + RandomName()));
        }
        while (folder.Exists);

        folder.Create();
        _folders.Enqueue(folder);
        return folder;
    }

    /// <summary>
    /// Twelve random lowercase letters, as long as <see cref="Path.GetRandomFileName"/>'s name so a socket's path stays short, but with no
    /// digit (#1626): a planetary capture's path is read for its telescope folder by folder outward
    /// (<c>PlanetaryCaptureName.TelescopeOfPath</c>), and a random name of a-z0-5 holding <c>4in</c> or <c>102mm</c> named one, which
    /// failed a test that expected none now and then.
    /// </summary>
    internal static string RandomName()
    {
        Span<char> name = stackalloc char[12];
        for (var i = 0; i < name.Length; i++)
        {
            name[i] = (char)('a' + Random.Shared.Next(26));
        }
        return new string(name);
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
