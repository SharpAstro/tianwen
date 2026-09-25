using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
/// <para>A sharing violation lasts as long as one read or one rename takes, so both sides retry it for a bounded
/// time instead of failing.</para>
/// <para>A write replaces the whole file, so a write loses whatever another process wrote since this one read it.
/// A file every writer ADDS to (the comet apparition cache) goes through <see cref="UpdateAsync"/>, which holds the
/// file's lock across the read, the merge and the write.</para>
/// </remarks>
public static partial class SharedFile
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int MaxAttempts = 40;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Replaces <paramref name="path"/> with what <paramref name="write"/> produces, all at once: a reader sees the
    /// old file or the new one, never a part, and a crash mid-write leaves the old one.
    /// </summary>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream, cancellationToken);
            }

            await RetryAsync(() => Replace(staging, path), cancellationToken);
        }
        finally
        {
            // Gone after a successful move; left behind by a failed write or move, which nothing else would clean.
            File.Delete(staging);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> to read without refusing a writer's replace of it.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static Task<FileStream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => RetryAsync(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), cancellationToken);

    /// <summary>
    /// Holds <paramref name="path"/>'s lock, across processes, until the returned handle is disposed: take it
    /// around a read, a merge and a write so no other process writes in between.
    /// </summary>
    public static async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // The lock file stays: deleting it on release lets a third process lock a file the second one still has
        // open on another platform's semantics. It is empty and one per shared file.
        return await RetryAsync(() => new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), cancellationToken);
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
    // the one IOException an open raises there for another process is the advisory lock FileShare.None takes.
    private static bool IsTransient(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => false,
        UnauthorizedAccessException => OperatingSystem.IsWindows(),
        IOException io when OperatingSystem.IsWindows() => (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation,
        IOException => true,
        _ => false
    };
}
