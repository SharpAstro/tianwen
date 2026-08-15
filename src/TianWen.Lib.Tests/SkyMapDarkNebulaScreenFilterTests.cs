using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The dark-nebula on-screen-size rule is split across the two overlay phases: the gather admits a
    /// SUPERSET (permissive at the widest field of view its cache key can be reused across) and
    /// <see cref="OverlayEngine.ProjectSkyMapCandidatesInto"/> applies the exact test per frame.
    ///
    /// <para><b>Why the split exists.</b> The test is view-dependent, and it was the last view-dependent
    /// thing left inside the CACHED phase -- which meant a wide zoom with [D] on re-ran the full-sky walk
    /// for every 10% FOV bucket it crossed. Measured against the deployed build: 30 gathers over a
    /// zoom-out with dark nebulae on, against 3 with them off.</para>
    ///
    /// <para><b>Why the gather does not simply drop the filter.</b> That was the first attempt and it is
    /// far too expensive: only 190 of 4,827 shaped dark nebulae survive at 180 degrees, so removing it
    /// would inflate the cached set -- and its label building -- by ~4,600 entries. Clamping the
    /// pre-filter to the threshold FOV admits 1,655 instead, which is the union over the whole wide
    /// range and therefore exact rather than merely cheaper.</para>
    ///
    /// <para>These pin both halves: nothing illegible is ever drawn, the visible set still shrinks as the
    /// view widens, and the gathered set is genuinely FOV-independent above the threshold (which is what
    /// entitles the cache key to drop the FOV).</para>
    /// </summary>
    [Collection("Astrometry")]
    public class SkyMapDarkNebulaScreenFilterTests(ITestOutputHelper output)
    {
        private const float Width = 1600f;
        private const float Height = 1000f;
        private static readonly RectF32 Rect = new(0f, 0f, Width, Height);

        /// <summary>
        /// Waits for the bulk load, which these tests genuinely need rather than merely prefer. The
        /// gather's cross-catalog dedupe suppresses an object when a cross-index of it has already been
        /// seen, so a DB that is still filling in cross-references answers a LATER gather with FEWER
        /// candidates than an earlier one -- which reads exactly like a field-of-view dependence and is
        /// not one. It cost a false failure here (312 objects, all "missing" from the last gather in the
        /// sequence) before the cause was found.
        /// </summary>
        private static async Task<CelestialObjectDB> LoadAsync()
        {
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true);
            return db;
        }

        private static List<OverlayCandidate> GatherAt(CelestialObjectDB db, double fov)
        {
            var state = new SkyMapState { CenterRA = 18.0, CenterDec = -25.0, FieldOfViewDeg = fov };
            var candidates = new List<OverlayCandidate>(16384);
            OverlayEngine.GatherSkyMapOverlayCandidates(
                state.ComputeViewMatrix(), fov, Rect, 1f, db, null, candidates);
            return candidates;
        }

        private static List<OverlayItem> ProjectAt(IReadOnlyList<OverlayCandidate> candidates, double fov)
        {
            var state = new SkyMapState { CenterRA = 18.0, CenterDec = -25.0, FieldOfViewDeg = fov };
            state.CurrentViewMatrix = state.ComputeViewMatrix();
            var items = new List<OverlayItem>(16384);
            OverlayEngine.ProjectSkyMapCandidatesInto(candidates, state, Rect, 1f, items);
            return items;
        }

        /// <summary>
        /// The exact rule, enforced where it now lives. Every dark nebula that survives projection must
        /// clear the legibility threshold AT THE PROJECTED FIELD OF VIEW -- not at the wider one the
        /// cached candidate list was gathered for. A superset that is never narrowed is the failure mode
        /// this whole split could have introduced, and it would look like clutter rather than a crash.
        /// </summary>
        [Theory]
        [InlineData(90.0)]
        [InlineData(120.0)]
        [InlineData(150.0)]
        [InlineData(180.0)]
        public async Task NoIllegibleDarkNebulaSurvivesProjection(double fov)
        {
            var db = await LoadAsync();
            var candidates = GatherAt(db, OverlayEngine.WideFovDeg);
            var byKey = candidates.ToDictionary(c => (ulong)c.CatalogIndex, c => c);

            var items = ProjectAt(candidates, fov);
            var arcminToPixels = OverlayEngine.GetArcminToPixels(Height, fov);

            var drawn = 0;
            foreach (var item in items)
            {
                if (!byKey.TryGetValue(item.StableSortKey, out var cand)) continue;
                if (float.IsNaN(cand.ScreenSizeFilterArcmin) || cand.IsPinned) continue;
                drawn++;
                (cand.ScreenSizeFilterArcmin * arcminToPixels)
                    .ShouldBeGreaterThanOrEqualTo(OverlayEngine.DarkNebulaMinOnScreenPx,
                        $"a dark nebula {cand.ScreenSizeFilterArcmin:F1} arcmin across was drawn at fov {fov}");
            }

            output.WriteLine($"fov {fov,5:F0}: {items.Count,6} items projected, {drawn,5} size-filtered dark nebulae drawn");
            drawn.ShouldBeGreaterThan(0, "the sample view should contain SOME legible dark nebulae");
        }

        /// <summary>
        /// The half a one-directional check cannot see: the visible set must still SHRINK as the view
        /// widens, exactly as it did when the filter ran inside the gather. A projection that quietly
        /// stopped filtering would satisfy "nothing illegible survives" only by drawing nothing, and a
        /// projection that never narrowed the superset would show a flat count here.
        /// </summary>
        [Fact]
        public async Task TheVisibleDarkNebulaCountFallsAsTheViewWidens()
        {
            var db = await LoadAsync();
            var candidates = GatherAt(db, OverlayEngine.WideFovDeg);
            var byKey = candidates.ToDictionary(c => (ulong)c.CatalogIndex, c => c);

            var counts = new List<(double Fov, int Drawn)>();
            foreach (var fov in new[] { 90.0, 120.0, 150.0, 180.0 })
            {
                var drawn = ProjectAt(candidates, fov)
                    .Count(i => byKey.TryGetValue(i.StableSortKey, out var c)
                        && !float.IsNaN(c.ScreenSizeFilterArcmin));
                counts.Add((fov, drawn));
                output.WriteLine($"  fov {fov,5:F0}: {drawn,5} dark nebulae drawn");
            }

            for (var i = 1; i < counts.Count; i++)
            {
                counts[i].Drawn.ShouldBeLessThan(counts[i - 1].Drawn,
                    $"widening from {counts[i - 1].Fov} to {counts[i].Fov} must hide more dark nebulae");
            }
        }

        /// <summary>
        /// What entitles the cache key to drop the field of view above the threshold: the gathered set is
        /// the SAME at every wide FOV. Assert on the candidate identities rather than the count, since two
        /// different sets of equal size would pass a count check.
        /// </summary>
        [Fact]
        public async Task TheGatheredSetIsIdenticalAcrossTheWholeWideRange()
        {
            var db = await LoadAsync();
            var baseline = GatherAt(db, OverlayEngine.WideFovDeg)
                .Select(c => (ulong)c.CatalogIndex).ToHashSet();

            foreach (var fov in new[] { 120.0, 150.0, 180.0 })
            {
                var here = GatherAt(db, fov).Select(c => (ulong)c.CatalogIndex).ToHashSet();
                here.SetEquals(baseline).ShouldBeTrue(
                    $"the gather at fov {fov} differs from the one at {OverlayEngine.WideFovDeg} by "
                    + $"{here.Except(baseline).Count()} added / {baseline.Except(here).Count()} missing");
            }

            output.WriteLine($"wide-range gathered set: {baseline.Count} candidates, identical at 90/120/150/180");
        }
    }
}
