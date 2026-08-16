using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// <para><b>Why the existing 0.49% figure cannot be reused.</b> That was measured at EIGHT members.
    /// <see cref="Tycho2RegionSelectorTests"/> shows a view's regions arrive as runs averaging ~143 KB
    /// at the default field and ~40 KB when zoomed, so members have to be in that size class to be
    /// addressable at all -- hundreds or thousands of them, not eight. Every member resets the LZMA
    /// dictionary (<c>LzipEncoder</c> caps it to the member's own length), so the penalty grows as
    /// members shrink, and extrapolating from eight would understate it by an unknown factor.</para>
    ///
    /// <para><b>Members are region-aligned, never size-aligned.</b> A member that splits a GSC region
    /// puts half a region behind a boundary a client cannot address, so the greedy packer below only
    /// ever cuts between regions and the nominal size is a target rather than a stride. The 37 KB
    /// header gets a member to itself: a client must decode it before it can know which other members
    /// it wants, so it is the one member every visit fetches.</para>
    ///
    /// <para>Env-gated (<c>TIANWEN_TYC2_BAKE_PROBE=1</c>) like the E2E probes: it recompresses 43.5 MB
    /// several times over and is an instrument, not an assertion.</para>
    /// </summary>
    public class Tycho2RegionBakeProbe(ITestOutputHelper output)
    {
        private static readonly int[] TargetMemberSizes =
            [4 << 20, 1 << 20, 256 << 10, 64 << 10, 32 << 10, 16 << 10];

        /// <summary>Uncompressed byte span of one member, plus the region index it starts at.</summary>
        private readonly record struct Member(int Start, int End, int FirstRegion)
        {
            internal int Length => End - Start;
        }

        [Fact]
        public void MeasureAsync()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYC2_BAKE_PROBE") == "1",
                "probe: set TIANWEN_TYC2_BAKE_PROBE=1 (recompresses the 43.5 MB catalog several times)");

            var bounds = LoadResource(".tyc2_gsc_bounds.bin.lz");
            var catalog = LoadResource(".tyc2.bin.lz");
            var selector = new Tycho2RegionSelector(bounds);
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + streamCount * 4).ToArray();

            var shipped = CompressedSize(catalog, 0, catalog.Length);
            output.WriteLine($"raw {catalog.Length:N0} B, {streamCount:N0} regions");
            output.WriteLine($"single member (what ships today): {shipped:N0} B "
                + $"({100.0 * shipped / catalog.Length:F1}% of raw)");
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
                var members = PackRegionAligned(catalog, header, streamCount, target);
                var sizes = CompressMembers(catalog, members);
                var totalCompressed = sizes.Sum(s => (long)s);

                var cells = new List<string>();
                foreach (var (_, ra, dec, fov) in probes)
                {
                    var regions = new List<int>();
                    selector.SelectVisible(ra, dec, fov, regions);
                    var ranges = Tycho2RegionSelector.ToByteRanges(regions, header, catalog.Length, 0);

                    var needed = MembersCovering(members, ranges);
                    // Member 0 is the header, which every client fetches before it can ask for anything.
                    needed.Add(0);
                    var download = needed.Sum(m => (long)sizes[m]);

                    // Members are laid out in index order in the compressed file, so a RUN of
                    // consecutive members is one Range GET. Counting members instead of runs would
                    // over-report requests badly at small member sizes -- exactly where the split is
                    // supposed to be winning.
                    cells.Add($"{ConsecutiveRuns(needed),5:N0} req {download / (1024.0 * 1024.0),6:F2} MB");
                }

                output.WriteLine($"{Human(target),8} {members.Count,8:N0} "
                    + $"{totalCompressed / (1024.0 * 1024.0),9:F2} "
                    + $"{100.0 * totalCompressed / shipped - 100.0,11:+0.0;-0.0}%   "
                    + string.Join("  ", cells));
            }

            output.WriteLine("");
            output.WriteLine("Read the last three columns as requests + COMPRESSED bytes actually downloaded,");
            output.WriteLine($"against today's single {shipped / (1024.0 * 1024.0):F2} MB fetch for any view whatsoever.");
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
            var header = catalog.AsSpan(0, 4 + streamCount * 4).ToArray();

            // The legal cut points: the file start, the end of the header (== the first region's
            // start), every region start, and the file end. Byte 0 counts because the header is a
            // member in its own right -- a client must decode it before it can name any other member.
            var starts = new HashSet<int> { 0, catalog.Length };
            for (var region = 0; region < streamCount; region++)
            {
                starts.Add(RegionStart(header, region));
            }

            var members = PackRegionAligned(catalog, header, streamCount, 64 << 10);
            members.Count.ShouldBeGreaterThan(1, "a 64 KB target over 43 MB must produce many members");

            foreach (var member in members)
            {
                starts.ShouldContain(member.Start, $"member starting at {member.Start} splits a region");
                starts.ShouldContain(member.End, $"member ending at {member.End} splits a region");
            }

            // Contiguous and gapless: the members must reconstruct the file exactly, not merely cover it.
            members[0].Start.ShouldBe(0);
            members[^1].End.ShouldBe(catalog.Length);
            for (var i = 1; i < members.Count; i++)
            {
                members[i].Start.ShouldBe(members[i - 1].End, $"gap or overlap before member {i}");
            }
        }

        /// <summary>
        /// The invariant the desktop rests on: a region-aligned multi-member file decodes to bytes
        /// identical to the single-member one, so <c>Tycho2RaDecIndex</c>, the offset table and every
        /// record offset are untouched by the bake. Bounded to the first few MB so it can run in CI --
        /// the property is per-member, so more members would re-prove the same thing more slowly.
        /// </summary>
        [Fact]
        public void ARegionAlignedMultiMemberFileDecodesToTheIdenticalBytes()
        {
            var catalog = LoadResource(".tyc2.bin.lz");
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + streamCount * 4).ToArray();

            var members = PackRegionAligned(catalog, header, streamCount, 64 << 10)
                .TakeWhile(m => m.End <= 3 << 20).ToList();
            members.Count.ShouldBeGreaterThan(4, "the slice must span several members to prove anything");

            var slice = catalog.AsSpan(0, members[^1].End).ToArray();

            using var baked = new System.IO.MemoryStream();
            foreach (var member in members)
            {
                baked.Write(LzipEncoder.Compress(catalog.AsSpan(member.Start, member.Length)));
            }

            LzipDecoder.Decompress(baked.ToArray()).ShouldBe(slice);
        }

        /// <summary>
        /// Greedy region-aligned packing: accumulate whole regions until the member reaches its target,
        /// then cut. The header is member 0 on its own. Never splits a region, so every member holds a
        /// whole number of them and a client's range maps to whole members.
        /// </summary>
        private static List<Member> PackRegionAligned(byte[] catalog, ReadOnlySpan<byte> header, int streamCount, int target)
        {
            var members = new List<Member>();
            var headerEnd = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            members.Add(new Member(0, headerEnd, 0));

            var start = headerEnd;
            var firstRegion = 0;
            for (var region = 0; region < streamCount; region++)
            {
                var end = region + 1 < streamCount
                    ? BinaryPrimitives.ReadInt32LittleEndian(header[((region + 2) * 4)..])
                    : catalog.Length;

                if (end - start >= target)
                {
                    members.Add(new Member(start, end, firstRegion));
                    start = end;
                    firstRegion = region + 1;
                }
            }

            if (start < catalog.Length)
            {
                members.Add(new Member(start, catalog.Length, firstRegion));
            }

            return members;
        }

        private static int[] CompressMembers(byte[] catalog, List<Member> members)
        {
            var sizes = new int[members.Count];
            Parallel.For(0, members.Count, i =>
            {
                sizes[i] = CompressedSize(catalog, members[i].Start, members[i].Length);
            });
            return sizes;
        }

        private static int CompressedSize(byte[] data, int start, int length)
            => LzipEncoder.Compress(data.AsSpan(start, length)).Length;

        /// <summary>Every member index whose byte span intersects any requested range.</summary>
        private static HashSet<int> MembersCovering(List<Member> members, List<Tycho2RegionSelector.ByteRange> ranges)
        {
            var needed = new HashSet<int>();
            foreach (var range in ranges)
            {
                for (var i = 0; i < members.Count; i++)
                {
                    if (members[i].Start < range.End && members[i].End > range.Start)
                    {
                        needed.Add(i);
                    }
                }
            }
            return needed;
        }

        /// <summary>Number of maximal runs of consecutive indices, i.e. how many Range GETs the set costs.</summary>
        private static int ConsecutiveRuns(HashSet<int> indices)
        {
            var sorted = indices.Order().ToArray();
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
