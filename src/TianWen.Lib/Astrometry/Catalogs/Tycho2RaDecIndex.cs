using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Astrometry.Catalogs;

internal sealed class Tycho2RaDecIndex
{
    const int RaGridSize = 24 * 15;
    const int DecGridSize = 2 * 90 + 1;

    private readonly List<ushort>?[,] _gscRegionsPerCell = new List<ushort>[RaGridSize, DecGridSize];
    private readonly byte[] _tycho2Data;
    private readonly int _streamCount;

    internal Tycho2RaDecIndex(byte[] tycho2Data, int streamCount, ReadOnlySpan<byte> boundsData)
    {
        _tycho2Data = tycho2Data;
        _streamCount = streamCount;

        var cellLists = new List<ushort>?[RaGridSize, DecGridSize];

        for (int gscIdx = 0; gscIdx < streamCount; gscIdx++)
        {
            var offset = gscIdx * 16;
            var minRA  = BinaryPrimitives.ReadSingleLittleEndian(boundsData[offset..]);
            var maxRA  = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(offset + 4)..]);
            var minDec = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(offset + 8)..]);
            var maxDec = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(offset + 12)..]);

            if (minRA > maxRA && minDec > maxDec)
            {
                continue;
            }

            var tyc1 = (ushort)(gscIdx + 1);

            int minRaIdx = (int)(minRA * 15) % RaGridSize;
            int maxRaIdx = (int)(maxRA * 15) % RaGridSize;
            int minDecIdx = Math.Clamp((int)(minDec + 90), 0, DecGridSize - 1);
            int maxDecIdx = Math.Clamp((int)(maxDec + 90), 0, DecGridSize - 1);

            bool wrapsRA = minRA > maxRA;

            for (int decIdx = minDecIdx; decIdx <= maxDecIdx; decIdx++)
            {
                if (wrapsRA)
                {
                    for (int raIdx = minRaIdx; raIdx < RaGridSize; raIdx++)
                        AddGscToCell(cellLists, raIdx, decIdx, tyc1);
                    for (int raIdx = 0; raIdx <= maxRaIdx; raIdx++)
                        AddGscToCell(cellLists, raIdx, decIdx, tyc1);
                }
                else
                {
                    for (int raIdx = minRaIdx; raIdx <= maxRaIdx; raIdx++)
                        AddGscToCell(cellLists, raIdx, decIdx, tyc1);
                }
            }
        }

        for (int ra = 0; ra < RaGridSize; ra++)
            for (int dec = 0; dec < DecGridSize; dec++)
                if (cellLists[ra, dec] is { Count: > 0 } list)
                    _gscRegionsPerCell[ra, dec] = list;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddGscToCell(List<ushort>?[,] cellLists, int raIdx, int decIdx, ushort tyc1)
    {
        cellLists[raIdx, decIdx] ??= new List<ushort>(8);
        cellLists[raIdx, decIdx]!.Add(tyc1);
    }

    internal bool Contains(CatalogIndex index, double ra, double dec)
    {
        if (!TryGetGridIndex(ra, dec, out var raIdx, out var decIdx))
            return false;

        var (cat, value, msbSet) = index.ToCatalogAndValue();
        if (cat is not Catalog.Tycho2 || !msbSet)
            return false;

        var (tyc1, tyc2, tyc3) = DecodeTyc2CatalogIndex(value);

        var regions = _gscRegionsPerCell[raIdx, decIdx];
        if (regions is null)
            return false;

        foreach (var regionTyc1 in regions)
        {
            if (regionTyc1 == tyc1)
                return StarExistsInRegion(tyc1, (ushort)tyc2, (byte)tyc3);
        }

        return false;
    }

    internal List<ushort>? GetOverlappingRegions(double ra, double dec)
    {
        if (TryGetGridIndex(ra, dec, out var raIdx, out var decIdx))
            return _gscRegionsPerCell[raIdx, decIdx];
        return null;
    }

    /// <summary>
    /// Polar-cap fast path: returns all Tycho-2 stars in a Dec band (full RA),
    /// scanning each overlapping GSC region's binary entries exactly once.
    ///
    /// The general per-cell path (<see cref="GetStarsInCell"/>) is O(cells x
    /// stars-in-overlapping-regions) — when the search radius covers the full
    /// 24h of RA at the pole the same polar GSC regions get linearly re-scanned
    /// hundreds of times and one CatalogPlateSolver invocation explodes from
    /// seconds to minutes. By collecting the unique regions across the Dec band
    /// and walking each region's entries once with a Dec-only filter, this
    /// drops to O(unique-regions x entries) — typically 100x faster near
    /// |Dec| = 90°. Mirrors the polar-pan optimisation in commit 69c7266.
    /// </summary>
    /// <param name="minDec">Lower Dec bound in degrees (inclusive).</param>
    /// <param name="maxDec">Upper Dec bound in degrees (inclusive).</param>
    internal IEnumerable<CatalogIndex> EnumerateStarsInDecBand(double minDec, double maxDec)
    {
        if (maxDec < minDec)
        {
            yield break;
        }

        var minDecIdx = Math.Clamp((int)Math.Floor(minDec + 90.0), 0, DecGridSize - 1);
        var maxDecIdx = Math.Clamp((int)Math.Ceiling(maxDec + 90.0), 0, DecGridSize - 1);

        // Collect every unique GSC region that overlaps any cell in the Dec band.
        // Polar caps typically yield only a handful of regions; the dedup is what
        // makes this fast.
        var seenRegions = new HashSet<ushort>();
        for (int decIdx = minDecIdx; decIdx <= maxDecIdx; decIdx++)
        {
            for (int raIdx = 0; raIdx < RaGridSize; raIdx++)
            {
                if (_gscRegionsPerCell[raIdx, decIdx] is { } regions)
                {
                    foreach (var tyc1 in regions)
                    {
                        seenRegions.Add(tyc1);
                    }
                }
            }
        }

        const int entrySize = 13;
        var minDecF = (float)minDec;
        var maxDecF = (float)maxDec;

        foreach (var tyc1 in seenRegions)
        {
            var gscIdx = tyc1 - 1;
            if (gscIdx < 0 || gscIdx >= _streamCount)
            {
                continue;
            }

            GetRegionOffsets(gscIdx, out var startOffset, out var endOffset);
            var entryCount = (endOffset - startOffset) / entrySize;

            for (int i = 0; i < entryCount; i++)
            {
                var pos = startOffset + i * entrySize;
                // Re-slice the array per-iteration -- can't preserve a Span<byte>
                // across the yield boundary (compiler captures locals into the
                // state machine and Span isn't ref-storable).
                var entryDec = BinaryPrimitives.ReadSingleLittleEndian(_tycho2Data.AsSpan(pos + 7, 4));

                if (entryDec < minDecF || entryDec > maxDecF)
                {
                    continue;
                }

                var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(_tycho2Data.AsSpan(pos, 2));
                var tyc3 = _tycho2Data[pos + 2];

                var encoded = EncodeTyc2CatalogIndex(Catalog.Tycho2, tyc1, tyc2, tyc3);
                yield return AbbreviationToCatalogIndex(encoded, isBase91Encoded: true);
            }
        }
    }

    internal List<CatalogIndex> GetStarsInCell(double ra, double dec)
    {
        var result = new List<CatalogIndex>();
        var regions = GetOverlappingRegions(ra, dec);
        if (regions is null || !TryGetGridIndex(ra, dec, out var raIdx, out _))
            return result;

        float cellMinRA = raIdx / 15f;
        float cellMaxRA = (raIdx + 1) / 15f;
        float cellMinDec = (float)(Math.Floor(dec + 90) - 90);
        float cellMaxDec = cellMinDec + 1f;

        const int entrySize = 13;

        foreach (var tyc1 in regions)
        {
            var gscIdx = tyc1 - 1;
            if (gscIdx < 0 || gscIdx >= _streamCount)
                continue;

            GetRegionOffsets(gscIdx, out var startOffset, out var endOffset);
            var entryCount = (endOffset - startOffset) / entrySize;

            for (int i = 0; i < entryCount; i++)
            {
                var pos = startOffset + i * entrySize;
                var entryRA = BinaryPrimitives.ReadSingleLittleEndian(_tycho2Data.AsSpan(pos + 3, 4));
                var entryDec = BinaryPrimitives.ReadSingleLittleEndian(_tycho2Data.AsSpan(pos + 7, 4));

                if (entryRA >= cellMinRA && entryRA < cellMaxRA && entryDec >= cellMinDec && entryDec < cellMaxDec)
                {
                    var tyc2 = BinaryPrimitives.ReadUInt16LittleEndian(_tycho2Data.AsSpan(pos, 2));
                    var tyc3 = _tycho2Data[pos + 2];

                    var encoded = EncodeTyc2CatalogIndex(Catalog.Tycho2, tyc1, tyc2, tyc3);
                    result.Add(AbbreviationToCatalogIndex(encoded, isBase91Encoded: true));
                }
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool StarExistsInRegion(ushort tyc1, ushort tyc2, byte tyc3)
    {
        var gscIdx = tyc1 - 1;
        if (gscIdx < 0 || gscIdx >= _streamCount)
            return false;

        const int entrySize = 13;
        GetRegionOffsets(gscIdx, out var startOffset, out var endOffset);
        var entryCount = (endOffset - startOffset) / entrySize;
        var data = _tycho2Data.AsSpan();

        for (int i = 0; i < entryCount; i++)
        {
            var entry = data[(startOffset + i * entrySize)..];
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == tyc2 && entry[2] == tyc3)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetRegionOffsets(int gscIdx, out int startOffset, out int endOffset)
    {
        var data = _tycho2Data.AsSpan();
        startOffset = BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 1) * 4)..]);
        endOffset = gscIdx + 1 < _streamCount
            ? BinaryPrimitives.ReadInt32LittleEndian(data[((gscIdx + 2) * 4)..])
            : _tycho2Data.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetGridIndex(double ra, double dec, out int raIdx, out int decIdx)
    {
        if (!double.IsNaN(ra) && !double.IsNaN(dec))
        {
            raIdx = (int)(ra * 15) % RaGridSize;
            decIdx = Math.Clamp((int)(dec + 90), 0, DecGridSize - 1);
            return true;
        }
        raIdx = -1;
        decIdx = -1;
        return false;
    }
}
