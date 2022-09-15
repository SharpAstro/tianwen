using System;
using System.Linq;
using System.Numerics;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

/// <summary>
/// Represents a unique entry in a catalogue s.th. NGC0001, M13 or IC0001
/// </summary>
public enum CatalogIndex : ulong { }

public static class CatalogIndexEx
{
    public static string ToAbbreviation(this CatalogIndex catalogIndex) => EnumValueToAbbreviation((ulong)catalogIndex);

    private static readonly Catalog[] CatalogEntriesBySizeDesc = Enum.GetValues<Catalog>().OrderByDescending(x => (ulong)x).ToArray();

    public static Catalog ToCatalog(this CatalogIndex catalogIndex)
    {
        if (catalogIndex == 0)
        {
            return 0;
        }

        var catIdxAsUlong = (ulong)catalogIndex;
        var catIndexLZC = BitOperations.LeadingZeroCount(catIdxAsUlong);

        for (var i = 0; i < CatalogEntriesBySizeDesc.Length; i++)
        {
            var entry = CatalogEntriesBySizeDesc[i];
            var entryLZC = BitOperations.LeadingZeroCount((ulong)entry);
            var catalogIndexCat = (Catalog)(catIdxAsUlong >> (entryLZC - catIndexLZC));
            if (entry == catalogIndexCat)
            {
                return entry;
            }
        }

        throw new ArgumentException($"Cannot find Catalog type of {catalogIndex}", nameof(catalogIndex));
    }
}
