using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Append-only JSONL checkpoint file, the shape every durable dataset-build artifact uses: one
    /// line per record, appended in one block as the LAST step of the work it records, so "the line
    /// is present" means "that work is finished and on disk". A run that is killed part-way leaves a
    /// prefix of completed records and nothing else, which is what makes a build restartable without
    /// a repair step.
    ///
    /// <para><b>Self-healing tail.</b> A process killed mid-append leaves a partial final line. Every
    /// complete record ends in <c>'\n'</c>, so a file not ending in one has a torn tail; the next
    /// append scans back to the last newline and truncates there. Healing on APPEND rather than on
    /// read is deliberate: it means a torn line can never get buried mid-file, where every JSONL
    /// consumer downstream (including Python) would choke on it. Readers additionally skip
    /// unparseable lines so the torn tail is survivable even before the next append.</para>
    ///
    /// <para>Shared by <c>DatasetTileExporter</c>'s tile manifest and <see cref="DatasetPsfStore"/>;
    /// both had to get the backward scan exactly right, and one copy of it is enough.</para>
    /// </summary>
    public static class JsonLinesFile
    {
        /// <summary>
        /// Heals any torn tail, then appends <paramref name="payload"/> (which must already be
        /// newline-terminated JSON lines). Creates the file and its directory when absent.
        /// </summary>
        public static async Task AppendAsync(string path, string payload, CancellationToken cancellationToken)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // OpenOrCreate + ReadWrite (not FileMode.Append, which forbids the backward scan).
            //
            // FileShare.Read, not None: a store is written by exactly one process (both the output
            // directory and the scratch root are lock-guarded) but it is READ all the time, by report
            // code and by anyone inspecting a running bake. FileShare.None made every one of those
            // readers a hazard rather than merely a nuisance, and it cost a session: a four-hour bake
            // was 65 sessions in when a `Get-Content` of psf-sessions.jsonl collided with the append
            // that closes a session, which threw IOException, fell to the per-session catch and
            // marked an otherwise complete session FAILED.
            //
            // Sharing reads is safe against TruncateTornTail's SetLength below: it only ever removes
            // an already-incomplete final line, and every reader here skips unparseable lines anyway,
            // so the worst a concurrent reader sees is the torn tail it was going to skip regardless.
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            TruncateTornTail(stream);
            stream.Seek(0, SeekOrigin.End);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(payload.AsMemory(), cancellationToken);
        }

        /// <summary>Every complete record ends in <c>'\n'</c>; a file not ending in one has a torn
        /// tail from an interrupted append. Scan backwards to the last newline (the torn tail is at
        /// most one record, so the byte-wise walk is trivially cheap) and truncate there.</summary>
        internal static void TruncateTornTail(FileStream stream)
        {
            if (stream.Length == 0)
            {
                return;
            }
            var pos = stream.Length - 1;
            stream.Seek(pos, SeekOrigin.Begin);
            if (stream.ReadByte() == '\n')
            {
                return; // clean tail; every record complete
            }
            while (pos > 0)
            {
                pos--;
                stream.Seek(pos, SeekOrigin.Begin);
                if (stream.ReadByte() == '\n')
                {
                    stream.SetLength(pos + 1);
                    return;
                }
            }
            stream.SetLength(0); // no newline at all; the whole file is one torn line
        }

        /// <summary>
        /// Reads a store into a key-keyed map, LAST record per key winning. Unparseable lines (a torn
        /// tail from a killed run, healed on the next append) are skipped and counted in the log,
        /// never fatal; a missing file yields an empty map, so a first run degrades cleanly.
        ///
        /// <para>The three dataset stores (PSF, skips, timings) differed only in their record type
        /// and key selector, and each had its own copy of this loop plus its own subtly different
        /// warning text. <paramref name="typeInfo"/> is passed in rather than resolved, so this stays
        /// AOT-safe: every caller supplies its own source-generated context.</para>
        /// </summary>
        /// <param name="label">Store name for the torn-tail warning, e.g. "PSF store".</param>
        public static async Task<Dictionary<string, T>> ReadLastPerKeyAsync<T>(
            string path,
            JsonTypeInfo<T> typeInfo,
            Func<T, string> keySelector,
            string label,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return byKey;
            }

            var skipped = 0;
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                T? record;
                // Resilience over untrusted tail bytes (killed mid-append): there is no
                // TryDeserialize, so the torn-line skip has to be exception-based.
                try
                {
                    record = JsonSerializer.Deserialize(line, typeInfo);
                }
                catch (JsonException)
                {
                    skipped++;
                    continue;
                }
                if (record is not null)
                {
                    byKey[keySelector(record)] = record;
                }
            }
            if (skipped > 0)
            {
                logger?.LogWarning("{Label} {Path}: skipped {Skipped} unparseable line(s) (torn tail from an interrupted run).",
                    label, path, skipped);
            }
            return byKey;
        }

        /// <summary>Serialises one record and appends it as a single newline-terminated line, after
        /// the work it records is complete, so the store never holds a half-finished entry.</summary>
        public static Task AppendRecordAsync<T>(
            string path, T record, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            sb.Append(JsonSerializer.Serialize(record, typeInfo));
            sb.Append('\n');
            return AppendAsync(path, sb.ToString(), cancellationToken);
        }

        /// <summary>
        /// <see cref="AppendRecordAsync"/> for diagnostic stores, where the append must never take
        /// down the run that produced the record: a null or empty <paramref name="path"/> is a no-op
        /// (a caller that did not ask for a store, the normal case in tests and the stacking CLI),
        /// and an I/O or permission failure becomes a warning.
        ///
        /// <para>Deliberate: trading the remaining sixty-seven sessions of a bake for a failed
        /// diagnostics append on a full disk is the worse outcome, and the log line at the call site
        /// is the fallback record.</para>
        /// </summary>
        public static async Task RecordBestEffortAsync<T>(
            string? path,
            T record,
            JsonTypeInfo<T> typeInfo,
            string label,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (path is not { Length: > 0 })
            {
                return;
            }
            try
            {
                await AppendRecordAsync(path, record, typeInfo, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Could not append to the {Label} {Path}; the log line at the call site is the only record.",
                    label, path);
            }
        }

        /// <summary>
        /// Returns the first free <c>&lt;path&gt;.bak-N</c> beside <paramref name="path"/>. Used to
        /// move an artifact aside instead of deleting it: a fresh (non-resume) run legitimately
        /// starts a new manifest, but the old one is the only record of what was already exported,
        /// so it is rotated rather than erased. Index-based rather than timestamped so it needs no
        /// clock (and so it stays deterministic in tests).
        /// </summary>
        public static string NextFreeBackupPath(string path)
        {
            for (var i = 1; ; i++)
            {
                var candidate = $"{path}.bak-{i}";
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
