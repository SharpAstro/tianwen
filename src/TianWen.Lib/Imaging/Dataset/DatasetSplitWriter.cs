using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// The id whose bucket decides the set: the session itself, or the whole night for one side of a
    /// meridian flip (<see cref="ImagingSession.FlipSide"/>).
    ///
    /// <para><b>This is what keeps a flipped night out of both sets at once.</b> The combined master
    /// and its two sides are the same sky at three depths, so hashing their ids separately would put
    /// some in train and some in test, and the held-out numbers would be measured on sky the model
    /// had already seen. Stripping the suffix also leaves every id that has no suffix hashing exactly
    /// as before, so no existing assignment moves.</para>
    /// </summary>
    public static string GroupIdOf(string sessionId)
    {
        var marker = "|" + ImagingSession.FlipSideKey + "=";
        var at = sessionId.LastIndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? sessionId : sessionId[..at];
    }

    /// <summary>True when <paramref name="sessionId"/> is in the held-out TEST set for the given
    /// fraction. Pure + stable: same id + fraction always yields the same answer.</summary>
    public static bool IsTestSession(string sessionId, double testFraction)
        => StableBucket(GroupIdOf(sessionId)) < (uint)(Math.Clamp(testFraction, 0.0, 1.0) * Resolution);

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
        => IsTestSession(sessionId, testFraction, alwaysHeldOut, prior: null);

    /// <summary>
    /// The same, keeping the set a session had in a previous bake (<paramref name="prior"/>) when it
    /// existed then. Forced first, then the prior, then the hash.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a prior at all.</b> The id carries the session's <c>FILTER</c> card
    /// (<see cref="ImagingSession.GroupId"/>), so a curation pass that writes a missing card RENAMES the
    /// session, and a renamed session would be re-drawn from a fresh hash: across the 79 filterless sessions
    /// of the 2026-09-29 store that moves about twenty between sets, half of them from held-out into
    /// training. Matching the prior by the id WITHOUT its filter component (<see cref="WithoutFilter"/>)
    /// keeps every one where it was, and a session the prior never saw still takes its hash.</para>
    /// </remarks>
    public static bool IsTestSession(string sessionId, double testFraction, IReadOnlySet<string>? alwaysHeldOut, PriorSplit? prior)
        // Holding out a night holds out its flip sides too: naming the night is the natural way to
        // ask for it, and a side left behind in training would put the held-out sky back in the
        // training set through the other door. A forced id written before the session gained a FILTER
        // still names it.
        => IsForced(sessionId, alwaysHeldOut)
           || (prior?.WasTest(sessionId) ?? IsTestSession(sessionId, testFraction));

    /// <summary>Whether <paramref name="alwaysHeldOut"/> names the session: by its id, its night's id, or
    /// either before the session gained a <c>FILTER</c>.</summary>
    public static bool IsForced(string sessionId, IReadOnlySet<string>? alwaysHeldOut)
    {
        if (alwaysHeldOut is null)
        {
            return false;
        }
        var group = GroupIdOf(sessionId);
        return alwaysHeldOut.Contains(sessionId) || alwaysHeldOut.Contains(group) || alwaysHeldOut.Contains(WithoutFilter(group));
    }

    /// <summary>
    /// A group id as it read before its session had a <c>FILTER</c> card: <c>dir|CAM|OBJECT</c>, or
    /// <c>dir|CAM</c> when there is no target. An id with no filter component comes back unchanged.
    /// </summary>
    public static string WithoutFilter(string groupId)
    {
        var parts = groupId.Split('|');
        return parts.Length != 4 ? groupId
            : parts[2].Length > 0 ? string.Join('|', parts[0], parts[1], parts[2])
            : string.Join('|', parts[0], parts[1]);
    }

    /// <summary>
    /// A previous bake's split: every session it knew (its ledger) and the ones it held out (its pinned
    /// file), by group id.
    /// </summary>
    public sealed class PriorSplit
    {
        private readonly HashSet<string> _known;
        private readonly HashSet<string> _test;

        /// <summary>A prior over these session ids; flip sides fold onto their night.</summary>
        public PriorSplit(IEnumerable<string> knownIds, IEnumerable<string> testIds)
        {
            _known = new HashSet<string>(knownIds.Select(GroupIdOf), StringComparer.Ordinal);
            _test = new HashSet<string>(testIds.Select(GroupIdOf), StringComparer.Ordinal);
        }

        /// <summary>Sessions the prior knew.</summary>
        public int KnownCount => _known.Count;

        /// <summary>
        /// The set the session was in, or null when the prior never saw it: matched by its night's id,
        /// then by that id before the session gained a <c>FILTER</c>.
        /// </summary>
        public bool? WasTest(string sessionId)
        {
            var group = GroupIdOf(sessionId);
            if (_known.Contains(group))
            {
                return _test.Contains(group);
            }
            var legacy = WithoutFilter(group);
            return legacy != group && _known.Contains(legacy) ? _test.Contains(legacy) : null;
        }

        /// <summary>Whether the prior placed this session only through its id before a <c>FILTER</c> was added.</summary>
        public bool MatchedByLegacyId(string sessionId)
        {
            var group = GroupIdOf(sessionId);
            return !_known.Contains(group) && _known.Contains(WithoutFilter(group));
        }

        /// <summary>
        /// The prior a store holds: its session ledger's ids and its pinned test file. Empty for a store
        /// that has neither, which leaves every session to its hash.
        /// </summary>
        public static async Task<PriorSplit> ReadAsync(string storeDir, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var ledger = await DatasetSessionLedger.ReadAsync(
                Path.Combine(storeDir, "stats", DatasetSessionLedger.FileName), logger, cancellationToken);
            return new PriorSplit(ledger.Keys, ReadPinned(Path.Combine(storeDir, TestSessionsFileName)));
        }
    }

    /// <summary>The ids a pinned <see cref="TestSessionsFileName"/> holds out, comments and markers stripped;
    /// empty when the file does not exist.</summary>
    public static ImmutableArray<string> ReadPinned(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        var ids = ImmutableArray.CreateBuilder<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            // A marker such as "\t# FORCED" follows the id.
            var cut = line.IndexOfAny(['\t', '#']);
            var id = (cut < 0 ? line : line[..cut]).Trim();
            if (id.Length > 0)
            {
                ids.Add(id);
            }
        }
        return ids.ToImmutable();
    }

    /// <summary>The held-out TEST session ids among <paramref name="sessionIds"/>, ordinal-sorted
    /// (canonical order, independent of input order).</summary>
    public static ImmutableArray<string> SelectTestSessions(IEnumerable<string> sessionIds, double testFraction)
        => SelectTestSessions(sessionIds, testFraction, alwaysHeldOut: null);

    /// <summary>The held-out TEST session ids, including any forced by
    /// <paramref name="alwaysHeldOut"/>. Ordinal-sorted.</summary>
    public static ImmutableArray<string> SelectTestSessions(
        IEnumerable<string> sessionIds, double testFraction, IReadOnlySet<string>? alwaysHeldOut)
        => SelectTestSessions(sessionIds, testFraction, alwaysHeldOut, prior: null);

    /// <summary>The held-out TEST session ids, a session the <paramref name="prior"/> knew keeping its set.</summary>
    public static ImmutableArray<string> SelectTestSessions(
        IEnumerable<string> sessionIds, double testFraction, IReadOnlySet<string>? alwaysHeldOut, PriorSplit? prior)
    {
        var test = ImmutableArray.CreateBuilder<string>();
        foreach (var id in sessionIds)
        {
            if (IsTestSession(id, testFraction, alwaysHeldOut, prior))
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
    public static Task<ImmutableArray<string>> WriteAsync(
        IEnumerable<string> sessionIds, double testFraction, string path,
        IReadOnlySet<string>? alwaysHeldOut, CancellationToken cancellationToken = default)
        => WriteAsync(sessionIds, testFraction, path, alwaysHeldOut, prior: null, cancellationToken);

    /// <summary>The same, a session the <paramref name="prior"/> knew keeping its set.</summary>
    public static async Task<ImmutableArray<string>> WriteAsync(
        IEnumerable<string> sessionIds, double testFraction, string path,
        IReadOnlySet<string>? alwaysHeldOut, PriorSplit? prior, CancellationToken cancellationToken = default)
    {
        var test = SelectTestSessions(sessionIds, testFraction, alwaysHeldOut, prior);
        var sb = new StringBuilder();
        sb.AppendLine("# Pinned held-out TEST sessions (by session id). Training MUST exclude these.");
        sb.AppendLine("# A session the previous bake knew keeps its set (matched by its id, or by its id before it gained a");
        sb.AppendLine("# FILTER card); a new session takes a stable hash bucket of its id -- adding sessions never reshuffles.");
        sb.AppendLine("# A line marked FORCED was held out deliberately, not by its bucket.");
        sb.AppendLine($"# An id ending '|{ImagingSession.FlipSideKey}=<side>' is ONE SIDE of a meridian flip and is the");
        sb.AppendLine("# SAME SKY as the id without that suffix: it belongs to whichever set that id is in, whether or");
        sb.AppendLine("# not it is listed here. Ask DatasetSplitWriter.IsTestSession rather than matching this file");
        sb.AppendLine("# literally, or a side trains on sky the eval is measured over.");
        foreach (var id in test)
        {
            var forced = IsForced(id, alwaysHeldOut);
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
