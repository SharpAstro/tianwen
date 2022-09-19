using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Numerics;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

/// <summary>
/// Represents a unique entry in a catalogue s.th. NGC0001, M13 or IC0001
/// </summary>
public enum CatalogIndex : ulong { }

public static class CatalogIndexEx
{
    public static string ToCanonical(this CatalogIndex catalogIndex)
    {
        var catalog = catalogIndex.ToCatalog();
        var catalogLZC = BitOperations.LeadingZeroCount((ulong)catalog);
        var prefix = catalog.ToCanonical();
        var catalogIndexUl = (ulong)catalogIndex;
        var catalogIndexLZC = BitOperations.LeadingZeroCount(catalogIndexUl);

        var withoutCatalogPrefixUl = catalogIndexUl &= ~(ulong.MaxValue << (-catalogIndexLZC + catalogLZC));
        var withoutPrefixAsStr = EnumValueToAbbreviation(withoutCatalogPrefixUl).TrimStart('0');

        if (withoutPrefixAsStr.Length is 0)
        {
            throw new ArgumentException($"Catalog index {catalogIndex} could not be parsed", nameof(catalogIndex));
        }

        int Nor_Idx;
        if (catalog is Catalog.PSR)
        {
            return FormatPSR(prefix, withoutPrefixAsStr);
        }
        else if (catalog is Catalog.NGC or Catalog.IC && (Nor_Idx = withoutPrefixAsStr.IndexOfAny(new char[] { '_', 'N' })) > 0)
        {
            return string.Concat(prefix, " ", withoutPrefixAsStr[..Nor_Idx], withoutPrefixAsStr[Nor_Idx] == 'N' ? " NED" : "", withoutPrefixAsStr[(Nor_Idx + 1)..]);
        }
        else
        {
            var sep = catalog switch
            {
                Catalog.Sharpless or Catalog.TrES or Catalog.WASP or Catalog.XO => "-",
                _ => " "
            };

            return string.Concat(prefix, sep, withoutPrefixAsStr);
        }
    }

    private static string FormatPSR(string prefix, string withoutPrefixAsStr)
    {
        var epoch = withoutPrefixAsStr[0];
        var base64Str = withoutPrefixAsStr[1..];
        var padding = Math.DivRem(base64Str.Length, 3, out _);
        var bytes = Convert.FromBase64String(string.Concat(base64Str, new('=', padding)));
        var intN = BitConverter.ToInt32(bytes);
        var intH = IPAddress.NetworkToHostOrder(intN);
        var decIsNeg = (intH & 1) == 0b1;
        intH >>= 1;
        var dec = intH & Utils.PSRDecMask;
        intH >>= Utils.PSRRaShift - 1;
        var ra = intH & Utils.PSRRaMask;
        return string.Concat(
            prefix, " ", epoch,
            ra.ToString("D4", CultureInfo.InvariantCulture),
            decIsNeg ? '-' : '+',
            dec.ToString("D" + (epoch == 'B' ? 2 : 4), CultureInfo.InvariantCulture)
        );
    }

    public static string ToAbbreviation(this CatalogIndex catalogIndex) => EnumValueToAbbreviation((ulong)catalogIndex);

    private static readonly Dictionary<byte, Catalog[]> PartitionedCategories = Enum.GetValues<Catalog>().PartitionCatalogsByMSB();

    static Dictionary<byte, Catalog[]> PartitionCatalogsByMSB(this Catalog[] entries)
    {
        var dict = new Dictionary<byte, Catalog[]>(26);

        foreach (var entry in entries.OrderByDescending(x => (ulong)x))
        {
            var catAsUlong = (ulong)entry;
            var catLZC = BitOperations.LeadingZeroCount(catAsUlong);
            var key = CatKey(catAsUlong, catLZC);
            dict.AddLookupEntry(key, entry);
        }

        return dict;
    }

    private static byte CatKey(ulong catAsUlong, int lzc) => (byte)((catAsUlong >> BitsInUlong - lzc - ASCIIBits) & ASCIIMask);

    public static Catalog ToCatalog(this CatalogIndex catalogIndex)
    {
        if (catalogIndex == 0)
        {
            return 0;
        }

        var catIdxAsUlong = (ulong)catalogIndex;
        var catIndexLZC = BitOperations.LeadingZeroCount(catIdxAsUlong);

        if (!PartitionedCategories.TryGetValue(CatKey(catIdxAsUlong, catIndexLZC), out var categories))
        {
            return 0;
        }

        for (var i = 0; i < categories.Length; i++)
        {
            var entry = categories[i];
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
