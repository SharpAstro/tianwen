using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Magnitude-prefix indexing over a flat star-instance buffer: sort brightest-first once, then
    /// answer "how many instances are at or above this magnitude limit" as an array lookup.
    ///
    /// <para><b>Why this exists.</b> The Tycho-2 star buffer holds ~2.5 million instances. Submitting
    /// all of them every frame is unbounded GPU work that does not shrink when the view does, and at
    /// a typical field most of those stars are below the magnitude limit and contribute nothing.
    /// The unbounded form is not merely slow: it <b>TDR'd an Adreno X1-85</b> on the desktop path,
    /// which is why <see cref="VkSkyMapPipeline"/> already culls this way. Sorting brightest-first
    /// makes the visible set a PREFIX of the buffer, so the cull costs one lookup and a smaller draw
    /// count -- no per-frame CPU pass, no re-upload, nothing to keep in sync.</para>
    ///
    /// <para>Surface-agnostic on purpose: the Vulkan pipeline applies these per spatial chunk (it can
    /// offset a draw by <c>firstInstance</c>), while the WebGL pipeline applies them across the whole
    /// buffer (WebGL2 has no base-instance, so it draws one prefix). Same primitives, different
    /// granularity -- and the magnitude arithmetic has exactly one implementation.</para>
    /// </summary>
    public static class StarMagnitudeIndex
    {
        /// <summary>Bin count: V 0..15 in 0.5-magnitude steps.</summary>
        public const int BinCount = 30;

        /// <summary>
        /// One star exactly as <see cref="SkyMapState.FloatsPerStar"/> lays it out, so a star buffer
        /// can be reinterpreted as records instead of being indexed by hand. Blittable and exactly
        /// 20 bytes, which is what makes the <see cref="MemoryMarshal.Cast{TFrom, TTo}(Span{TFrom})"/>
        /// below a free reinterpretation rather than a copy.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct StarRecord
        {
            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public readonly float Magnitude;
            public readonly float ColourIndex;
        }

        /// <summary>
        /// A STRUCT comparer, used with the generic <c>Sort&lt;T, TComparer&gt;</c> overload so it is
        /// constrained rather than boxed. A <see cref="Comparison{T}"/> would be wrapped in a class
        /// internally, which is the one allocation this whole approach exists to avoid.
        /// </summary>
        private readonly struct ByMagnitude : IComparer<StarRecord>
        {
            public int Compare(StarRecord a, StarRecord b) => a.Magnitude.CompareTo(b.Magnitude);
        }

        /// <summary>
        /// Sorts a star buffer brightest-first, in place and <b>without allocating</b>.
        ///
        /// <para>This runs on the buffer build (once per catalog load, off the render thread), not per
        /// frame -- but the buffer is ~2.5 million stars, so the obvious index-sort-then-scatter form
        /// costs ~70 MB of transient arrays at exactly the moment the user is waiting for the atlas,
        /// and on single-threaded WASM that is not free. Reinterpreting the floats as records lets
        /// <c>Span.Sort</c> do it in place with a struct comparer: no index array, no scatter buffer,
        /// no GC pressure.</para>
        ///
        /// <para>Throws on a span that is not a whole number of records -- see
        /// <see cref="EnsureWholeRecords"/> for why that is not merely pedantic.</para>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="span"/>'s length is not a multiple of the record size.
        /// </exception>
        public static void SortBrightestFirst(Span<float> span)
        {
            EnsureWholeRecords(span.Length, nameof(span));
            MemoryMarshal.Cast<float, StarRecord>(span).Sort(default(ByMagnitude));
        }

        /// <summary>
        /// Rejects a span that is not a whole number of star records.
        ///
        /// <para><b>Why this throws instead of coping.</b> <see cref="MemoryMarshal.Cast{TFrom, TTo}(Span{TFrom})"/>
        /// TRUNCATES: a span of 12 floats reinterprets as 2 records and the trailing 2 floats are
        /// simply not there. Nothing fails. The sort then reorders the records it can see while those
        /// trailing floats stay where they are, so they end up describing a different star than the
        /// one whose fields precede them, and <see cref="ComputeBins"/> indexes a buffer that no
        /// longer matches what will be drawn. The symptom is a plausible-looking star field with
        /// stars in the wrong places -- not a crash, not a blank screen, and nothing that points at
        /// the caller who computed a length wrong. A bad length here is always a programming error
        /// (every caller derives it from a star count), so failing loudly at the boundary is strictly
        /// better than rendering a subtly wrong sky.</para>
        ///
        /// <para>The unit comes from the record type itself rather than from
        /// <see cref="SkyMapState.FloatsPerStar"/>, so it is by construction the same unit the cast
        /// truncates on and cannot drift from it.</para>
        /// </summary>
        internal static void EnsureWholeRecords(int length, string paramName)
        {
            var floatsPerRecord = Unsafe.SizeOf<StarRecord>() / sizeof(float);
            if (length % floatsPerRecord != 0)
            {
                throw new ArgumentException(
                    $"Star buffer length {length} is not a whole number of {floatsPerRecord}-float records; "
                    + "the partial tail would be silently dropped and left describing the wrong star.",
                    paramName);
            }
        }

        /// <summary>
        /// Builds the magnitude to instance-count lookup from a buffer already sorted by
        /// <see cref="SortBrightestFirst"/>. Allocates only the 30-entry table it returns.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="sortedSpan"/>'s length is not a multiple of the record size.
        /// </exception>
        public static uint[] ComputeBins(ReadOnlySpan<float> sortedSpan)
        {
            EnsureWholeRecords(sortedSpan.Length, nameof(sortedSpan));
            var records = MemoryMarshal.Cast<float, StarRecord>(sortedSpan);
            var magBins = new uint[BinCount];

            uint idx = 0;
            for (var bin = 0; bin < BinCount; bin++)
            {
                var magThreshold = (bin + 1) * 0.5f;
                while (idx < records.Length && records[(int)idx].Magnitude <= magThreshold)
                {
                    idx++;
                }
                magBins[bin] = idx;
            }
            return magBins;
        }

        /// <summary>
        /// Instances to draw for the given magnitude limit. Because the buffer is sorted
        /// brightest-first this is a prefix count, so the caller draws instances <c>[0, n)</c>.
        /// A limit beyond the last bin clamps to "everything indexed", never to zero.
        ///
        /// <para>This is the only member on the per-frame path (once per visible chunk on Vulkan, once
        /// per buffer on WebGL) and it is an array index plus a clamp -- no allocation, nothing to
        /// pool. The sort and the bin build above are one-time buffer-build work.</para>
        /// </summary>
        public static uint VisibleCount(uint[] magBins, float magLimit)
        {
            if (magBins.Length == 0)
            {
                return 0;
            }
            var bin = Math.Clamp((int)(magLimit * 2) - 1, 0, magBins.Length - 1);
            return magBins[bin];
        }
    }
}
