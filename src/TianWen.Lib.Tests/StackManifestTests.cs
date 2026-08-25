using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The manifest exists so two layers meant to be combined are built from identical inputs, so the
    /// things pinned here are the ones whose drift would make a screen combine meaningless rather than
    /// merely inconsistent.
    /// </summary>
    public class StackManifestTests
    {
        private static StackManifest Sample() => new(
            StackManifest.CurrentSchemaVersion,
            "10PTempel2_light_60s_-5C_g1600",
            "TianWen.Imaging.Stacking.Integrator",
            new DateTimeOffset(2026, 8, 25, 20, 45, 0, TimeSpan.Zero),
            @"C:\temp\comet-stack\LIGHT\ref.fits",
            "AABBCC",
            SnrMin: 5f,
            MinStars: 2000,
            [
                new ManifestFrame(@"C:\temp\comet-stack\LIGHT\ref.fits", "AABBCC", FrameFate.Matched,
                    new DateTimeOffset(2026, 8, 16, 13, 49, 24, TimeSpan.Zero), ManifestFrame.From(Matrix3x2.Identity)),
                new ManifestFrame(@"C:\temp\comet-stack\LIGHT\a.fits", "DDEEFF", FrameFate.Matched,
                    new DateTimeOffset(2026, 8, 16, 10, 53, 18, TimeSpan.Zero),
                    ManifestFrame.From(new Matrix3x2(1f, -0.0002f, -0.0002f, 1f, -6.6532f, 25.654f))),
                new ManifestFrame(@"C:\temp\comet-stack\LIGHT\b.fits", "112233", FrameFate.SkippedTooFewStars,
                    new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero), null),
                new ManifestFrame(@"C:\temp\comet-stack\LIGHT\c.fits", "445566", FrameFate.SkippedQualityReject,
                    new DateTimeOffset(2026, 8, 16, 11, 1, 0, TimeSpan.Zero),
                    ManifestFrame.From(Matrix3x2.Identity)),
            ]);

        [Fact]
        public async Task ARoundTripPreservesEveryPinnedFact()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tw-manifest-{Guid.NewGuid():N}.json");
            try
            {
                var original = Sample();
                await original.WriteAsync(path, TestContext.Current.CancellationToken);
                var read = await StackManifest.TryReadAsync(path, TestContext.Current.CancellationToken);

                read.ShouldNotBeNull();
                read.SchemaVersion.ShouldBe(StackManifest.CurrentSchemaVersion);
                read.ReferencePath.ShouldBe(original.ReferencePath);
                read.ReferenceDigest.ShouldBe(original.ReferenceDigest);
                read.SnrMin.ShouldBe(original.SnrMin);
                read.MinStars.ShouldBe(original.MinStars);
                read.Frames.Length.ShouldBe(4);

                // The transform must survive to float precision: it IS the reason artifact 3 exists,
                // and a rounded one silently offsets a whole layer.
                var a = read.Frames[1];
                a.Fate.ShouldBe(FrameFate.Matched);
                var m = a.AsMatrix();
                m.ShouldNotBeNull();
                m.Value.M31.ShouldBe(-6.6532f);
                m.Value.M32.ShouldBe(25.654f);
                m.Value.M12.ShouldBe(-0.0002f);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>A frame that did not match carries no transform, and must not be silently handed
        /// an identity one: identity is a real answer (it is what the reference gets) and would stack
        /// an unregistered frame at the canvas origin.</summary>
        [Fact]
        public async Task AnUnmatchedFrameHasNoTransform()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tw-manifest-{Guid.NewGuid():N}.json");
            try
            {
                await Sample().WriteAsync(path, TestContext.Current.CancellationToken);
                var read = await StackManifest.TryReadAsync(path, TestContext.Current.CancellationToken);

                var tooFew = read!.Frames[2];
                tooFew.Fate.ShouldBe(FrameFate.SkippedTooFewStars);
                tooFew.StarTransform.ShouldBeNull();
                tooFew.AsMatrix().ShouldBeNull();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>Selection is by DIGEST and only matched frames are offered. A quality-rejected
        /// frame keeps its transform in the file (it was solved) but must never be selected, or the
        /// second layer includes a frame the first threw away and the two differ in depth and noise.</summary>
        [Fact]
        public void OnlyMatchedFramesAreSelectableAndTheyAreKeyedByDigest()
        {
            var byDigest = Sample().MatchedByDigest();

            byDigest.Count.ShouldBe(2);
            byDigest.ShouldContainKey("AABBCC");
            byDigest.ShouldContainKey("DDEEFF");
            byDigest.ShouldNotContainKey("112233");  // too few stars
            byDigest.ShouldNotContainKey("445566");  // quality reject, despite having a transform
        }

        /// <summary>Fates serialise as NAMES. This sidecar's justification is that a human reads it,
        /// and a bare 0 defeats that; it also makes the format reorder-proof, since renumbering
        /// FrameFate would otherwise change what every manifest already on disk means.</summary>
        [Fact]
        public async Task FatesAreWrittenAsNamesNotNumbers()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tw-manifest-{Guid.NewGuid():N}.json");
            try
            {
                await Sample().WriteAsync(path, TestContext.Current.CancellationToken);
                var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

                json.ShouldContain("\"Matched\"");
                json.ShouldContain("\"SkippedTooFewStars\"");
                json.ShouldContain("\"SkippedQualityReject\"");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>The digest is over the DATA section, so a HEADER edit must not change it. This is
        /// the 2026-08-25 SITEELEV amendment in miniature: it rewrote 525 headers and changed every
        /// mtime without touching a pixel, and an mtime or whole-file key would have invalidated the
        /// lot.</summary>
        [Fact]
        public void AHeaderEditDoesNotChangeTheDataDigest()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"tw-digest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var a = Path.Combine(dir, "a.fits");
                var b = Path.Combine(dir, "b.fits");
                // Same 2 x 2 Int16 data unit; headers differ only in a SITEELEV card's value.
                File.WriteAllBytes(a, MinimalFits(siteElev: "120.0"));
                File.WriteAllBytes(b, MinimalFits(siteElev: " 74.0"));

                var da = StackManifest.DigestData(a);
                var db = StackManifest.DigestData(b);

                da.ShouldNotBeEmpty();
                da.ShouldBe(db);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>Two frames whose PIXELS differ must digest differently, or the manifest would
        /// happily hand one frame's transform to another.</summary>
        [Fact]
        public void DifferentPixelsDigestDifferently()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"tw-digest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var a = Path.Combine(dir, "a.fits");
                var b = Path.Combine(dir, "b.fits");
                File.WriteAllBytes(a, MinimalFits(siteElev: "120.0", firstPixel: 1));
                File.WriteAllBytes(b, MinimalFits(siteElev: "120.0", firstPixel: 2));

                StackManifest.DigestData(a).ShouldNotBe(StackManifest.DigestData(b));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>A 2 x 2 Int16 FITS: one 2880-byte header block plus one 2880-byte data block.</summary>
        private static byte[] MinimalFits(string siteElev, short firstPixel = 1)
        {
            static string Card(string key, string value) => (key.PadRight(8) + "= " + value.PadLeft(20)).PadRight(80);
            var header =
                Card("SIMPLE", "T") +
                Card("BITPIX", "16") +
                Card("NAXIS", "2") +
                Card("NAXIS1", "2") +
                Card("NAXIS2", "2") +
                Card("SITEELEV", siteElev) +
                "END".PadRight(80);
            var block = new byte[5760];
            System.Text.Encoding.ASCII.GetBytes(header.PadRight(2880), block);
            for (var i = header.Length; i < 2880; i++) block[i] = (byte)' ';
            System.Text.Encoding.ASCII.GetBytes(header, 0, header.Length, block, 0);
            // Big-endian Int16 samples: firstPixel then 2, 3, 4.
            short[] pixels = [firstPixel, 2, 3, 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                block[2880 + i * 2] = (byte)(pixels[i] >> 8);
                block[2880 + i * 2 + 1] = (byte)(pixels[i] & 0xFF);
            }
            return block;
        }
    }
}
