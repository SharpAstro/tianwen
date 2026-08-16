using System;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The incremental chunk layout has to be indistinguishable from the one-shot
    /// <see cref="StarChunkIndex.Build"/>, because the draw path cannot tell which produced the
    /// buffer it is culling. If they ever disagree the symptom is stars culled in the wrong place --
    /// a plausible-looking sky, not an error.
    /// </summary>
    public class StarChunkAccumulatorTests
    {
        private const int Stride = SkyMapState.FloatsPerStar;

        private static float[] Field(int perAxis, int seed)
        {
            var rng = new Random(seed);
            var verts = new float[perAxis * perAxis * Stride];
            var i = 0;
            for (var ra = 0; ra < perAxis; ra++)
            {
                for (var dec = 0; dec < perAxis; dec++)
                {
                    var (x, y, z) = SkyMapState.RaDecToUnitVec(
                        24.0 * ra / perAxis, -85.0 + (170.0 * dec / (perAxis - 1)));
                    var b = i * Stride;
                    verts[b] = x;
                    verts[b + 1] = y;
                    verts[b + 2] = z;
                    // Deliberately off the bin thresholds and repeating, so several stars share a
                    // bucket and within-bucket order is genuinely free to differ between the two.
                    verts[b + 3] = 0.7f + ((float)rng.NextDouble() * 13.5f);
                    verts[b + 4] = 0.65f;
                    i++;
                }
            }
            return verts;
        }

        /// <summary>
        /// Feeding the same stars in several arrivals must produce the same chunk TABLE as building
        /// them all at once -- same offsets, same counts, same bin prefixes, same cones.
        /// </summary>
        [Fact]
        public void AccumulatingInBatchesMatchesBuildingInOneGo()
        {
            var verts = Field(perAxis: 30, seed: 7);
            var total = verts.Length / Stride;

            var oneGo = StarChunkIndex.Build(verts.AsSpan().ToArray().AsSpan());
            var expectedVerts = verts.AsSpan().ToArray();
            StarChunkIndex.Build(expectedVerts);

            var acc = new StarChunkAccumulator();
            foreach (var batch in Batches(verts, total, 7))
            {
                acc.Add(batch);
            }
            var (packedVerts, packedCount, packedChunks) = acc.Pack();

            packedCount.ShouldBe(total, "no star may be lost or duplicated across arrivals");
            acc.StarCount.ShouldBe(total);
            packedChunks.Length.ShouldBe(oneGo.Length);

            for (var c = 0; c < oneGo.Length; c++)
            {
                packedChunks[c].Offset.ShouldBe(oneGo[c].Offset, $"chunk {c} offset");
                packedChunks[c].Count.ShouldBe(oneGo[c].Count, $"chunk {c} count");
                packedChunks[c].MagBins.ShouldBe(oneGo[c].MagBins, $"chunk {c} bins");
                packedChunks[c].ConeX.ShouldBe(oneGo[c].ConeX, 1e-5f, $"chunk {c} cone x");
                packedChunks[c].ConeY.ShouldBe(oneGo[c].ConeY, 1e-5f, $"chunk {c} cone y");
                packedChunks[c].ConeZ.ShouldBe(oneGo[c].ConeZ, 1e-5f, $"chunk {c} cone z");
                packedChunks[c].ConeRadiusRad.ShouldBe(oneGo[c].ConeRadiusRad, 1e-5f, $"chunk {c} cone r");
            }

            // Same stars, and each chunk's slice holds the same MULTISET as the one-shot build. Order
            // within a 0.5-magnitude bucket may differ (arrival order vs input order) and is
            // unobservable, so the comparison is deliberately on the sorted magnitudes.
            foreach (var chunk in packedChunks.Where(c => c.Count > 0))
            {
                var mine = Magnitudes(packedVerts, chunk.Offset, chunk.Count);
                var theirs = Magnitudes(expectedVerts, chunk.Offset, chunk.Count);
                mine.ShouldBe(theirs, $"chunk at {chunk.Offset} holds different stars");
            }
        }

        /// <summary>
        /// The prefix property, which is the only thing the draw actually relies on, must survive
        /// arriving in pieces.
        /// </summary>
        [Fact]
        public void EveryChunkPrefixStillMatchesItsMagnitudeLimit()
        {
            var verts = Field(perAxis: 24, seed: 11);
            var acc = new StarChunkAccumulator();
            foreach (var batch in Batches(verts, verts.Length / Stride, 5))
            {
                acc.Add(batch);
            }
            var (packed, _, chunks) = acc.Pack();

            foreach (var chunk in chunks.Where(c => c.Count > 0))
            {
                for (var bin = 0; bin < StarMagnitudeIndex.BinCount; bin++)
                {
                    var limit = (bin + 1) * 0.5f;
                    var drawn = StarMagnitudeIndex.VisibleCount(chunk.MagBins, limit);
                    for (var k = 0u; k < drawn; k++)
                    {
                        packed[(int)(chunk.Offset + k) * Stride + 3].ShouldBeLessThanOrEqualTo(limit);
                    }
                    for (var k = drawn; k < chunk.Count; k++)
                    {
                        packed[(int)(chunk.Offset + k) * Stride + 3].ShouldBeGreaterThan(limit);
                    }
                }
            }
        }

        /// <summary>
        /// Packing twice with an arrival in between must not corrupt the first buffer: the pipeline
        /// keeps the reference for a later render-thread upload, so a reused array would be rewritten
        /// underneath it.
        /// </summary>
        [Fact]
        public void APackIsANewBufferSoAnEarlierOneStaysValid()
        {
            var verts = Field(perAxis: 12, seed: 3);
            var half = (verts.Length / Stride / 2) * Stride;

            var acc = new StarChunkAccumulator();
            acc.Add(verts.AsSpan(0, half));
            var (first, firstCount, _) = acc.Pack();
            var snapshot = first.AsSpan(0, firstCount * Stride).ToArray();

            acc.Add(verts.AsSpan(half));
            var (second, secondCount, _) = acc.Pack();

            ReferenceEquals(first, second).ShouldBeFalse("a pack must not hand back the previous array");
            first.AsSpan(0, firstCount * Stride).ToArray().ShouldBe(snapshot, "the earlier buffer was mutated");
            secondCount.ShouldBe(verts.Length / Stride);
        }

        [Fact]
        public void AnEmptyAccumulatorPacksToAFullyCulledTable()
        {
            var (verts, count, chunks) = new StarChunkAccumulator().Pack();

            count.ShouldBe(0);
            verts.Length.ShouldBe(0);
            chunks.Length.ShouldBe(StarChunkIndex.ChunkCount);
            chunks.ShouldAllBe(c => c.Count == 0);
        }

        [Fact]
        public void APartialRecordThrowsRatherThanAccumulatingGarbage()
            => Should.Throw<ArgumentException>(() => new StarChunkAccumulator().Add(new float[Stride + 2]));

        private static float[] Magnitudes(float[] verts, uint offset, uint count)
        {
            var mags = new float[count];
            for (var k = 0u; k < count; k++)
            {
                mags[k] = verts[(int)(offset + k) * Stride + 3];
            }
            Array.Sort(mags);
            return mags;
        }

        private static System.Collections.Generic.IEnumerable<float[]> Batches(float[] verts, int total, int batches)
        {
            var per = (total / batches) + 1;
            for (var start = 0; start < total; start += per)
            {
                var n = Math.Min(per, total - start);
                yield return verts.AsSpan(start * Stride, n * Stride).ToArray();
            }
        }
    }
}
