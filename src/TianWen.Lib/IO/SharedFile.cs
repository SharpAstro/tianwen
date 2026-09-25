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
/// <para>A replace on NTFS leaves the name ABSENT for a moment, with either rename: 0.1 to 0.3 ms at rest,
/// about one query in 25,000 beside two writers, and longer under load (a look 5 ms later still missed, once in
/// 60 runs of 2,000 writes). A <see cref="File.Exists"/> or a listing inside that moment calls a file that
/// exists gone, and a reader that believes it acts on it: the profile list drops a profile, a planner that
/// loaded no pins saves that back. So on Windows absence is believed only after looks spread over about 130 ms
/// (<see cref="TryOpenReadAsync"/>, and <see cref="ListAsync"/> for a file the last listing had), a cost only an
/// absent file pays.</para>
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
    private static readonly TimeSpan[] AbsenceRelooks = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100)];

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
    /// only after looking again (see remarks).
    /// </summary>
    /// <returns>Null when the file does not exist.</returns>
    public static async Task<FileStream?> TryOpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        for (var look = 0; ; look++)
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

            if (look == AbsenceRelooks.Length || !OperatingSystem.IsWindows())
            {
                return null;
            }

            await SystemTimeProvider.Instance.SleepAsync(AbsenceRelooks[look], cancellationToken);
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
    {
        var seen = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        for (var look = 0; ; look++)
        {
            if (folder.Exists)
            {
                foreach (var file in folder.EnumerateFiles("*" + suffix))
                {
                    seen.TryAdd(file.FullName, file);
                }
            }

            if (expected is null || look == AbsenceRelooks.Length || !OperatingSystem.IsWindows() || AllSeen())
            {
                return [.. seen.Values];
            }

            await SystemTimeProvider.Instance.SleepAsync(AbsenceRelooks[look], cancellationToken);
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
