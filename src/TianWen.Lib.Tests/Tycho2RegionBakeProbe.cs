using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Prices the region-aligned multi-member bake in <c>docs/plans/web-tycho2.md</c>: what the split
    /// costs in compression ratio, and what a view then actually has to download.
    ///
    /// <para><b>Why the existing 0.49% figure cannot be reused.</b> That was measured at EIGHT
    /// members. <see cref="Tycho2RegionSelectorTests"/> shows a view's regions arrive as runs
    /// averaging ~143 KB at the default field and ~40 KB when zoomed, so members have to be in that
    /// size class to be addressable at all -- hundreds of them, not eight. Every member resets the
    /// LZMA dictionary (<c>LzipEncoder</c> caps it to the member's own length), so the penalty grows
    /// as members shrink, by an amount eight members cannot predict.</para>
    ///
    /// <para><b>Requests are FILES, not ranges.</b> Byte ranges are unusable on both hosts -- GitHub
    /// Pages answers 206 over the gzip representation, Cloudflare Pages ignores Range entirely -- so
    /// consecutive members cannot be coalesced into one GET and the knee sits higher than a
    /// range-based delivery would put it. See the plan for the measurements.</para>
    ///
    /// <para>Env-gated (<c>TIANWEN_TYC2_BAKE_PROBE=1</c>) like the E2E probes: it recompresses 43.5 MB
    /// several times over and is an instrument, not an assertion. The two invariant tests beside it
    /// are NOT gated -- they are cheap and they are what keeps the desktop safe.</para>
    /// </summary>
    public class Tycho2RegionBakeProbe(ITestOutputHelper output)
    {
        // Weighted toward the 128 KB - 1 MB band: once members are FILES the request count stops
        // being coalescable, so the knee moves up from where a range-based delivery would put it.
        private static readonly int[] TargetMemberSizes =
            [2 << 20, 1 << 20, 512 << 10, 256 << 10, 128 << 10, 64 << 10, 32 << 10];

        [Fact]
        public void MeasureAsync()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYC2_BAKE_PROBE") == "1",
                "probe: set TIANWEN_TYC2_BAKE_PROBE=1 (recompresses the 43.5 MB catalog several times)");

            var bounds = LoadResource(".tyc2_gsc_bounds.bin.lz");
            var catalog = LoadResource(".tyc2.bin.lz");
            var selector = new Tycho2RegionSelector(bounds);
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + (streamCount * 4)).ToArray();

            var shipped = CompressedSize(catalog, 0, catalog.Length);
            output.WriteLine($"raw {catalog.Length:N0} B, {streamCount:N0} regions");
            output.WriteLine($"single member: {shipped:N0} B ({100.0 * shipped / catalog.Length:F1}% of raw)");
            output.WriteLine("");

            // The reference views: the widest default field, a mid zoom, and a deep zoom, at the
            // densest sky. A wide view is the worst case for over-fetch and the one that decides this.
            var probes = new (string Name, double Ra, double Dec, double Fov)[]
            {
                ("galactic centre 60", 17.76, -28.94, 60.0),
                ("galactic centre 10", 17.76, -28.94, 10.0),
                ("galactic centre 2", 17.76, -28.94, 2.0),
            };

            output.WriteLine($"{"target",8} {"members",8} {"total MB",9} {"vs 1-member",12}   "
                + string.Join("  ", probes.Select(p => $"{p.Name,-20}")));

            foreach (var target in TargetMemberSizes)
            {
                var (byteBoundary, regionBoundary) = Tycho2MemberManifest.Pack(
                    header, streamCount, catalog.Length, target);
                var sizes = CompressMembers(catalog, byteBoundary);
                var totalCompressed = sizes.Sum(s => (long)s);
                var manifest = Tycho2MemberManifest.Create(regionBoundary, streamCount, catalog.Length);

                var cells = new List<string>();
                foreach (var (_, ra, dec, fov) in probes)
                {
                    var regions = new List<int>();
                    selector.SelectVisible(ra, dec, fov, regions);

                    // Ask the manifest, not the byte ranges: this is the exact path the client walks,
                    // so a bug in the region-to-member mapping shows up in the measurement too.
                    var needed = new List<int>();
                    manifest.MembersForRegions(regions, needed);
                    needed.Add(0); // the header member, which every client fetches unconditionally

                    var download = needed.Sum(m => (long)sizes[m]);
                    cells.Add($"{needed.Count,4:N0}f/{ConsecutiveRuns(needed),3:N0}r "
                        + $"{download / (1024.0 * 1024.0),6:F2} MB");
                }

                output.WriteLine($"{Human(target),8} {manifest.MemberCount,8:N0} "
                    + $"{totalCompressed / (1024.0 * 1024.0),9:F2} "
                    + $"{100.0 * totalCompressed / shipped - 100.0,11:+0.0;-0.0}%   "
                    + string.Join("  ", cells));
            }

            output.WriteLine("");
            output.WriteLine("Requests are FILES (f) with the un-coalescable range count (r) beside them;");
            output.WriteLine($"bytes are COMPRESSED, against a single {shipped / (1024.0 * 1024.0):F2} MB fetch for any view.");
        }

        /// <summary>
        /// The invariant that makes members addressable at all: every boundary falls between two GSC
        /// regions, so a region is never split across a boundary a client cannot ask for half of.
        /// Free to check -- the packer needs no compression -- and it is what separates this from a
        /// plain <c>MemberSize</c> stride, which would cut wherever the byte count happened to land.
        /// </summary>
        [Fact]
        public void EveryMemberBoundaryFallsBetweenRegions()
        {
            var catalog = LoadResource(".tyc2.bin.lz");
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + (streamCount * 4)).ToArray();

            // The legal cut points: the file start, every region start (the first of which is also
            // the end of the header), and the file end. Byte 0 counts because the header is a member
            // in its own right -- a client must decode it before a record's tyc1 is knowable.
            var legal = new HashSet<int> { 0, catalog.Length };
            for (var region = 0; region < streamCount; region++)
            {
                legal.Add(RegionStart(header, region));
            }

            var (byteBoundary, regionBoundary) = Tycho2MemberManifest.Pack(
                header, streamCount, catalog.Length, 64 << 10);

            byteBoundary.Length.ShouldBeGreaterThan(3, "a 64 KB target over 43 MB must produce many members");
            regionBoundary.Length.ShouldBe(byteBoundary.Length, "one region boundary per byte boundary");

            foreach (var at in byteBoundary)
            {
                legal.ShouldContain(at, $"member boundary at byte {at} splits a region");
            }

            // Contiguous and gapless: the members must RECONSTRUCT the file, not merely cover it.
            byteBoundary[0].ShouldBe(0);
            byteBoundary[^1].ShouldBe(catalog.Length);
            for (var i = 1; i < byteBoundary.Length; i++)
            {
                byteBoundary[i].ShouldBeGreaterThan(byteBoundary[i - 1], $"empty or inverted member {i - 1}");
            }

            // The two boundary arrays must agree: the byte a member starts at IS its first region's
            // start. Member 0 is the header and holds no regions, hence the empty first range.
            regionBoundary[0].ShouldBe(0);
            regionBoundary[1].ShouldBe(0, "member 0 is the header and holds no regions");
            regionBoundary[^1].ShouldBe(streamCount);
            for (var i = 1; i < byteBoundary.Length - 1; i++)
            {
                byteBoundary[i].ShouldBe(RegionStart(header, regionBoundary[i]),
                    $"member {i} starts at a byte that is not region {regionBoundary[i]}'s start");
            }
        }

        /// <summary>
        /// The invariant the desktop rests on: a region-aligned multi-member file decodes to bytes
        /// identical to the single-member one, so <c>Tycho2RaDecIndex</c>, the offset table and every
        /// record offset are untouched by the bake. Bounded to the first few MB so it can run in CI --
        /// the property is per-member, so more members would re-prove the same thing more slowly.
        /// (<c>tools/bake-tycho2</c> runs the unbounded version and refuses to write if it fails.)
        /// </summary>
        [Fact]
        public void ARegionAlignedMultiMemberFileDecodesToTheIdenticalBytes()
        {
            var catalog = LoadResource(".tyc2.bin.lz");
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + (streamCount * 4)).ToArray();

            var (byteBoundary, _) = Tycho2MemberManifest.Pack(header, streamCount, catalog.Length, 64 << 10);
            var take = byteBoundary.TakeWhile(b => b <= 3 << 20).ToArray();
            take.Length.ShouldBeGreaterThan(5, "the slice must span several members to prove anything");

            using var baked = new MemoryStream();
            for (var i = 1; i < take.Length; i++)
            {
                baked.Write(LzipEncoder.Compress(catalog.AsSpan(take[i - 1], take[i] - take[i - 1])));
            }

            LzipDecoder.Decompress(baked.ToArray()).ShouldBe(catalog.AsSpan(0, take[^1]).ToArray());
        }

        private static int[] CompressMembers(byte[] catalog, int[] byteBoundary)
        {
            var sizes = new int[byteBoundary.Length - 1];
            Parallel.For(0, sizes.Length, i =>
            {
                sizes[i] = CompressedSize(catalog, byteBoundary[i], byteBoundary[i + 1] - byteBoundary[i]);
            });
            return sizes;
        }

        private static int CompressedSize(byte[] data, int start, int length)
            => LzipEncoder.Compress(data.AsSpan(start, length)).Length;

        /// <summary>Maximal runs of consecutive indices: what the set WOULD have cost as byte ranges,
        /// kept beside the file count so the gap between the two stays visible.</summary>
        private static int ConsecutiveRuns(IEnumerable<int> indices)
        {
            var sorted = indices.Distinct().Order().ToArray();
            if (sorted.Length == 0) return 0;

            var runs = 1;
            for (var i = 1; i < sorted.Length; i++)
            {
                if (sorted[i] != sorted[i - 1] + 1) runs++;
            }
            return runs;
        }

        private static int RegionStart(ReadOnlySpan<byte> header, int gscIdx)
            => BinaryPrimitives.ReadInt32LittleEndian(header[((gscIdx + 1) * 4)..]);

        private static string Human(int bytes)
            => bytes >= 1 << 20 ? $"{bytes >> 20} MB" : $"{bytes >> 10} KB";

        private static byte[] LoadResource(string suffix)
        {
            var asm = typeof(ICelestialObjectDB).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            name.ShouldNotBeNull($"{suffix} must be embedded in the (non-Lightweight) test build");

            using var stream = asm.GetManifestResourceStream(name).ShouldNotBeNull();
            return LzipDecoder.Decompress(stream);
        }
    }
}
