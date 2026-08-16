using System;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// One spatial chunk's slice of a chunk-grouped star buffer: its instance range
    /// (<see cref="Offset"/> + <see cref="Count"/>), the per-chunk magnitude prefix table
    /// (<see cref="StarMagnitudeIndex"/>), and a bounding cone (axis unit vector + angular radius in
    /// radians) used to cull the whole chunk against the view cone before any of it is submitted.
    /// </summary>
    public readonly record struct StarChunk(
        uint Offset, uint Count, uint[] MagBins,
        float ConeX, float ConeY, float ConeZ, float ConeRadiusRad);

    /// <summary>
    /// Spatial chunking for a star-instance buffer: partition into a coarse RA/Dec grid, group the
    /// buffer so each chunk is contiguous, then sort + magnitude-index within each chunk.
    ///
    /// <para><b>Why chunking is needed on top of the magnitude prefix.</b> The magnitude cull alone
    /// bounds a WIDE view well (at the default 60-degree field it draws ~3% of Tycho-2), but it stops
    /// bounding anything as the effective limit climbs: at V&lt;=12 the prefix is 81% of the catalog,
    /// so a one-degree field still submits ~2M instances for a patch of sky that holds almost none of
    /// them. The cone cull is what makes a deep zoom cheap -- it is the axis that magnitude cannot
    /// cover, and vice versa.</para>
    ///
    /// <para>Surface-agnostic because both backends can now draw an instance sub-range: Vulkan via
    /// <c>firstInstance</c>, WebGL via a per-instance attribute offset (WebGl.Renderer 1.24). The
    /// geometry and the grouping have exactly one implementation.</para>
    /// </summary>
    public static class StarChunkIndex
    {
        /// <summary>RA slices (2h / 30 degrees each).</summary>
        public const int GridCols = 12;

        /// <summary>Dec slices (15 degrees each).</summary>
        public const int GridRows = 12;

        /// <summary>Chunks per buffer; the returned array is always this long.</summary>
        public const int ChunkCount = GridCols * GridRows;

        /// <summary>
        /// Groups <paramref name="verts"/> by sky region IN PLACE and returns the per-chunk layout.
        /// Each chunk is ordered brightest-first and carries its own magnitude prefix table, so a draw
        /// is "for each visible chunk, submit its prefix".
        ///
        /// <para><b>Ordering is by half-magnitude bucket, not a total sort, and that is the contract
        /// the consumer actually needs.</b> The only reader of the order is
        /// <see cref="StarMagnitudeIndex.VisibleCount"/>, which answers with a bin's prefix count --
        /// so all that must hold is that every star at or above a bin's threshold precedes every star
        /// below it. Ordering within a single 0.5-magnitude bucket is unobservable. Buying a total
        /// order instead costs a comparison sort, and on the browser build that sort was the entire
        /// problem: see the remarks on <see cref="StarMagnitudeIndex.SortBrightestFirst"/>. The
        /// counting scatter here is O(n), needs no comparer, and folds the region grouping and the
        /// magnitude ordering into ONE pass over the buffer instead of a scatter followed by 144
        /// sorts.</para>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="verts"/>'s length is not a multiple of the star record size.
        /// </exception>
        public static StarChunk[] Build(Span<float> verts)
        {
            // Same reason as StarMagnitudeIndex: integer division would quietly ignore a partial tail
            // and regroup around it, and the result renders as a plausible sky rather than as an error.
            StarMagnitudeIndex.EnsureWholeRecords(verts.Length, nameof(verts));

            const int floatsPerStar = SkyMapState.FloatsPerStar;
            const int buckets = StarMagnitudeIndex.BucketCount;
            var count = verts.Length / floatsPerStar;
            var chunks = new StarChunk[ChunkCount];
            if (count == 0)
            {
                return chunks; // every chunk Count == 0 -> all culled at draw
            }

            const float raCellDeg = 360f / GridCols;
            const float decCellDeg = 180f / GridRows;
            const float rad2deg = 180f / MathF.PI;

            // 1. One key per star: its chunk MAJOR, its magnitude bucket MINOR. Ascending key order is
            //    therefore already "grouped by region, brightest-first within a region", which is what
            //    makes a single counting scatter do the whole job.
            var keyOf = new int[count];
            var counts = new int[ChunkCount * buckets];
            for (var i = 0; i < count; i++)
            {
                var b = i * floatsPerStar;
                float x = verts[b], y = verts[b + 1], z = verts[b + 2];
                var decDeg = MathF.Asin(Math.Clamp(z, -1f, 1f)) * rad2deg;            // [-90, 90]
                var raDeg = MathF.Atan2(y, x) * rad2deg;                              // (-180, 180]
                if (raDeg < 0f)
                {
                    raDeg += 360f;                                                    // [0, 360)
                }
                var col = Math.Clamp((int)(raDeg / raCellDeg), 0, GridCols - 1);
                var row = Math.Clamp((int)((decDeg + 90f) / decCellDeg), 0, GridRows - 1);
                var key = ((row * GridCols) + col) * buckets + StarMagnitudeIndex.BucketOf(verts[b + 3]);
                keyOf[i] = key;
                counts[key]++;
            }

            // 2. Prefix offsets per key. The per-chunk instance offset falls out as the offset of its
            //    first (brightest) bucket, and the per-chunk bin table as the running total across its
            //    own buckets -- so the prefix tables cost no extra pass over the stars.
            var offsets = new int[(ChunkCount * buckets) + 1];
            for (int k = 0, running = 0; k < ChunkCount * buckets; k++)
            {
                offsets[k] = running;
                running += counts[k];
            }
            offsets[ChunkCount * buckets] = count;

            // 3. Stable scatter into a grouped copy, then write it back over the input span.
            var grouped = new float[count * floatsPerStar];
            var cursor = (int[])offsets.Clone();
            for (var i = 0; i < count; i++)
            {
                var dst = cursor[keyOf[i]]++ * floatsPerStar;
                verts.Slice(i * floatsPerStar, floatsPerStar).CopyTo(grouped.AsSpan(dst, floatsPerStar));
            }
            grouped.AsSpan().CopyTo(verts);

            // 4. Per chunk: read the bin table straight off the histogram, then the bounding cone.
            for (var c = 0; c < ChunkCount; c++)
            {
                var first = c * buckets;
                var offset = offsets[first];
                var n = offsets[first + buckets] - offset;
                if (n == 0)
                {
                    chunks[c] = new StarChunk(0, 0, [], 0f, 0f, 1f, 0f);
                    continue;
                }

                // magBins[b] = stars in this chunk at or above bin b's threshold = the running total
                // over buckets 0..b. The overflow bucket is deliberately never reached, which is what
                // keeps stars fainter than the last bin out of every prefix.
                var bins = new uint[StarMagnitudeIndex.BinCount];
                var running = 0;
                for (var b = 0; b < StarMagnitudeIndex.BinCount; b++)
                {
                    running += counts[first + b];
                    bins[b] = (uint)running;
                }

                var sub = verts.Slice(offset * floatsPerStar, n * floatsPerStar);
                var (cx, cy, cz, radRad) = ComputeCone(sub, floatsPerStar);
                chunks[c] = new StarChunk((uint)offset, (uint)n, bins, cx, cy, cz, radRad);
            }
            return chunks;
        }

        /// <summary>
        /// Whether a chunk can contribute to a view: false only when the angular separation of the two
        /// cone axes exceeds the sum of their radii, i.e. the cones provably cannot intersect.
        ///
        /// <para>Rotation-invariant, so it is correct in equatorial AND horizon mode without the
        /// caller knowing which. The view axis is the look-at direction and the view radius should be
        /// the FULL field of view -- generous enough to cover the viewport diagonal at any reasonable
        /// aspect, so chunks never pop in and out at the screen edges.</para>
        /// </summary>
        public static bool IsVisible(in StarChunk chunk, float viewX, float viewY, float viewZ, float viewRadiusRad)
        {
            var dot = viewX * chunk.ConeX + viewY * chunk.ConeY + viewZ * chunk.ConeZ;
            var sep = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            return sep <= viewRadiusRad + chunk.ConeRadiusRad;
        }

        /// <summary>
        /// Bounding cone for a chunk's stars: axis = the normalized mean of the member unit vectors,
        /// radius = the maximum angular distance (radians) from that axis to any member.
        /// </summary>
        private static (float X, float Y, float Z, float RadiusRad) ComputeCone(
            ReadOnlySpan<float> span, int floatsPerStar)
        {
            var n = span.Length / floatsPerStar;
            double sx = 0, sy = 0, sz = 0;
            for (var i = 0; i < n; i++)
            {
                var b = i * floatsPerStar;
                sx += span[b]; sy += span[b + 1]; sz += span[b + 2];
            }
            var len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (len < 1e-9)
            {
                return (0f, 0f, 1f, MathF.PI); // antipodal cancellation -> whole-sky cone, never culled
            }
            float ax = (float)(sx / len), ay = (float)(sy / len), az = (float)(sz / len);

            var minDot = 1f;
            for (var i = 0; i < n; i++)
            {
                var b = i * floatsPerStar;
                var dot = ax * span[b] + ay * span[b + 1] + az * span[b + 2];
                if (dot < minDot)
                {
                    minDot = dot;
                }
            }
            return (ax, ay, az, MathF.Acos(Math.Clamp(minDot, -1f, 1f)));
        }
    }
}
