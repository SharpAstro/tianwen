using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The spatial half of the star cull. Magnitude bounds a wide field and stops bounding anything as
    /// the limit climbs with zoom; the cone bounds a deep zoom and does nothing at full sky. Neither
    /// covers the other, so both are pinned -- and the property that matters most is that culling
    /// never LOSES a star that should have been drawn.
    /// </summary>
    public class StarChunkIndexTests
    {
        private const int Stride = SkyMapState.FloatsPerStar;

        /// <summary>A star at a known sky position, with its identity encoded in the colour field so a
        /// regrouping that scrambled records is detectable.</summary>
        private static void Write(Span<float> verts, int index, double raHours, double decDeg, float mag)
        {
            var (x, y, z) = SkyMapState.RaDecToUnitVec(raHours, decDeg);
            var b = index * Stride;
            verts[b] = x;
            verts[b + 1] = y;
            verts[b + 2] = z;
            verts[b + 3] = mag;
            verts[b + 4] = index;
        }

        /// <summary>A spread over the whole sphere, magnitudes cycling so every chunk holds a mix.</summary>
        private static float[] WholeSkyField(int perAxis = 24)
        {
            var verts = new float[perAxis * perAxis * Stride];
            var i = 0;
            for (var ra = 0; ra < perAxis; ra++)
            {
                for (var dec = 0; dec < perAxis; dec++)
                {
                    var raHours = 24.0 * ra / perAxis;
                    var decDeg = -85.0 + 170.0 * dec / (perAxis - 1);
                    Write(verts, i, raHours, decDeg, 1f + (i % 24) * 0.5f);
                    i++;
                }
            }
            return verts;
        }

        [Fact]
        public void ChunksPartitionTheBufferExactlyWithNoGapsOrOverlap()
        {
            var verts = WholeSkyField();
            var total = verts.Length / Stride;

            var chunks = StarChunkIndex.Build(verts);

            chunks.Length.ShouldBe(StarChunkIndex.ChunkCount);
            chunks.Sum(c => (int)c.Count).ShouldBe(total, "every star must land in exactly one chunk");

            // Ranges are contiguous and ascending, so an offset is a valid firstInstance into the buffer.
            uint expectedOffset = 0;
            foreach (var chunk in chunks.Where(c => c.Count > 0))
            {
                chunk.Offset.ShouldBe(expectedOffset);
                expectedOffset += chunk.Count;
            }
            expectedOffset.ShouldBe((uint)total);
        }

        [Fact]
        public void EveryStarSurvivesTheRegroupingWithItsRecordIntact()
        {
            var verts = WholeSkyField();
            var total = verts.Length / Stride;

            StarChunkIndex.Build(verts);

            // The colour field carries each star's original index; regrouping must permute records,
            // never split one across two.
            var seen = new HashSet<int>();
            for (var i = 0; i < total; i++)
            {
                var b = i * Stride;
                var identity = (int)verts[b + 4];
                seen.Add(identity).ShouldBeTrue($"star {identity} appears twice");

                // Its unit vector must still be a unit vector -- a torn record would not be.
                var len = MathF.Sqrt(verts[b] * verts[b] + verts[b + 1] * verts[b + 1] + verts[b + 2] * verts[b + 2]);
                len.ShouldBe(1f, 1e-3f, $"star at slot {i} has a torn position");
            }
            seen.Count.ShouldBe(total);
        }

        [Fact]
        public void EachChunkIsSortedBrightestFirstSoItsPrefixIsMeaningful()
        {
            var verts = WholeSkyField();

            var chunks = StarChunkIndex.Build(verts);

            foreach (var chunk in chunks.Where(c => c.Count > 1))
            {
                for (var i = 1; i < chunk.Count; i++)
                {
                    var prev = verts[(int)(chunk.Offset + i - 1) * Stride + 3];
                    var cur = verts[(int)(chunk.Offset + i) * Stride + 3];
                    cur.ShouldBeGreaterThanOrEqualTo(prev, $"chunk at {chunk.Offset} is not brightest-first");
                }
            }
        }

        /// <summary>
        /// The safety property: at a whole-sky view with a limit past every star, the cull must submit
        /// every instance. A cone test that is even slightly too tight shows up here as missing stars
        /// rather than as anything that looks like an error.
        /// </summary>
        [Fact]
        public void AWholeSkyViewDrawsEveryStar()
        {
            var verts = WholeSkyField();
            var total = verts.Length / Stride;
            var chunks = StarChunkIndex.Build(verts);

            var (vx, vy, vz) = SkyMapState.RaDecToUnitVec(6.0, 20.0);
            var drawn = chunks
                .Where(c => c.Count > 0 && StarChunkIndex.IsVisible(c, vx, vy, vz, (float)double.DegreesToRadians(180.0)))
                .Sum(c => (int)StarMagnitudeIndex.VisibleCount(c.MagBins, 30f));

            drawn.ShouldBe(total);
        }

        /// <summary>
        /// A zoomed-in view must reject most of the sky -- otherwise the cull compiles, passes the
        /// safety test above, and buys nothing, which is exactly the state the web pipeline was in.
        ///
        /// <para>15 degrees, not 2: a chunk cone spans ~8 degrees on top of the ~10 degrees to the
        /// nearest chunk axis, so at 2 degrees the correct answer is that NOTHING is visible. That is
        /// not a bug -- this field's stars are 7 to 15 degrees apart, so a 2-degree field really does
        /// contain none of them -- but it makes the test assert the cull is broken rather than tight.
        /// The threshold has to be wider than the grid it is culling.</para>
        /// </summary>
        [Fact]
        public void AZoomedInViewCullsMostOfTheSky()
        {
            var verts = WholeSkyField();
            var chunks = StarChunkIndex.Build(verts);
            var populated = chunks.Count(c => c.Count > 0);

            var (vx, vy, vz) = SkyMapState.RaDecToUnitVec(6.0, 0.0);
            var visible = chunks.Count(c => c.Count > 0
                && StarChunkIndex.IsVisible(c, vx, vy, vz, (float)double.DegreesToRadians(15.0)));

            visible.ShouldBeGreaterThan(0, "the view direction's own neighbourhood must survive");
            visible.ShouldBeLessThan(populated / 4, $"only {visible} of {populated} chunks should survive a 15-degree view");
        }

        [Fact]
        public void AChunkOnTheOppositeSideOfTheSkyIsNeverVisibleToANarrowView()
        {
            var verts = WholeSkyField();
            var chunks = StarChunkIndex.Build(verts);
            var (vx, vy, vz) = SkyMapState.RaDecToUnitVec(0.0, 0.0);

            // The antipode's chunk: RA 12h, Dec 0.
            var (ax, ay, az) = SkyMapState.RaDecToUnitVec(12.0, 0.0);
            var antipodal = chunks.Where(c => c.Count > 0)
                .OrderByDescending(c => c.ConeX * ax + c.ConeY * ay + c.ConeZ * az)
                .First();

            StarChunkIndex.IsVisible(antipodal, vx, vy, vz, (float)double.DegreesToRadians(5.0)).ShouldBeFalse();
        }

        [Fact]
        public void AnEmptyBufferProducesAFullyCulledChunkTable()
        {
            var chunks = StarChunkIndex.Build([]);

            chunks.Length.ShouldBe(StarChunkIndex.ChunkCount);
            chunks.ShouldAllBe(c => c.Count == 0);
        }

        [Fact]
        public void APartialRecordThrowsRatherThanRegroupingGarbage()
            => Should.Throw<ArgumentException>(() => StarChunkIndex.Build(new float[Stride + 2]));
    }
}
