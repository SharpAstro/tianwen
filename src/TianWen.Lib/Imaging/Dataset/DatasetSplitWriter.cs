using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Writes the pinned train/test split for the dataset (docs/plans/ai-denoise-deconv.md §3, task
/// P0/#42). The split is <b>by session, never by tile or frame</b> — adjacent tiles of one session
/// share noise + PSF statistics, so a tile-level split leaks the test distribution into training and
/// inflates the held-out metrics. A session is assigned to the held-out TEST set iff a stable hash
/// bucket of its portable id falls under <c>testFraction</c>: the assignment depends only on the
/// session's own id, so it is identical across machines and — critically — <b>never reshuffles as the
/// archive grows</b> (adding sessions can't move an existing one between train and test, which would
/// silently invalidate every past eval number).
/// </summary>
public static class DatasetSplitWriter
{
    /// <summary>Canonical file name under the dataset output root.</summary>
    public const string TestSessionsFileName = "test-sessions.txt";

    /// <summary>Hash bucket resolution — a session's id maps to <c>[0, Resolution)</c> and is TEST
    /// when that bucket is below <c>testFraction * Resolution</c>.</summary>
    private const uint Resolution = 10000;

    /// <summary>True when <paramref name="sessionId"/> is in the held-out TEST set for the given
    /// fraction. Pure + stable: same id + fraction always yields the same answer.</summary>
    public static bool IsTestSession(string sessionId, double testFraction)
        => StableBucket(sessionId) < (uint)(Math.Clamp(testFraction, 0.0, 1.0) * Resolution);

    /// <summary>The held-out TEST session ids among <paramref name="sessionIds"/>, ordinal-sorted
    /// (canonical order, independent of input order).</summary>
    public static ImmutableArray<string> SelectTestSessions(IEnumerable<string> sessionIds, double testFraction)
    {
        var test = ImmutableArray.CreateBuilder<string>();
        foreach (var id in sessionIds)
        {
            if (IsTestSession(id, testFraction))
            {
                test.Add(id);
            }
        }
        return test.ToImmutable().Sort(StringComparer.Ordinal);
    }

    /// <summary>Selects + writes <see cref="TestSessionsFileName"/> (one session id per line, sorted,
    /// with a header comment) and returns the chosen test ids.</summary>
    public static async Task<ImmutableArray<string>> WriteAsync(
        IEnumerable<string> sessionIds, double testFraction, string path, CancellationToken cancellationToken = default)
    {
        var test = SelectTestSessions(sessionIds, testFraction);
        var sb = new StringBuilder();
        sb.AppendLine("# Pinned held-out TEST sessions (by session id). Training MUST exclude these.");
        sb.AppendLine("# Assignment is a stable hash bucket of the id -- adding sessions never reshuffles the split.");
        foreach (var id in test)
        {
            sb.AppendLine(id);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken);
        return test;
    }

    private static uint StableBucket(string id)
    {
        // FNV-1a 32-bit folded into [0, Resolution). Deterministic across runs + machines (unlike
        // the randomised string.GetHashCode), which is what makes the split "pinned".
        var hash = 2166136261u;
        foreach (var ch in id)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash % Resolution;
    }
}
