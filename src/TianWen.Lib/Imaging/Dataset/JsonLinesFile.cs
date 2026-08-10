using System;
using System.IO;
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
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
