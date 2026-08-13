using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Durable per-session record of every session the bake did NOT export, appended as that session
    /// is dropped. The counterpart to <see cref="DatasetPsfStore"/>, which records only what succeeded.
    ///
    /// <para><b>Why this exists: the failing sessions are the regression fixture.</b> They are
    /// deliberately kept in the source set rather than excluded by path or object, because a change in
    /// WHICH sessions fail, or in their numbers, is the signal that a detection or registration change
    /// moved something. Excluding them would make that untrackable. But a skip previously left nothing
    /// behind except a WARNING line in a log, so comparing one bake against another meant grepping
    /// console logs by hand, and that is precisely how a wrong diagnosis of the HIP 42861 skip
    /// ("genuinely too star-poor", when the frames carry 44 to 97 stars each) survived three bakes and
    /// was twice re-reported as a fresh finding. A machine-readable record makes the comparison a diff.
    /// </para>
    ///
    /// <para><b>Numbers, not prose.</b> The stored record is
    /// <see cref="RegistrationCensus.Spread"/> verbatim; the human-readable line is rendered from it by
    /// <see cref="RegistrationCensus.Describe"/> at log time and is deliberately NOT stored as well.
    /// Two persisted renderings of one measurement is how one of them ends up stale, which this file's
    /// own subject matter is an instance of.</para>
    ///
    /// <para><b>Last-wins by session id</b>, appended never rewritten, matching
    /// <see cref="DatasetPsfStore"/>: a re-run adds a line and the earlier record stays readable, so a
    /// session that starts or stops failing leaves both states on disk. A session that later SUCCEEDS
    /// keeps its stale skip record here, so read this against the PSF store rather than alone, and
    /// treat presence in <see cref="DatasetPsfStore"/> as authoritative for "did it export".</para>
    /// </summary>
    public static class DatasetSkipStore
    {
        /// <summary>Store file name, written beside the PSF store under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string FileName = "skipped-sessions.jsonl";

        /// <summary>
        /// One dropped session. <paramref name="Reason"/> is a short stable slug, not a sentence, so a
        /// diff across bakes groups by cause instead of by wording.
        /// </summary>
        /// <param name="Reason">Stable cause slug, e.g. <c>fewer-than-2-registered</c>.</param>
        /// <param name="Census">The per-frame spread, or <see langword="null"/> when the session was
        /// dropped before any frame was measured (no survivors at all).</param>
        public sealed record SkippedSession(
            string SessionId,
            string Reason,
            int Survivors,
            int Registered,
            int SkippedTooFewStars,
            int SkippedNoQuadFit,
            string? ReferenceFile,
            int ReferenceStars,
            int ReferenceQuads,
            RegistrationCensus.Spread? Census);

        /// <summary>
        /// Reads the store into a session-id keyed map, last record per id winning. Unparseable lines
        /// (a torn tail from a killed run, healed on the next append) are skipped and counted in the
        /// log, never fatal; a missing file yields an empty map, so a first run degrades cleanly.
        /// </summary>
        public static async Task<Dictionary<string, SkippedSession>> ReadAsync(
            string path, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var byId = new Dictionary<string, SkippedSession>(StringComparer.Ordinal);
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
                SkippedSession? record;
                // Same reasoning as DatasetPsfStore: there is no TryDeserialize, so tolerating a torn
                // tail has to be exception-based.
                try
                {
                    record = JsonSerializer.Deserialize(line, DatasetSkipJsonContext.Default.SkippedSession);
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
                logger?.LogWarning("Skip store {Path}: skipped {Skipped} unparseable line(s) (torn tail from an interrupted run).", path, skipped);
            }
            return byId;
        }

        /// <summary>
        /// Best-effort append used by every skip site: creates the stats directory, swallows an I/O
        /// or permission failure into a warning, and does nothing at all when
        /// <paramref name="path"/> is absent (a caller that did not ask for a store, which is the
        /// normal case for tests and the stacking CLI, not an error).
        ///
        /// <para>Best-effort deliberately: the session is already lost, and taking down a bake that
        /// is otherwise fine because a diagnostics line could not be written would trade one session
        /// for the remaining sixty-seven. The WARNING at the skip site is the fallback record.</para>
        /// </summary>
        public static async Task RecordAsync(
            string? path, SkippedSession record, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            if (path is not { Length: > 0 })
            {
                return;
            }
            try
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir);
                }
                await AppendAsync(path, record, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex,
                    "  [{Session}] could not append to the skip store {Path}; the WARNING above is the only record.",
                    record.SessionId, path);
            }
        }

        /// <summary>Appends one dropped session's record.</summary>
        public static Task AppendAsync(string path, SkippedSession record, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            sb.Append(JsonSerializer.Serialize(record, DatasetSkipJsonContext.Default.SkippedSession));
            sb.Append('\n');
            return JsonLinesFile.AppendAsync(path, sb.ToString(), cancellationToken);
        }
    }

    [JsonSerializable(typeof(DatasetSkipStore.SkippedSession))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    internal partial class DatasetSkipJsonContext : JsonSerializerContext;
}
