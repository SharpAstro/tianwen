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
/// P0/#42). The split is <b>by session, never by tile or frame</b>; adjacent tiles of one session
/// share noise + PSF statistics, so a tile-level split leaks the test distribution into training and
/// inflates the held-out metrics. A session is assigned to the held-out TEST set iff a stable hash
/// bucket of its portable id falls under <c>testFraction</c>: the assignment depends only on the
/// session's own id, so it is identical across machines and, critically, <b>never reshuffles as the
/// archive grows</b> (adding sessions can't move an existing one between train and test, which would
/// silently invalidate every past eval number).
/// </summary>
public static class DatasetSplitWriter
{
    /// <summary>Canonical file name under the dataset output root.</summary>
    public const string TestSessionsFileName = "test-sessions.txt";

    /// <summary>Hash bucket resolution: a session's id maps to <c>[0, Resolution)</c> and is TEST
    /// when that bucket is below <c>testFraction * Resolution</c>.</summary>
    private const uint Resolution = 10000;

    /// <summary>True when <paramref name="sessionId"/> is in the held-out TEST set for the given
    /// fraction. Pure + stable: same id + fraction always yields the same answer.</summary>
    public static bool IsTestSession(string sessionId, double testFraction)
        => StableBucket(sessionId) < (uint)(Math.Clamp(testFraction, 0.0, 1.0) * Resolution);

    /// <summary>
    /// The same, plus sessions FORCED into the held-out set whatever their bucket says.
    /// </summary>
    /// <remarks>
    /// <para>For a session that must never train, on a judgement the hash cannot know: a night whose
    /// field rotation is so strong the stacked canvas is mostly partial coverage is worthless as a
    /// training example and valuable as a TEST fixture, being exactly the input the auto-crop and the
    /// gradient remover have to survive. Deleting it would throw away that fixture; leaving it in
    /// training would teach the net a canvas artefact.</para>
    /// <para>It FORCES INTO test and can never pull a session out, so the guarantee that matters is
    /// preserved: adding a forced id cannot move any other session between train and test, and so
    /// cannot silently invalidate a past eval number. A forced id that is already in the bucket is a
    /// no-op.</para>
    /// </remarks>
    public static bool IsTestSession(string sessionId, double testFraction, IReadOnlySet<string>? alwaysHeldOut)
        => (alwaysHeldOut is not null && alwaysHeldOut.Contains(sessionId))
           || IsTestSession(sessionId, testFraction);

    /// <summary>The held-out TEST session ids among <paramref name="sessionIds"/>, ordinal-sorted
    /// (canonical order, independent of input order).</summary>
    public static ImmutableArray<string> SelectTestSessions(IEnumerable<string> sessionIds, double testFraction)
        => SelectTestSessions(sessionIds, testFraction, alwaysHeldOut: null);

    /// <summary>The held-out TEST session ids, including any forced by
    /// <paramref name="alwaysHeldOut"/>. Ordinal-sorted.</summary>
    public static ImmutableArray<string> SelectTestSessions(
        IEnumerable<string> sessionIds, double testFraction, IReadOnlySet<string>? alwaysHeldOut)
    {
        var test = ImmutableArray.CreateBuilder<string>();
        foreach (var id in sessionIds)
        {
            if (IsTestSession(id, testFraction, alwaysHeldOut))
            {
                test.Add(id);
            }
        }
        return test.ToImmutable().Sort(StringComparer.Ordinal);
    }

    /// <summary>Selects + writes <see cref="TestSessionsFileName"/> (one session id per line, sorted,
    /// with a header comment) and returns the chosen test ids.</summary>
    public static Task<ImmutableArray<string>> WriteAsync(
        IEnumerable<string> sessionIds, double testFraction, string path, CancellationToken cancellationToken = default)
        => WriteAsync(sessionIds, testFraction, path, alwaysHeldOut: null, cancellationToken);

    /// <summary>The same, honouring <paramref name="alwaysHeldOut"/> and MARKING those entries in the
    /// file, so a reader can tell a deliberate exclusion from a hash outcome.</summary>
    public static async Task<ImmutableArray<string>> WriteAsync(
        IEnumerable<string> sessionIds, double testFraction, string path,
        IReadOnlySet<string>? alwaysHeldOut, CancellationToken cancellationToken = default)
    {
        var test = SelectTestSessions(sessionIds, testFraction, alwaysHeldOut);
        var sb = new StringBuilder();
        sb.AppendLine("# Pinned held-out TEST sessions (by session id). Training MUST exclude these.");
        sb.AppendLine("# Assignment is a stable hash bucket of the id -- adding sessions never reshuffles the split.");
        sb.AppendLine("# A line marked FORCED was held out deliberately, not by its bucket.");
        foreach (var id in test)
        {
            var forced = alwaysHeldOut is not null && alwaysHeldOut.Contains(id);
            sb.AppendLine(forced ? $"{id}	# FORCED" : id);
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
