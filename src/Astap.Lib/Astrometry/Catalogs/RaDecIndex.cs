using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Astap.Lib.Astrometry.Catalogs;

public sealed class RaDecIndex : IRaDecIndex
{
    const int RaToRaIdxFactor = 15;

    private readonly (CatalogIndex i1, CatalogIndex[]? ext)[,] _index = new (CatalogIndex i1, CatalogIndex[]? ext)[24 * RaToRaIdxFactor, 2 * 90 + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void Add(in CelestialObject obj)
    {
        if (TryGetIndex(obj.RA, obj.Dec, out var raIdx, out var decIdx))
        {
            _index.AddLookupEntry(raIdx, decIdx, obj.Index);
        }
    }

    public IReadOnlyCollection<CatalogIndex> this[double ra, double dec]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (TryGetIndex(ra, dec, out var raIdx, out var decIdx) && _index.TryGetLookupEntries(raIdx, decIdx, out var combined))
            {
                return combined;
            }
            else
            {
                return Array.Empty<CatalogIndex>();
            }
        }
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
