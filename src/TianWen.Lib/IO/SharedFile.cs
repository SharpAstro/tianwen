using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Lib.IO;

/// <summary>
/// A small file that more than one PROCESS reads and writes: everything under AppData (profiles, the comet and
/// weather caches, the planner, session and remote-rig records). Once the GUI and the TUI are clients of a local
/// server this is routine, not a race nobody hits: the server's limit watcher alone re-reads every profile every
/// 5 s (P0c item 3 of docs/plans/hardware-in-the-server.md, #788).
/// </summary>
/// <remarks>
/// <para>A write stages its bytes under a temp name no other writer can pick, then renames it over the file. The
/// temp name used to be fixed, so two writers opened one temp file and the second failed.</para>
/// <para>The rename replaces the file even while a reader holds it, as a rename does on Unix: on Windows that takes
/// the POSIX-semantics rename, since <c>MoveFileEx</c> refuses to replace a file any handle holds open, and a
/// reader holding the old version failed the writer. A read opens with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/>, without which even that rename is refused.</para>
/// <para>A replace on NTFS leaves the name ABSENT for a moment, inside its one replace: 0.1 to 0.3 ms at rest,
/// about one query in 25,000 beside two writers, and longer under load (a look 5 ms later still missed, once in
/// 60 runs of 2,000 writes). A <see cref="File.Exists"/> or a listing inside that moment calls a file that
/// exists gone, and a reader that believes it acts on it: the profile list drops a profile, a planner that
/// loaded no pins saves that back.</para>
/// <para>Looking again at fixed times is not evidence of absence. CI run 43825096116 (test-windows, two writers
/// replacing one profile back to back, 1,254 reads beside 2,000 writes) had one read find the name absent at all
/// four looks (now, +5, +25, +100 ms) with nothing logged. That is the evidence. The mechanism is one of two
/// that it cannot tell apart: several replaces' absent windows, one per look, or ONE window longer than the
/// 130 ms the looks spanned (a writer descheduled inside its one replace). A one-off probe run in review on the
/// desktop (Release, 16 cores, two writers calling <see cref="WriteAsync"/> flat out for 20 s, 20,265 writes; its
/// script is not in the repo) saw the name absent at 2 of 266,782 looks (about 7.5e-6 per look), 0 nulls from
/// 120,253 <see cref="TryOpenReadAsync"/> calls, and a staging file visible at both absent looks. Four independent
/// misses would need a per-look rate near 0.2 on the runner, so one long window fits the CI miss at least as well
/// as several. Neither is measured on the runner. The on-demand shared-file-repeat workflow is the proof at CI scale.
/// The rule below covers both, and is about what the looks show, not how many there are or how far apart: a
/// write stages <c>name.guid.tmp</c> beside the name before it replaces it, and that file exists until the
/// replace is done. Absence is believed only when a look finds no staging file beside the name (and, on Windows,
/// a second look 5 ms later finds the name absent with no staging file again). While a staging file is there the
/// reader polls every 5 ms, up to one second, after which the staging file is taken as a crashed writer's orphan
/// and the name as gone. The look at the name and the look for a staging file are two looks, and a replace that
/// finishes between them shows in neither, so the last look before an absence is believed is always at the name.
/// A file deleted for good has no staging file, so it is answered after one 5 ms relook
/// (<see cref="TryOpenReadAsync"/>, and <see cref="ListAsync"/> for a file the last listing had).</para>
/// <para>Why not a lock between readers and writers, which would make that exact: measured, it wedges. The
/// real-time scanner holds a freshly replaced file in kernel mode (the System process, invisible to a share
/// check), and with a replace taking the directory's lock that hold stopped clearing: every rename onto the file
/// was refused for minutes, in half the runs with readers locking too and 2 in 30 with only writers locking,
/// against none in about 150 runs without the lock.</para>
/// <para>A sharing violation lasts as long as one read or one rename takes, so it is retried for a bounded time
/// instead of failing; that is what rides out another program holding a file without delete sharing.</para>
/// <para>A write replaces the whole file, so a write loses whatever another process wrote since this one read it.
/// A file every writer ADDS to (the comet apparition cache) goes through <see cref="UpdateJsonAsync"/>, which holds
/// its directory's lock (<c>.lock</c> beside the file) across the read, the merge and the write. Those writes are
/// rare, which is what keeps that lock clear of the wedge above.</para>
/// </remarks>
public static partial class SharedFile
{
    private const string LockFileName = ".lock";
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int MaxAttempts = 40;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LockWaitBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AbsenceLook = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan AbsenceDeadline = TimeSpan.FromSeconds(1);

    // Windows hides the name for a moment inside a replace, so one quiet miss may be the tail of a window that has
    // already closed; a second quiet look finds the file. Elsewhere a miss with no staging file is the answer, once
    // the name has been looked for after the staging file was (see the last look in TryOpenReadAsync).
    internal static int QuietLooksNeeded => OperatingSystem.IsWindows() ? 2 : 1;

