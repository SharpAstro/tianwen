using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>What a run decided about one frame. A skip REASON is recorded rather than the frame
    /// being dropped from the list, because "considered and rejected for too few stars" and "never
    /// offered" are different facts and only the first is reproducible.</summary>
    public enum FrameFate
    {
        Matched,
        SkippedTooFewStars,
        SkippedNoQuadFit,
        SkippedQualityReject,
    }

    /// <param name="Path">Frame path as this run saw it. For legibility in a log; NEVER identity.</param>
    /// <param name="DataDigest">SHA-256 over the FITS DATA section. This is identity.</param>
    /// <param name="StarTransform">The six <see cref="Matrix3x2"/> elements of the STAR solution, or
    /// <c>null</c> when the frame did not match. Deliberately the star solution rather than whatever
    /// the producing run composed on top: a comet run subtracts the body's drift AFTER the star
    /// solution, so storing the composed transform would bake one run's target into every later layer.</param>
    public sealed record ManifestFrame(
        string Path,
        string DataDigest,
        FrameFate Fate,
        DateTimeOffset ExposureStartUtc,
        float[]? StarTransform)
    {
        public Matrix3x2? AsMatrix() => StarTransform is { Length: 6 } t
            ? new Matrix3x2(t[0], t[1], t[2], t[3], t[4], t[5])
            : null;

        public static float[] From(Matrix3x2 m) => [m.M11, m.M12, m.M21, m.M22, m.M31, m.M32];
    }

    /// <summary>
    /// What one stacking run decided, so a later run is built from IDENTICAL inputs rather than
    /// re-deriving them and hoping they agree.
    ///
    /// <para><b>Reproducibility is the point; speed is the side effect.</b> Reusing the transforms
    /// skips measure AND register, which the stage table puts at 44.6% + 3.8% of wall clock. But the
    /// failure this exists to prevent is "re-run it and get a different reference frame": the
    /// reference is picked by composite PSF score independently per run, so a different reference is
    /// a different canvas origin and orientation, and two layers that do not overlay at all. A screen
    /// combine of those is meaningless rather than merely inconsistent.</para>
    ///
    /// <para><b>Three things are pinned</b>, in increasing order of how badly they bite: the frame
    /// LIST (a frame one layer dropped must not silently contribute to the other, or the layers differ
    /// in depth and noise); the per-frame TRANSFORM (a starless plate has no quads and cannot be
    /// star-registered at all, so its transform has to come from the original it was derived from);
    /// and the REFERENCE frame.</para>
    ///
    /// <para><b>Identity is a digest of the DATA section, never a path and never an mtime.</b> The
    /// 2026-08-25 SITEELEV amendment rewrote 525 headers and changed every mtime without touching a
    /// pixel, and star positions depend only on pixels, so an mtime key would have invalidated a whole
    /// archive for a header edit. The path travels alongside because it is what a human reads in a
    /// log, but a frame is never matched on it.</para>
    ///
    /// <para>Detection parameters are recorded because a star list found at one threshold is not the
    /// list found at another, so transforms derived under different settings are not interchangeable.</para>
    /// </summary>
    public sealed record StackManifest(
        int SchemaVersion,
        string Slug,
        string CreatedBy,
        DateTimeOffset CreatedUtc,
        string ReferencePath,
        string ReferenceDigest,
        float SnrMin,
        int MinStars,
        ManifestFrame[] Frames)
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>Sidecar path for a master: <c>master_foo.fits</c> -&gt; <c>master_foo.manifest.json</c>.</summary>
        public static string PathFor(string masterFitsPath)
            => Path.ChangeExtension(masterFitsPath, null) + ".manifest.json";

        /// <summary>
        /// Digest over the first image HDU's DATA section, via <see cref="ContentDigest"/> (which
        /// carries the reasoning about why this is not a cryptographic hash).
        ///
        /// <para>The DATA section rather than the file, which is the whole point: the 2026-08-25
        /// SITEELEV amendment rewrote 525 headers and changed every mtime without touching a pixel,
        /// and star positions depend only on pixels. XISF draws the same line, checksumming data
        /// blocks rather than whole units.</para>
        ///
        /// <para>Walks HDUs the way every reader does (see <c>FitsHduExtensions</c>): a tile-compressed
        /// or multi-extension file opens with an empty primary, so stopping at the first END would
        /// digest zero bytes for all of them and make every such file identical.</para>
        /// </summary>
        public static string DigestData(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var block = new byte[2880];
            while (true)
            {
                long naxis = -1;
                long bitpix = 0;
                long npix = 1;
                var sawEnd = false;
                while (!sawEnd)
                {
                    if (!ReadFull(fs, block))
                    {
                        return "";
                    }
                    for (var i = 0; i < 2880; i += 80)
                    {
                        var card = Encoding.ASCII.GetString(block, i, 80);
                        var key = card.AsSpan(0, 8).Trim();
                        if (key.SequenceEqual("END"))
                        {
                            sawEnd = true;
                            break;
                        }
                        if (card[8] != '=')
                        {
                            continue;
                        }
                        var val = card.AsSpan(10, 20).Trim();
                        if (key.SequenceEqual("BITPIX"))
                        {
                            _ = long.TryParse(val, out bitpix);
                        }
                        else if (key.SequenceEqual("NAXIS"))
                        {
                            _ = long.TryParse(val, out naxis);
                        }
                        else if (key.StartsWith("NAXIS") && long.TryParse(val, out var axis))
                        {
                            npix *= axis;
                        }
                    }
                }

                if (naxis <= 0)
                {
                    // NAXIS=0 is an empty HDU with no data unit, so the next block starts the next
                    // header. This is the normal shape of an fpack primary.
                    continue;
                }

                var dataBytes = Math.Abs(bitpix) / 8 * npix;
                return ContentDigest.OfStream(fs, dataBytes);
            }
        }

        private static bool ReadFull(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var got = stream.Read(buffer, offset, buffer.Length - offset);
                if (got <= 0)
                {
                    return false;
                }
                offset += got;
            }
            return true;
        }

        /// <summary>Writes atomically. A half-written manifest that still parses is worse than none:
        /// the next run would build its layer from a truncated frame list and the combine would look
        /// perfectly fine while being shallower on one side.</summary>
        public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
        {
            var tmp = path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, this, StackManifestJsonContext.Default.StackManifest, cancellationToken);
            }
            File.Move(tmp, path, overwrite: true);
        }

        public static async Task<StackManifest?> TryReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync(fs, StackManifestJsonContext.Default.StackManifest, cancellationToken);
        }

        /// <summary>The matched frames keyed by data digest, which is what a consuming run selects on.</summary>
        public Dictionary<string, ManifestFrame> MatchedByDigest()
        {
            var byDigest = new Dictionary<string, ManifestFrame>(Frames.Length, StringComparer.Ordinal);
            foreach (var frame in Frames)
            {
                if (frame.Fate is FrameFate.Matched && frame.DataDigest.Length > 0)
                {
                    byDigest[frame.DataDigest] = frame;
                }
            }
            return byDigest;
        }
    }

    // Enums as STRINGS here, unlike the hosting wire contract which is deliberately numeric. This is
    // a file a human reads, and the whole argument for carrying paths alongside digests is legibility;
    // a bare 0 defeats that. It also makes the format reorder-proof: renumbering FrameFate would
    // silently change what every manifest already on disk means.
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(StackManifest))]
    public sealed partial class StackManifestJsonContext : JsonSerializerContext
    {
    }
}
