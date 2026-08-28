using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The framing a client reads before it can fetch any of the sky. Small surface, but every bug in
    /// it is a wrong-sky bug rather than a crash: a mis-mapped region silently fetches the wrong
    /// member and draws stars that belong somewhere else, which no amount of looking at the render
    /// would identify as a manifest problem.
    /// </summary>
    public class Tycho2MemberManifestTests
    {
        /// <summary>Four members over ten regions; member 0 is the header and holds none.</summary>
        private static Tycho2MemberManifest Sample()
            => Tycho2MemberManifest.Create([0, 0, 3, 7, 10], regionCount: 10, rawLength: 12345);

        [Fact]
        public void RoundTripsThroughItsOwnBytes()
        {
            var manifest = Sample();

            var read = Tycho2MemberManifest.Read(manifest.Write());

            read.MemberCount.ShouldBe(manifest.MemberCount);
            read.RegionCount.ShouldBe(10);
            read.RawLength.ShouldBe(12345);
            for (var i = 0; i <= read.MemberCount; i++)
            {
                read.RegionBoundary(i).ShouldBe(manifest.RegionBoundary(i));
            }
        }

        /// <summary>
        /// The mapping, against a linear scan over every region. A binary search over boundaries is
        /// exactly the kind of code that is right for 99% of inputs and wrong at the edges, and the
        /// edges here are the first and last member of the sky.
        /// </summary>
        [Fact]
        public void MemberForRegionAgreesWithALinearScanEverywhere()
        {
            var manifest = Sample();

            for (var region = 0; region < manifest.RegionCount; region++)
            {
                var expected = -1;
                for (var m = 0; m < manifest.MemberCount; m++)
                {
                    if (manifest.RegionBoundary(m) <= region && region < manifest.RegionBoundary(m + 1))
                    {
                        expected = m;
                    }
                }

                manifest.MemberForRegion(region).ShouldBe(expected, $"region {region}");
            }
        }

        /// <summary>
        /// Out of range must answer -1, not clamp. Clamping would turn a bad region number into a
        /// plausible fetch of the first or last member -- stars drawn in the wrong place, no error.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(10)]
        [InlineData(9999)]
        public void AnOutOfRangeRegionIsRejectedRatherThanClamped(int region)
            => Sample().MemberForRegion(region).ShouldBe(-1);

        [Fact]
        public void MembersForRegionsIsAscendingAndDeduplicated()
        {
            var into = new List<int>();

            // Regions 0,1,2 share a member; 3 and 5 share the next; 8 is in the last.
            Sample().MembersForRegions([0, 1, 2, 3, 5, 8], into);

            into.ShouldBe([1, 2, 3]);
        }

        /// <summary>
        /// The header member is deliberately NOT produced by a region query: a client needs it
        /// unconditionally, so having it fall out of "what does this view need" would only create a
        /// way to forget it when the view happens to select nothing.
        /// </summary>
        [Fact]
        public void ARegionQueryNeverReturnsTheHeaderMember()
        {
            var into = new List<int>();

            Sample().MembersForRegions([0, 9], into);

            into.ShouldNotContain(0);
        }

        [Theory]
        [InlineData(0)]   // empty
        [InlineData(8)]   // magic only
        [InlineData(24)]  // prologue plus one boundary, but claims many
        public void ATruncatedManifestThrowsInsteadOfProducingAPartialSky(int length)
        {
            var bytes = Sample().Write().AsSpan(0, length).ToArray();

            Should.Throw<InvalidOperationException>(() => Tycho2MemberManifest.Read(bytes));
        }

        [Fact]
        public void AForeignFileIsRejectedByItsMagic()
        {
            var bytes = Sample().Write();
            bytes[1] = (byte)'X';

            Should.Throw<InvalidOperationException>(() => Tycho2MemberManifest.Read(bytes));
        }

        [Fact]
        public void AFutureVersionIsRejectedRatherThanMisread()
        {
            var bytes = Sample().Write();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 999);

            Should.Throw<InvalidOperationException>(() => Tycho2MemberManifest.Read(bytes));
        }

        [Fact]
        public void MemberFileNamesSortInMemberOrder()
        {
            var names = Enumerable.Range(0, 200).Select(Tycho2MemberManifest.MemberFileName).ToArray();

            names[0].ShouldBe("m0000.lz");
            names[166].ShouldBe("m0166.lz");
            names.ShouldBe([.. names.Order(StringComparer.Ordinal)], "a directory listing must be catalog order");
        }

        /// <summary>
        /// End to end on the real catalog: pack it, then check that the member a region maps to is the
        /// member whose byte span actually contains that region's records. This is the join between
        /// the two boundary arrays, and it is the one thing the client cannot check for itself.
        /// </summary>
        [Fact]
        public void OnTheRealCatalogEveryRegionMapsToTheMemberThatHoldsItsBytes()
        {
            var asm = Tycho2TestCatalog.AssemblyWith(".tyc2.bin.lz");
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));
            name.ShouldNotBeNull("tyc2.bin.lz must be embedded in the (non-Lightweight) test build");

            using var stream = asm.GetManifestResourceStream(name).ShouldNotBeNull();
            var catalog = LzipDecoder.Decompress(stream);
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + (streamCount * 4)).ToArray();

            var (byteBoundary, regionBoundary) = Tycho2MemberManifest.Pack(
                header, streamCount, catalog.Length, 256 * 1024);
            var manifest = Tycho2MemberManifest.Create(regionBoundary, streamCount, catalog.Length);

            for (var region = 0; region < streamCount; region++)
            {
                var member = manifest.MemberForRegion(region);
                member.ShouldBeGreaterThan(0, $"region {region} must not map to the header member");

                var regionStart = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan((region + 1) * 4));
                regionStart.ShouldBeGreaterThanOrEqualTo(byteBoundary[member], $"region {region}");
                regionStart.ShouldBeLessThan(byteBoundary[member + 1], $"region {region}");
            }
        }
    }
}
