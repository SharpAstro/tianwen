using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Durable per-session stage timings, the third store beside <see cref="DatasetPsfStore"/> (what
    /// succeeded) and <see cref="DatasetSkipStore"/> (what did not), appended as each session finishes.
    ///
    /// <para><b>Why persist rather than just log.</b> "Is the new bake slower, and where?" is a
    /// question about two runs, and answering it from console logs meant reconstructing stage
    /// boundaries from message shapes and timestamps. That reconstruction is what produced the first
    /// throughput numbers for this pipeline, and it also got the export denominator wrong, because a
    /// reader downstream has to guess what each stage was repeating over. A stored record carries the
    /// duration and its own denominator together, so a comparison is a diff.</para>
    ///
    /// <para><b>Last-wins by session id</b>, appended never rewritten, matching the other two stores:
    /// a re-run adds a line and the earlier timing stays readable, which is the point when the
    /// question is whether something got slower.</para>
    ///
    /// <para><b>The rendered table is not stored.</b> <see cref="StageTimings.DescribeTable"/> builds
    /// it from these numbers on demand. Two persisted renderings of one measurement is how one of them
    /// ends up stale.</para>
    /// </summary>
    public static class DatasetTimingStore
    {
        /// <summary>Store file name, written beside the other stores under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string FileName = "session-timings.jsonl";

        /// <summary>
        /// One session's cost. The shape fields (<paramref name="Camera"/>,
        /// <paramref name="CanvasWidth"/>, <paramref name="MasterStrategy"/>) are here because a
        /// timing without them cannot be compared: a session is slower than another mostly because it
        /// has more subs on a bigger canvas through a different integrator, and re-deriving that from
        /// the PSF store means joining two files on a session id to answer a question about one.
        /// </summary>
        /// <param name="WallSeconds">The caller's own measured wall time for the session, which is
        /// NOT the sum of <paramref name="Stages"/>: whatever falls between stages is unaccounted, and
        /// a growing gap is the signal that the stage boundaries no longer describe the run.</param>
        public sealed record SessionTiming(
            string SessionId,
            string Camera,
            int Lights,
            int Registered,
            int CanvasWidth,
            int CanvasHeight,
            string MasterStrategy,
            double WallSeconds,
            ImmutableArray<StageTimings.Stage> Stages);

        /// <inheritdoc cref="DatasetPsfStore.ReadAsync"/>
        public static Task<Dictionary<string, SessionTiming>> ReadAsync(
            string path, ILogger? logger = null, CancellationToken cancellationToken = default) =>
            JsonLinesFile.ReadLastPerKeyAsync(
                path, DatasetTimingJsonContext.Default.SessionTiming, static r => r.SessionId,
                "timing store", logger, cancellationToken);

        /// <summary>
        /// Best-effort append: a timing record is a diagnostic, so a failure to write one must never
        /// cost the session that produced it. A null or empty path is a no-op.
        /// </summary>
        public static Task RecordAsync(
            string? path, SessionTiming record, ILogger? logger = null, CancellationToken cancellationToken = default) =>
            JsonLinesFile.RecordBestEffortAsync(
                path, record, DatasetTimingJsonContext.Default.SessionTiming,
                "timing store", logger, cancellationToken);

        /// <summary>Appends one session's timing record.</summary>
        public static Task AppendAsync(string path, SessionTiming record, CancellationToken cancellationToken = default) =>
            JsonLinesFile.AppendRecordAsync(path, record, DatasetTimingJsonContext.Default.SessionTiming, cancellationToken);
    }

    [JsonSerializable(typeof(DatasetTimingStore.SessionTiming))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    internal partial class DatasetTimingJsonContext : JsonSerializerContext;
}
