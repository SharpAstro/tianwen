using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Astap.Lib.Astrometry.Catalogs;

public sealed class RaDecIndex : IRaDecIndex
{
    const double DecToDecIdxFactor = 0.25; // 4x4 degree squares
    const double RaToRaIdxFactor = 15.0 * DecToDecIdxFactor;

    private readonly (CatalogIndex i1, CatalogIndex[]? ext)[,] _index = new (CatalogIndex i1, CatalogIndex[]? ext)[(int)Math.Ceiling(24 * RaToRaIdxFactor), (int)Math.Ceiling(2 * 90 * DecToDecIdxFactor)];

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void Add(in CelestialObject obj)
    {
        if (TryGetIndex(obj.RA, obj.Dec, out var raIdx, out var decIdx))
        {
            _index.AddLookupEntry(raIdx, decIdx, obj.Index);
        }
    }

    public IReadOnlyCollection<CatalogIndex> this[double ra, double dec]
        => TryGetIndex(ra, dec, out var raIdx, out var decIdx) && _index.TryGetLookupEntries(raIdx, decIdx, out var combined)
            ? combined
            : (IReadOnlyCollection<CatalogIndex>)Array.Empty<CatalogIndex>();

    private bool TryGetIndex(double ra, double dec, out int raIdx, out int decIdx)
    {
        if (!double.IsNaN(ra) && !double.IsNaN(dec))
        {
            raIdx = (int)(ra * RaToRaIdxFactor) % _index.GetLength(0);
            decIdx = Math.Max(0, (int)((dec + 90) * DecToDecIdxFactor)) % _index.GetLength(1);
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
