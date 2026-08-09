using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Astrometry.Catalogs;

public sealed class RaDecIndex : IRaDecIndex
{
    const int RaToRaIdxFactor = 15;

    private readonly (CatalogIndex i1, CatalogIndex[]? ext)[,] _index = new (CatalogIndex i1, CatalogIndex[]? ext)[24 * RaToRaIdxFactor, 2 * 90 + 1];

    /// <summary>
    /// Per-cell merged view of <see cref="_index"/>, built on first query after the last
    /// <see cref="Add"/> and reused by every query after that. Null until built, and reset to null
    /// by <see cref="Add"/> so it can never answer from stale contents.
    /// <para>
    /// <b>Why it exists.</b> The storage shape is "first entry inline plus an overflow array",
    /// which is compact but is not a list, so serving a query used to merge the two into a
    /// <i>freshly allocated</i> array on every single lookup. That is invisible for a point query
    /// and ruinous for a sweep: the sky map's overlay gather walks one cell per square degree, so a
    /// wide field allocated tens of thousands of small arrays per pass and a full-sky field
    /// allocated 78 MB in one gather. Merging once per cell instead of once per lookup makes a
    /// repeat sweep allocation-free, at a one-off cost of one array per populated cell.
    /// </para>
    /// </summary>
    private CatalogIndex[]?[,]? _merged;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void Add(in CelestialObject obj)
    {
        if (TryGetIndex(obj.RA, obj.Dec, out var raIdx, out var decIdx))
        {
            _index.AddLookupEntry(raIdx, decIdx, obj.Index);
            // Drop the merged view rather than patching it: adds happen while the catalog loads,
            // queries come afterwards, so this rebuilds at most once more. Invalidating here is
            // what removes the need for callers to remember to seal the index at the end of init;
            // a late add cannot leave a stale cell behind.
            _merged = null;
        }
    }

    public IReadOnlyCollection<CatalogIndex> this[double ra, double dec]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (!TryGetIndex(ra, dec, out var raIdx, out var decIdx))
            {
                return Array.Empty<CatalogIndex>();
            }

            return (_merged ?? BuildMerged())[raIdx, decIdx] ?? (IReadOnlyCollection<CatalogIndex>)Array.Empty<CatalogIndex>();
        }
    }

    /// <summary>
    /// Materialises <see cref="_merged"/> for every populated cell. Racing callers may each build
    /// one; the results are identical by construction and publication is a single reference write,
    /// so the loser's copy is simply collected. That is cheaper than serialising every reader on a
    /// lock for a table that is written only while the catalog loads.
    /// </summary>
    private CatalogIndex[]?[,] BuildMerged()
    {
        var raLen = _index.GetLength(0);
        var decLen = _index.GetLength(1);
        var merged = new CatalogIndex[]?[raLen, decLen];

        for (var raIdx = 0; raIdx < raLen; raIdx++)
        {
            for (var decIdx = 0; decIdx < decLen; decIdx++)
            {
                if (_index.TryGetLookupEntries(raIdx, decIdx, out var combined) && combined.Count > 0)
                {
                    // TryGetLookupEntries already hands back a private array it just built for us,
                    // so adopt it instead of copying again. Empty cells stay null: one null check
                    // beats storing 65k references to the same empty array.
                    merged[raIdx, decIdx] = combined as CatalogIndex[] ?? [.. combined];
                }
            }
        }

        _merged = merged;
        return merged;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryGetIndex(double ra, double dec, out int raIdx, out int decIdx)
    {
        if (!double.IsNaN(ra) && !double.IsNaN(dec))
        {
            raIdx = (int)(ra * RaToRaIdxFactor) % _index.GetLength(0);
            decIdx = Math.Max(0, (int)(dec + 90)) % _index.GetLength(1);
            return true;
        }
        else
        {
            raIdx = -1;
            decIdx = -1;
            return false;
        }
    }
}
