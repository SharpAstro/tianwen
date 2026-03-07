using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Astrometry.Catalogs;

internal sealed class CompositeRaDecIndex(RaDecIndex primary, Tycho2RaDecIndex? tycho2) : IRaDecIndex
{
    public IReadOnlyCollection<CatalogIndex> this[double ra, double dec]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var direct = primary[ra, dec];
            return tycho2 is not null ? new CompositeCollection(direct, tycho2, ra, dec) : direct;
        }
    }

    private sealed class CompositeCollection(
        IReadOnlyCollection<CatalogIndex> directEntries,
        Tycho2RaDecIndex tycho2Index,
        double ra,
        double dec
    ) : ICollection<CatalogIndex>, IReadOnlyCollection<CatalogIndex>
    {
        public int Count => directEntries.Count > 0 || tycho2Index.GetOverlappingRegions(ra, dec) is { Length: > 0 } ? 1 : 0;

        public bool IsReadOnly => true;

        public bool Contains(CatalogIndex item)
        {
            foreach (var entry in directEntries)
            {
                if (entry == item)
                    return true;
            }

            return tycho2Index.Contains(item, ra, dec);
        }

        public IEnumerator<CatalogIndex> GetEnumerator()
        {
            foreach (var entry in directEntries)
                yield return entry;

            foreach (var tycEntry in tycho2Index.GetStarsInCell(ra, dec))
                yield return tycEntry;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        void ICollection<CatalogIndex>.Add(CatalogIndex item) => throw new NotSupportedException();
        void ICollection<CatalogIndex>.Clear() => throw new NotSupportedException();
        bool ICollection<CatalogIndex>.Remove(CatalogIndex item) => throw new NotSupportedException();
        void ICollection<CatalogIndex>.CopyTo(CatalogIndex[] array, int arrayIndex) => throw new NotSupportedException();
    }
}
