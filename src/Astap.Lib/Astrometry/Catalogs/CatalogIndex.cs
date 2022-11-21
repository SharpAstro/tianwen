using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

/// <summary>
/// Represents a unique entry in a catalogue s.th. NGC0001, M13 or IC0001
/// </summary>
public enum CatalogIndex : ulong { }

public static class CatalogIndexEx
{
    public static string ToCanonical(this CatalogIndex catalogIndex)
    {
        var (catalog, decoded, isMSBSet) = catalogIndex.ToCatalogAndDecoded();

        var prefix = catalog.ToCanonical();
        if (isMSBSet)
        {
            return catalog switch
            {
                Catalog.PSR =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 4, 4, PSRRaMask, PSRRaShift, PSRDecMask, PSREpochSupport),
                Catalog.TwoMass or Catalog.TwoMassX =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 8, 7, TwoMassRaMask, TwoMassRaShift, TwoMassDecMask, TwoMassEncOptions),
                Catalog.WDS =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 5, 4, WDSRaMask, WDSRaShift, WDSDecMask, WDSEncOptions),
                Catalog.BonnerDurchmusterung =>
                    BDEncodedIndexToCanonical(prefix, decoded, 0, 2, BDRaMask, BDRaShift, BDDecMask, BDEncOptions),
                _ => throw new ArgumentException($"Catalog index {catalogIndex} with MSB = true could not be parsed", nameof(catalogIndex))
            };
        }
        else
        {
            var withoutPrefixAsStr = EnumValueToAbbreviation(decoded).AsSpan().TrimStart('0');

            if (withoutPrefixAsStr.Length is 0)
            {
                throw new ArgumentException($"Catalog index {catalogIndex} could not be parsed", nameof(catalogIndex));
            }

            int Nor_Idx;
            if (catalog is Catalog.NGC or Catalog.IC && (Nor_Idx = withoutPrefixAsStr.IndexOfAny('_', 'N')) > 0)
            {
                var sb = new StringBuilder(MaxLenInASCII)
                    .Append(prefix)
                    .Append(' ')
                    .Append(withoutPrefixAsStr[..Nor_Idx])
                    .Append(withoutPrefixAsStr[Nor_Idx] == 'N' ? " NED" : "")
                    .Append(withoutPrefixAsStr[(Nor_Idx + 1)..]);
                return sb.ToString();
            }
            else
            {
                var sep = catalog switch
                {
                    Catalog.Sharpless2 or Catalog.TrES or Catalog.WASP or Catalog.XO => "-",
                    Catalog.Messier or Catalog.Caldwell => "",
                    _ => " "
                };

                return string.Concat(prefix, sep, withoutPrefixAsStr);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string BDEncodedIndexToCanonical(string prefix, ulong decoded, int raDigits, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions)
    {
        DecodeBase91(decoded, decDigits, raMask, raShift, decMask, encOptions, out var decIsNeg, out _, out var actualDecDigits, out var dec, out var ra);

        return string.Concat(
            prefix,
            decIsNeg ? "-" : "+",
            dec.ToString("D" + actualDecDigits, CultureInfo.InvariantCulture),
            " ",
            ra.ToString("D" + raDigits, CultureInfo.InvariantCulture)
        );
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string RADecEncodedIndexToCanonical(string prefix, ulong decoded, int raDigits, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions)
    {
        DecodeBase91(decoded, decDigits, raMask, raShift, decMask, encOptions, out var decIsNeg, out var epoch, out var actualDecDigits, out var dec, out var ra);

        // 2 digits for B is only valid for PSR
        return string.Concat(
            prefix, " ", epoch,
            ra.ToString("D" + raDigits, CultureInfo.InvariantCulture),
            decIsNeg ? "-" : "+",
            dec.ToString("D" + actualDecDigits, CultureInfo.InvariantCulture)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void DecodeBase91(ulong decoded, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions, out bool decIsNeg, out string epoch, out int actualDecDigits, out ulong dec, out ulong ra)
    {
        // sign
        decIsNeg = (decoded & 1) == 0b1;
        decoded >>= 1;

        if (encOptions.HasFlag(Base91EncRADecOptions.ImpliedJ2000))
        {
            epoch = "J";
            actualDecDigits = decDigits;
        }
        else
        {
            var isJ = (decoded & 1) == 0b1;
            epoch = isJ ? "J" : "B";
            actualDecDigits = isJ ? decDigits : 2;
            decoded >>= 1;
        }

        // dec
        dec = decoded & decMask;
        decoded >>= raShift;

        // ra
        ra = decoded & raMask;
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

    private static byte CatKey(ulong catAsUlong, int lzc) => (byte)(catAsUlong >> BitsInUlong - lzc - ASCIIBits & ASCIIMask);


    public static Catalog ToCatalog(this CatalogIndex catalogIndex)
    {
        var (catalog, _, _) = catalogIndex.ToCatalogAndDecoded();
        return catalog;
    }

    /// <summary>
    /// Identifies the <see cref="Catalog"/> of the given <see cref="CatalogIndex"/> <paramref name="catalogIndex"/>.
    /// Will return 0 for Catalog if Catalog cannot be determined.
    /// Additionally the decoded result together with the MSB is set information is returned so it can be used to extract
    /// embedded information such as RA and DEC or catalog index number.
    /// </summary>
    /// <param name="catalogIndex"></param>
    /// <returns>(catalog entry, decoded integral without catalog prefix/suffix, true if MSB is set)</returns>
    /// <exception cref="ArgumentException">Will throw if catalog cannot be found in the known list of catalogs</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (Catalog catalog, ulong decoded, bool msbSet) ToCatalogAndDecoded(this CatalogIndex catalogIndex)
    {
        if (catalogIndex == 0)
        {
            return (0, 0UL, false);
        }

        var catalogIndexUl = (ulong)catalogIndex;
        var isMSBSet = (catalogIndexUl & MSBUlongMask) == MSBUlongMask;
        catalogIndexUl ^= isMSBSet ? MSBUlongMask : 0ul;

        if (isMSBSet)
        {
            var encoded = EnumValueToAbbreviation(catalogIndexUl);
            if (Base91Encoder.DecodeBytes(encoded) is byte[] decoded && decoded.Length > 1)
            {
                const int max = BytesInUlong;
                Span<byte> bytesUlN = stackalloc byte[max];
                decoded.CopyTo(bytesUlN[(max - decoded.Length)..]);

                var decodedULH = BinaryPrimitives.ReadUInt64BigEndian(bytesUlN);

                return ((Catalog)(decoded[^1] & ASCIIMask), decodedULH >> ASCIIBits, true);
            }
            else
            {
                return (0, 0UL, false);
            }
        }

        var catalogIndexLZC = BitOperations.LeadingZeroCount(catalogIndexUl);

        if (!PartitionedCategories.TryGetValue(CatKey(catalogIndexUl, catalogIndexLZC), out var categories))
        {
            return (0, 0UL, false);
        }

        for (var i = 0; i < categories.Length; i++)
        {
            var entry = categories[i];
            var entryLZC = BitOperations.LeadingZeroCount((ulong)entry);
            var shift = entryLZC - catalogIndexLZC;
            var catalogIndexCat = (Catalog)(catalogIndexUl >> shift);
            if (entry == catalogIndexCat)
            {
                var decoded = catalogIndexUl & ~(ulong.MaxValue << shift);
                return (entry, decoded, false);
            }
        }

        throw new ArgumentException($"Cannot find Catalog type of {catalogIndex}", nameof(catalogIndex));
    }
}
