using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The viability gate for the region-aligned bake in
    /// <c>docs/plans/web-tycho2.md</c>: 9537 GSC regions over 41253 square degrees is ~4.3 square
    /// degrees each, so a wide view touches many hundreds of them. Fetching the sky you are looking at
    /// is only affordable if those regions arrive as a modest number of CONTIGUOUS runs, because
    /// regions are ordered by <c>tyc1</c>, which runs in declination bands.
    ///
    /// <para>The plan says to compute that distribution before writing any fetch code, and this is it:
    /// no browser, no network, just the shipped bounds table and the catalog's own offset table. The
    /// numbers are logged rather than pinned -- there is no correct value to assert, only a shape that
    /// either supports the design or kills it.</para>
    ///
    /// <para><b>The correctness tests beside it are the load-bearing half.</b> A selector that quietly
    /// under-selects produces beautiful numbers and a sky with holes in it, and the report alone could
    /// never tell the difference. So the geometry is checked against actual star positions and against
    /// the shipped <see cref="Tycho2RaDecIndex"/>, not just eyeballed.</para>
    /// </summary>
    public class Tycho2RegionSelectorTests(ITestOutputHelper output)
    {
        /// <summary>Named sky directions chosen for how differently they stress the layout: the
        /// galactic plane is where the stars are, the poles are where the RA bands converge, and RA 0h
        /// is the wrap seam.</summary>
        public static readonly (string Name, double RaHours, double DecDeg)[] Views =
        [
            ("galactic centre", 17.76, -28.94),
            ("galactic anticentre", 5.76, 28.94),
            ("north galactic pole", 12.86, 27.13),
            ("Orion", 5.58, -1.20),
            ("RA 0h seam", 0.00, 0.00),
            ("north celestial pole", 0.00, 89.90),
            ("south celestial pole", 0.00, -89.90),
        ];

        private static readonly double[] Fovs = [60.0, 30.0, 10.0, 2.0, 0.5];

        private static byte[] LoadResource(string suffix)
        {
            var asm = Tycho2TestCatalog.AssemblyWith(suffix);
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            name.ShouldNotBeNull($"{suffix} must be embedded in the (non-Lightweight) test build");

            using var stream = asm.GetManifestResourceStream(name).ShouldNotBeNull();
            return LzipDecoder.Decompress(stream);
        }

        private static Tycho2RegionSelector Selector() => new(LoadResource(".tyc2_gsc_bounds.bin.lz"));

        private static List<int> Select(Tycho2RegionSelector selector, double ra, double dec, double radius)
        {
            var regions = new List<int>();
            selector.SelectVisible(ra, dec, radius, regions);
            return regions;
        }

        /// <summary>Maximal runs of consecutive region indices, i.e. the ranges a client would ask for
        /// if it refused to fetch a single unwanted byte.</summary>
        private static int RunCount(IReadOnlyList<int> regions)
        {
            if (regions.Count == 0) return 0;
            var runs = 1;
            for (var i = 1; i < regions.Count; i++)
            {
                if (regions[i] != regions[i - 1] + 1) runs++;
            }
            return runs;
        }

        /// <summary>
        /// THE GATE. Reports, for each view and field of view, how many regions are visible, how many
        /// contiguous runs they form, and what the request/byte trade looks like as gap-merging is
        /// loosened. Read the runs column: a few dozen means the design works, hundreds means it does
        /// not and the plan needs a different answer.
        /// </summary>
        [Fact]
        public void ReportTheRegionRunDistributionAcrossViewsAndZooms()
        {
            var selector = Selector();
            var catalog = LoadResource(".tyc2.bin.lz");
            var header = catalog.AsSpan(0, 4 + selector.RegionCount * 4).ToArray();
            var total = catalog.Length;

            output.WriteLine($"catalog {total:N0} B, {selector.RegionCount:N0} GSC regions");
            output.WriteLine("");
            output.WriteLine($"{"view",-22} {"FOV",5} {"regions",8} {"runs",6} "
                + $"{"reqs@0",7} {"MB@0",7} {"reqs@256K",10} {"MB@256K",8} {"reqs@1M",8} {"MB@1M",7}");

            foreach (var (name, ra, dec) in Views)
            {
                foreach (var fov in Fovs)
                {
                    var regions = Select(selector, ra, dec, fov);
                    var runs = RunCount(regions);

                    var exact = Tycho2RegionSelector.ToByteRanges(regions, header, total, 0);
                    var merged256 = Tycho2RegionSelector.ToByteRanges(regions, header, total, 256 * 1024);
                    var merged1M = Tycho2RegionSelector.ToByteRanges(regions, header, total, 1024 * 1024);

                    static double Mb(List<Tycho2RegionSelector.ByteRange> r)
                        => r.Sum(x => (long)x.Length) / (1024.0 * 1024.0);

                    output.WriteLine($"{name,-22} {fov,5:0.#} {regions.Count,8:N0} {runs,6:N0} "
                        + $"{exact.Count,7:N0} {Mb(exact),7:F2} {merged256.Count,10:N0} {Mb(merged256),8:F2} "
                        + $"{merged1M.Count,8:N0} {Mb(merged1M),7:F2}");
                }
            }

            // The one thing worth failing on: a view must never need every region, or the whole idea of
            // fetching a subset is void. Deliberately loose -- this is an instrument, not a pin.
            var wide = Select(selector, 17.76, -28.94, 60.0);
            wide.Count.ShouldBeGreaterThan(0, "a populated view that selects nothing means the geometry is inverted");
            wide.Count.ShouldBeLessThan(selector.RegionCount, "a 60-degree view that needs the whole sky is not a subset");
        }

        /// <summary>
        /// The invariant that decides whether any of this is usable: every star inside the view cone
        /// lives in a selected region. Checked against the real 2.5M-star catalog by walking the
        /// records region by region -- an under-selecting bounding test is otherwise invisible until
        /// the sky renders with holes in it.
        /// </summary>
        [Theory]
        [InlineData(17.76, -28.94, 10.0)]   // dense galactic plane
        [InlineData(0.00, 89.90, 20.0)]     // pole: RA bands converge, wrap in every direction
        [InlineData(0.00, 0.00, 5.0)]       // the RA 0h seam
        public void EveryStarInsideTheConeLivesInASelectedRegion(double raHours, double decDeg, double radiusDeg)
        {
            var selector = Selector();
            var catalog = LoadResource(".tyc2.bin.lz");
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var selected = Select(selector, raHours, decDeg, radiusDeg).ToHashSet();

            var (cx, cy, cz) = UnitVec(raHours * 15.0, decDeg);
            var cosRadius = Math.Cos(double.DegreesToRadians(radiusDeg));

            var inCone = 0;
            var missed = 0;
            var firstMiss = -1;

            for (var region = 0; region < streamCount; region++)
            {
                var start = BinaryPrimitives.ReadInt32LittleEndian(catalog.AsSpan((region + 1) * 4));
                var end = region + 1 < streamCount
                    ? BinaryPrimitives.ReadInt32LittleEndian(catalog.AsSpan((region + 2) * 4))
                    : catalog.Length;

                for (var at = start; at + Tycho2RegionSelector.BytesPerStar <= end;
                     at += Tycho2RegionSelector.BytesPerStar)
                {
                    var starRa = BinaryPrimitives.ReadSingleLittleEndian(catalog.AsSpan(at + 3, 4));
                    var starDec = BinaryPrimitives.ReadSingleLittleEndian(catalog.AsSpan(at + 7, 4));
                    var (sx, sy, sz) = UnitVec(starRa * 15.0, starDec);

                    if (cx * sx + cy * sy + cz * sz < cosRadius)
                    {
                        continue;
                    }

                    inCone++;
                    if (!selected.Contains(region))
                    {
                        missed++;
                        if (firstMiss < 0) firstMiss = region;
                    }
                }
            }

            output.WriteLine($"RA {raHours}h Dec {decDeg} r={radiusDeg}: {inCone:N0} stars in cone, "
                + $"{selected.Count:N0} regions selected, {missed:N0} missed");

            inCone.ShouldBeGreaterThan(0, "the probe must actually cover stars, or it proves nothing");
            missed.ShouldBe(0, $"star in the cone whose region {firstMiss} was not selected");
        }

        /// <summary>
        /// Cross-check against the index that already ships: whatever
        /// <see cref="Tycho2RaDecIndex.GetOverlappingRegions"/> reports for the cell under a point, a
        /// selection generous enough to cover that cell must contain. Two independent readings of the
        /// same bounds table agreeing is what makes a bug in either of them visible.
        /// </summary>
        [Fact]
        public void TheSelectionCoversWhatTheShippedSpatialIndexReportsForTheSameCell()
        {
            var selector = Selector();
            var catalog = LoadResource(".tyc2.bin.lz");
            var bounds = LoadResource(".tyc2_gsc_bounds.bin.lz");
            var streamCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var index = new Tycho2RaDecIndex(catalog, streamCount, bounds);

            // A cell is 1 degree of Dec by 1/15 h of RA, so its diagonal is just over a degree; 1.5
            // degrees covers it with room to spare without swamping the comparison.
            foreach (var (name, ra, dec) in Views)
            {
                var shipped = index.GetOverlappingRegions(ra, dec);
                if (shipped is null)
                {
                    continue;
                }

                var mine = Select(selector, ra, dec, 1.5).ToHashSet();
                foreach (var tyc1 in shipped)
                {
                    mine.ShouldContain(tyc1 - 1, $"{name}: region tyc1={tyc1} is in the shipped index's cell but was not selected");
                }
            }
        }

        [Fact]
        public void AFullSkyRadiusSelectsEveryNonEmptyRegion()
        {
            var selector = Selector();

            var all = Select(selector, 0.0, 0.0, 180.0);

            all.Count.ShouldBeGreaterThan(9000, "only the baker's empty-region sentinels may be absent");
            all.Count.ShouldBeLessThanOrEqualTo(selector.RegionCount);
            for (var i = 1; i < all.Count; i++)
            {
                all[i].ShouldBeGreaterThan(all[i - 1], "selection must be ascending for run coalescing to work");
            }
        }

        /// <summary>
        /// Merging must be monotone in the gap allowance -- more slack can only mean fewer requests and
        /// more bytes. It is the property the whole trade-off table rests on, and an off-by-one in the
        /// merge condition breaks it while still producing a plausible-looking row.
        /// </summary>
        [Fact]
        public void LooseningTheGapNeverAddsRequestsAndNeverRemovesBytes()
        {
            var selector = Selector();
            var catalog = LoadResource(".tyc2.bin.lz");
            var header = catalog.AsSpan(0, 4 + selector.RegionCount * 4).ToArray();
            var regions = Select(selector, 17.76, -28.94, 10.0);

            var previousCount = int.MaxValue;
            var previousBytes = 0L;
            foreach (var gap in new[] { 0, 4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024 })
            {
                var ranges = Tycho2RegionSelector.ToByteRanges(regions, header, catalog.Length, gap);
                var bytes = ranges.Sum(r => (long)r.Length);

                ranges.Count.ShouldBeLessThanOrEqualTo(previousCount, $"gap {gap}");
                bytes.ShouldBeGreaterThanOrEqualTo(previousBytes, $"gap {gap}");

                // Ranges must stay disjoint and ascending, or a client would fetch overlapping bytes.
                for (var i = 1; i < ranges.Count; i++)
                {
                    ranges[i].Start.ShouldBeGreaterThan(ranges[i - 1].End, $"gap {gap}, range {i}");
                }

                previousCount = ranges.Count;
                previousBytes = bytes;
            }
        }

        private static (double X, double Y, double Z) UnitVec(double raDeg, double decDeg)
        {
            var ra = double.DegreesToRadians(raDeg);
            var dec = double.DegreesToRadians(decDeg);
            var cosDec = Math.Cos(dec);
            return (cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
        }
    }
}
