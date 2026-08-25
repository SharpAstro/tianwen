using System;
using System.IO;
using System.IO.Hashing;

namespace TianWen.Lib.Imaging
{
    /// <summary>
    /// The one answer to "are these the same bytes". Content-addressed identity for caches, censuses
    /// and the stack manifest, where the question is never "has someone tampered with this".
    ///
    /// <para><b>Non-cryptographic on purpose.</b> Nothing here is signed or defended, so the collision
    /// resistance of a SHA-family hash would be paid for and unused. XISF is the instructive contrast:
    /// PixInsight checksums its data blocks with SHA-1/256/512 precisely BECAUSE those checksums back
    /// an XML digital signature. Ours back a lookup.</para>
    ///
    /// <para><b>Measured, because the intuition was wrong.</b> On 135 FITS frames (3.14 GB, 16 threads)
    /// reading alone took 0.37-0.50 s and reading plus SHA-256 took 0.77 s -- so the hash was about
    /// half the cost, not the rounding error it was assumed to be. XxHash128 is XXH3's vectorised core
    /// with a 128-bit output, which puts this back at read speed. FNV-1a, the other content hash in
    /// this codebase's orbit (the pdf-viewer fork's SdfGlyphDiskCache, keyed on font bytes), is
    /// byte-at-a-time scalar: correct there, where its own comment scopes it to 100 KB-1 MB fonts, and
    /// roughly an order of magnitude too slow at GB scale.</para>
    ///
    /// <para>Values carry their algorithm, following XISF's <c>sha256:...</c> convention, so changing
    /// it later is detectable rather than silently producing values that compare unequal for the wrong
    /// reason.</para>
    /// </summary>
    public static class ContentDigest
    {
        /// <summary>Algorithm tag prefixing every value this class emits.</summary>
        public const string Algorithm = "xxh128";

        /// <summary>Formats a raw hash as <c>xxh128:HEX</c>.</summary>
        public static string Format(ReadOnlySpan<byte> hash) => Algorithm + ":" + Convert.ToHexString(hash);

        /// <summary>Digest of an entire file's bytes. Returns an empty string when it cannot be read,
        /// so a caller counting distinct content can skip rather than throw.</summary>
        public static string OfFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                return OfStream(fs, long.MaxValue);
            }
            catch (IOException)
            {
                return "";
            }
        }

        /// <summary>Digest of at most <paramref name="byteCount"/> bytes from the stream's current
        /// position. Streams in 1 MiB chunks so a multi-gigabyte data unit never lands on the LOH.</summary>
        public static string OfStream(Stream stream, long byteCount)
        {
            var hash = new XxHash128();
            var buffer = new byte[1 << 20];
            var remaining = byteCount;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var got = stream.Read(buffer, 0, want);
                if (got <= 0)
                {
                    break;
                }
                hash.Append(buffer.AsSpan(0, got));
                remaining -= got;
            }
            return Format(hash.GetCurrentHash());
        }
    }
}
