using System;
using System.IO;
using System.Text;

namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>
    /// Resolves a frame back to the frame a <see cref="StackManifest"/> knows it by.
    ///
    /// <para><b>Why a DERIVED plate needs a card at all.</b> The manifest keys frames on a digest of
    /// their data section, which is the right identity for a raw light: it survives a header edit
    /// (the 2026-08-25 SITEELEV amendment rewrote 525 headers without touching a pixel) and changes
    /// when the pixels do. But a starless plate has different pixels ON PURPOSE, so it can never
    /// digest to its original. Its link to the manifest is provenance rather than identity, and
    /// <c>SRCDGST</c> carries it.</para>
    ///
    /// <para>Matching on filename instead was the obvious alternative and is rejected: the whole
    /// reason identity is a digest is that names and timestamps move independently of pixels, and a
    /// derived plate is exactly where a naming convention is most likely to drift.</para>
    ///
    /// <para>StarXTerminator preserves the header -- measured on this pipeline's own output, 102 of
    /// 106 cards survive an <c>sxt</c> round trip, the four casualties being BSCALE / BZERO /
    /// DATAMAX / DATAMIN, which it regenerates -- so a card stamped before star removal comes out
    /// the other side. Stamping AFTER is still preferred, because it does not depend on that.</para>
    /// </summary>
    public static class FrameProvenance
    {
        /// <summary>FITS keyword naming the data digest of the frame this one was derived from.
        /// Eight characters, because FITS keywords cap there.</summary>
        public const string SourceDigestKeyword = "SRCDGST";

        /// <summary>
        /// The digest the manifest knows this frame by: <see cref="SourceDigestKeyword"/> when the
        /// file declares one (a derived plate), otherwise the file's own data digest (a raw light).
        /// </summary>
        public static string SourceDigestOf(string path)
        {
            var declared = TryReadSourceDigest(path);
            return declared is { Length: > 0 } ? declared : StackManifest.DigestData(path);
        }

        /// <summary>Reads <c>SRCDGST</c> from the first header, or null when absent. Deliberately a
        /// raw block walk rather than a full FITS open: this runs once per frame per manifest-driven
        /// run and only ever needs one card out of the first header.</summary>
        public static string? TryReadSourceDigest(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 2880);
                var block = new byte[2880];
                for (var guard = 0; guard < 64; guard++)
                {
                    var offset = 0;
                    while (offset < block.Length)
                    {
                        var got = fs.Read(block, offset, block.Length - offset);
                        if (got <= 0)
                        {
                            return null;
                        }
                        offset += got;
                    }
                    for (var i = 0; i < 2880; i += 80)
                    {
                        var card = Encoding.ASCII.GetString(block, i, 80);
                        var key = card.AsSpan(0, 8).Trim();
                        if (key.SequenceEqual("END"))
                        {
                            return null;
                        }
                        if (!key.SequenceEqual(SourceDigestKeyword))
                        {
                            continue;
                        }
                        var value = card.AsSpan(10).Trim();
                        if (value.Length > 0 && value[0] == '\'')
                        {
                            var end = value[1..].IndexOf('\'');
                            if (end >= 0)
                            {
                                return value[1..(end + 1)].Trim().ToString();
                            }
                        }
                        var slash = value.IndexOf('/');
                        return (slash >= 0 ? value[..slash] : value).Trim().ToString();
                    }
                }
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
