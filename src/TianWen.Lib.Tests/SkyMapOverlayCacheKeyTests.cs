using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The CPU object-overlay candidate cache (the browser sky map's Phase-A grid walk) is keyed on a
    /// QUANTIZED view, so panning or zooming a little must reuse the gathered set. These pin the
    /// quantization's one job: the number of gathers a gesture costs.
    ///
    /// <para><b>Why a count and not an image.</b> A cache miss draws the byte-identical frame, just
    /// slower, so nothing about the output can distinguish a key that holds from one that misses on
    /// every event -- only a count can. This is the same reason
    /// <see cref="SkyMapState.PlanetCacheRebuilds"/> exists.</para>
    ///
    /// <para><b>The bug these were written for.</b> The centre grid step was derived from the RAW field
    /// of view rather than the bucketed one, so a zoom rescaled the grid continuously and the rounded
    /// centre moved on every single event even with the centre held perfectly still. A pinch therefore
    /// re-gathered per tick -- the exact opposite of what the FOV bucketing exists for, and measured in
    /// a browser trace as touchmove p95 91 ms / max 246 ms, with 91% of the main thread's busy time
    /// inside move handling.</para>
    /// </summary>
    [Collection("UI")]
    public class SkyMapOverlayCacheKeyTests(ITestOutputHelper output)
    {
        private const float Width = 800f;
        private const float Height = 600f;

        private static SkyMapTab<RgbaImage> BuildTab()
            => new SkyMapTab<RgbaImage>(new RgbaImageRenderer((int)Width, (int)Height)) { Bus = new SignalBus() };

        /// <summary>Keys the tab's overlay cache for the current view, as a render would.</summary>
        private static object KeyFor(SkyMapTab<RgbaImage> tab, double centreRa, double centreDec, double fov)
        {
            tab.State.CenterRA = centreRa;
            tab.State.CenterDec = centreDec;
            tab.State.FieldOfViewDeg = fov;
            tab.State.CurrentViewMatrix = tab.State.ComputeViewMatrix();

            var rect = new RectF32(0f, 0f, Width, Height);
            var cx = rect.X + rect.Width * 0.5f;
            var cy = rect.Y + rect.Height * 0.5f;
            var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, fov);
            return tab.BuildOverlayKeyForTest(rect, fov, cx, cy, ppr, new PlannerState());
        }

        /// <summary>Gathers a gesture would cost: one per key change, plus the first.</summary>
        private static int GathersOver(IEnumerable<object> keys)
        {
            var gathers = 0;
            object? last = null;
            foreach (var k in keys)
            {
                if (last is null || !k.Equals(last))
                {
                    gathers++;
                }
                last = k;
            }
            return gathers;
        }

        /// <summary>
        /// The regression itself. A pinch zoom with the centre held EXACTLY still must cost a gather per
        /// FOV bucket, not one per event. The bound is the bucket count over the range (~10% steps over
        /// a 2x range is ~8) with headroom, not the measured number -- this guards the coupling, not the
        /// bucket width.
        /// </summary>
        [Fact]
        public void AZoomWithAStillCentreGathersPerFovBucketNotPerEvent()
        {
            var tab = BuildTab();
            var keys = new List<object>();
            for (var fov = 60.0; fov > 30.0; fov *= 0.99) // ~1% per event, as a trackpad pinch arrives
            {
                keys.Add(KeyFor(tab, centreRa: 6.5, centreDec: 40.0, fov));
            }

            var gathers = GathersOver(keys);
            output.WriteLine($"zoom 60->30 deg over {keys.Count} events, centre fixed: {gathers} gathers");

            gathers.ShouldBeLessThanOrEqualTo(12,
                "a zoom must re-gather per FOV bucket; per-event means the centre grid is riding the raw FOV");
            gathers.ShouldBeGreaterThan(1, "the FOV genuinely changed, so the set cannot be gathered once");
        }

        /// <summary>
        /// The other half of the same coupling, and the one a bucket-count assertion alone would miss: a
        /// zoom that stays INSIDE one FOV bucket must not gather again at all.
        /// </summary>
        [Fact]
        public void AZoomWithinOneFovBucketDoesNotGatherAgain()
        {
            var tab = BuildTab();
            // 1.1^k buckets: 60.0 -> 60.5 cannot cross one.
            var keys = new List<object>
            {
                KeyFor(tab, 6.5, 40.0, 60.0),
                KeyFor(tab, 6.5, 40.0, 60.2),
                KeyFor(tab, 6.5, 40.0, 60.5),
            };

            GathersOver(keys).ShouldBe(1);
        }

        /// <summary>
        /// A pan is the gesture the quantization was designed for and was always fine; asserted here so
        /// a future change to the grid cannot fix the zoom by making the pan worse.
        /// </summary>
        [Fact]
        public void APanCostsAGatherPerCellNotPerEvent()
        {
            var tab = BuildTab();
            var keys = new List<object>();
            for (var i = 0; i < 70; i++) // 1.4h of RA at a fixed 60 degree FOV
            {
                keys.Add(KeyFor(tab, centreRa: 6.5 + i * 0.02, centreDec: 40.0, fov: 60.0));
            }

            var gathers = GathersOver(keys);
            output.WriteLine($"pan 1.4h of RA over {keys.Count} events at fov=60: {gathers} gathers");
            gathers.ShouldBeLessThanOrEqualTo(6);
        }

        /// <summary>
        /// Panning inside a single cell must not gather at all -- the case the whole cache exists for.
        /// </summary>
        [Fact]
        public void APanWithinOneCellDoesNotGatherAgain()
        {
            var tab = BuildTab();
            var keys = new List<object>
            {
                KeyFor(tab, 6.5000, 40.00, 60.0),
                KeyFor(tab, 6.5005, 40.01, 60.0),
                KeyFor(tab, 6.5010, 40.02, 60.0),
            };

            GathersOver(keys).ShouldBe(1);
        }

        /// <summary>
        /// Past the wide-FOV threshold the gather sweeps the whole sphere, so the centre drops out of
        /// the key entirely and a pan at that zoom must be free.
        /// </summary>
        [Fact]
        public void AtAWideFovThePanDropsOutOfTheKey()
        {
            var tab = BuildTab();
            var keys = new List<object>();
            for (var i = 0; i < 24; i++)
            {
                keys.Add(KeyFor(tab, centreRa: i, centreDec: 0.0, fov: 120.0));
            }

            GathersOver(keys).ShouldBe(1);
        }
    }
}
