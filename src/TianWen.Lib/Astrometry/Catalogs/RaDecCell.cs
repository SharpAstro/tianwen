using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// One grid cell of an <see cref="IRaDecIndex"/>, enumerated without allocating: the deep-sky
/// entries first, straight off the index's per-cell array, then the Tycho-2 stars, scanned out of
/// the catalogue's binary regions as the caller advances. <c>foreach</c> over it costs nothing on
/// the heap, which is the point: the sky map's hover resolve walks nine cells per painted frame.
/// </summary>
/// <remarks>
/// <para>
/// The indexer <see cref="IRaDecIndex.this"/> answers the same question as an
/// <see cref="IReadOnlyCollection{T}"/>, which for the composite grid meant a
/// <see cref="List{T}"/> built per cell (grown by doubling, so about 2 to 3 KB of copies for the
/// hundred-odd stars a dense cell holds), a wrapper object, a compiler-built iterator and a boxed
/// enumerator for the deep-sky half: 27.6 KB per resolve over bare sky, or 1.7 MB/s of Gen0 at
/// 60 fps with the pointer resting on the star field. This struct is that enumeration with the
/// list taken out; the scan and the cell-box test are the same code the list was built with, and
/// the order is the same (deep-sky entries, then regions in the cell's list order, then entries in
/// stream order), so <c>RaDecCellEnumerationTests</c> can hold the two answers equal cell by cell.
/// </para>
/// <para>
/// A struct rather than a <c>yield</c> iterator because the scan reads spans of the catalogue's
/// byte array, and a span cannot live across a yield boundary; a hand-written <c>MoveNext</c>
/// holds offsets instead and re-slices per entry, with no state machine to allocate. It is a plain
/// struct rather than a <c>ref struct</c> so a caller may hold one in a field.
/// </para>
/// </remarks>
public readonly struct RaDecCell
{
    private readonly CatalogIndex[]? _direct;
    private readonly byte[]? _tycho2Data;
    private readonly int _streamCount;
    private readonly List<ushort>? _regions;
    private readonly float _cellMinRa;
    private readonly float _cellMaxRa;
    private readonly float _cellMinDec;
    private readonly float _cellMaxDec;

    /// <summary>A cell with only deep-sky entries (or none), the shape <see cref="RaDecIndex"/> answers with.</summary>
    internal RaDecCell(CatalogIndex[]? direct)
    {
        _direct = direct;
    }

    /// <summary>A cell with deep-sky entries followed by the Tycho-2 stars of the cell box, the composite grid's shape.</summary>
    internal RaDecCell(
        CatalogIndex[]? direct, byte[] tycho2Data, int streamCount, List<ushort>? regions,
        float cellMinRa, float cellMaxRa, float cellMinDec, float cellMaxDec)
    {
        _direct = direct;
        _tycho2Data = tycho2Data;
        _streamCount = streamCount;
        _regions = regions;
        _cellMinRa = cellMinRa;
        _cellMaxRa = cellMaxRa;
        _cellMinDec = cellMinDec;
        _cellMaxDec = cellMaxDec;
    }

    /// <summary>
    /// The fallback for an <see cref="IRaDecIndex"/> that does not implement the allocation-free
    /// path itself: materialises the collection once. Used by <see cref="IRaDecIndex.EnumerateCell"/>'s
    /// default body, which is to say by test fakes; both real indexes override it.
    /// </summary>
    public static RaDecCell FromCollection(IReadOnlyCollection<CatalogIndex> entries)
    {
        return new RaDecCell(entries as CatalogIndex[] ?? [.. entries]);
    }

    /// <summary>The deep-sky entries of the cell, which the enumeration yields first.</summary>
    public ReadOnlySpan<CatalogIndex> DirectEntries => _direct;

    public Enumerator GetEnumerator() => new Enumerator(this);

    /// <summary>
    /// Walks the deep-sky array, then every overlapping GSC region's 17-byte entries, yielding the
    /// ones whose position is inside the cell box. Same test, same order as the list it replaced.
    /// </summary>
    public struct Enumerator
    {
        private const int EntrySize = 17;

        private readonly RaDecCell _cell;
        private int _directPos;
        private int _regionPos;
        private int _entryPos;
        private int _entryEnd;
        private ushort _tyc1;
        private CatalogIndex _current;

        internal Enumerator(RaDecCell cell)
        {
            _cell = cell;
            _directPos = 0;
            _regionPos = 0;
            // The first MoveNext into the Tycho-2 half opens the first region; until then the entry
            // window is empty so it falls straight through to the region loop.
            _entryPos = 0;
            _entryEnd = 0;
            _tyc1 = 0;
            _current = default;
        }

        public readonly CatalogIndex Current => _current;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            var direct = _cell._direct;
            if (direct is not null && _directPos < direct.Length)
            {
                _current = direct[_directPos++];
                return true;
            }

            var data = _cell._tycho2Data;
            var regions = _cell._regions;
            if (data is null || regions is null)
            {
                return false;
            }

            while (true)
            {
                // Scan the open region's remaining entries for the next one inside the cell box.
                while (_entryPos < _entryEnd)
                {
                    var pos = _entryPos;
                    _entryPos += EntrySize;
                    var entry = data.AsSpan(pos, EntrySize);
                    var entryRA = BinaryPrimitives.ReadSingleLittleEndian(entry[3..]);
                    var entryDec = BinaryPrimitives.ReadSingleLittleEndian(entry[7..]);
                    if (entryRA >= _cell._cellMinRa && entryRA < _cell._cellMaxRa
                        && entryDec >= _cell._cellMinDec && entryDec < _cell._cellMaxDec)
                    {
                        var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                        var tyc3 = entry[2];
                        // The direct, stackalloc form rather than a string built only to be parsed
                        // back (see EnumerateStarsInDecBand). This is the per-cell path the sky
                        // map's click and hover resolve walks nine times per press or frame.
                        _current = Tyc2CatalogIndex(Catalog.Tycho2, _tyc1, tyc2, tyc3);
                        return true;
                    }
                }

                // Open the next region that has a stream, or finish.
                if (_regionPos >= regions.Count)
                {
                    return false;
                }

                _tyc1 = regions[_regionPos++];
                var gscIdx = _tyc1 - 1;
                if (gscIdx < 0 || gscIdx >= _cell._streamCount)
                {
                    continue;
                }

                Tycho2RaDecIndex.GetRegionOffsets(data, _cell._streamCount, gscIdx, out _entryPos, out _entryEnd);
                // A region's byte range is a whole number of entries; the end is exclusive.
                _entryEnd = _entryPos + (_entryEnd - _entryPos) / EntrySize * EntrySize;
            }
        }
    }
}
