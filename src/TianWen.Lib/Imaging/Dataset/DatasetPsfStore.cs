using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
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
        public static async Task<Dictionary<string, DatasetPsfNoiseReport.SessionPsf>> ReadAsync(
            string path, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var byId = new Dictionary<string, DatasetPsfNoiseReport.SessionPsf>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return byId;
            }

            var skipped = 0;
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                DatasetPsfNoiseReport.SessionPsf? record;
                // Resilience over untrusted tail bytes (killed mid-append): there is no
                // TryDeserialize, so the torn-line skip has to be exception-based.
                try
                {
                    record = JsonSerializer.Deserialize(line, DatasetPsfJsonContext.Default.SessionPsf);
                }
                catch (JsonException)
                {
                    skipped++;
                    continue;
                }
                if (record is not null)
                {
                    byId[record.SessionId] = record;
                }
            }
            if (skipped > 0)
            {
                logger?.LogWarning("PSF store {Path}: skipped {Skipped} unparseable line(s) (torn tail from an interrupted run).", path, skipped);
            }
            return byId;
        }

        /// <summary>Appends one session's record. One line, one write, after the measurement is
        /// complete, so the store never contains a half-measured session.</summary>
        public static Task AppendAsync(string path, DatasetPsfNoiseReport.SessionPsf record, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            sb.Append(JsonSerializer.Serialize(record, DatasetPsfJsonContext.Default.SessionPsf));
            sb.Append('\n');
            return JsonLinesFile.AppendAsync(path, sb.ToString(), cancellationToken);
        }
    }

    [JsonSerializable(typeof(DatasetPsfNoiseReport.SessionPsf))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    internal partial class DatasetPsfJsonContext : JsonSerializerContext;
}
