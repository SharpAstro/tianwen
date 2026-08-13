using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Durable per-session store for the PSF/noise report's INPUTS, appended one line per session as
    /// that session finishes (<see cref="JsonLinesFile"/>).
    ///
    /// <para><b>Why this exists.</b> The report used to be derived state held only in memory for the
    /// length of one run, and <c>WriteMarkdownAsync</c> overwrote the file at the end. So a resumed
    /// run, which by design does not re-register sessions whose tiles are already exported, rewrote
    /// the whole report from just the sessions it happened to touch: a 50-session archive-wide report
    /// became a 1-session one, and the previous content was gone. The measurement cannot be recovered
    /// afterwards either, because it needs the session master, which lives in scratch that is wiped
    /// per session. Persisting the inputs makes the report accumulate across runs instead of being
    /// rebuilt from whatever the last run saw, and makes a killed run cost only its in-flight
    /// session.</para>
    ///
    /// <para><b>Last-wins by session id.</b> Re-measuring a session appends a second line rather than
    /// rewriting the file, and <see cref="ReadAsync"/> keeps the last record per id. Nothing is ever
    /// erased, so a re-run that turns out worse can still be compared against what it replaced.</para>
    /// </summary>
    public static class DatasetPsfStore
    {
        /// <summary>Store file name, written beside the rendered report under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string FileName = "psf-sessions.jsonl";

        /// <summary>
        /// Reads the store into a session-id keyed map, last record per id winning. Unparseable lines
        /// (a torn tail from a killed run, healed on the next append) are skipped and counted in the
        /// log, never fatal; a missing file yields an empty map, so a first run degrades cleanly.
        /// </summary>
        public static Task<Dictionary<string, DatasetPsfNoiseReport.SessionPsf>> ReadAsync(
            string path, ILogger? logger = null, CancellationToken cancellationToken = default) =>
            JsonLinesFile.ReadLastPerKeyAsync(
                path, DatasetPsfJsonContext.Default.SessionPsf, static r => r.SessionId,
                "PSF store", logger, cancellationToken);

        /// <summary>Appends one session's record. One line, one write, after the measurement is
        /// complete, so the store never contains a half-measured session.</summary>
        public static Task AppendAsync(string path, DatasetPsfNoiseReport.SessionPsf record, CancellationToken cancellationToken = default) =>
            JsonLinesFile.AppendRecordAsync(path, record, DatasetPsfJsonContext.Default.SessionPsf, cancellationToken);
    }

    [JsonSerializable(typeof(DatasetPsfNoiseReport.SessionPsf))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    internal partial class DatasetPsfJsonContext : JsonSerializerContext;
}