    // A write stages '<name>.<guid>.tmp' before it replaces the name and removes nothing until the replace is
    // done, so a staging file beside the name is the evidence that a replace is under way.
    private static bool ReplaceInProgress(string directory, string stagingPattern)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }
        foreach (var _ in Directory.EnumerateFiles(directory, stagingPattern))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with what <paramref name="write"/> produces, all at once: a reader sees the
    /// old file or the new one, never a part, and a crash mid-write leaves the old one.
    /// </summary>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryOf(path);
        Directory.CreateDirectory(directory);
        var staging = StagingPath(path);
        try
        {
            await StageAsync(staging, write, cancellationToken);
            await RetryAsync(() => Replace(staging, path), cancellationToken);
        }
        finally
        {
            // Gone after a successful replace; left behind by a failed write or replace, which nothing else would clean.
            File.Delete(staging);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> to read without refusing a writer's replace of it, believing the file absent
    /// only when no replace is in progress (see remarks).
    /// </summary>
    /// <returns>Null when the file does not exist.</returns>
    public static async Task<FileStream?> TryOpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryOf(path);
        var stagingPattern = Path.GetFileName(path) + ".*.tmp";
        var started = Stopwatch.GetTimestamp();
        var quiet = 0;
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    return await RetryAsync(() => OpenShared(path), cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    // Replaced between the look and the open.
                }
            }

            if (ReplaceInProgress(directory, stagingPattern))
            {
                quiet = 0;
                if (Stopwatch.GetElapsedTime(started) >= AbsenceDeadline)
                {
                    // A staging file that never turns into a replace is an orphan of a crashed writer.
                    return null;
                }
            }
            else if (++quiet >= QuietLooksNeeded)
            {
                // The last look is at the NAME, never at the staging file. The two looks above are not one: a
                // replace that finishes between them shows neither (the name was not there yet, the staging file
                // is there no longer), and that is a file that exists. A staging file leaves only by becoming the
                // name, so with none seen the name is looked for once more, and only a miss there is an absence.
                if (!File.Exists(path))
                {
                    return null;
                }
                continue;
            }

            await SystemTimeProvider.Instance.SleepAsync(AbsenceLook, cancellationToken);
        }
    }

    /// <inheritdoc cref="TryOpenReadAsync"/>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static async Task<FileStream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => await TryOpenReadAsync(path, cancellationToken) ?? throw new FileNotFoundException($"Could not find file '{path}'.", path);

    /// <summary>
    /// The files in <paramref name="folder"/> whose names end in <paramref name="suffix"/>. A file in
    /// <paramref name="expected"/> (what the caller listed last time) that a listing lacks is only taken as gone
    /// after looking again, since a replace hides a name for a moment (see remarks).
    /// </summary>
    /// <param name="expected">Full paths the previous listing had, or null for a first listing.</param>
    public static async Task<IReadOnlyList<FileInfo>> ListAsync(DirectoryInfo folder, string suffix, IReadOnlyCollection<string>? expected,
        CancellationToken cancellationToken = default)
        => (await ListCountingLooksAsync(folder, suffix, expected, cancellationToken)).Files;

    /// <summary>
    /// <see cref="ListAsync"/>, with how many times it looked at the folder. A listing that believes an absence stops
    /// after its quiet looks; one that takes something for a replace under way looks again every few milliseconds
    /// until the deadline. The count tells the two apart with no wall-clock bar, which is what a test of the rule at
    /// the listing itself needs.
    /// </summary>
    internal static async Task<(IReadOnlyList<FileInfo> Files, int Looks)> ListCountingLooksAsync(DirectoryInfo folder, string suffix,
        IReadOnlyCollection<string>? expected, CancellationToken cancellationToken = default)
    {
        var seen = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var started = Stopwatch.GetTimestamp();
        var quiet = 0;
        var lastLook = false;
        var looks = 0;
        while (true)
        {
            looks++;
            if (folder.Exists)
            {
                foreach (var file in folder.EnumerateFiles("*" + suffix))
                {
                    seen.TryAdd(file.FullName, file);
                }
            }

            if (expected is null || AllSeen() || lastLook)
            {
                return ([.. seen.Values], looks);
            }

            if (AnyBeingReplaced(folder.FullName, Missing(expected, seen)))
            {
                quiet = 0;
                if (Stopwatch.GetElapsedTime(started) >= AbsenceDeadline)
                {
                    return ([.. seen.Values], looks);
                }
            }
            else if (++quiet >= QuietLooksNeeded)
            {
                // The last look is at the names, as in TryOpenReadAsync: a replace that finished between the
                // listing above and the look for its staging file shows in neither.
                lastLook = true;
                continue;
            }

            await SystemTimeProvider.Instance.SleepAsync(AbsenceLook, cancellationToken);
        }

        bool AllSeen()
        {
            foreach (var path in expected)
            {
                if (!seen.ContainsKey(path))
                {
                    return false;
                }
            }
            return true;
        }

        static IEnumerable<string> Missing(IReadOnlyCollection<string> all, Dictionary<string, FileInfo> found)
        {
            foreach (var path in all)
            {
                if (!found.ContainsKey(path))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// Whether any of <paramref name="missing"/> has a staging file beside it in <paramref name="folder"/>, which is
    /// a replace of THAT name under way. Asked per name, as <see cref="TryOpenReadAsync"/> asks: a listing used to
    /// look for any <c>*.tmp</c> in the folder, so a crashed writer's orphan for ANOTHER file read as a replace in
    /// progress and the first listing after an expected file was deleted waited out the whole deadline, a second,
    /// before believing it (#2186).
    /// </summary>
    internal static bool AnyBeingReplaced(string folder, IEnumerable<string> missing)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        // One pass over the folder's staging files, each asked whose it is: a look per missing name was a scan of the
        // folder per name per look, for a listing that had lost many. The names are only gathered once a staging
        // file is met, which is rare.
        HashSet<string>? names = null;
        foreach (var staging in Directory.EnumerateFiles(folder, "*.tmp"))
        {
            if (names is null)
            {
                names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var path in missing)
                {
                    names.Add(Path.GetFileName(path));
                }
            }
            if (names.Count == 0)
            {
                return false;
            }

            // What the per-name look matches is '<name>.*.tmp', so a staging file is a name's when the name is all of
            // it before one of its dots and at least '.tmp' follows that dot.
            var file = Path.GetFileName(staging);
            for (var dot = file.IndexOf('.'); dot >= 0 && dot <= file.Length - 5; dot = file.IndexOf('.', dot + 1))
            {
                if (dot > 0 && names.Contains(file[..dot]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a JSON file, lets <paramref name="update"/> derive the next version from it (null when there is none,
    /// or it cannot be parsed), and writes that, holding the directory's lock across all three so no other process
    /// writes in between. For a file every writer ADDS to; a document one writer owns whole is a
    /// <see cref="WriteAsync"/>.
    /// </summary>
    public static async Task UpdateJsonAsync<T>(string path, JsonTypeInfo<T> jsonTypeInfo, Func<T?, T> update, ILogger? logger = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var directory = DirectoryOf(path);
        Directory.CreateDirectory(directory);
        var staging = StagingPath(path);
        try
        {
            using var updating = await LockAsync(directory, exclusive: true, cancellationToken);
            T? current = null;
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = await RetryAsync(() => OpenShared(path), cancellationToken);
                    current = await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken);
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex, "Replacing unreadable JSON in {FilePath}", path);
                }
            }

            var next = update(current);
            await StageAsync(staging, (stream, token) => JsonSerializer.SerializeAsync(stream, next, jsonTypeInfo, token), cancellationToken);
            await RetryAsync(() => Replace(staging, path), cancellationToken);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static string DirectoryOf(string path)
        => Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException($"'{path}' names no directory", nameof(path));

    private static string StagingPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static async Task StageAsync(string staging, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await write(stream, cancellationToken);
    }

    private static FileStream OpenShared(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // A readers-writer lock across processes, made of the sharing modes (and of flock on Unix, which .NET takes
    // shared or exclusive to match): readers open the lock file sharing read with each other, a writer opens it
    // sharing nothing. The lock file stays, since deleting it on release would let a third process lock a new file
    // while the second still holds the old one.
    private static async Task<IDisposable> LockAsync(string directory, bool exclusive, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(directory, LockFileName);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return exclusive
                    ? new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (IsTransient(ex) && Stopwatch.GetElapsedTime(started) < LockWaitBudget)
            {
                // Not the transient retry: that backs off, and a holder that updates again takes the lock straight
                // back, so a waiter backing off to 100 ms missed every gap and gave up (measured: two writers in a
                // loop). A holder keeps it for one look, one open or one replace, so poll often, at a jittered
                // interval so two waiters do not fall into step, for as long as another process could plausibly take.
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 9)), cancellationToken);
            }
        }
    }

    private static void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows() || !TryReplaceWithPosixRename(source, destination))
        {
            File.Move(source, destination, overwrite: true);
        }
    }

    private static async Task RetryAsync(Action attempt, CancellationToken cancellationToken)
        => await RetryAsync(() => { attempt(); return true; }, cancellationToken);

    private static async Task<T> RetryAsync<T>(Func<T> attempt, CancellationToken cancellationToken)
    {
        var delay = FirstRetryDelay;
        for (var tries = 1; ; tries++)
        {
            try
            {
                return attempt();
            }
            catch (Exception ex) when (tries < MaxAttempts && IsTransient(ex))
            {
                // A real wait on the real clock: another PROCESS holds the file, and no fake clock advances that.
                await SystemTimeProvider.Instance.SleepAsync(delay, cancellationToken);
                delay = delay * 2 < MaxRetryDelay ? delay * 2 : MaxRetryDelay;
            }
        }
    }

    // Windows reports another handle's sharing as a sharing or lock violation, and a replace over a file open
    // without delete sharing (or one whose delete is pending) as access denied. Unix has no mandatory sharing, so
    // the one IOException an open raises there for another process is the advisory lock a lock file takes.
    private static bool IsTransient(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => false,
        UnauthorizedAccessException => OperatingSystem.IsWindows(),
        IOException io when OperatingSystem.IsWindows() => (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation,
        IOException => true,
        _ => false
    };
}
