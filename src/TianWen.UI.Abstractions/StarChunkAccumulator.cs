using System;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The incremental counterpart of <see cref="StarChunkIndex.Build"/>: stars are keyed into their
    /// (chunk, magnitude bucket) slot ONCE as they arrive, and a pack is a concatenation.
    ///
    /// <para><b>Why a client that fetches sky incrementally needs this.</b> <c>Build</c> is a whole-
    /// buffer operation, so wiring it to an incremental fetch means re-keying and re-grouping every
    /// star already held each time a few more arrive -- work proportional to what is HELD, repeated
    /// per settle, when what changed is proportional to what ARRIVED. On the deployed browser atlas
    /// that was the last O(everything) term left on the pan path, next to a flatten with the same
    /// shape. Here, arriving stars pay the keying once and a settle pays a memcpy.</para>
    ///
    /// <para><b>The layout is byte-identical to what <c>Build</c> produces</b> for the same set of
    /// stars, because both key through <see cref="StarChunkIndex.SlotOf"/> and both derive the chunk
    /// table from the slot histogram. Only the ORDER WITHIN one slot can differ (each is arrival
    /// order rather than input order), and that is unobservable: a slot is a single 0.5-magnitude
    /// bucket of a single chunk, and the only reader of the order asks for a bin prefix.</para>
    ///
    /// <para>Not thread-safe, and does not need to be: it is fed by the fetch task and packed by the
    /// same task, then the packed array is handed off by reference.</para>
    /// </summary>
    public sealed class StarChunkAccumulator
    {
        private const int FloatsPerStar = SkyMapState.FloatsPerStar;

        /// <summary>Per-slot storage, allocated on first use so an empty sky costs 4464 nulls.</summary>
        private readonly float[]?[] _slots = new float[StarChunkIndex.SlotCount][];
        private readonly int[] _counts = new int[StarChunkIndex.SlotCount];

        /// <summary>Chunks whose contents changed since the last pack, so a cone is recomputed only
        /// where it can actually have moved. A pan touches a handful of the 144.</summary>
        private readonly bool[] _dirty = new bool[StarChunkIndex.ChunkCount];
        private StarChunk[] _chunks = new StarChunk[StarChunkIndex.ChunkCount];

        /// <summary>Stars held across every slot.</summary>
        public int StarCount { get; private set; }

        /// <summary>
        /// Adds a flattened star run (<see cref="SkyMapState.FloatsPerStar"/> floats per star, the
        /// layout <see cref="SkyMapState.FillTycho2StarVertices"/> writes). The caller may reuse or
        /// discard its buffer afterwards; the stars are copied into slot storage.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="verts"/>'s length is not a multiple of the star record size.
        /// </exception>
        public void Add(ReadOnlySpan<float> verts)
        {
            StarMagnitudeIndex.EnsureWholeRecords(verts.Length, nameof(verts));

            var count = verts.Length / FloatsPerStar;
            for (var i = 0; i < count; i++)
            {
                var b = i * FloatsPerStar;
                var slot = StarChunkIndex.SlotOf(verts[b], verts[b + 1], verts[b + 2], verts[b + 3]);

                var used = _counts[slot];
                var store = _slots[slot];
                if (store is null)
                {
                    // Slots are wildly uneven (a dense Milky Way chunk against an empty polar one), so
                    // start small and let the doubling below find the size rather than reserving for
                    // the worst case 4464 times over.
                    store = new float[16 * FloatsPerStar];
                    _slots[slot] = store;
                }
                else if ((used + 1) * FloatsPerStar > store.Length)
                {
                    Array.Resize(ref store, store.Length * 2);
                    _slots[slot] = store;
                }

                verts.Slice(b, FloatsPerStar).CopyTo(store.AsSpan(used * FloatsPerStar, FloatsPerStar));
                _counts[slot] = used + 1;
                _dirty[slot / StarMagnitudeIndex.BucketCount] = true;
                StarCount++;
            }
        }

        /// <summary>
        /// Concatenates every slot, in slot order, into one instance buffer and returns it with its
        /// chunk table -- the same pair <see cref="StarChunkIndex.Build"/> returns.
        ///
        /// <para>A fresh array each time, deliberately: the pipeline keeps the reference for a later
        /// render-thread upload, so reusing one would rewrite a buffer it is still holding.</para>
        /// </summary>
        public (float[] Verts, int Count, StarChunk[] Chunks) Pack()
        {
            var verts = new float[StarCount * FloatsPerStar];
            var at = 0;
            for (var slot = 0; slot < StarChunkIndex.SlotCount; slot++)
            {
                var n = _counts[slot];
                if (n == 0)
                {
                    continue;
                }
                _slots[slot]!.AsSpan(0, n * FloatsPerStar).CopyTo(verts.AsSpan(at));
                at += n * FloatsPerStar;
            }

            var previous = _chunks;
            _chunks = StarChunkIndex.TableFromCounts(_counts, (chunk, offset, n) =>
            {
                // A clean chunk's cone cannot have moved -- its stars are the same ones -- but its
                // OFFSET can, because a chunk before it grew. So the cone is reused while the offset
                // and counts are always taken fresh from the histogram.
                if (!_dirty[chunk] && previous[chunk].Count == (uint)n)
                {
                    var p = previous[chunk];
                    return (p.ConeX, p.ConeY, p.ConeZ, p.ConeRadiusRad);
                }
                return StarChunkIndex.ComputeConeOf(verts.AsSpan(offset * FloatsPerStar, n * FloatsPerStar));
            });

            Array.Clear(_dirty);
            return (verts, StarCount, _chunks);
        }
    }
}
