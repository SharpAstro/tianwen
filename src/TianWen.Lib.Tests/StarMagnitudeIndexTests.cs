using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The magnitude-prefix cull both sky-map pipelines draw through. The invariant that matters is
    /// simple to state and expensive to get wrong: after sorting, the stars at or above a magnitude
    /// limit are exactly the first N instances, so a draw can be bounded by a count alone.
    /// </summary>
    public class StarMagnitudeIndexTests(ITestOutputHelper output)
    {
        private const int Stride = SkyMapState.FloatsPerStar;

        /// <summary>Builds a star buffer whose non-magnitude fields encode the star's identity, so a
        /// sort that moved a magnitude without its payload is detectable.</summary>
        private static float[] Buffer(params float[] magnitudes)
        {
            var verts = new float[magnitudes.Length * Stride];
            for (var i = 0; i < magnitudes.Length; i++)
            {
                var b = i * Stride;
                verts[b] = i;            // x  \
                verts[b + 1] = i * 10;   // y   > identity payload
                verts[b + 2] = i * 100;  // z  /
                verts[b + 3] = magnitudes[i];
                verts[b + 4] = i * 1000; // colour index
            }
            return verts;
        }

        private static float MagnitudeAt(ReadOnlySpan<float> verts, int star) => verts[star * Stride + 3];

        [Fact]
        public void SortingOrdersStarsBrightestFirst()
        {
            var verts = Buffer(7.5f, 2.0f, 11.25f, 0.5f, 4.0f);

            StarMagnitudeIndex.SortBrightestFirst(verts);

            Enumerable.Range(0, 5).Select(i => MagnitudeAt(verts, i))
                .ShouldBe([0.5f, 2.0f, 4.0f, 7.5f, 11.25f]);
        }

        /// <summary>
        /// The failure this guards is the classic one for a key-and-payload sort: the magnitudes come
        /// out ordered while the positions and colours stay put, which renders as a correctly-culled
        /// field of stars in all the wrong places. Every field is checked against the identity the
        /// star was built with.
        /// </summary>
        [Fact]
        public void SortingCarriesEachStarsWholeRecordWithIt()
        {
            var verts = Buffer(7.5f, 2.0f, 11.25f, 0.5f, 4.0f);

            StarMagnitudeIndex.SortBrightestFirst(verts);

            // Brightest-first order of the original indices: 3 (0.5), 1 (2.0), 4 (4.0), 0 (7.5), 2 (11.25)
            foreach (var (position, original) in new[] { (0, 3), (1, 1), (2, 4), (3, 0), (4, 2) })
            {
                var b = position * Stride;
                verts[b].ShouldBe(original);
                verts[b + 1].ShouldBe(original * 10);
                verts[b + 2].ShouldBe(original * 100);
                verts[b + 4].ShouldBe(original * 1000);
            }
        }

        [Fact]
        public void VisibleCountIsThePrefixOfStarsAtOrAboveTheLimit()
        {
            var verts = Buffer(0.5f, 2.0f, 4.0f, 7.5f, 11.25f);
            StarMagnitudeIndex.SortBrightestFirst(verts);
            var bins = StarMagnitudeIndex.ComputeBins(verts);

            StarMagnitudeIndex.VisibleCount(bins, 0.5f).ShouldBe(1u);
            StarMagnitudeIndex.VisibleCount(bins, 2.0f).ShouldBe(2u);
            StarMagnitudeIndex.VisibleCount(bins, 4.0f).ShouldBe(3u);
            StarMagnitudeIndex.VisibleCount(bins, 8.5f).ShouldBe(4u);
            StarMagnitudeIndex.VisibleCount(bins, 12.0f).ShouldBe(5u);
        }

        /// <summary>
        /// A limit past the last bin must mean "everything", not "nothing". The sky map's
        /// <see cref="SkyMapState.EffectiveMagnitudeLimit"/> climbs with zoom and readily exceeds the
        /// table's 15-magnitude span, and a wrap to zero there would blank the star field at exactly
        /// the zoom where the user is looking hardest.
        /// </summary>
        [Theory]
        [InlineData(15.0f)]
        [InlineData(20.0f)]
        [InlineData(1000.0f)]
        public void ALimitBeyondTheTableDrawsEverything(float limit)
        {
            var verts = Buffer(0.5f, 2.0f, 4.0f, 7.5f, 11.25f);
            StarMagnitudeIndex.SortBrightestFirst(verts);

            StarMagnitudeIndex.VisibleCount(StarMagnitudeIndex.ComputeBins(verts), limit).ShouldBe(5u);
        }

        [Fact]
        public void ALimitBrighterThanEveryStarDrawsNothing()
        {
            var verts = Buffer(6.0f, 7.0f, 8.0f);
            StarMagnitudeIndex.SortBrightestFirst(verts);

            StarMagnitudeIndex.VisibleCount(StarMagnitudeIndex.ComputeBins(verts), 0.5f).ShouldBe(0u);
        }

        /// <summary>
        /// A length that is not a whole number of records must fail loudly. The cast underneath
        /// truncates, so the quiet outcome would be a sorted prefix with a stale tail still holding
        /// another star's fields: a plausible-looking field with stars in the wrong places, no crash,
        /// and nothing pointing at the caller that computed the length wrong.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(11)]
        public void APartialRecordThrowsInsteadOfBeingSilentlyDropped(int length)
        {
            var verts = new float[length];

            Should.Throw<ArgumentException>(() => StarMagnitudeIndex.SortBrightestFirst(verts));
            Should.Throw<ArgumentException>(() => StarMagnitudeIndex.ComputeBins(verts));
        }

        [Fact]
        public void AnEmptyBufferIndexesAndCullsWithoutThrowing()
        {
            var verts = Array.Empty<float>();

            StarMagnitudeIndex.SortBrightestFirst(verts);
            var bins = StarMagnitudeIndex.ComputeBins(verts);

            bins.Length.ShouldBe(StarMagnitudeIndex.BinCount);
            StarMagnitudeIndex.VisibleCount(bins, 8.5f).ShouldBe(0u);
            StarMagnitudeIndex.VisibleCount([], 8.5f).ShouldBe(0u);
        }

        /// <summary>
        /// The property the pipelines actually rely on, over a spread wide enough to exercise every
        /// bin boundary: the culled prefix is exactly the set of stars at or above the limit, so no
        /// star that should be drawn is dropped and none that should not is drawn.
        /// </summary>
        [Fact]
        public void ThePrefixMatchesADirectCountAtEveryBinBoundary()
        {
            var magnitudes = Enumerable.Range(0, 300).Select(i => i * 0.05f).ToArray();
            var verts = Buffer(magnitudes);
            StarMagnitudeIndex.SortBrightestFirst(verts);
            var bins = StarMagnitudeIndex.ComputeBins(verts);

            for (var bin = 1; bin <= StarMagnitudeIndex.BinCount; bin++)
            {
                var limit = bin * 0.5f;
                var expected = (uint)magnitudes.Count(m => m <= limit);
                StarMagnitudeIndex.VisibleCount(bins, limit).ShouldBe(expected, $"limit V<={limit}");
            }
        }

        /// <summary>
        /// The whole point of the cull, measured on the REAL catalog rather than a synthetic spread:
        /// at the sky map's default limit the star draw must be a small fraction of the ~2.5M-star
        /// buffer. The web pipeline used to submit every instance every frame regardless of the view,
        /// which pinned the GPU process at 59% during a drag and dropped 944 of 1287 frames; on the
        /// desktop the same unbounded form TDR'd an Adreno X1-85.
        ///
        /// <para>The bound is deliberately loose (a tenth of the catalog) because it is guarding
        /// against the cull being lost or inverted, not pinning Tycho-2's magnitude distribution --
        /// a catalog refresh must not turn this red. The measured figures are logged.</para>
        /// </summary>
        [Fact]
        public void OnTheRealCatalogTheDefaultLimitDrawsASmallFractionOfTheBuffer()
        {
            var asm = typeof(ICelestialObjectDB).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));
            name.ShouldNotBeNull("tyc2.bin.lz must be embedded in the (non-Lightweight) test build");

            using var stream = asm.GetManifestResourceStream(name).ShouldNotBeNull();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            var db = new CelestialObjectDB();
            db.TryLoadTycho2BulkFromCompressed(ms.ToArray()).ShouldBeTrue();

            var total = ((ICelestialObjectDB)db).Tycho2StarCount;
            var verts = new float[total * Stride];
            var written = SkyMapState.FillTycho2StarVertices(db, dtJulianYears: 0.0, verts);
            var span = verts.AsSpan(0, written * Stride);

            StarMagnitudeIndex.SortBrightestFirst(span);
            var bins = StarMagnitudeIndex.ComputeBins(span);

            var atDefault = StarMagnitudeIndex.VisibleCount(bins, new SkyMapState().MagnitudeLimit);
            output.WriteLine($"Tycho-2: {written} stars indexed");
            foreach (var limit in new[] { 6.0f, 8.5f, 10.0f, 12.0f })
            {
                var n = StarMagnitudeIndex.VisibleCount(bins, limit);
                output.WriteLine($"  V<={limit,-5} -> {n,9} instances ({100.0 * n / written:F2}% of the buffer)");
            }

            atDefault.ShouldBeGreaterThan(0u, "a cull that draws nothing is a blank sky, not a fast one");
            atDefault.ShouldBeLessThan((uint)(written / 10), "the default limit must bound the draw well below the whole catalog");

            // Sorted brightest-first is what makes the visible set a prefix at all.
            for (var i = 1; i < written; i++)
            {
                if (MagnitudeAt(span, i) < MagnitudeAt(span, i - 1))
                {
                    Assert.Fail($"star buffer is not brightest-first at index {i}");
                }
            }
        }
    }
}
