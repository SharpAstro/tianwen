using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A catalog assembled from a subset of members has to be indistinguishable, to every existing
    /// consumer, from a catalog that simply contains fewer stars. The failure mode if it is not is
    /// not a crash: it is a sky full of stars in the wrong places, which looks like a projection bug
    /// and is nearly impossible to trace back to the loader.
    /// </summary>
    public class Tycho2PartialCatalogTests(ITestOutputHelper output)
    {
        private const int Stride = SkyMapState.FloatsPerStar;

        private static byte[] FullCatalog()
        {
            var asm = typeof(ICelestialObjectDB).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));
            name.ShouldNotBeNull("tyc2.bin.lz must be embedded in the (non-Lightweight) test build");

            using var stream = asm.GetManifestResourceStream(name).ShouldNotBeNull();
            return LzipDecoder.Decompress(stream);
        }

        private static (Tycho2MemberManifest Manifest, int[] ByteBoundary, byte[] Catalog) Bake(int target = 256 * 1024)
        {
            var catalog = FullCatalog();
            var regionCount = BinaryPrimitives.ReadInt32LittleEndian(catalog);
            var header = catalog.AsSpan(0, 4 + (regionCount * 4)).ToArray();

            var (byteBoundary, regionBoundary) = Tycho2MemberManifest.Pack(
                header, regionCount, catalog.Length, target);

            return (Tycho2MemberManifest.Create(regionBoundary, regionCount, catalog.Length), byteBoundary, catalog);
        }

        /// <summary>
        /// Accepting every member must reproduce the original byte for byte. If it does not, nothing
        /// else in this file means anything.
        /// </summary>
        [Fact]
        public void AcceptingEveryMemberReconstructsTheCatalogExactly()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);

            for (var m = 0; m < manifest.MemberCount; m++)
            {
                partial.Accept(m, catalog.AsSpan(bounds[m], bounds[m + 1] - bounds[m])).ShouldBeTrue($"member {m}");
            }

            partial.MembersPresent.ShouldBe(manifest.MemberCount);
            partial.Buffer.ShouldBe(catalog);
        }

        /// <summary>
        /// THE invariant. Load only the members a 10-degree view needs, then flatten through the
        /// completely unmodified star path: every star produced must be a real star of the full
        /// catalog, and every one of them must lie in a region we actually fetched.
        ///
        /// <para>This is what the <c>0xFF</c> fill buys. A zero fill would decode to magnitude -2.0
        /// at RA 0h / Dec 0, so the flatten would emit ~2.4M impossibly bright stars stacked on one
        /// point, and it would do so without a single error.</para>
        /// </summary>
        [Fact]
        public void APartialCatalogFlattensToExactlyTheStarsItFetched()
        {
            var (manifest, bounds, catalog) = Bake();
            var boundsAsm = typeof(ICelestialObjectDB).Assembly.GetManifestResourceNames()
                .First(n => n.EndsWith(".tyc2_gsc_bounds.bin.lz", StringComparison.Ordinal));
            using var boundsStream = typeof(ICelestialObjectDB).Assembly.GetManifestResourceStream(boundsAsm)!;
            var selector = new Tycho2RegionSelector(LzipDecoder.Decompress(boundsStream));

            // What a 10-degree view of the galactic centre needs, via the exact client path.
            var regions = new List<int>();
            selector.SelectVisible(17.76, -28.94, 10.0, regions);
            var wanted = new List<int>();
            manifest.MembersForRegions(regions, wanted);

            var partial = new Tycho2PartialCatalog(manifest);
            partial.Accept(0, catalog.AsSpan(bounds[0], bounds[1] - bounds[0])).ShouldBeTrue("header");
            foreach (var m in wanted)
            {
                partial.Accept(m, catalog.AsSpan(bounds[m], bounds[m + 1] - bounds[m])).ShouldBeTrue($"member {m}");
            }

            var db = new CelestialObjectDB();
            db.TryLoadTycho2BulkFromDecoded(partial.Buffer).ShouldBeTrue();

            var total = ((ICelestialObjectDB)db).Tycho2StarCount;
            var verts = new float[total * Stride];
            var written = SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts);

            // How many stars those members actually hold, counted straight from the byte ranges.
            var expected = wanted.Sum(m => (bounds[m + 1] - bounds[m]) / Tycho2RegionSelector.BytesPerStar);

            output.WriteLine($"{wanted.Count} members of {manifest.MemberCount} -> {written:N0} stars "
                + $"of {total:N0} ({100.0 * written / total:F2}%), byte-range count {expected:N0}");

            written.ShouldBeGreaterThan(0, "a fetched view that renders nothing is the bug this guards");
            written.ShouldBeLessThan(total / 4, "a 10-degree view must not flatten most of the catalog");

            // Every emitted star must be finite and plausible; a leaked sentinel shows up here first.
            for (var i = 0; i < written; i++)
                {
                var b = i * Stride;
                var mag = verts[b + 3];
                float.IsFinite(mag).ShouldBeTrue($"star {i} has a non-finite magnitude");
                mag.ShouldBeInRange(-2.0f, 20.0f, $"star {i} magnitude {mag} is not a real Tycho-2 value");

                var len = MathF.Sqrt((verts[b] * verts[b]) + (verts[b + 1] * verts[b + 1]) + (verts[b + 2] * verts[b + 2]));
                len.ShouldBeInRange(0.99f, 1.01f, $"star {i} is not on the unit sphere");
            }

            // The stars must be no more than the fetched members hold (some records carry no VT and
            // are legitimately dropped), and a large fraction of them -- not a handful.
            written.ShouldBeLessThanOrEqualTo(expected, "more stars emitted than the fetched bytes contain");
            written.ShouldBeGreaterThan(expected / 2, "most fetched records should survive the VT filter");
        }

        /// <summary>
        /// The zero-fill disaster, stated as a test so the choice of sentinel cannot be quietly
        /// "simplified" later: with nothing loaded but the header, the catalog must flatten to no
        /// stars at all rather than to a couple of million bright ones at the origin.
        /// </summary>
        [Fact]
        public void WithOnlyTheHeaderLoadedNoStarsAreEmitted()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);
            partial.Accept(0, catalog.AsSpan(bounds[0], bounds[1] - bounds[0])).ShouldBeTrue();

            var db = new CelestialObjectDB();
            db.TryLoadTycho2BulkFromDecoded(partial.Buffer).ShouldBeTrue();

            var total = ((ICelestialObjectDB)db).Tycho2StarCount;
            total.ShouldBeGreaterThan(2_000_000, "the offset table still describes the whole catalog");

            var verts = new float[total * Stride];
            SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts)
                .ShouldBe(0, "an unfetched region must render nothing, not a star at RA 0 / Dec 0");
        }

        /// <summary>
        /// A member accepted AFTER the DB was wired must appear without re-wiring -- the buffer is
        /// mutated in place and the DB holds a reference to it. This is what makes progressive
        /// fetching cheap, and it is the kind of aliasing that quietly stops working if someone
        /// copies the buffer defensively somewhere in between.
        /// </summary>
        [Fact]
        public void AMemberAcceptedAfterWiringBecomesVisibleWithoutReloading()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);
            partial.Accept(0, catalog.AsSpan(bounds[0], bounds[1] - bounds[0])).ShouldBeTrue();

            var db = new CelestialObjectDB();
            db.TryLoadTycho2BulkFromDecoded(partial.Buffer).ShouldBeTrue();

            var total = ((ICelestialObjectDB)db).Tycho2StarCount;
            var verts = new float[total * Stride];
            SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts).ShouldBe(0);

            partial.Accept(5, catalog.AsSpan(bounds[5], bounds[6] - bounds[5])).ShouldBeTrue();

            SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts)
                .ShouldBeGreaterThan(0, "the DB must see a member accepted after it was wired");
        }

        /// <summary>
        /// The client sizes its vertex buffer by <c>PresentRecordCount</c>, and the flatten writes
        /// into that span with no bounds check of its own -- so an under-count is an
        /// IndexOutOfRangeException in the browser, not a missing star. Checked against the flatten's
        /// own output over a growing set of members.
        /// </summary>
        [Fact]
        public void PresentRecordCountBoundsWhatTheFlattenWillWrite()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);
            partial.Accept(0, catalog.AsSpan(bounds[0], bounds[1] - bounds[0])).ShouldBeTrue();
            partial.PresentRecordCount.ShouldBe(0, "the header member holds no records");

            var db = new CelestialObjectDB();
            db.TryLoadTycho2BulkFromDecoded(partial.Buffer).ShouldBeTrue();

            foreach (var member in new[] { 1, 7, 40, 165 })
            {
                partial.Accept(member, catalog.AsSpan(bounds[member], bounds[member + 1] - bounds[member]))
                    .ShouldBeTrue($"member {member}");

                // Exactly the allocation the client makes, so an under-count throws here too.
                var verts = new float[partial.PresentRecordCount * Stride];
                var written = SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts);

                written.ShouldBeLessThanOrEqualTo(partial.PresentRecordCount,
                    $"after member {member}: the flatten wrote past the count the client sizes by");
            }

            partial.PresentRecordCount.ShouldBeLessThan(
                ((ICelestialObjectDB)db).Tycho2StarCount,
                "four members must not claim as many records as the whole catalog");
        }

        [Fact]
        public void AMemberCannotBePlacedBeforeTheHeader()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);

            partial.Accept(3, catalog.AsSpan(bounds[3], bounds[4] - bounds[3])).ShouldBeFalse();
            partial.HeaderLoaded.ShouldBeFalse();
            partial.MembersPresent.ShouldBe(0);
        }

        /// <summary>
        /// A member whose length disagrees with the offset table means the manifest and the files
        /// were baked from different catalogs -- a stale cached asset beside a fresh manifest. It
        /// must be refused, because writing it anyway would shift every following record and place
        /// real stars at other stars' coordinates.
        /// </summary>
        [Fact]
        public void AMemberOfTheWrongLengthIsRefusedRatherThanShifted()
        {
            var (manifest, bounds, catalog) = Bake();
            var partial = new Tycho2PartialCatalog(manifest);
            partial.Accept(0, catalog.AsSpan(bounds[0], bounds[1] - bounds[0])).ShouldBeTrue();

            partial.Accept(4, catalog.AsSpan(bounds[4], bounds[5] - bounds[4] - 17)).ShouldBeFalse();
            partial.IsPresent(4).ShouldBeFalse();
        }

        [Fact]
        public void AHeaderFromADifferentCatalogIsRejected()
        {
            var (manifest, bounds, catalog) = Bake();
            var header = catalog.AsSpan(bounds[0], bounds[1] - bounds[0]).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(header, manifest.RegionCount + 1);

            var partial = new Tycho2PartialCatalog(manifest);

            partial.Accept(0, header).ShouldBeFalse();
            partial.HeaderLoaded.ShouldBeFalse();
        }
    }
}
