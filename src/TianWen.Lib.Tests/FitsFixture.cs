using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Builds FITS files by hand for tests that edit irreplaceable data, so the test owns every
    /// byte rather than inheriting whatever a writer chose.
    ///
    /// <para>Shared by <see cref="FitsHeaderEditorTests"/> and <see cref="ArchiveSweepTests"/>,
    /// which need the same three things: a file whose payload is known, a second NAME for that
    /// file, and the identity the file system keys on. A second copy of any of those would be a
    /// second definition of what a hard link is, in the tests for the code that decides it.</para>
    /// </summary>
    internal static class FitsFixture
    {
        public const int Block = FitsHeaderEditor.BlockSize;
        public const int Card = FitsHeaderEditor.CardSize;

        /// <summary>A directory of this test's own, named after the caller so a failure says which
        /// test left it behind.</summary>
        public static string CreateTempDir(string suite, string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), suite, name, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            // The REAL path. The temp path is commonly an 8.3 short form ("C:\Users\ABCDEF~1\..."),
            // which the prune sweep rightly refuses as an alias: hand the tests the name the file
            // system uses, as a person running the sweep would be told to.
            return HardLinkProbe.TryGetFinalPath(dir) ?? dir;
        }

        /// <summary>A primary header of <paramref name="extraCards"/> plus the mandatory structural
        /// cards, then a deterministic payload. The payload is a pseudo-random ramp rather than
        /// zeros, because a run of zeros hides a truncation.</summary>
        public static (string Path, byte[] Payload) WriteFits(
            string dir, string name, IEnumerable<string> extraCards, int payloadBytes = Block * 3, int seed = 0)
        {
            var cards = new List<string>
            {
                "SIMPLE  =                    T / C# FITS",
                "BITPIX  =                   16",
                "NAXIS   =                    2 / Dimensionality",
                "NAXIS1  =                   40",
                "NAXIS2  =                   36",
                "BZERO   =                32768",
            };
            cards.AddRange(extraCards);

            var headerBlocks = (cards.Count + 1 + 35) / 36;
            var header = new byte[headerBlocks * Block];
            header.AsSpan().Fill((byte)' ');
            for (var i = 0; i < cards.Count; i++)
            {
                Encoding.ASCII.GetBytes(cards[i].PadRight(Card), header.AsSpan(i * Card, Card));
            }
            Encoding.ASCII.GetBytes("END".PadRight(Card), header.AsSpan(cards.Count * Card, Card));

            var payload = new byte[payloadBytes];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 31 + 7 + seed * 101) & 0xFF);
            }

            var path = Path.Combine(dir, name);
            using (var fs = File.Create(path))
            {
                fs.Write(header);
                fs.Write(payload);
            }
            return (path, payload);
        }

        public static byte[] Sha(ReadOnlySpan<byte> data) => SHA256.HashData(data);

        public static byte[] ShaOfFile(string path) => Sha(File.ReadAllBytes(path));

        /// <summary>Adds <paramref name="link"/> as another name for <paramref name="existing"/>,
        /// skipping the test when the volume cannot do it. Goes through the production helper so the
        /// fixture and the code under test agree about what a hard link is.</summary>
        public static void LinkOrSkip(string link, string existing)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            Assert.SkipUnless(
                HardLinkProbe.TryCreateHardLink(link, existing, out var error),
                $"Could not create a hard link on this volume: {error}");
        }

        /// <summary>The identity of the file a path names, failing the test if it cannot be read.</summary>
        public static HardLinkProbe.FileIdentity IdentityOf(string path)
        {
            var identity = HardLinkProbe.TryGetIdentity(path);
            Assert.NotNull(identity);
            return identity.Value;
        }
    }
}
