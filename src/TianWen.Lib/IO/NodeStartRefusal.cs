using System;
using System.IO;

namespace TianWen.Lib.IO;

/// <summary>
/// Why a node or its keeper would not start, left where the client that started it can read it (#1077). A client starts
/// the keeper detached, with no window and nothing to read its stderr, so a refusal printed to the console reached no one:
/// the user saw "the keeper ended with 2" and a pointer to a log that cannot exist, since the refusals come before any
/// logging does (a command line that is wrong, a socket path a Unix domain socket cannot hold, the lock held by another
/// node). The process writes its reason here as it refuses, and the client reads it once the keeper has ended.
/// <para>
/// One file in the data root, the folder the client creates and the keeper inherits, and never a log: a refusal is the
/// LAST thing a failed start says, so the client clears the file before it starts a keeper and reads it after.
/// </para>
/// </summary>
public static class NodeStartRefusal
{
    public const string FileName = "node-start-refusal.txt";

    /// <summary>
    /// Records <paramref name="reason"/> in <paramref name="dataRoot"/>. Best effort by design: a process refusing to
    /// start has its exit code to give, and failing to explain it must never replace that.
    /// </summary>
    public static void Write(string dataRoot, string reason)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var path = Path.Combine(dataRoot, FileName);
            // Staged under a name of its own, so a reader sees the whole reason or none of it.
            var staged = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(staged, reason);
            File.Move(staged, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it: the exit code stands.
        }
    }

    /// <summary>
    /// The reason the last refusal recorded, on one line, or null when there is none. Read after the keeper has ended,
    /// which is after it wrote it.
    /// </summary>
    public static string? TryRead(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllText(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? null : string.Join(" ", lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets a refusal from an earlier start, so a new one is never read as this one's.</summary>
    public static void Clear(string dataRoot)
    {
        try
        {
            File.Delete(Path.Combine(dataRoot, FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be removed is overwritten by the next refusal, and read only after one.
        }
    }
}
